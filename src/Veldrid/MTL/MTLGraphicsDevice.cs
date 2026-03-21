using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Veldrid.MetalBindings;

namespace Veldrid.MTL
{
    internal unsafe class MtlGraphicsDevice : GraphicsDevice
    {
        public MTLDevice Device => device;
        public MTLCommandQueue CommandQueue => commandQueue;
        public MtlFeatureSupport MetalFeatures { get; }
        public ResourceBindingModel ResourceBindingModel { get; }
        public bool PreferMemorylessDepthTargets { get; }

        public override string DeviceName { get; }

        public override string VendorName => "Apple";

        public override GraphicsApiVersion ApiVersion { get; }

        public override GraphicsBackend BackendType => GraphicsBackend.Metal;

        public override bool IsUvOriginTopLeft => true;

        public override bool IsDepthRangeZeroToOne => true;

        public override bool IsClipSpaceYInverted => false;

        public override ResourceFactory ResourceFactory { get; }

        public override Swapchain MainSwapchain => mainSwapchain;

        public override GraphicsDeviceFeatures Features { get; }
        private static readonly Lazy<bool> s_is_supported = new Lazy<bool>(getIsSupported);

        private static readonly Dictionary<IntPtr, MtlGraphicsDevice> s_aot_registered_blocks
            = new Dictionary<IntPtr, MtlGraphicsDevice>();

        private readonly MTLDevice device;
        private readonly MTLCommandQueue commandQueue;
        private readonly MtlSwapchain mainSwapchain;
        private readonly bool[] supportedSampleCounts;

        private readonly object submittedCommandsLock = new object();
        private readonly CommandBufferUsageList<MtlCommandList> submittedCLs = new CommandBufferUsageList<MtlCommandList>();

        private readonly object resetEventsLock = new object();
        private readonly List<ManualResetEvent[]> resetEvents = new List<ManualResetEvent[]>();

        private const string unaligned_buffer_copy_pipeline_mac_os_name = "MTL_UnalignedBufferCopy_macOS";
        private const string unaligned_buffer_copy_pipelinei_os_name = "MTL_UnalignedBufferCopy_iOS";
        private readonly object unalignedBufferCopyPipelineLock = new object();
        private readonly IntPtr libSystem;

        // ReSharper disable once PrivateFieldCanBeConvertedToLocalVariable
        private readonly IntPtr concreteGlobalBlock;

        // ReSharper disable once PrivateFieldCanBeConvertedToLocalVariable
        private readonly IntPtr completionHandlerFuncPtr;

        private readonly IntPtr completionBlockDescriptor;
        private readonly IntPtr completionBlockLiteral;

        private readonly IMtlDisplayLink displayLink;
        private readonly AutoResetEvent nextFrameReadyEvent;
        private readonly EventWaitHandle frameEndedEvent = new EventWaitHandle(true, EventResetMode.ManualReset);
        private readonly BackendInfoMetal metalInfo;
        private MTLCommandBuffer latestSubmittedCb;
        private MtlShader unalignedBufferCopyShader;
        private MTLComputePipelineState unalignedBufferCopyPipeline;

        // ReSharper disable once PrivateFieldCanBeConvertedToLocalVariable
        private readonly MTLCommandBufferHandler completionHandler;

        public MtlGraphicsDevice(GraphicsDeviceOptions options, SwapchainDescription? swapchainDesc)
            : this(options, swapchainDesc, new MetalDeviceOptions())
        {
        }

        public MtlGraphicsDevice(
            GraphicsDeviceOptions options,
            SwapchainDescription? swapchainDesc,
            MetalDeviceOptions metalOptions)
        {
            device = MTLDevice.MTLCreateSystemDefaultDevice();
            DeviceName = device.name;
            MetalFeatures = new MtlFeatureSupport(device);

            int major = (int)MetalFeatures.MaxFeatureSet / 10000;
            int minor = (int)MetalFeatures.MaxFeatureSet % 10000;
            ApiVersion = new GraphicsApiVersion(major, minor, 0, 0);

            Features = new GraphicsDeviceFeatures(
                true,
                false,
                false,
                MetalFeatures.IsSupported(MTLFeatureSet.macOS_GPUFamily1_v3),
                false,
                MetalFeatures.IsDrawBaseVertexInstanceSupported(),
                MetalFeatures.IsDrawBaseVertexInstanceSupported(),
                true,
                true,
                true,
                true,
                true,
                true, // TODO: Should be macOS 10.11+ and iOS 11.0+.
                true,
                true,
                true,
                true,
                true,
                false);
            ResourceBindingModel = options.ResourceBindingModel;
            PreferMemorylessDepthTargets = metalOptions.PreferMemorylessDepthTargets;

            if (MetalFeatures.IsMacOS)
            {
                libSystem = NativeLibrary.Load("libSystem.dylib");
                concreteGlobalBlock = NativeLibrary.GetExport(libSystem, "_NSConcreteGlobalBlock");
                completionHandler = OnCommandBufferCompleted;
                displayLink = new MtlcvDisplayLink();
            }
            else
            {
                concreteGlobalBlock = IntPtr.Zero;
                completionHandler = OnCommandBufferCompleted_Static;
            }

            if (displayLink != null)
            {
                nextFrameReadyEvent = new AutoResetEvent(true);
                displayLink.Callback += OnDisplayLinkCallback;
            }

            completionHandlerFuncPtr = Marshal.GetFunctionPointerForDelegate(completionHandler);
            completionBlockDescriptor = Marshal.AllocHGlobal(Unsafe.SizeOf<BlockDescriptor>());
            var descriptorPtr = (BlockDescriptor*)completionBlockDescriptor;
            descriptorPtr->reserved = 0;
            descriptorPtr->Block_size = (ulong)Unsafe.SizeOf<BlockDescriptor>();

            completionBlockLiteral = Marshal.AllocHGlobal(Unsafe.SizeOf<BlockLiteral>());
            var blockPtr = (BlockLiteral*)completionBlockLiteral;
            blockPtr->isa = concreteGlobalBlock;
            blockPtr->flags = (1 << 28) | (1 << 29);
            blockPtr->invoke = completionHandlerFuncPtr;
            blockPtr->descriptor = descriptorPtr;

            if (!MetalFeatures.IsMacOS)
            {
                lock (s_aot_registered_blocks)
                    s_aot_registered_blocks.Add(completionBlockLiteral, this);
            }

            ResourceFactory = new MtlResourceFactory(this);
            commandQueue = device.newCommandQueue();

            var allSampleCounts = (TextureSampleCount[])Enum.GetValues(typeof(TextureSampleCount));
            supportedSampleCounts = new bool[allSampleCounts.Length];

            for (int i = 0; i < allSampleCounts.Length; i++)
            {
                var count = allSampleCounts[i];
                uint uintValue = FormatHelpers.GetSampleCountUInt32(count);
                if (device.supportsTextureSampleCount(uintValue)) supportedSampleCounts[i] = true;
            }

            if (swapchainDesc != null)
            {
                var desc = swapchainDesc.Value;
                mainSwapchain = new MtlSwapchain(this, ref desc);
            }

            metalInfo = new BackendInfoMetal(this);

            PostDeviceCreated();
        }

        public override void UpdateActiveDisplay(int x, int y, int w, int h)
        {
            displayLink?.UpdateActiveDisplay(x, y, w, h);
        }

        public override double GetActualRefreshPeriod()
        {
            if (displayLink != null) return displayLink.GetActualOutputVideoRefreshPeriod();

            return -1.0f;
        }

        public override TextureSampleCount GetSampleCountLimit(PixelFormat format, bool depthFormat)
        {
            for (int i = supportedSampleCounts.Length - 1; i >= 0; i--)
            {
                if (supportedSampleCounts[i])
                    return (TextureSampleCount)i;
            }

            return TextureSampleCount.Count1;
        }

        public override bool GetMetalInfo(out BackendInfoMetal info)
        {
            info = metalInfo;
            return true;
        }

        public override bool WaitForFence(Fence fence, ulong nanosecondTimeout)
        {
            return Util.AssertSubtype<Fence, MtlFence>(fence).Wait(nanosecondTimeout);
        }

        public override bool WaitForFences(Fence[] fences, bool waitAll, ulong nanosecondTimeout)
        {
            int msTimeout;
            if (nanosecondTimeout == ulong.MaxValue)
                msTimeout = -1;
            else
                msTimeout = (int)Math.Min(nanosecondTimeout / 1_000_000, int.MaxValue);

            var events = getResetEventArray(fences.Length);
            for (int i = 0; i < fences.Length; i++)
                events[i] = Util.AssertSubtype<Fence, MtlFence>(fences[i]).ResetEvent;

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
            Util.AssertSubtype<Fence, MtlFence>(fence).Reset();
        }

        internal static bool IsSupported()
        {
            return s_is_supported.Value;
        }

        internal MTLComputePipelineState GetUnalignedBufferCopyPipeline()
        {
            lock (unalignedBufferCopyPipelineLock)
            {
                if (unalignedBufferCopyPipeline.IsNull)
                {
                    var descriptor = MTLUtil.AllocInit<MTLComputePipelineDescriptor>(
                        nameof(MTLComputePipelineDescriptor));
                    var buffer0 = descriptor.buffers[0];
                    buffer0.mutability = MTLMutability.Mutable;
                    var buffer1 = descriptor.buffers[1];
                    buffer1.mutability = MTLMutability.Mutable;

                    Debug.Assert(unalignedBufferCopyShader == null);
                    string name = MetalFeatures.IsMacOS ? unaligned_buffer_copy_pipeline_mac_os_name : unaligned_buffer_copy_pipelinei_os_name;

                    using (var resourceStream = typeof(MtlGraphicsDevice).Assembly.GetManifestResourceStream(name)!)
                    {
                        byte[] data = new byte[resourceStream.Length];

                        using (var ms = new MemoryStream(data))
                        {
                            resourceStream.CopyTo(ms);
                            var shaderDesc = new ShaderDescription(ShaderStages.Compute, data, "copy_bytes");
                            unalignedBufferCopyShader = new MtlShader(ref shaderDesc, this);
                        }
                    }

                    descriptor.computeFunction = unalignedBufferCopyShader.Function;
                    unalignedBufferCopyPipeline = device.newComputePipelineStateWithDescriptor(descriptor);
                    ObjectiveCRuntime.release(descriptor.NativePtr);
                }

                return unalignedBufferCopyPipeline;
            }
        }

        internal override uint GetUniformBufferMinOffsetAlignmentCore()
        {
            return MetalFeatures.IsMacOS ? 16u : 256u;
        }

        internal override uint GetStructuredBufferMinOffsetAlignmentCore()
        {
            return 16u;
        }

        protected override MappedResource MapCore(IMappableResource resource, MapMode mode, uint subresource)
        {
            if (resource is MtlBuffer buffer)
                return mapBuffer(buffer, mode);

            var texture = Util.AssertSubtype<IMappableResource, MtlTexture>(resource);
            return mapTexture(texture, mode, subresource);
        }

        protected override void PlatformDispose()
        {
            WaitForIdle();

            if (!unalignedBufferCopyPipeline.IsNull)
            {
                unalignedBufferCopyShader.Dispose();
                ObjectiveCRuntime.release(unalignedBufferCopyPipeline.NativePtr);
            }

            mainSwapchain?.Dispose();
            ObjectiveCRuntime.release(commandQueue.NativePtr);
            ObjectiveCRuntime.release(device.NativePtr);

            lock (s_aot_registered_blocks) s_aot_registered_blocks.Remove(completionBlockLiteral);

            NativeLibrary.Free(libSystem);

            Marshal.FreeHGlobal(completionBlockDescriptor);
            Marshal.FreeHGlobal(completionBlockLiteral);

            displayLink?.Dispose();
        }

        protected override void UnmapCore(IMappableResource resource, uint subresource)
        {
        }

        // Xamarin AOT requires native callbacks be static.
        [MonoPInvokeCallback(typeof(MTLCommandBufferHandler))]
        private static void OnCommandBufferCompleted_Static(IntPtr block, MTLCommandBuffer cb)
        {
            lock (s_aot_registered_blocks)
            {
                if (s_aot_registered_blocks.TryGetValue(block, out var gd))
                    gd.OnCommandBufferCompleted(block, cb);
            }
        }

        private static bool getIsSupported()
        {
            bool result = false;

            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    if (RuntimeInformation.OSDescription.Contains("Darwin"))
                    {
                        var allDevices = MTLDevice.MTLCopyAllDevices();
                        result |= (ulong)allDevices.count > 0;
                        ObjectiveCRuntime.release(allDevices.NativePtr);
                    }
                    else
                    {
                        var defaultDevice = MTLDevice.MTLCreateSystemDefaultDevice();

                        if (defaultDevice.NativePtr != IntPtr.Zero)
                        {
                            result = true;
                            ObjectiveCRuntime.release(defaultDevice.NativePtr);
                        }
                    }
                }
            }
            catch
            {
                result = false;
            }

            return result;
        }

        private void OnCommandBufferCompleted(IntPtr block, MTLCommandBuffer cb)
        {
            lock (submittedCommandsLock)
            {
                foreach (var cl in submittedCLs.EnumerateAndRemove(cb))
                    cl.OnCompleted(cb);

                if (latestSubmittedCb.NativePtr == cb.NativePtr)
                    latestSubmittedCb = default;
            }

            ObjectiveCRuntime.release(cb.NativePtr);
        }

        private void OnDisplayLinkCallback()
        {
            nextFrameReadyEvent.Set();
            frameEndedEvent.WaitOne();
        }

        private MappedResource mapBuffer(MtlBuffer buffer, MapMode mode)
        {
            return new MappedResource(
                buffer,
                mode,
                (IntPtr)buffer.Pointer,
                buffer.SizeInBytes,
                0,
                buffer.SizeInBytes,
                buffer.SizeInBytes);
        }

        private MappedResource mapTexture(MtlTexture texture, MapMode mode, uint subresource)
        {
            Debug.Assert(!texture.StagingBuffer.IsNull);
            var data = texture.StagingBufferPointer;
            Util.GetMipLevelAndArrayLayer(texture, subresource, out uint mipLevel, out uint arrayLayer);
            Util.GetMipDimensions(texture, mipLevel, out uint _, out uint _, out uint _);
            uint subresourceSize = texture.GetSubresourceSize(mipLevel, arrayLayer);
            texture.GetSubresourceLayout(mipLevel, arrayLayer, out uint rowPitch, out uint depthPitch);
            ulong offset = Util.ComputeSubresourceOffset(texture, mipLevel, arrayLayer);
            byte* offsetPtr = (byte*)data + offset;
            return new MappedResource(texture, mode, (IntPtr)offsetPtr, subresourceSize, subresource, rowPitch, depthPitch);
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

        private protected override void SubmitCommandsCore(CommandList commandList, Fence fence)
        {
            var mtlCl = Util.AssertSubtype<CommandList, MtlCommandList>(commandList);

            mtlCl.CommandBuffer.addCompletedHandler(completionBlockLiteral);

            lock (submittedCommandsLock)
            {
                if (fence != null)
                    mtlCl.SetCompletionFence(mtlCl.CommandBuffer, Util.AssertSubtype<Fence, MtlFence>(fence));

                submittedCLs.Add(mtlCl.CommandBuffer, mtlCl);
                latestSubmittedCb = mtlCl.Commit();
            }
        }

        private protected override void WaitForNextFrameReadyCore()
        {
            frameEndedEvent.Reset();
            nextFrameReadyEvent?.WaitOne(TimeSpan.FromSeconds(1)); // Should never time out.

            // in iOS, if one frame takes longer than the next V-Sync request, the next frame will be processed immediately rather than being delayed to a subsequent V-Sync request,
            // therefore we will request the next drawable here as a method of waiting until we're ready to draw the next frame.
            if (!MetalFeatures.IsMacOS)
            {
                var mtlSwapchainFramebuffer = Util.AssertSubtype<Framebuffer, MtlSwapchainFramebuffer>(mainSwapchain.Framebuffer);
                mtlSwapchainFramebuffer.EnsureDrawableAvailable();
            }
        }

        private protected override bool GetPixelFormatSupportCore(
            PixelFormat format,
            TextureType type,
            TextureUsage usage,
            out PixelFormatProperties properties)
        {
            if (!MtlFormats.IsFormatSupported(format, usage, MetalFeatures))
            {
                properties = default;
                return false;
            }

            uint sampleCounts = 0;

            for (int i = 0; i < supportedSampleCounts.Length; i++)
            {
                if (supportedSampleCounts[i])
                    sampleCounts |= (uint)(1 << i);
            }

            var maxFeatureSet = MetalFeatures.MaxFeatureSet;
            uint maxArrayLayer = MtlFormats.GetMaxTextureVolume(maxFeatureSet);
            uint maxWidth;
            uint maxHeight;
            uint maxDepth;

            if (type == TextureType.Texture1D)
            {
                maxWidth = MtlFormats.GetMaxTexture1DWidth(maxFeatureSet);
                maxHeight = 1;
                maxDepth = 1;
            }
            else if (type == TextureType.Texture2D)
            {
                uint maxDimensions = (usage & TextureUsage.Cubemap) != 0
                    ? MtlFormats.GetMaxTextureCubeDimensions(maxFeatureSet)
                    : MtlFormats.GetMaxTexture2DDimensions(maxFeatureSet);

                maxWidth = maxDimensions;
                maxHeight = maxDimensions;
                maxDepth = 1;
            }
            else if (type == TextureType.Texture3D)
            {
                maxWidth = maxArrayLayer;
                maxHeight = maxArrayLayer;
                maxDepth = maxArrayLayer;
                maxArrayLayer = 1;
            }
            else
                throw Illegal.Value<TextureType>();

            properties = new PixelFormatProperties(
                maxWidth,
                maxHeight,
                maxDepth,
                uint.MaxValue,
                maxArrayLayer,
                sampleCounts);
            return true;
        }

        private protected override void SwapBuffersCore(Swapchain swapchain)
        {
            var mtlSc = Util.AssertSubtype<Swapchain, MtlSwapchain>(swapchain);
            IntPtr currentDrawablePtr = mtlSc.CurrentDrawable.NativePtr;

            if (currentDrawablePtr != IntPtr.Zero)
            {
                using (NSAutoreleasePool.Begin())
                {
                    var submitCb = commandQueue.commandBuffer();
                    submitCb.presentDrawable(currentDrawablePtr);
                    submitCb.commit();
                }

                mtlSc.InvalidateDrawable();
            }

            frameEndedEvent.Set();
        }

        private protected override void UpdateBufferCore(DeviceBuffer buffer, uint bufferOffsetInBytes, IntPtr source, uint sizeInBytes)
        {
            var mtlBuffer = Util.AssertSubtype<DeviceBuffer, MtlBuffer>(buffer);
            var destPtr = mtlBuffer.Pointer;
            byte* destOffsetPtr = (byte*)destPtr + bufferOffsetInBytes;

            if (destPtr == null)
                throw new VeldridException("Attempting to write to a MTLBuffer that is inaccessible from a CPU.");

            Unsafe.CopyBlock(destOffsetPtr, source.ToPointer(), sizeInBytes);
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
            var mtlTex = Util.AssertSubtype<Texture, MtlTexture>(texture);

            if (mtlTex.StagingBuffer.IsNull)
            {
                var stagingTex = ResourceFactory.CreateTexture(new TextureDescription(
                    width, height, depth, 1, 1, texture.Format, TextureUsage.Staging, texture.Type));
                UpdateTexture(stagingTex, source, sizeInBytes, 0, 0, 0, width, height, depth, 0, 0);
                var cl = ResourceFactory.CreateCommandList();
                cl.Begin();
                cl.CopyTexture(
                    stagingTex, 0, 0, 0, 0, 0,
                    texture, x, y, z, mipLevel, arrayLayer,
                    width, height, depth, 1);
                cl.End();
                SubmitCommands(cl);

                cl.Dispose();
                stagingTex.Dispose();
            }
            else
            {
                mtlTex.GetSubresourceLayout(mipLevel, arrayLayer, out uint dstRowPitch, out uint dstDepthPitch);
                ulong dstOffset = Util.ComputeSubresourceOffset(mtlTex, mipLevel, arrayLayer);
                uint srcRowPitch = FormatHelpers.GetRowPitch(width, texture.Format);
                uint srcDepthPitch = FormatHelpers.GetDepthPitch(srcRowPitch, height, texture.Format);
                Util.CopyTextureRegion(
                    source.ToPointer(),
                    0, 0, 0,
                    srcRowPitch, srcDepthPitch,
                    (byte*)mtlTex.StagingBufferPointer + dstOffset,
                    x, y, z,
                    dstRowPitch, dstDepthPitch,
                    width, height, depth,
                    texture.Format);
            }
        }

        private protected override void WaitForIdleCore()
        {
            MTLCommandBuffer lastCb;

            lock (submittedCommandsLock)
            {
                lastCb = latestSubmittedCb;
                ObjectiveCRuntime.retain(lastCb.NativePtr);
            }

            if (lastCb.NativePtr != IntPtr.Zero && lastCb.status != MTLCommandBufferStatus.Completed)
                lastCb.waitUntilCompleted();

            ObjectiveCRuntime.release(lastCb.NativePtr);
        }
    }

    internal sealed class MonoPInvokeCallbackAttribute : Attribute
    {
        public MonoPInvokeCallbackAttribute(Type t) { }
    }
}
