using System;
using Veldrid.MetalBindings;

namespace Veldrid.MTL
{
    internal class MtlTexture : Texture
    {
        /// <summary>
        ///     The native MTLTexture object. This property is only valid for non-staging Textures.
        /// </summary>
        public virtual MTLTexture DeviceTexture { get; }

        /// <summary>
        ///     The staging MTLBuffer object. This property is only valid for staging Textures.
        /// </summary>
        public MTLBuffer StagingBuffer { get; }

        public override PixelFormat Format { get; }

        public override uint Width { get; }

        public override uint Height { get; }

        public override uint Depth { get; }

        public override uint MipLevels { get; }

        public override uint ArrayLayers { get; }

        public override TextureUsage Usage { get; }

        public override TextureType Type { get; }

        public override TextureSampleCount SampleCount { get; }
        public override bool IsDisposed => disposed;
        public virtual MTLPixelFormat MtlPixelFormat { get; }
        public virtual MTLTextureType MtlTextureType { get; }
        public MTLStorageMode MtlStorageMode { get; }

        public unsafe void* StagingBufferPointer { get; private set; }
        public override string Name { get; set; }
        private bool disposed;

        public MtlTexture(ref TextureDescription description, MtlGraphicsDevice gd)
        {
            Width = description.Width;
            Height = description.Height;
            Depth = description.Depth;
            ArrayLayers = description.ArrayLayers;
            MipLevels = description.MipLevels;
            Format = description.Format;
            Usage = description.Usage;
            Type = description.Type;
            SampleCount = description.SampleCount;
            bool isDepth = (Usage & TextureUsage.DepthStencil) == TextureUsage.DepthStencil;

            MtlPixelFormat = MtlFormats.VdToMtlPixelFormat(Format, isDepth);
            MtlTextureType = MtlFormats.VdToMtlTextureType(
                Type,
                ArrayLayers,
                SampleCount != TextureSampleCount.Count1,
                (Usage & TextureUsage.Cubemap) != 0);

            if (Usage != TextureUsage.Staging)
            {
                MtlStorageMode = isDepth && gd.PreferMemorylessDepthTargets ? MTLStorageMode.Memoryless : MTLStorageMode.Private;

                var texDescriptor = MTLTextureDescriptor.New();
                texDescriptor.width = Width;
                texDescriptor.height = Height;
                texDescriptor.depth = Depth;
                texDescriptor.mipmapLevelCount = MipLevels;
                texDescriptor.arrayLength = ArrayLayers;
                texDescriptor.sampleCount = FormatHelpers.GetSampleCountUInt32(SampleCount);
                texDescriptor.textureType = MtlTextureType;
                texDescriptor.pixelFormat = MtlPixelFormat;
                texDescriptor.textureUsage = MtlFormats.VdToMtlTextureUsage(Usage);
                texDescriptor.storageMode = MtlStorageMode;

                DeviceTexture = gd.Device.newTextureWithDescriptor(texDescriptor);
                ObjectiveCRuntime.release(texDescriptor.NativePtr);
            }
            else
            {
                uint totalStorageSize = 0;

                for (uint level = 0; level < MipLevels; level++)
                {
                    Util.GetMipDimensions(this, level, out uint levelWidth, out uint levelHeight, out uint levelDepth);
                    totalStorageSize += levelDepth * FormatHelpers.GetDepthPitch(
                        FormatHelpers.GetRowPitch(levelWidth, Format),
                        levelHeight,
                        Format);
                }

                totalStorageSize *= ArrayLayers;

                StagingBuffer = gd.Device.newBufferWithLengthOptions(
                    totalStorageSize,
                    MTLResourceOptions.StorageModeShared);

                unsafe
                {
                    StagingBufferPointer = StagingBuffer.contents();
                }
            }
        }

        public MtlTexture(ulong nativeTexture, ref TextureDescription description)
        {
            DeviceTexture = new MTLTexture((IntPtr)nativeTexture);
            Width = description.Width;
            Height = description.Height;
            Depth = description.Depth;
            ArrayLayers = description.ArrayLayers;
            MipLevels = description.MipLevels;
            Format = description.Format;
            Usage = description.Usage;
            Type = description.Type;
            SampleCount = description.SampleCount;
            bool isDepth = (Usage & TextureUsage.DepthStencil) == TextureUsage.DepthStencil;

            MtlPixelFormat = MtlFormats.VdToMtlPixelFormat(Format, isDepth);
            MtlTextureType = MtlFormats.VdToMtlTextureType(
                Type,
                ArrayLayers,
                SampleCount != TextureSampleCount.Count1,
                (Usage & TextureUsage.Cubemap) != 0);
        }

        protected MtlTexture()
        {
        }

        internal uint GetSubresourceSize(uint mipLevel, uint arrayLayer)
        {
            uint blockSize = FormatHelpers.IsCompressedFormat(Format) ? 4u : 1u;
            Util.GetMipDimensions(this, mipLevel, out uint width, out uint height, out uint depth);
            uint storageWidth = Math.Max(blockSize, width);
            uint storageHeight = Math.Max(blockSize, height);
            return depth * FormatHelpers.GetDepthPitch(
                FormatHelpers.GetRowPitch(storageWidth, Format),
                storageHeight,
                Format);
        }

        internal void GetSubresourceLayout(uint mipLevel, uint arrayLayer, out uint rowPitch, out uint depthPitch)
        {
            uint blockSize = FormatHelpers.IsCompressedFormat(Format) ? 4u : 1u;
            Util.GetMipDimensions(this, mipLevel, out uint mipWidth, out uint mipHeight, out uint _);
            uint storageWidth = Math.Max(blockSize, mipWidth);
            uint storageHeight = Math.Max(blockSize, mipHeight);
            rowPitch = FormatHelpers.GetRowPitch(storageWidth, Format);
            depthPitch = FormatHelpers.GetDepthPitch(rowPitch, storageHeight, Format);
        }

        private protected override void DisposeCore()
        {
            if (!disposed)
            {
                disposed = true;

                // ReSharper disable once ConvertIfStatementToConditionalTernaryExpression
                if (StagingBuffer.IsNull)
                    ObjectiveCRuntime.release(DeviceTexture.NativePtr);
                else
                    ObjectiveCRuntime.release(StagingBuffer.NativePtr);
            }
        }
    }
}
