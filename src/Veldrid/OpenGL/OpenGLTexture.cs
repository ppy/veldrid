using System;
using System.Diagnostics;
using Veldrid.OpenGLBindings;
using static Veldrid.OpenGLBindings.OpenGLNative;
using static Veldrid.OpenGL.OpenGLUtil;

namespace Veldrid.OpenGL
{
    internal unsafe class OpenGLTexture : Texture, IOpenGLDeferredResource
    {
        public uint Texture => texture;

        public override uint Width { get; }

        public override uint Height { get; }

        public override uint Depth { get; }

        public override PixelFormat Format { get; }

        public override uint MipLevels { get; }

        public override uint ArrayLayers { get; }

        public override TextureUsage Usage { get; }

        public override TextureType Type { get; }

        public override TextureSampleCount SampleCount { get; }

        public override bool IsDisposed => disposeRequested;

        public GLPixelFormat GLPixelFormat { get; }
        public GLPixelType GLPixelType { get; }
        public PixelInternalFormat GLInternalFormat { get; }

        public override string Name
        {
            get => name;
            set
            {
                name = value;
                nameChanged = true;
            }
        }

        public TextureTarget TextureTarget { get; internal set; }

        public bool Created { get; private set; }
        private readonly OpenGLGraphicsDevice gd;
        private uint texture;
        private readonly uint[] framebuffers;
        private readonly uint[] pbos;
        private readonly uint[] pboSizes;
        private bool disposeRequested;
        private bool disposed;

        private string name;
        private bool nameChanged;

        public OpenGLTexture(OpenGLGraphicsDevice gd, ref TextureDescription description)
        {
            this.gd = gd;

            Width = description.Width;
            Height = description.Height;
            Depth = description.Depth;

            if (description.Format == PixelFormat.D24UNormS8UInt && !gd.Extensions.OesPackedDepthStencil) {
                Console.WriteLine(
                    "[Veldrid] GL_OES_packed_depth_stencil not available — downgrading D24_UNorm_S8_UInt to D32_Float_S8_UInt");
                Format = PixelFormat.D32FloatS8UInt;
            }
            else {
                Format = description.Format;
            }
            MipLevels = description.MipLevels;
            ArrayLayers = description.ArrayLayers;
            Usage = description.Usage;
            Type = description.Type;
            SampleCount = description.SampleCount;

            framebuffers = new uint[MipLevels * ArrayLayers];
            pbos = new uint[MipLevels * ArrayLayers];
            pboSizes = new uint[MipLevels * ArrayLayers];

            GLPixelFormat = OpenGLFormats.VdToGLPixelFormat(Format);
            GLPixelType = OpenGLFormats.VdToGLPixelType(Format);
            GLInternalFormat = OpenGLFormats.VdToGLPixelInternalFormat(Format);

            if ((Usage & TextureUsage.DepthStencil) == TextureUsage.DepthStencil)
            {
                GLPixelFormat = FormatHelpers.IsStencilFormat(Format)
                    ? GLPixelFormat.DepthStencil
                    : GLPixelFormat.DepthComponent;
                if (Format == PixelFormat.R16UNorm)
                    GLInternalFormat = PixelInternalFormat.DepthComponent16;
                else if (Format == PixelFormat.R32Float) GLInternalFormat = PixelInternalFormat.DepthComponent32f;
            }

            if ((Usage & TextureUsage.Cubemap) == TextureUsage.Cubemap)
                TextureTarget = ArrayLayers == 1 ? TextureTarget.TextureCubeMap : TextureTarget.TextureCubeMapArray;
            else if (Type == TextureType.Texture1D)
                TextureTarget = ArrayLayers == 1 ? TextureTarget.Texture1D : TextureTarget.Texture1DArray;
            else if (Type == TextureType.Texture2D)
            {
                if (ArrayLayers == 1)
                    TextureTarget = SampleCount == TextureSampleCount.Count1 ? TextureTarget.Texture2D : TextureTarget.Texture2DMultisample;
                else
                    TextureTarget = SampleCount == TextureSampleCount.Count1 ? TextureTarget.Texture2DArray : TextureTarget.Texture2DMultisampleArray;
            }
            else
            {
                Debug.Assert(Type == TextureType.Texture3D);
                TextureTarget = TextureTarget.Texture3D;
            }
        }

        public OpenGLTexture(OpenGLGraphicsDevice gd, uint nativeTexture, ref TextureDescription description)
        {
            this.gd = gd;
            texture = nativeTexture;
            Width = description.Width;
            Height = description.Height;
            Depth = description.Depth;

            if (description.Format == PixelFormat.D24UNormS8UInt && !gd.Extensions.OesPackedDepthStencil) {
                Console.WriteLine(
                    "[Veldrid] GL_OES_packed_depth_stencil not available — downgrading D24_UNorm_S8_UInt to D32_Float_S8_UInt");
                Format = PixelFormat.D32FloatS8UInt;
            }
            else {
                Format = description.Format;
            }
            MipLevels = description.MipLevels;
            ArrayLayers = description.ArrayLayers;
            Usage = description.Usage;
            Type = description.Type;
            SampleCount = description.SampleCount;

            framebuffers = new uint[MipLevels * ArrayLayers];
            pbos = new uint[MipLevels * ArrayLayers];
            pboSizes = new uint[MipLevels * ArrayLayers];

            GLPixelFormat = OpenGLFormats.VdToGLPixelFormat(Format);
            GLPixelType = OpenGLFormats.VdToGLPixelType(Format);
            GLInternalFormat = OpenGLFormats.VdToGLPixelInternalFormat(Format);

            if ((Usage & TextureUsage.DepthStencil) == TextureUsage.DepthStencil)
            {
                GLPixelFormat = FormatHelpers.IsStencilFormat(Format)
                    ? GLPixelFormat.DepthStencil
                    : GLPixelFormat.DepthComponent;
                if (Format == PixelFormat.R16UNorm)
                    GLInternalFormat = PixelInternalFormat.DepthComponent16;
                else if (Format == PixelFormat.R32Float) GLInternalFormat = PixelInternalFormat.DepthComponent32f;
            }

            if ((Usage & TextureUsage.Cubemap) == TextureUsage.Cubemap)
                TextureTarget = ArrayLayers == 1 ? TextureTarget.TextureCubeMap : TextureTarget.TextureCubeMapArray;
            else if (Type == TextureType.Texture1D)
                TextureTarget = ArrayLayers == 1 ? TextureTarget.Texture1D : TextureTarget.Texture1DArray;
            else if (Type == TextureType.Texture2D)
            {
                if (ArrayLayers == 1)
                    TextureTarget = SampleCount == TextureSampleCount.Count1 ? TextureTarget.Texture2D : TextureTarget.Texture2DMultisample;
                else
                    TextureTarget = SampleCount == TextureSampleCount.Count1 ? TextureTarget.Texture2DArray : TextureTarget.Texture2DMultisampleArray;
            }
            else
            {
                Debug.Assert(Type == TextureType.Texture3D);
                TextureTarget = TextureTarget.Texture3D;
            }

            Created = true;
        }

        public void EnsureResourcesCreated()
        {
            if (!Created) createGLResources();

            if (nameChanged)
            {
                nameChanged = false;
                if (gd.Extensions.KhrDebug) SetObjectLabel(ObjectLabelIdentifier.Texture, texture, name);
            }
        }

        public uint GetFramebuffer(uint mipLevel, uint arrayLayer)
        {
            Debug.Assert(!FormatHelpers.IsCompressedFormat(Format));
            Debug.Assert(Created);

            uint subresource = CalculateSubresource(mipLevel, arrayLayer);

            if (framebuffers[subresource] == 0)
            {
                var framebufferTarget = SampleCount == TextureSampleCount.Count1
                    ? FramebufferTarget.DrawFramebuffer
                    : FramebufferTarget.ReadFramebuffer;

                glGenFramebuffers(1, out framebuffers[subresource]);
                CheckLastError();

                glBindFramebuffer(framebufferTarget, framebuffers[subresource]);
                CheckLastError();

                gd.TextureSamplerManager.SetTextureTransient(TextureTarget, Texture);

                if (TextureTarget == TextureTarget.Texture2D || TextureTarget == TextureTarget.Texture2DMultisample)
                {
                    glFramebufferTexture2D(
                        framebufferTarget,
                        GLFramebufferAttachment.ColorAttachment0,
                        TextureTarget,
                        Texture,
                        (int)mipLevel);
                    CheckLastError();
                }
                else if (TextureTarget == TextureTarget.Texture2DArray
                         || TextureTarget == TextureTarget.Texture2DMultisampleArray
                         || TextureTarget == TextureTarget.Texture3D)
                {
                    glFramebufferTextureLayer(
                        framebufferTarget,
                        GLFramebufferAttachment.ColorAttachment0,
                        Texture,
                        (int)mipLevel,
                        (int)arrayLayer);
                    CheckLastError();
                }

                var errorCode = glCheckFramebufferStatus(framebufferTarget);
                if (errorCode != FramebufferErrorCode.FramebufferComplete) throw new VeldridException("Failed to create texture copy FBO: " + errorCode);
            }

            return framebuffers[subresource];
        }

        public uint GetPixelBuffer(uint subresource)
        {
            Debug.Assert(Created);

            if (pbos[subresource] == 0)
            {
                glGenBuffers(1, out pbos[subresource]);
                CheckLastError();

                glBindBuffer(BufferTarget.CopyWriteBuffer, pbos[subresource]);
                CheckLastError();

                uint dataSize = Width * Height * FormatSizeHelpers.GetSizeInBytes(Format);
                glBufferData(
                    BufferTarget.CopyWriteBuffer,
                    dataSize,
                    null,
                    BufferUsageHint.StaticCopy);
                CheckLastError();
                pboSizes[subresource] = dataSize;
            }

            return pbos[subresource];
        }

        public uint GetPixelBufferSize(uint subresource)
        {
            Debug.Assert(Created);
            Debug.Assert(pbos[subresource] != 0);
            return pboSizes[subresource];
        }

        public void DestroyGLResources()
        {
            if (!disposed)
            {
                disposed = true;

                glDeleteTextures(1, ref texture);
                CheckLastError();

                for (int i = 0; i < framebuffers.Length; i++)
                {
                    if (framebuffers[i] != 0) glDeleteFramebuffers(1, ref framebuffers[i]);
                }

                for (int i = 0; i < pbos.Length; i++)
                {
                    if (pbos[i] != 0) glDeleteBuffers(1, ref pbos[i]);
                }
            }
        }

        private void createGLResources()
        {
            bool dsa = gd.Extensions.ArbDirectStateAccess;

            if (dsa)
            {
                uint t;
                glCreateTextures(TextureTarget, 1, &t);
                CheckLastError();
                texture = t;
            }
            else
            {
                glGenTextures(1, out texture);
                CheckLastError();

                gd.TextureSamplerManager.SetTextureTransient(TextureTarget, texture);
                CheckLastError();
            }

            bool isDepthTex = (Usage & TextureUsage.DepthStencil) == TextureUsage.DepthStencil;

            if (TextureTarget == TextureTarget.Texture1D)
            {
                if (dsa)
                {
                    glTextureStorage1D(
                        texture,
                        MipLevels,
                        OpenGLFormats.VdToGLSizedInternalFormat(Format, isDepthTex),
                        Width);
                    CheckLastError();
                }
                else if (gd.Extensions.TextureStorage)
                {
                    glTexStorage1D(
                        TextureTarget.Texture1D,
                        MipLevels,
                        OpenGLFormats.VdToGLSizedInternalFormat(Format, isDepthTex),
                        Width);
                    CheckLastError();
                }
                else
                {
                    uint levelWidth = Width;

                    for (int currentLevel = 0; currentLevel < MipLevels; currentLevel++)
                    {
                        // Set size, load empty data into texture
                        glTexImage1D(
                            TextureTarget.Texture1D,
                            currentLevel,
                            GLInternalFormat,
                            levelWidth,
                            0, // border
                            GLPixelFormat,
                            GLPixelType,
                            null);
                        CheckLastError();

                        levelWidth = Math.Max(1, levelWidth / 2);
                    }
                }
            }
            else if (TextureTarget == TextureTarget.Texture2D || TextureTarget == TextureTarget.Texture1DArray)
            {
                uint heightOrArrayLayers = TextureTarget == TextureTarget.Texture2D ? Height : ArrayLayers;

                if (dsa)
                {
                    glTextureStorage2D(
                        texture,
                        MipLevels,
                        OpenGLFormats.VdToGLSizedInternalFormat(Format, isDepthTex),
                        Width,
                        heightOrArrayLayers);
                    CheckLastError();
                }
                else if (gd.Extensions.TextureStorage)
                {
                    glTexStorage2D(
                        TextureTarget,
                        MipLevels,
                        OpenGLFormats.VdToGLSizedInternalFormat(Format, isDepthTex),
                        Width,
                        heightOrArrayLayers);
                    CheckLastError();
                }
                else
                {
                    uint levelWidth = Width;
                    uint levelHeight = heightOrArrayLayers;

                    for (int currentLevel = 0; currentLevel < MipLevels; currentLevel++)
                    {
                        // Set size, load empty data into texture
                        glTexImage2D(
                            TextureTarget,
                            currentLevel,
                            GLInternalFormat,
                            levelWidth,
                            levelHeight,
                            0, // border
                            GLPixelFormat,
                            GLPixelType,
                            null);
                        CheckLastError();

                        levelWidth = Math.Max(1, levelWidth / 2);
                        if (TextureTarget == TextureTarget.Texture2D) levelHeight = Math.Max(1, levelHeight / 2);
                    }
                }
            }
            else if (TextureTarget == TextureTarget.Texture2DArray)
            {
                if (dsa)
                {
                    glTextureStorage3D(
                        texture,
                        MipLevels,
                        OpenGLFormats.VdToGLSizedInternalFormat(Format, isDepthTex),
                        Width,
                        Height,
                        ArrayLayers);
                    CheckLastError();
                }
                else if (gd.Extensions.TextureStorage)
                {
                    glTexStorage3D(
                        TextureTarget.Texture2DArray,
                        MipLevels,
                        OpenGLFormats.VdToGLSizedInternalFormat(Format, isDepthTex),
                        Width,
                        Height,
                        ArrayLayers);
                    CheckLastError();
                }
                else
                {
                    uint levelWidth = Width;
                    uint levelHeight = Height;

                    for (int currentLevel = 0; currentLevel < MipLevels; currentLevel++)
                    {
                        glTexImage3D(
                            TextureTarget.Texture2DArray,
                            currentLevel,
                            GLInternalFormat,
                            levelWidth,
                            levelHeight,
                            ArrayLayers,
                            0, // border
                            GLPixelFormat,
                            GLPixelType,
                            null);
                        CheckLastError();

                        levelWidth = Math.Max(1, levelWidth / 2);
                        levelHeight = Math.Max(1, levelHeight / 2);
                    }
                }
            }
            else if (TextureTarget == TextureTarget.Texture2DMultisample)
            {
                if (dsa)
                {
                    glTextureStorage2DMultisample(
                        texture,
                        FormatHelpers.GetSampleCountUInt32(SampleCount),
                        OpenGLFormats.VdToGLSizedInternalFormat(Format, isDepthTex),
                        Width,
                        Height,
                        false);
                    CheckLastError();
                }
                else
                {
                    if (gd.Extensions.TextureStorageMultisample)
                    {
                        glTexStorage2DMultisample(
                            TextureTarget.Texture2DMultisample,
                            FormatHelpers.GetSampleCountUInt32(SampleCount),
                            OpenGLFormats.VdToGLSizedInternalFormat(Format, isDepthTex),
                            Width,
                            Height,
                            false);
                        CheckLastError();
                    }
                    else
                    {
                        glTexImage2DMultiSample(
                            TextureTarget.Texture2DMultisample,
                            FormatHelpers.GetSampleCountUInt32(SampleCount),
                            GLInternalFormat,
                            Width,
                            Height,
                            false);
                    }

                    CheckLastError();
                }
            }
            else if (TextureTarget == TextureTarget.Texture2DMultisampleArray)
            {
                if (dsa)
                {
                    glTextureStorage3DMultisample(
                        texture,
                        FormatHelpers.GetSampleCountUInt32(SampleCount),
                        OpenGLFormats.VdToGLSizedInternalFormat(Format, isDepthTex),
                        Width,
                        Height,
                        ArrayLayers,
                        false);
                    CheckLastError();
                }
                else
                {
                    if (gd.Extensions.TextureStorageMultisample)
                    {
                        glTexStorage3DMultisample(
                            TextureTarget.Texture2DMultisampleArray,
                            FormatHelpers.GetSampleCountUInt32(SampleCount),
                            OpenGLFormats.VdToGLSizedInternalFormat(Format, isDepthTex),
                            Width,
                            Height,
                            ArrayLayers,
                            false);
                    }
                    else
                    {
                        glTexImage3DMultisample(
                            TextureTarget.Texture2DMultisampleArray,
                            FormatHelpers.GetSampleCountUInt32(SampleCount),
                            GLInternalFormat,
                            Width,
                            Height,
                            ArrayLayers,
                            false);
                        CheckLastError();
                    }
                }
            }
            else if (TextureTarget == TextureTarget.TextureCubeMap)
            {
                if (dsa)
                {
                    glTextureStorage2D(
                        texture,
                        MipLevels,
                        OpenGLFormats.VdToGLSizedInternalFormat(Format, isDepthTex),
                        Width,
                        Height);
                    CheckLastError();
                }
                else if (gd.Extensions.TextureStorage)
                {
                    glTexStorage2D(
                        TextureTarget.TextureCubeMap,
                        MipLevels,
                        OpenGLFormats.VdToGLSizedInternalFormat(Format, isDepthTex),
                        Width,
                        Height);
                    CheckLastError();
                }
                else
                {
                    uint levelWidth = Width;
                    uint levelHeight = Height;

                    for (int currentLevel = 0; currentLevel < MipLevels; currentLevel++)
                    {
                        for (int face = 0; face < 6; face++)
                        {
                            // Set size, load empty data into texture
                            glTexImage2D(
                                TextureTarget.TextureCubeMapPositiveX + face,
                                currentLevel,
                                GLInternalFormat,
                                levelWidth,
                                levelHeight,
                                0, // border
                                GLPixelFormat,
                                GLPixelType,
                                null);
                            CheckLastError();
                        }

                        levelWidth = Math.Max(1, levelWidth / 2);
                        levelHeight = Math.Max(1, levelHeight / 2);
                    }
                }
            }
            else if (TextureTarget == TextureTarget.TextureCubeMapArray)
            {
                if (dsa)
                {
                    glTextureStorage3D(
                        texture,
                        MipLevels,
                        OpenGLFormats.VdToGLSizedInternalFormat(Format, isDepthTex),
                        Width,
                        Height,
                        ArrayLayers * 6);
                    CheckLastError();
                }
                else if (gd.Extensions.TextureStorage)
                {
                    glTexStorage3D(
                        TextureTarget.TextureCubeMapArray,
                        MipLevels,
                        OpenGLFormats.VdToGLSizedInternalFormat(Format, isDepthTex),
                        Width,
                        Height,
                        ArrayLayers * 6);
                    CheckLastError();
                }
                else
                {
                    uint levelWidth = Width;
                    uint levelHeight = Height;

                    for (int currentLevel = 0; currentLevel < MipLevels; currentLevel++)
                    {
                        for (int face = 0; face < 6; face++)
                        {
                            // Set size, load empty data into texture
                            glTexImage3D(
                                TextureTarget.Texture2DArray,
                                currentLevel,
                                GLInternalFormat,
                                levelWidth,
                                levelHeight,
                                ArrayLayers * 6,
                                0, // border
                                GLPixelFormat,
                                GLPixelType,
                                null);
                            CheckLastError();
                        }

                        levelWidth = Math.Max(1, levelWidth / 2);
                        levelHeight = Math.Max(1, levelHeight / 2);
                    }
                }
            }
            else if (TextureTarget == TextureTarget.Texture3D)
            {
                if (dsa)
                {
                    glTextureStorage3D(
                        texture,
                        MipLevels,
                        OpenGLFormats.VdToGLSizedInternalFormat(Format, isDepthTex),
                        Width,
                        Height,
                        Depth);
                    CheckLastError();
                }
                else if (gd.Extensions.TextureStorage)
                {
                    glTexStorage3D(
                        TextureTarget.Texture3D,
                        MipLevels,
                        OpenGLFormats.VdToGLSizedInternalFormat(Format, isDepthTex),
                        Width,
                        Height,
                        Depth);
                    CheckLastError();
                }
                else
                {
                    uint levelWidth = Width;
                    uint levelHeight = Height;
                    uint levelDepth = Depth;

                    for (int currentLevel = 0; currentLevel < MipLevels; currentLevel++)
                    {
                        for (int face = 0; face < 6; face++)
                        {
                            // Set size, load empty data into texture
                            glTexImage3D(
                                TextureTarget.Texture3D,
                                currentLevel,
                                GLInternalFormat,
                                levelWidth,
                                levelHeight,
                                levelDepth,
                                0, // border
                                GLPixelFormat,
                                GLPixelType,
                                null);
                            CheckLastError();
                        }

                        levelWidth = Math.Max(1, levelWidth / 2);
                        levelHeight = Math.Max(1, levelHeight / 2);
                        levelDepth = Math.Max(1, levelDepth / 2);
                    }
                }
            }
            else
                throw new VeldridException("Invalid texture target: " + TextureTarget);

            Created = true;
        }

        private protected override void DisposeCore()
        {
            if (!disposeRequested)
            {
                disposeRequested = true;
                gd.EnqueueDisposal(this);
            }
        }
    }
}
