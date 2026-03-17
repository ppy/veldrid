using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Veldrid.Android;
using Veldrid.MetalBindings;
using Veldrid.OpenGL.EAGL;
using Veldrid.OpenGLBindings;
using static Veldrid.OpenGLBindings.OpenGLNative;
using static Veldrid.OpenGL.OpenGLUtil;
using static Veldrid.OpenGL.EGL.EglNative;

namespace Veldrid.OpenGL
{
    internal unsafe class OpenGLGraphicsDevice : GraphicsDevice
    {
        public override string DeviceName => deviceName;

        public override string VendorName => vendorName;

        public override GraphicsApiVersion ApiVersion => apiVersion;

        public override GraphicsBackend BackendType => backendType;

        public override bool IsUvOriginTopLeft => false;

        public override bool IsDepthRangeZeroToOne => isDepthRangeZeroToOne;

        public override bool IsClipSpaceYInverted => false;

        public override ResourceFactory ResourceFactory => resourceFactory;

        public OpenGLExtensions Extensions { get; private set; }

        public override Swapchain MainSwapchain => mainSwapchain;

        public string Version { get; private set; }

        public string ShadingLanguageVersion { get; private set; }

        public OpenGLTextureSamplerManager TextureSamplerManager { get; private set; }

        public override GraphicsDeviceFeatures Features => features;

        public StagingMemoryPool StagingMemoryPool { get; } = new StagingMemoryPool();

        public override bool SyncToVerticalBlank
        {
            get => syncToVBlank;
            set
            {
                if (syncToVBlank != value)
                {
                    syncToVBlank = value;
                    executionThread.SetSyncToVerticalBlank(value);
                }
            }
        }

        private readonly ConcurrentQueue<IOpenGLDeferredResource> resourcesToDispose
            = new ConcurrentQueue<IOpenGLDeferredResource>();

        private readonly object commandListDisposalLock = new object();

        private readonly Dictionary<OpenGLCommandList, int> submittedCommandListCounts
            = new Dictionary<OpenGLCommandList, int>();

        private readonly HashSet<OpenGLCommandList> commandListsToDispose = new HashSet<OpenGLCommandList>();

        private readonly object mappedResourceLock = new object();

        private readonly Dictionary<MappedResourceCacheKey, MappedResourceInfoWithStaging> mappedResources
            = new Dictionary<MappedResourceCacheKey, MappedResourceInfoWithStaging>();

        private readonly object resetEventsLock = new object();
        private readonly List<ManualResetEvent[]> resetEvents = new List<ManualResetEvent[]>();

        private static readonly uint update_texture_args_size = (uint)Unsafe.SizeOf<UpdateTextureArgs>();
        private ResourceFactory resourceFactory;
        private string deviceName;
        private string vendorName;
        private GraphicsApiVersion apiVersion;
        private GraphicsBackend backendType;
        private GraphicsDeviceFeatures features;
        private uint vao;
        private IntPtr glContext;
        private Action<IntPtr> makeCurrent;
        private Action<IntPtr> deleteContext;
        private Action swapBuffers;
        private Action<bool> setSyncToVBlank;
        private OpenGLSwapchainFramebuffer swapchainFramebuffer;
        private OpenGLCommandExecutor commandExecutor;
        private DebugProc debugMessageCallback;
        private bool isDepthRangeZeroToOne;
        private BackendInfoOpenGL openglInfo;

        private TextureSampleCount maxColorTextureSamples;
        private uint maxTextureSize;
        private uint maxTextureDepth;
        private uint maxTextureArrayLayers;
        private uint minUboOffsetAlignment;
        private uint minSsboOffsetAlignment;
        private BlockingCollection<ExecutionThreadWorkItem> workItems;
        private ExecutionThread executionThread;
        private Swapchain mainSwapchain;

        private bool syncToVBlank;

        public OpenGLGraphicsDevice(
            GraphicsDeviceOptions options,
            OpenGLPlatformInfo platformInfo,
            uint width,
            uint height)
        {
            init(options, platformInfo, width, height, true);
        }

        public OpenGLGraphicsDevice(GraphicsDeviceOptions options, SwapchainDescription swapchainDescription)
        {
            options.SwapchainDepthFormat = swapchainDescription.DepthFormat;
            options.SwapchainSrgbFormat = swapchainDescription.ColorSrgb;
            options.SyncToVerticalBlank = swapchainDescription.SyncToVerticalBlank;

            var source = swapchainDescription.Source;

            if (source is UIViewSwapchainSource uiViewSource)
                initializeUIView(options, uiViewSource.UIView);
            else if (source is AndroidSurfaceSwapchainSource androidSource)
            {
                IntPtr aNativeWindow = AndroidRuntime.ANativeWindow_fromSurface(
                    androidSource.JniEnv,
                    androidSource.Surface);
                initializeANativeWindow(options, aNativeWindow, swapchainDescription);
            }
            else
            {
                throw new VeldridException(
                    "This function does not support creating an OpenGLES GraphicsDevice with the given SwapchainSource.");
            }
        }

        public override TextureSampleCount GetSampleCountLimit(PixelFormat format, bool depthFormat)
        {
            return maxColorTextureSamples;
        }

        public override bool WaitForFence(Fence fence, ulong nanosecondTimeout)
        {
            return Util.AssertSubtype<Fence, OpenGLFence>(fence).Wait(nanosecondTimeout);
        }

        public override bool WaitForFences(Fence[] fences, bool waitAll, ulong nanosecondTimeout)
        {
            int msTimeout;
            if (nanosecondTimeout == ulong.MaxValue)
                msTimeout = -1;
            else
                msTimeout = (int)Math.Min(nanosecondTimeout / 1_000_000, int.MaxValue);

            var events = getResetEventArray(fences.Length);
            for (int i = 0; i < fences.Length; i++) events[i] = Util.AssertSubtype<Fence, OpenGLFence>(fences[i]).ResetEvent;
            bool result;

            if (waitAll)
                result = WaitHandle.WaitAll(events.Cast<WaitHandle>().ToArray(), msTimeout);
            else
            {
                int index = WaitHandle.WaitAny(events.Cast<WaitHandle>().ToArray(), msTimeout);
                result = index != WaitHandle.WaitTimeout;
            }

            returnResetEventArray(events);

            return result;
        }

        public override void ResetFence(Fence fence)
        {
            Util.AssertSubtype<Fence, OpenGLFence>(fence).Reset();
        }

        public void EnableDebugCallback()
        {
            EnableDebugCallback(DebugSeverity.DebugSeverityNotification);
        }

        public void EnableDebugCallback(DebugSeverity minimumSeverity)
        {
            EnableDebugCallback(defaultDebugCallback(minimumSeverity));
        }

        public void EnableDebugCallback(DebugProc callback)
        {
            glEnable(EnableCap.DebugOutput);
            CheckLastError();
            // The debug callback delegate must be persisted, otherwise errors will occur
            // when the OpenGL drivers attempt to call it after it has been collected.
            debugMessageCallback = callback;
            glDebugMessageCallback(debugMessageCallback, null);
            CheckLastError();
        }

        public override bool GetOpenGLInfo(out BackendInfoOpenGL info)
        {
            info = openglInfo;
            return true;
        }

        internal void EnqueueDisposal(IOpenGLDeferredResource resource)
        {
            resourcesToDispose.Enqueue(resource);
        }

        internal void EnqueueDisposal(OpenGLCommandList commandList)
        {
            lock (commandListDisposalLock)
            {
                if (getCount(commandList) > 0)
                    commandListsToDispose.Add(commandList);
                else
                    commandList.DestroyResources();
            }
        }

        internal bool CheckCommandListDisposal(OpenGLCommandList commandList)
        {
            lock (commandListDisposalLock)
            {
                int count = decrementCount(commandList);

                if (count == 0)
                {
                    if (commandListsToDispose.Remove(commandList))
                    {
                        commandList.DestroyResources();
                        return true;
                    }
                }

                return false;
            }
        }

        internal void ExecuteOnGLThread(Action action)
        {
            executionThread.Run(action);
            executionThread.WaitForIdle();
        }

        internal void FlushAndFinish()
        {
            executionThread.FlushAndFinish();
        }

        internal void EnsureResourceInitialized(IOpenGLDeferredResource deferredResource)
        {
            executionThread.InitializeResource(deferredResource);
        }

        internal override uint GetUniformBufferMinOffsetAlignmentCore()
        {
            return minUboOffsetAlignment;
        }

        internal override uint GetStructuredBufferMinOffsetAlignmentCore()
        {
            return minSsboOffsetAlignment;
        }

        protected override MappedResource MapCore(IMappableResource resource, MapMode mode, uint subresource)
        {
            var key = new MappedResourceCacheKey(resource, subresource);

            lock (mappedResourceLock)
            {
                if (mappedResources.TryGetValue(key, out var info))
                {
                    if (info.Mode != mode) throw new VeldridException("The given resource was already mapped with a different MapMode.");

                    info.RefCount += 1;
                    mappedResources[key] = info;
                    return info.MappedResource;
                }
            }

            return executionThread.Map(resource, mode, subresource);
        }

        protected override void UnmapCore(IMappableResource resource, uint subresource)
        {
            executionThread.Unmap(resource, subresource);
        }

        protected override void PlatformDispose()
        {
            FlushAndFinish();
            executionThread.Terminate();
        }

        private static int getDepthBits(PixelFormat value)
        {
            switch (value)
            {
                case PixelFormat.R16UNorm:
                    return 16;

                case PixelFormat.R32Float:
                    return 32;

                case PixelFormat.D24UNormS8UInt:
                    return 24;

                case PixelFormat.D32FloatS8UInt:
                    return 32;

                default:
                    throw new VeldridException($"Unsupported depth format: {value}");
            }
        }

        private static int getStencilBits(PixelFormat value)
        {
            switch (value)
            {
                case PixelFormat.D24UNormS8UInt:
                case PixelFormat.D32FloatS8UInt:
                    return 8;

                case PixelFormat.R16UNorm:
                case PixelFormat.R32Float:
                    return 0;

                default:
                    throw new VeldridException($"Unsupported depth format: {value}");
            }
        }

        private void init(
            GraphicsDeviceOptions options,
            OpenGLPlatformInfo platformInfo,
            uint width,
            uint height,
            bool loadFunctions)
        {
            syncToVBlank = options.SyncToVerticalBlank;
            glContext = platformInfo.OpenGLContextHandle;
            makeCurrent = platformInfo.MakeCurrent;
            deleteContext = platformInfo.DeleteContext;
            swapBuffers = platformInfo.SwapBuffers;
            setSyncToVBlank = platformInfo.SetSyncToVerticalBlank;
            LoadGetString(glContext, platformInfo.GetProcAddress);
            Version = Util.GetString(glGetString(StringName.Version));
            ShadingLanguageVersion = Util.GetString(glGetString(StringName.ShadingLanguageVersion));
            vendorName = Util.GetString(glGetString(StringName.Vendor));
            deviceName = Util.GetString(glGetString(StringName.Renderer));
            backendType = Version.StartsWith("OpenGL ES", StringComparison.Ordinal) ? GraphicsBackend.OpenGLES : GraphicsBackend.OpenGL;

            LoadAllFunctions(glContext, platformInfo.GetProcAddress, backendType == GraphicsBackend.OpenGLES);

            int majorVersion, minorVersion;
            glGetIntegerv(GetPName.MajorVersion, &majorVersion);
            CheckLastError();
            glGetIntegerv(GetPName.MinorVersion, &minorVersion);
            CheckLastError();

            GraphicsApiVersion.TryParseGLVersion(Version, out apiVersion);

            if (apiVersion.Major != majorVersion ||
                apiVersion.Minor != minorVersion)
            {
                // This mismatch should never be hit in valid OpenGL implementations.
                apiVersion = new GraphicsApiVersion(majorVersion, minorVersion, 0, 0);
            }

            int extensionCount;
            glGetIntegerv(GetPName.NumExtensions, &extensionCount);
            CheckLastError();

            var extensions = new HashSet<string>();

            for (uint i = 0; i < extensionCount; i++)
            {
                byte* extensionNamePtr = glGetStringi(StringNameIndexed.Extensions, i);
                CheckLastError();

                if (extensionNamePtr != null)
                {
                    string extensionName = Util.GetString(extensionNamePtr);
                    extensions.Add(extensionName);
                }
            }

            Extensions = new OpenGLExtensions(extensions, backendType, majorVersion, minorVersion);

            bool drawIndirect = Extensions.DrawIndirect || Extensions.MultiDrawIndirect;
            features = new GraphicsDeviceFeatures(
                Extensions.ComputeShaders,
                Extensions.GeometryShader,
                Extensions.TessellationShader,
                Extensions.ArbViewportArray,
                backendType == GraphicsBackend.OpenGL,
                Extensions.DrawElementsBaseVertex,
                Extensions.GLVersion(4, 2),
                drawIndirect,
                drawIndirect,
                backendType == GraphicsBackend.OpenGL,
                Extensions.AnisotropicFilter,
                backendType == GraphicsBackend.OpenGL,
                backendType == GraphicsBackend.OpenGL,
                Extensions.IndependentBlend,
                Extensions.StorageBuffers,
                Extensions.ArbTextureView,
                Extensions.KhrDebug || Extensions.ExtDebugMarker,
                Extensions.ArbUniformBufferObject,
                Extensions.ArbGpuShaderFp64);

            int uboAlignment;
            glGetIntegerv(GetPName.UniformBufferOffsetAlignment, &uboAlignment);
            CheckLastError();
            minUboOffsetAlignment = (uint)uboAlignment;

            if (features.StructuredBuffer)
            {
                int ssboAlignment;
                glGetIntegerv(GetPName.ShaderStorageBufferOffsetAlignment, &ssboAlignment);
                CheckLastError();
                minSsboOffsetAlignment = (uint)ssboAlignment;
            }

            resourceFactory = new OpenGLResourceFactory(this);

            glGenVertexArrays(1, out vao);
            CheckLastError();

            glBindVertexArray(vao);
            CheckLastError();

            if (options.Debug && (Extensions.KhrDebug || Extensions.ArbDebugOutput)) EnableDebugCallback();

            bool backbufferIsSrgb = manualSrgbBackbufferQuery();

            PixelFormat swapchainFormat;
            if (options.SwapchainSrgbFormat && (backbufferIsSrgb || RuntimeInformation.IsOSPlatform(OSPlatform.OSX)))
                swapchainFormat = PixelFormat.B8G8R8A8UNormSRgb;
            else
                swapchainFormat = PixelFormat.B8G8R8A8UNorm;

            swapchainFramebuffer = new OpenGLSwapchainFramebuffer(
                width,
                height,
                swapchainFormat,
                options.SwapchainDepthFormat,
                swapchainFormat != PixelFormat.B8G8R8A8UNormSRgb);

            // Set miscellaneous initial states.
            if (backendType == GraphicsBackend.OpenGL)
            {
                glEnable(EnableCap.TextureCubeMapSeamless);
                CheckLastError();
            }

            TextureSamplerManager = new OpenGLTextureSamplerManager(Extensions);
            commandExecutor = new OpenGLCommandExecutor(this, platformInfo);

            int maxColorTextureSamplesInt;

            if (backendType == GraphicsBackend.OpenGL)
            {
                glGetIntegerv(GetPName.MaxColorTextureSamples, &maxColorTextureSamplesInt);
                CheckLastError();
            }
            else
            {
                glGetIntegerv(GetPName.MaxSamples, &maxColorTextureSamplesInt);
                CheckLastError();
            }

            maxColorTextureSamples = maxColorTextureSamplesInt switch
            {
                >= 32 => TextureSampleCount.Count32,
                >= 16 => TextureSampleCount.Count16,
                >= 8 => TextureSampleCount.Count8,
                >= 4 => TextureSampleCount.Count4,
                >= 2 => TextureSampleCount.Count2,
                _ => TextureSampleCount.Count1
            };

            int maxTexSize;
            glGetIntegerv(GetPName.MaxTextureSize, &maxTexSize);
            CheckLastError();

            int maxTexDepth;
            glGetIntegerv(GetPName.Max3DTextureSize, &maxTexDepth);
            CheckLastError();

            int maxTexArrayLayers;
            glGetIntegerv(GetPName.MaxArrayTextureLayers, &maxTexArrayLayers);
            CheckLastError();

            if (options.PreferDepthRangeZeroToOne && Extensions.ArbClipControl)
            {
                glClipControl(ClipControlOrigin.LowerLeft, ClipControlDepthRange.ZeroToOne);
                CheckLastError();
                isDepthRangeZeroToOne = true;
            }

            maxTextureSize = (uint)maxTexSize;
            maxTextureDepth = (uint)maxTexDepth;
            maxTextureArrayLayers = (uint)maxTexArrayLayers;

            mainSwapchain = new OpenGLSwapchain(
                this,
                swapchainFramebuffer,
                platformInfo.ResizeSwapchain);

            workItems = new BlockingCollection<ExecutionThreadWorkItem>(new ConcurrentQueue<ExecutionThreadWorkItem>());
            platformInfo.ClearCurrentContext();
            executionThread = new ExecutionThread(this, workItems, makeCurrent, glContext);
            openglInfo = new BackendInfoOpenGL(this);

            PostDeviceCreated();
        }

        private bool manualSrgbBackbufferQuery()
        {
            if (backendType == GraphicsBackend.OpenGLES && !Extensions.ExtSRGBWriteControl) return false;

            glGenTextures(1, out uint copySrc);
            CheckLastError();

            float* data = stackalloc float[4];
            data[0] = 0.5f;
            data[1] = 0.5f;
            data[2] = 0.5f;
            data[3] = 1f;

            glActiveTexture(TextureUnit.Texture0);
            CheckLastError();
            glBindTexture(TextureTarget.Texture2D, copySrc);
            CheckLastError();
            glTexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba32f, 1, 1, 0, GLPixelFormat.Rgba, GLPixelType.Float, data);
            CheckLastError();
            glGenFramebuffers(1, out uint copySrcFb);
            CheckLastError();

            glBindFramebuffer(FramebufferTarget.ReadFramebuffer, copySrcFb);
            CheckLastError();
            glFramebufferTexture2D(FramebufferTarget.ReadFramebuffer, GLFramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, copySrc, 0);
            CheckLastError();

            glEnable(EnableCap.FramebufferSrgb);
            CheckLastError();
            glBlitFramebuffer(
                0, 0, 1, 1,
                0, 0, 1, 1,
                ClearBufferMask.ColorBufferBit,
                BlitFramebufferFilter.Nearest);
            CheckLastError();

            glDisable(EnableCap.FramebufferSrgb);
            CheckLastError();

            glBindFramebuffer(FramebufferTarget.ReadFramebuffer, 0);
            CheckLastError();
            glBindFramebuffer(FramebufferTarget.DrawFramebuffer, copySrcFb);
            CheckLastError();
            glBlitFramebuffer(
                0, 0, 1, 1,
                0, 0, 1, 1,
                ClearBufferMask.ColorBufferBit,
                BlitFramebufferFilter.Nearest);
            CheckLastError();

            if (backendType == GraphicsBackend.OpenGLES)
            {
                glBindFramebuffer(FramebufferTarget.ReadFramebuffer, copySrc);
                CheckLastError();
                glReadPixels(
                    0, 0, 1, 1,
                    GLPixelFormat.Rgba,
                    GLPixelType.Float,
                    data);
                CheckLastError();
            }
            else
            {
                glGetTexImage(TextureTarget.Texture2D, 0, GLPixelFormat.Rgba, GLPixelType.Float, data);
                CheckLastError();
            }

            glDeleteFramebuffers(1, ref copySrcFb);
            glDeleteTextures(1, ref copySrc);

            return data[0] > 0.6f;
        }

        private void initializeUIView(GraphicsDeviceOptions options, IntPtr uIViewPtr)
        {
            var eaglContext = EaglContext.Create(EaglRenderingAPI.OpenGLES3);
            if (!EaglContext.SetCurrentContext(eaglContext.NativePtr)) throw new VeldridException("Unable to make newly-created EAGLContext current.");

            var uiView = new UIView(uIViewPtr);

            var eaglLayer = CaeaglLayer.New();
            eaglLayer.Opaque = true;
            eaglLayer.Frame = uiView.frame;
            uiView.layer.addSublayer(eaglLayer.NativePtr);

            IntPtr glesLibrary = NativeLibrary.Load("/System/Library/Frameworks/OpenGLES.framework/OpenGLES");

            Func<string, IntPtr> getProcAddress = name => NativeLibrary.GetExport(glesLibrary, name);

            LoadAllFunctions(eaglContext.NativePtr, getProcAddress, true);

            glGenFramebuffers(1, out uint fb);
            CheckLastError();
            glBindFramebuffer(FramebufferTarget.Framebuffer, fb);
            CheckLastError();

            glGenRenderbuffers(1, out uint colorRb);
            CheckLastError();

            glBindRenderbuffer(RenderbufferTarget.Renderbuffer, colorRb);
            CheckLastError();

            bool result = eaglContext.RenderBufferStorage((UIntPtr)RenderbufferTarget.Renderbuffer, eaglLayer.NativePtr);
            if (!result) throw new VeldridException("Failed to associate OpenGLES Renderbuffer with CAEAGLLayer.");

            glGetRenderbufferParameteriv(
                RenderbufferTarget.Renderbuffer,
                RenderbufferPname.RenderbufferWidth,
                out int fbWidth);
            CheckLastError();

            glGetRenderbufferParameteriv(
                RenderbufferTarget.Renderbuffer,
                RenderbufferPname.RenderbufferHeight,
                out int fbHeight);
            CheckLastError();

            glFramebufferRenderbuffer(
                FramebufferTarget.Framebuffer,
                GLFramebufferAttachment.ColorAttachment0,
                RenderbufferTarget.Renderbuffer,
                colorRb);
            CheckLastError();

            uint depthRb = 0;
            PixelFormat? depthFormat = options.SwapchainDepthFormat;

            if (depthFormat != null)
            {
                glGenRenderbuffers(1, out depthRb);
                CheckLastError();

                glBindRenderbuffer(RenderbufferTarget.Renderbuffer, depthRb);
                CheckLastError();

                glRenderbufferStorage(
                    RenderbufferTarget.Renderbuffer,
                    (uint)OpenGLFormats.VdToGLSizedInternalFormat(depthFormat.Value, true),
                    (uint)fbWidth,
                    (uint)fbHeight);
                CheckLastError();

                glFramebufferRenderbuffer(
                    FramebufferTarget.Framebuffer,
                    GLFramebufferAttachment.DepthAttachment,
                    RenderbufferTarget.Renderbuffer,
                    depthRb);
                CheckLastError();
            }

            var status = glCheckFramebufferStatus(FramebufferTarget.Framebuffer);
            CheckLastError();
            if (status != FramebufferErrorCode.FramebufferComplete) throw new VeldridException("The OpenGLES main Swapchain Framebuffer was incomplete after initialization.");

            glBindFramebuffer(FramebufferTarget.Framebuffer, fb);
            CheckLastError();

            Action<IntPtr> setCurrentContext = ctx =>
            {
                if (!EaglContext.SetCurrentContext(ctx)) throw new VeldridException("Unable to set the thread's current GL context.");
            };

            Action swapBuffersFunc = () =>
            {
                glBindRenderbuffer(RenderbufferTarget.Renderbuffer, colorRb);
                CheckLastError();

                bool presentResult = eaglContext.PresentRenderBuffer((UIntPtr)RenderbufferTarget.Renderbuffer);
                CheckLastError();
                if (!presentResult) throw new VeldridException("Failed to present the EAGL RenderBuffer.");
            };

            Action setSwapchainFramebuffer = () =>
            {
                glBindFramebuffer(FramebufferTarget.Framebuffer, fb);
                CheckLastError();
            };

            Action<uint, uint> resizeSwapchain = (w, h) =>
            {
                eaglLayer.Frame = uiView.frame;

                executionThread.Run(() =>
                {
                    glBindRenderbuffer(RenderbufferTarget.Renderbuffer, colorRb);
                    CheckLastError();

                    bool rbStorageResult = eaglContext.RenderBufferStorage(
                        (UIntPtr)RenderbufferTarget.Renderbuffer,
                        eaglLayer.NativePtr);
                    if (!rbStorageResult) throw new VeldridException("Failed to associate OpenGLES Renderbuffer with CAEAGLLayer.");

                    glGetRenderbufferParameteriv(
                        RenderbufferTarget.Renderbuffer,
                        RenderbufferPname.RenderbufferWidth,
                        out int newWidth);
                    CheckLastError();

                    glGetRenderbufferParameteriv(
                        RenderbufferTarget.Renderbuffer,
                        RenderbufferPname.RenderbufferHeight,
                        out int newHeight);
                    CheckLastError();

                    if (depthFormat != null)
                    {
                        Debug.Assert(depthRb != 0);
                        glBindRenderbuffer(RenderbufferTarget.Renderbuffer, depthRb);
                        CheckLastError();

                        glRenderbufferStorage(
                            RenderbufferTarget.Renderbuffer,
                            (uint)OpenGLFormats.VdToGLSizedInternalFormat(depthFormat.Value, true),
                            (uint)newWidth,
                            (uint)newHeight);
                        CheckLastError();
                    }
                });
            };

            Action<IntPtr> destroyContext = ctx =>
            {
                eaglLayer.RemoveFromSuperlayer();
                eaglLayer.Release();
                eaglContext.Release();
                NativeLibrary.Free(glesLibrary);
            };

            var platformInfo = new OpenGLPlatformInfo(
                eaglContext.NativePtr,
                getProcAddress,
                setCurrentContext,
                () => EaglContext.CurrentContext.NativePtr,
                () => setCurrentContext(IntPtr.Zero),
                destroyContext,
                swapBuffersFunc,
                syncInterval => { },
                setSwapchainFramebuffer,
                resizeSwapchain);

            init(options, platformInfo, (uint)fbWidth, (uint)fbHeight, false);
        }

        private void initializeANativeWindow(
            GraphicsDeviceOptions options,
            IntPtr aNativeWindow,
            SwapchainDescription swapchainDescription)
        {
            IntPtr display = eglGetDisplay(IntPtr.Zero);
            if (display == IntPtr.Zero) throw new VeldridException($"Failed to get the default Android EGLDisplay: {eglGetError()}");

            int major, minor;
            if (eglInitialize(display, &major, &minor) == 0) throw new VeldridException($"Failed to initialize EGL: {eglGetError()}");

            int[] attribs =
            {
                EGL_RED_SIZE, 8,
                EGL_GREEN_SIZE, 8,
                EGL_BLUE_SIZE, 8,
                EGL_ALPHA_SIZE, 8,
                EGL_DEPTH_SIZE,
                swapchainDescription.DepthFormat != null
                    ? getDepthBits(swapchainDescription.DepthFormat.Value)
                    : 0,
                EGL_STENCIL_SIZE,
                swapchainDescription.DepthFormat != null
                    ? getStencilBits(swapchainDescription.DepthFormat.Value)
                    : 0,
                EGL_SURFACE_TYPE, EGL_WINDOW_BIT,
                EGL_RENDERABLE_TYPE, EGL_OPENGL_ES3_BIT,
                EGL_NONE
            };

            IntPtr* configs = stackalloc IntPtr[50];

            fixed (int* attribsPtr = attribs)
            {
                int numConfig;
                if (eglChooseConfig(display, attribsPtr, configs, 50, &numConfig) == 0) throw new VeldridException($"Failed to select a valid EGLConfig: {eglGetError()}");
            }

            IntPtr bestConfig = configs[0];

            int format;
            if (eglGetConfigAttrib(display, bestConfig, EGL_NATIVE_VISUAL_ID, &format) == 0) throw new VeldridException($"Failed to get the EGLConfig's format: {eglGetError()}");

            AndroidRuntime.ANativeWindow_setBuffersGeometry(aNativeWindow, 0, 0, format);

            IntPtr eglWindowSurface = eglCreateWindowSurface(display, bestConfig, aNativeWindow, null);

            if (eglWindowSurface == IntPtr.Zero)
            {
                throw new VeldridException(
                    $"Failed to create an EGL surface from the Android native window: {eglGetError()}");
            }

            int* contextAttribs = stackalloc int[3];
            contextAttribs[0] = EGL_CONTEXT_CLIENT_VERSION;
            contextAttribs[1] = 2;
            contextAttribs[2] = EGL_NONE;
            IntPtr context = eglCreateContext(display, bestConfig, IntPtr.Zero, contextAttribs);
            if (context == IntPtr.Zero) throw new VeldridException("Failed to create an EGLContext: " + eglGetError());

            Action<IntPtr> makeCurrentFunc = ctx =>
            {
                if (eglMakeCurrent(display, eglWindowSurface, eglWindowSurface, ctx) == 0) throw new VeldridException($"Failed to make the EGLContext {ctx} current: {eglGetError()}");
            };

            makeCurrentFunc(context);

            Action clearContext = () =>
            {
                if (eglMakeCurrent(display, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero) == 0) throw new VeldridException("Failed to clear the current EGLContext: " + eglGetError());
            };

            Action swapBuffersFunc = () =>
            {
                if (eglSwapBuffers(display, eglWindowSurface) == 0) throw new VeldridException("Failed to swap buffers: " + eglGetError());
            };

            Action<bool> setSync = vsync =>
            {
                if (eglSwapInterval(display, vsync ? 1 : 0) == 0) throw new VeldridException("Failed to set the swap interval: " + eglGetError());
            };

            // Set the desired initial state.
            setSync(swapchainDescription.SyncToVerticalBlank);

            Action<IntPtr> destroyContext = ctx =>
            {
                if (eglDestroyContext(display, ctx) == 0) throw new VeldridException($"Failed to destroy EGLContext {ctx}: {eglGetError()}");
            };

            var platformInfo = new OpenGLPlatformInfo(
                context,
                eglGetProcAddress,
                makeCurrentFunc,
                eglGetCurrentContext,
                clearContext,
                destroyContext,
                swapBuffersFunc,
                setSync);

            init(options, platformInfo, swapchainDescription.Width, swapchainDescription.Height, true);
        }

        private int incrementCount(OpenGLCommandList glCommandList)
        {
            if (submittedCommandListCounts.TryGetValue(glCommandList, out int count))
                count += 1;
            else
                count = 1;

            submittedCommandListCounts[glCommandList] = count;
            return count;
        }

        private int decrementCount(OpenGLCommandList glCommandList)
        {
            if (submittedCommandListCounts.TryGetValue(glCommandList, out int count))
                count -= 1;
            else
                count = -1;

            if (count == 0)
                submittedCommandListCounts.Remove(glCommandList);
            else
                submittedCommandListCounts[glCommandList] = count;
            return count;
        }

        private int getCount(OpenGLCommandList glCommandList)
        {
            return submittedCommandListCounts.TryGetValue(glCommandList, out int count) ? count : 0;
        }

        private ManualResetEvent[] getResetEventArray(int length)
        {
            lock (resetEventsLock)
            {
                for (int i = resetEvents.Count - 1; i > 0; i--)
                {
                    var array = resetEvents[i];

                    if (array.Length == length)
                    {
                        resetEvents.RemoveAt(i);
                        return array;
                    }
                }
            }

            var newArray = new ManualResetEvent[length];
            return newArray;
        }

        private void returnResetEventArray(ManualResetEvent[] array)
        {
            lock (resetEventsLock) resetEvents.Add(array);
        }

        private void flushDisposables()
        {
            while (resourcesToDispose.TryDequeue(out var resource)) resource.DestroyGLResources();
        }

        private DebugProc defaultDebugCallback(DebugSeverity minimumSeverity)
        {
            return (source, type, id, severity, length, message, userParam) =>
            {
                if (severity >= minimumSeverity
                    && type != DebugType.DebugTypeMarker
                    && type != DebugType.DebugTypePushGroup
                    && type != DebugType.DebugTypePopGroup)
                {
                    string messageString = Marshal.PtrToStringAnsi((IntPtr)message, (int)length);
                    Debug.WriteLine($"GL DEBUG MESSAGE: {source}, {type}, {id}. {severity}: {messageString}");
                }
            };
        }

        private protected override void SubmitCommandsCore(
            CommandList cl,
            Fence fence)
        {
            lock (commandListDisposalLock)
            {
                var glCommandList = Util.AssertSubtype<CommandList, OpenGLCommandList>(cl);
                var entryList = glCommandList.CurrentCommands;
                incrementCount(glCommandList);
                executionThread.ExecuteCommands(entryList);
                if (fence is OpenGLFence glFence) glFence.Set();
            }
        }

        private protected override void SwapBuffersCore(Swapchain swapchain)
        {
            WaitForIdle();

            executionThread.SwapBuffers();
        }

        private protected override void WaitForIdleCore()
        {
            executionThread.WaitForIdle();
        }

        private protected override void WaitForNextFrameReadyCore()
        {
        }

        private protected override bool GetPixelFormatSupportCore(
            PixelFormat format,
            TextureType type,
            TextureUsage usage,
            out PixelFormatProperties properties)
        {
            if ((type == TextureType.Texture1D && !features.Texture1D)
                || !OpenGLFormats.IsFormatSupported(Extensions, format, backendType))
            {
                properties = default;
                return false;
            }

            uint sampleCounts = 0;
            int max = (int)maxColorTextureSamples + 1;
            for (int i = 0; i < max; i++) sampleCounts |= (uint)(1 << i);

            properties = new PixelFormatProperties(
                maxTextureSize,
                type == TextureType.Texture1D ? 1 : maxTextureSize,
                type != TextureType.Texture3D ? 1 : maxTextureDepth,
                uint.MaxValue,
                type == TextureType.Texture3D ? 1 : maxTextureArrayLayers,
                sampleCounts);
            return true;
        }

        private protected override void UpdateBufferCore(DeviceBuffer buffer, uint bufferOffsetInBytes, IntPtr source, uint sizeInBytes)
        {
            lock (mappedResourceLock)
            {
                if (mappedResources.ContainsKey(new MappedResourceCacheKey(buffer, 0)))
                    throw new VeldridException("Cannot call UpdateBuffer on a currently-mapped Buffer.");
            }

            var sb = StagingMemoryPool.Stage(source, sizeInBytes);
            executionThread.UpdateBuffer(buffer, bufferOffsetInBytes, sb);
        }

        private protected override void UpdateTextureCore(
            Texture texture,
            IntPtr source,
            uint sizeInBytes,
            uint x,
            uint y,
            uint z,
            uint width,
            uint height,
            uint depth,
            uint mipLevel,
            uint arrayLayer)
        {
            var textureData = StagingMemoryPool.Stage(source, sizeInBytes);
            var argBlock = StagingMemoryPool.GetStagingBlock(update_texture_args_size);
            ref var args = ref Unsafe.AsRef<UpdateTextureArgs>(argBlock.Data);
            args.Data = (IntPtr)textureData.Data;
            args.X = x;
            args.Y = y;
            args.Z = z;
            args.Width = width;
            args.Height = height;
            args.Depth = depth;
            args.MipLevel = mipLevel;
            args.ArrayLayer = arrayLayer;

            executionThread.UpdateTexture(texture, argBlock.Id, textureData.Id);
        }

        private struct UpdateTextureArgs
        {
            public IntPtr Data;
            public uint X;
            public uint Y;
            public uint Z;
            public uint Width;
            public uint Height;
            public uint Depth;
            public uint MipLevel;
            public uint ArrayLayer;
        }

        private class ExecutionThread
        {
            private readonly OpenGLGraphicsDevice gd;
            private readonly BlockingCollection<ExecutionThreadWorkItem> workItems;
            private readonly AutoResetEvent executionEvent = new AutoResetEvent(false);
            private readonly Action<IntPtr> makeCurrent;
            private readonly IntPtr context;
            private readonly List<Exception> exceptions = new List<Exception>();
            private readonly object exceptionsLock = new object();
            private bool terminated;

            public ExecutionThread(
                OpenGLGraphicsDevice gd,
                BlockingCollection<ExecutionThreadWorkItem> workItems,
                Action<IntPtr> makeCurrent,
                IntPtr context)
            {
                this.gd = gd;
                this.workItems = workItems;
                this.makeCurrent = makeCurrent;
                this.context = context;
                var thread = new Thread(run)
                {
                    IsBackground = true
                };
                thread.Start();
            }

            public MappedResource Map(IMappableResource resource, MapMode mode, uint subresource)
            {
                checkExceptions();

                var mrp = new MapParams
                {
                    Map = true,
                    Subresource = subresource,
                    MapMode = mode
                };

                workItems.Add(new ExecutionThreadWorkItem(resource, &mrp, executionEvent));
                executionEvent.WaitOne();

                if (!mrp.Succeeded)
                    throw new VeldridException("Failed to map OpenGL resource.");

                return new MappedResource(resource, mode, mrp.Data, mrp.DataSize, mrp.Subresource, mrp.RowPitch, mrp.DepthPitch);
            }

            public void ExecuteCommands(IOpenGLCommandEntryList entryList)
            {
                checkExceptions();
                entryList.Parent.OnSubmitted(entryList);
                workItems.Add(new ExecutionThreadWorkItem(entryList));
            }

            internal void Unmap(IMappableResource resource, uint subresource)
            {
                checkExceptions();

                var mrp = new MapParams
                {
                    Map = false,
                    Subresource = subresource
                };

                workItems.Add(new ExecutionThreadWorkItem(resource, &mrp, executionEvent));
                executionEvent.WaitOne();
            }

            internal void UpdateBuffer(DeviceBuffer buffer, uint offsetInBytes, StagingBlock stagingBlock)
            {
                checkExceptions();

                workItems.Add(new ExecutionThreadWorkItem(buffer, offsetInBytes, stagingBlock));
            }

            internal void UpdateTexture(Texture texture, uint argBlockId, uint dataBlockId)
            {
                checkExceptions();

                workItems.Add(new ExecutionThreadWorkItem(texture, argBlockId, dataBlockId));
            }

            internal void Run(Action a)
            {
                checkExceptions();

                workItems.Add(new ExecutionThreadWorkItem(a));
            }

            internal void Terminate()
            {
                checkExceptions();

                workItems.Add(new ExecutionThreadWorkItem(WorkItemType.TerminateAction));
            }

            internal void WaitForIdle()
            {
                workItems.Add(new ExecutionThreadWorkItem(executionEvent, false));
                executionEvent.WaitOne();

                checkExceptions();
            }

            internal void SetSyncToVerticalBlank(bool value)
            {
                workItems.Add(new ExecutionThreadWorkItem(value));
            }

            internal void SwapBuffers()
            {
                workItems.Add(new ExecutionThreadWorkItem(WorkItemType.SwapBuffers));
            }

            internal void FlushAndFinish()
            {
                workItems.Add(new ExecutionThreadWorkItem(executionEvent, true));
                executionEvent.WaitOne();

                checkExceptions();
            }

            internal void InitializeResource(IOpenGLDeferredResource deferredResource)
            {
                var info = new InitializeResourceInfo(deferredResource, executionEvent);
                workItems.Add(new ExecutionThreadWorkItem(info));
                info.ResetEvent.WaitOne();

                if (info.Exception != null) throw info.Exception;
            }

            private void run()
            {
                makeCurrent(context);

                while (!terminated)
                {
                    var workItem = workItems.Take();
                    executeWorkItem(workItem);
                }
            }

            private void executeWorkItem(ExecutionThreadWorkItem workItem)
            {
                try
                {
                    switch (workItem.Type)
                    {
                        case WorkItemType.ExecuteList:
                        {
                            var list = (IOpenGLCommandEntryList)workItem.Object0;

                            try
                            {
                                list.ExecuteAll(gd.commandExecutor);
                            }
                            finally
                            {
                                if (!gd.CheckCommandListDisposal(list.Parent)) list.Parent.OnCompleted(list);
                            }
                        }
                            break;

                        case WorkItemType.Map:
                        {
                            var resourceToMap = (IMappableResource)workItem.Object0;
                            var resetEvent = (EventWaitHandle)workItem.Object1;

                            var resultPtr = (MapParams*)Util.UnpackIntPtr(workItem.UInt0, workItem.UInt1);

                            if (resultPtr->Map)
                            {
                                executeMapResource(
                                    resourceToMap,
                                    resetEvent,
                                    resultPtr);
                            }
                            else
                                executeUnmapResource(resourceToMap, resultPtr->Subresource, resetEvent);
                        }
                            break;

                        case WorkItemType.UpdateBuffer:
                        {
                            var updateBuffer = (DeviceBuffer)workItem.Object0;
                            uint offsetInBytes = workItem.UInt0;
                            var stagingBlock = gd.StagingMemoryPool.RetrieveById(workItem.UInt1);

                            gd.commandExecutor.UpdateBuffer(
                                updateBuffer,
                                offsetInBytes,
                                (IntPtr)stagingBlock.Data,
                                stagingBlock.SizeInBytes);

                            gd.StagingMemoryPool.Free(stagingBlock);
                        }
                            break;

                        case WorkItemType.UpdateTexture:
                            var texture = (Texture)workItem.Object0;
                            var pool = gd.StagingMemoryPool;
                            var argBlock = pool.RetrieveById(workItem.UInt0);
                            var textureData = pool.RetrieveById(workItem.UInt1);
                            ref var args = ref Unsafe.AsRef<UpdateTextureArgs>(argBlock.Data);

                            gd.commandExecutor.UpdateTexture(
                                texture, args.Data, args.X, args.Y, args.Z,
                                args.Width, args.Height, args.Depth, args.MipLevel, args.ArrayLayer);

                            pool.Free(argBlock);
                            pool.Free(textureData);
                            break;

                        case WorkItemType.GenericAction:
                        {
                            ((Action)workItem.Object0)();
                        }
                            break;

                        case WorkItemType.TerminateAction:
                        {
                            // Check if the OpenGL context has already been destroyed by the OS. If so, just exit out.
                            uint error = glGetError();
                            if (error == (uint)ErrorCode.InvalidOperation) return;

                            makeCurrent(gd.glContext);

                            gd.flushDisposables();
                            gd.deleteContext(gd.glContext);
                            gd.StagingMemoryPool.Dispose();
                            terminated = true;
                        }
                            break;

                        case WorkItemType.SetSyncToVerticalBlank:
                        {
                            bool value = workItem.UInt0 == 1;
                            gd.setSyncToVBlank(value);
                        }
                            break;

                        case WorkItemType.SwapBuffers:
                        {
                            gd.swapBuffers();
                            gd.flushDisposables();
                        }
                            break;

                        case WorkItemType.WaitForIdle:
                        {
                            gd.flushDisposables();
                            bool isFullFlush = workItem.UInt0 != 0;

                            if (isFullFlush)
                            {
                                glFlush();
                                glFinish();
                            }

                            ((EventWaitHandle)workItem.Object0).Set();
                        }
                            break;

                        case WorkItemType.InitializeResource:
                        {
                            var info = (InitializeResourceInfo)workItem.Object0;

                            try
                            {
                                info.DeferredResource.EnsureResourcesCreated();
                            }
                            catch (Exception e)
                            {
                                info.Exception = e;
                            }
                            finally
                            {
                                info.ResetEvent.Set();
                            }
                        }
                            break;

                        default:
                            throw new InvalidOperationException("Invalid command type: " + workItem.Type);
                    }
                }
                catch (Exception e) when (!Debugger.IsAttached)
                {
                    lock (exceptionsLock) exceptions.Add(e);
                }
            }

            private void executeMapResource(
                IMappableResource resource,
                EventWaitHandle waitHandle,
                MapParams* result)
            {
                uint subresource = result->Subresource;
                var mode = result->MapMode;

                var key = new MappedResourceCacheKey(resource, subresource);

                try
                {
                    lock (gd.mappedResourceLock)
                    {
                        Debug.Assert(!gd.mappedResources.ContainsKey(key));

                        if (resource is OpenGLBuffer buffer)
                        {
                            buffer.EnsureResourcesCreated();
                            void* mappedPtr;
                            var accessMask = OpenGLFormats.VdToGLMapMode(mode);

                            if (gd.Extensions.ArbDirectStateAccess)
                            {
                                mappedPtr = glMapNamedBufferRange(buffer.Buffer, IntPtr.Zero, buffer.SizeInBytes, accessMask);
                                CheckLastError();
                            }
                            else
                            {
                                glBindBuffer(BufferTarget.CopyWriteBuffer, buffer.Buffer);
                                CheckLastError();

                                mappedPtr = glMapBufferRange(BufferTarget.CopyWriteBuffer, IntPtr.Zero, (IntPtr)buffer.SizeInBytes, accessMask);
                                CheckLastError();
                            }

                            var info = new MappedResourceInfoWithStaging
                            {
                                MappedResource = new MappedResource(
                                    resource,
                                    mode,
                                    (IntPtr)mappedPtr,
                                    buffer.SizeInBytes),
                                RefCount = 1,
                                Mode = mode
                            };
                            gd.mappedResources.Add(key, info);
                            result->Data = (IntPtr)mappedPtr;
                            result->DataSize = buffer.SizeInBytes;
                            result->RowPitch = 0;
                            result->DepthPitch = 0;
                            result->Succeeded = true;
                        }
                        else
                        {
                            var texture = Util.AssertSubtype<IMappableResource, OpenGLTexture>(resource);
                            texture.EnsureResourcesCreated();

                            Util.GetMipLevelAndArrayLayer(texture, subresource, out uint mipLevel, out uint arrayLayer);
                            Util.GetMipDimensions(texture, mipLevel, out uint mipWidth, out uint mipHeight, out uint mipDepth);

                            uint depthSliceSize = FormatHelpers.GetDepthPitch(
                                FormatHelpers.GetRowPitch(mipWidth, texture.Format),
                                mipHeight,
                                texture.Format);
                            uint subresourceSize = depthSliceSize * mipDepth;
                            int compressedSize = 0;

                            bool isCompressed = FormatHelpers.IsCompressedFormat(texture.Format);

                            if (isCompressed)
                            {
                                glGetTexLevelParameteriv(
                                    texture.TextureTarget,
                                    (int)mipLevel,
                                    GetTextureParameter.TextureCompressedImageSize,
                                    &compressedSize);
                                CheckLastError();
                            }

                            var block = gd.StagingMemoryPool.GetStagingBlock(subresourceSize);

                            uint packAlignment = 4;
                            if (!isCompressed) packAlignment = FormatSizeHelpers.GetSizeInBytes(texture.Format);

                            if (packAlignment < 4)
                            {
                                glPixelStorei(PixelStoreParameter.PackAlignment, (int)packAlignment);
                                CheckLastError();
                            }

                            if (mode == MapMode.Read || mode == MapMode.ReadWrite)
                            {
                                if (!isCompressed)
                                {
                                    // Read data into buffer.
                                    if (gd.Extensions.ArbDirectStateAccess && texture.ArrayLayers == 1)
                                    {
                                        int zoffset = texture.ArrayLayers > 1 ? (int)arrayLayer : 0;
                                        glGetTextureSubImage(
                                            texture.Texture,
                                            (int)mipLevel,
                                            0, 0, zoffset,
                                            mipWidth, mipHeight, mipDepth,
                                            texture.GLPixelFormat,
                                            texture.GLPixelType,
                                            subresourceSize,
                                            block.Data);
                                        CheckLastError();
                                    }
                                    else
                                    {
                                        for (uint layer = 0; layer < mipDepth; layer++)
                                        {
                                            uint curLayer = arrayLayer + layer;
                                            uint curOffset = depthSliceSize * layer;
                                            glGenFramebuffers(1, out uint readFb);
                                            CheckLastError();
                                            glBindFramebuffer(FramebufferTarget.ReadFramebuffer, readFb);
                                            CheckLastError();

                                            if (texture.ArrayLayers > 1 || texture.Type == TextureType.Texture3D)
                                            {
                                                glFramebufferTextureLayer(
                                                    FramebufferTarget.ReadFramebuffer,
                                                    GLFramebufferAttachment.ColorAttachment0,
                                                    texture.Texture,
                                                    (int)mipLevel,
                                                    (int)curLayer);
                                                CheckLastError();
                                            }
                                            else if (texture.Type == TextureType.Texture1D)
                                            {
                                                glFramebufferTexture1D(
                                                    FramebufferTarget.ReadFramebuffer,
                                                    GLFramebufferAttachment.ColorAttachment0,
                                                    TextureTarget.Texture1D,
                                                    texture.Texture,
                                                    (int)mipLevel);
                                                CheckLastError();
                                            }
                                            else
                                            {
                                                glFramebufferTexture2D(
                                                    FramebufferTarget.ReadFramebuffer,
                                                    GLFramebufferAttachment.ColorAttachment0,
                                                    TextureTarget.Texture2D,
                                                    texture.Texture,
                                                    (int)mipLevel);
                                                CheckLastError();
                                            }

                                            glReadPixels(
                                                0, 0,
                                                mipWidth, mipHeight,
                                                texture.GLPixelFormat,
                                                texture.GLPixelType,
                                                (byte*)block.Data + curOffset);
                                            CheckLastError();
                                            glDeleteFramebuffers(1, ref readFb);
                                            CheckLastError();
                                        }
                                    }
                                }
                                else // isCompressed
                                {
                                    if (texture.TextureTarget == TextureTarget.Texture2DArray
                                        || texture.TextureTarget == TextureTarget.Texture2DMultisampleArray
                                        || texture.TextureTarget == TextureTarget.TextureCubeMapArray)
                                    {
                                        // We only want a single subresource (array slice), so we need to copy
                                        // a subsection of the downloaded data into our staging block.

                                        uint fullDataSize = (uint)compressedSize;
                                        var fullBlock = gd.StagingMemoryPool.GetStagingBlock(fullDataSize);

                                        if (gd.Extensions.ArbDirectStateAccess)
                                        {
                                            glGetCompressedTextureImage(
                                                texture.Texture,
                                                (int)mipLevel,
                                                fullBlock.SizeInBytes,
                                                fullBlock.Data);
                                            CheckLastError();
                                        }
                                        else
                                        {
                                            gd.TextureSamplerManager.SetTextureTransient(texture.TextureTarget, texture.Texture);
                                            CheckLastError();

                                            glGetCompressedTexImage(texture.TextureTarget, (int)mipLevel, fullBlock.Data);
                                            CheckLastError();
                                        }

                                        byte* sliceStart = (byte*)fullBlock.Data + arrayLayer * subresourceSize;
                                        Buffer.MemoryCopy(sliceStart, block.Data, subresourceSize, subresourceSize);
                                        gd.StagingMemoryPool.Free(fullBlock);
                                    }
                                    else
                                    {
                                        if (gd.Extensions.ArbDirectStateAccess)
                                        {
                                            glGetCompressedTextureImage(
                                                texture.Texture,
                                                (int)mipLevel,
                                                block.SizeInBytes,
                                                block.Data);
                                            CheckLastError();
                                        }
                                        else
                                        {
                                            gd.TextureSamplerManager.SetTextureTransient(texture.TextureTarget, texture.Texture);
                                            CheckLastError();

                                            glGetCompressedTexImage(texture.TextureTarget, (int)mipLevel, block.Data);
                                            CheckLastError();
                                        }
                                    }
                                }
                            }

                            if (packAlignment < 4)
                            {
                                glPixelStorei(PixelStoreParameter.PackAlignment, 4);
                                CheckLastError();
                            }

                            uint rowPitch = FormatHelpers.GetRowPitch(mipWidth, texture.Format);
                            uint depthPitch = FormatHelpers.GetDepthPitch(rowPitch, mipHeight, texture.Format);
                            var info = new MappedResourceInfoWithStaging
                            {
                                MappedResource = new MappedResource(
                                    resource,
                                    mode,
                                    (IntPtr)block.Data,
                                    subresourceSize,
                                    subresource,
                                    rowPitch,
                                    depthPitch),
                                RefCount = 1,
                                Mode = mode,
                                StagingBlock = block
                            };
                            gd.mappedResources.Add(key, info);
                            result->Data = (IntPtr)block.Data;
                            result->DataSize = subresourceSize;
                            result->RowPitch = rowPitch;
                            result->DepthPitch = depthPitch;
                            result->Succeeded = true;
                        }
                    }
                }
                catch
                {
                    result->Succeeded = false;
                    throw;
                }
                finally
                {
                    waitHandle.Set();
                }
            }

            private void executeUnmapResource(IMappableResource resource, uint subresource, EventWaitHandle waitHandle)
            {
                var key = new MappedResourceCacheKey(resource, subresource);

                lock (gd.mappedResourceLock)
                {
                    var info = gd.mappedResources[key];

                    if (info.RefCount == 1)
                    {
                        if (resource is OpenGLBuffer buffer)
                        {
                            if (gd.Extensions.ArbDirectStateAccess)
                            {
                                glUnmapNamedBuffer(buffer.Buffer);
                                CheckLastError();
                            }
                            else
                            {
                                glBindBuffer(BufferTarget.CopyWriteBuffer, buffer.Buffer);
                                CheckLastError();

                                glUnmapBuffer(BufferTarget.CopyWriteBuffer);
                                CheckLastError();
                            }
                        }
                        else
                        {
                            var texture = Util.AssertSubtype<IMappableResource, OpenGLTexture>(resource);

                            if (info.Mode == MapMode.Write || info.Mode == MapMode.ReadWrite)
                            {
                                Util.GetMipLevelAndArrayLayer(texture, subresource, out uint mipLevel, out uint arrayLayer);
                                Util.GetMipDimensions(texture, mipLevel, out uint width, out uint height, out uint depth);

                                IntPtr data = (IntPtr)info.StagingBlock.Data;

                                gd.commandExecutor.UpdateTexture(
                                    texture,
                                    data,
                                    0, 0, 0,
                                    width, height, depth,
                                    mipLevel,
                                    arrayLayer);
                            }

                            gd.StagingMemoryPool.Free(info.StagingBlock);
                        }

                        gd.mappedResources.Remove(key);
                    }
                }

                waitHandle.Set();
            }

            private void checkExceptions()
            {
                lock (exceptionsLock)
                {
                    if (exceptions.Count > 0)
                    {
                        var innerException = exceptions.Count == 1
                            ? exceptions[0]
                            : new AggregateException(exceptions.ToArray());
                        exceptions.Clear();
                        throw new VeldridException(
                            "Error(s) were encountered during the execution of OpenGL commands. See InnerException for more information.",
                            innerException);
                    }
                }
            }
        }

        public enum WorkItemType : byte
        {
            Map,
            Unmap,
            ExecuteList,
            UpdateBuffer,
            UpdateTexture,
            GenericAction,
            TerminateAction,
            SetSyncToVerticalBlank,
            SwapBuffers,
            WaitForIdle,
            InitializeResource
        }

        private struct ExecutionThreadWorkItem
        {
            public readonly WorkItemType Type;
            public readonly object Object0;
            public readonly object Object1;
            public readonly uint UInt0;
            public readonly uint UInt1;

            // ReSharper disable once NotAccessedField.Local
            public readonly uint UInt2;

            public ExecutionThreadWorkItem(
                IMappableResource resource,
                MapParams* mapResult,
                EventWaitHandle resetEvent)
            {
                Type = WorkItemType.Map;
                Object0 = resource;
                Object1 = resetEvent;

                Util.PackIntPtr((IntPtr)mapResult, out UInt0, out UInt1);
                UInt2 = 0;
            }

            public ExecutionThreadWorkItem(IOpenGLCommandEntryList commandList)
            {
                Type = WorkItemType.ExecuteList;
                Object0 = commandList;
                Object1 = null;

                UInt0 = 0;
                UInt1 = 0;
                UInt2 = 0;
            }

            public ExecutionThreadWorkItem(DeviceBuffer updateBuffer, uint offsetInBytes, StagingBlock stagedSource)
            {
                Type = WorkItemType.UpdateBuffer;
                Object0 = updateBuffer;
                Object1 = null;

                UInt0 = offsetInBytes;
                UInt1 = stagedSource.Id;
                UInt2 = 0;
            }

            public ExecutionThreadWorkItem(Action a, bool isTermination = false)
            {
                Type = isTermination ? WorkItemType.TerminateAction : WorkItemType.GenericAction;
                Object0 = a;
                Object1 = null;

                UInt0 = 0;
                UInt1 = 0;
                UInt2 = 0;
            }

            public ExecutionThreadWorkItem(Texture texture, uint argBlockId, uint dataBlockId)
            {
                Type = WorkItemType.UpdateTexture;
                Object0 = texture;
                Object1 = null;

                UInt0 = argBlockId;
                UInt1 = dataBlockId;
                UInt2 = 0;
            }

            public ExecutionThreadWorkItem(EventWaitHandle resetEvent, bool isFullFlush)
            {
                Type = WorkItemType.WaitForIdle;
                Object0 = resetEvent;
                Object1 = null;

                UInt0 = isFullFlush ? 1u : 0u;
                UInt1 = 0;
                UInt2 = 0;
            }

            public ExecutionThreadWorkItem(bool value)
            {
                Type = WorkItemType.SetSyncToVerticalBlank;
                Object0 = null;
                Object1 = null;

                UInt0 = value ? 1u : 0u;
                UInt1 = 0;
                UInt2 = 0;
            }

            public ExecutionThreadWorkItem(WorkItemType type)
            {
                Type = type;
                Object0 = null;
                Object1 = null;

                UInt0 = 0;
                UInt1 = 0;
                UInt2 = 0;
            }

            public ExecutionThreadWorkItem(InitializeResourceInfo info)
            {
                Type = WorkItemType.InitializeResource;
                Object0 = info;
                Object1 = null;

                UInt0 = 0;
                UInt1 = 0;
                UInt2 = 0;
            }
        }

        private struct MapParams
        {
            public MapMode MapMode;
            public uint Subresource;
            public bool Map;
            public bool Succeeded;
            public IntPtr Data;
            public uint DataSize;
            public uint RowPitch;
            public uint DepthPitch;
        }

        internal struct MappedResourceInfoWithStaging
        {
            public int RefCount;
            public MapMode Mode;
            public MappedResource MappedResource;
            public StagingBlock StagingBlock;
        }

        private class InitializeResourceInfo
        {
            public readonly IOpenGLDeferredResource DeferredResource;
            public readonly EventWaitHandle ResetEvent;
            public Exception Exception;

            public InitializeResourceInfo(IOpenGLDeferredResource deferredResource, EventWaitHandle resetEvent)
            {
                DeferredResource = deferredResource;
                ResetEvent = resetEvent;
            }
        }
    }
}
