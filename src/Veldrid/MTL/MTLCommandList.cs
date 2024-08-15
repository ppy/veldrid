using System;
using System.Collections.Generic;
using System.Diagnostics;
using Veldrid.MetalBindings;

namespace Veldrid.MTL
{
    internal unsafe class MtlCommandList : CommandList
    {
        public MTLCommandBuffer CommandBuffer => cb;

        public override bool IsDisposed => disposed;

        public override string Name { get; set; }
        private readonly MtlGraphicsDevice gd;

        private readonly List<MtlBuffer> availableStagingBuffers = new List<MtlBuffer>();
        private readonly CommandBufferDictionary<List<MtlBuffer>> submittedStagingBuffers = new CommandBufferDictionary<List<MtlBuffer>>();
        private readonly object submittedCommandsLock = new object();
        private readonly CommandBufferDictionary<MtlFence> completionFences = new CommandBufferDictionary<MtlFence>();

        private readonly Dictionary<UIntPtr, DeviceBufferRange> boundVertexBuffers = new Dictionary<UIntPtr, DeviceBufferRange>();
        private readonly Dictionary<UIntPtr, DeviceBufferRange> boundFragmentBuffers = new Dictionary<UIntPtr, DeviceBufferRange>();
        private readonly Dictionary<UIntPtr, DeviceBufferRange> boundComputeBuffers = new Dictionary<UIntPtr, DeviceBufferRange>();

        private readonly Dictionary<UIntPtr, MTLTexture> boundVertexTextures = new Dictionary<UIntPtr, MTLTexture>();
        private readonly Dictionary<UIntPtr, MTLTexture> boundFragmentTextures = new Dictionary<UIntPtr, MTLTexture>();
        private readonly Dictionary<UIntPtr, MTLTexture> boundComputeTextures = new Dictionary<UIntPtr, MTLTexture>();

        private readonly Dictionary<UIntPtr, MTLSamplerState> boundVertexSamplers = new Dictionary<UIntPtr, MTLSamplerState>();
        private readonly Dictionary<UIntPtr, MTLSamplerState> boundFragmentSamplers = new Dictionary<UIntPtr, MTLSamplerState>();
        private readonly Dictionary<UIntPtr, MTLSamplerState> boundComputeSamplers = new Dictionary<UIntPtr, MTLSamplerState>();

        private bool renderEncoderActive => !rce.IsNull;
        private bool blitEncoderActive => !bce.IsNull;
        private bool computeEncoderActive => !cce.IsNull;
        private MTLCommandBuffer cb;
        private MtlFramebuffer mtlFramebuffer;
        private uint viewportCount;
        private bool currentFramebufferEverActive;
        private MTLRenderCommandEncoder rce;
        private MTLBlitCommandEncoder bce;
        private MTLComputeCommandEncoder cce;
        private RgbaFloat?[] clearColors = Array.Empty<RgbaFloat?>();
        private (float depth, byte stencil)? clearDepth;
        private MtlBuffer indexBuffer;
        private uint ibOffset;
        private MTLIndexType indexType;
        private MtlPipeline lastGraphicsPipeline;
        private MtlPipeline graphicsPipeline;
        private MtlPipeline lastComputePipeline;
        private MtlPipeline computePipeline;
        private MTLViewport[] viewports = Array.Empty<MTLViewport>();
        private bool viewportsChanged;
        private MTLScissorRect[] activeScissorRects = Array.Empty<MTLScissorRect>();
        private MTLScissorRect[] scissorRects = Array.Empty<MTLScissorRect>();
        private uint graphicsResourceSetCount;
        private BoundResourceSetInfo[] graphicsResourceSets;
        private bool[] graphicsResourceSetsActive;
        private uint computeResourceSetCount;
        private BoundResourceSetInfo[] computeResourceSets;
        private bool[] computeResourceSetsActive;
        private uint vertexBufferCount;
        private uint nonVertexBufferCount;
        private MtlBuffer[] vertexBuffers;
        private bool[] vertexBuffersActive;
        private uint[] vbOffsets;
        private bool[] vbOffsetsActive;
        private bool disposed;

        public MtlCommandList(ref CommandListDescription description, MtlGraphicsDevice gd)
            : base(ref description, gd.Features, gd.UniformBufferMinOffsetAlignment, gd.StructuredBufferMinOffsetAlignment)
        {
            this.gd = gd;
        }

        #region Disposal

        public override void Dispose()
        {
            if (!disposed)
            {
                disposed = true;
                ensureNoRenderPass();

                lock (submittedStagingBuffers)
                {
                    foreach (var buffer in availableStagingBuffers)
                        buffer.Dispose();

                    foreach (var (_, buffers) in submittedStagingBuffers)
                    {
                        foreach (var buffer in buffers)
                            buffer.Dispose();
                    }

                    submittedStagingBuffers.Clear();
                }

                if (cb.NativePtr != IntPtr.Zero) ObjectiveCRuntime.release(cb.NativePtr);
            }
        }

        #endregion

        public MTLCommandBuffer Commit()
        {
            cb.commit();
            var ret = cb;
            cb = default;
            return ret;
        }

        public override void Begin()
        {
            if (cb.NativePtr != IntPtr.Zero) ObjectiveCRuntime.release(cb.NativePtr);

            using (NSAutoreleasePool.Begin())
            {
                cb = gd.CommandQueue.commandBuffer();
                ObjectiveCRuntime.retain(cb.NativePtr);
            }

            ClearCachedState();
        }

        public override void Dispatch(uint groupCountX, uint groupCountY, uint groupCountZ)
        {
            preComputeCommand();
            cce.dispatchThreadGroups(
                new MTLSize(groupCountX, groupCountY, groupCountZ),
                computePipeline.ThreadsPerThreadgroup);
        }

        public override void End()
        {
            ensureNoBlitEncoder();
            ensureNoComputeEncoder();

            if (!currentFramebufferEverActive && mtlFramebuffer != null) beginCurrentRenderPass();
            ensureNoRenderPass();
        }

        public override void SetScissorRect(uint index, uint x, uint y, uint width, uint height)
        {
            scissorRects[index] = new MTLScissorRect(x, y, width, height);
        }

        public override void SetViewport(uint index, ref Viewport viewport)
        {
            viewportsChanged = true;
            viewports[index] = new MTLViewport(
                viewport.X,
                viewport.Y,
                viewport.Width,
                viewport.Height,
                viewport.MinDepth,
                viewport.MaxDepth);
        }

        public void SetCompletionFence(MTLCommandBuffer cb, MtlFence fence)
        {
            lock (submittedCommandsLock)
            {
                Debug.Assert(!completionFences.TryGetValue(cb, out _));
                completionFences.Add(cb, fence);
            }
        }

        public void OnCompleted(MTLCommandBuffer cb)
        {
            lock (submittedCommandsLock)
            {
                if (completionFences.TryRemove(cb, out var fence))
                    fence.Set();

                if (submittedStagingBuffers.TryRemove(cb, out var buffers))
                    availableStagingBuffers.AddRange(buffers);
            }
        }

        protected override void CopyBufferCore(
            DeviceBuffer source,
            uint sourceOffset,
            DeviceBuffer destination,
            uint destinationOffset,
            uint sizeInBytes)
        {
            var mtlSrc = Util.AssertSubtype<DeviceBuffer, MtlBuffer>(source);
            var mtlDst = Util.AssertSubtype<DeviceBuffer, MtlBuffer>(destination);

            if (sourceOffset % 4 != 0 || destinationOffset % 4 != 0 || sizeInBytes % 4 != 0)
            {
                // Unaligned copy -- use special compute shader.
                ensureComputeEncoder();
                cce.setComputePipelineState(gd.GetUnalignedBufferCopyPipeline());
                cce.setBuffer(mtlSrc.DeviceBuffer, UIntPtr.Zero, 0);
                cce.setBuffer(mtlDst.DeviceBuffer, UIntPtr.Zero, 1);

                MtlUnalignedBufferCopyInfo copyInfo;
                copyInfo.SourceOffset = sourceOffset;
                copyInfo.DestinationOffset = destinationOffset;
                copyInfo.CopySize = sizeInBytes;

                cce.setBytes(&copyInfo, (UIntPtr)sizeof(MtlUnalignedBufferCopyInfo), 2);
                cce.dispatchThreadGroups(new MTLSize(1, 1, 1), new MTLSize(1, 1, 1));
            }
            else
            {
                ensureBlitEncoder();
                bce.copy(
                    mtlSrc.DeviceBuffer, sourceOffset,
                    mtlDst.DeviceBuffer, destinationOffset,
                    sizeInBytes);
            }
        }

        protected override void CopyTextureCore(
            Texture source, uint srcX, uint srcY, uint srcZ, uint srcMipLevel, uint srcBaseArrayLayer,
            Texture destination, uint dstX, uint dstY, uint dstZ, uint dstMipLevel, uint dstBaseArrayLayer,
            uint width, uint height, uint depth, uint layerCount)
        {
            ensureBlitEncoder();
            var srcMtlTexture = Util.AssertSubtype<Texture, MtlTexture>(source);
            var dstMtlTexture = Util.AssertSubtype<Texture, MtlTexture>(destination);

            bool srcIsStaging = (source.Usage & TextureUsage.Staging) != 0;
            bool dstIsStaging = (destination.Usage & TextureUsage.Staging) != 0;

            if (srcIsStaging && !dstIsStaging)
            {
                // Staging -> Normal
                var srcBuffer = srcMtlTexture.StagingBuffer;
                var dstTexture = dstMtlTexture.DeviceTexture;

                Util.GetMipDimensions(srcMtlTexture, srcMipLevel, out uint mipWidth, out uint mipHeight, out uint _);

                for (uint layer = 0; layer < layerCount; layer++)
                {
                    uint blockSize = FormatHelpers.IsCompressedFormat(srcMtlTexture.Format) ? 4u : 1u;
                    uint compressedSrcX = srcX / blockSize;
                    uint compressedSrcY = srcY / blockSize;
                    uint blockSizeInBytes = blockSize == 1
                        ? FormatSizeHelpers.GetSizeInBytes(srcMtlTexture.Format)
                        : FormatHelpers.GetBlockSizeInBytes(srcMtlTexture.Format);

                    ulong srcSubresourceBase = Util.ComputeSubresourceOffset(
                        srcMtlTexture,
                        srcMipLevel,
                        layer + srcBaseArrayLayer);
                    srcMtlTexture.GetSubresourceLayout(
                        srcMipLevel,
                        srcBaseArrayLayer + layer,
                        out uint srcRowPitch,
                        out uint srcDepthPitch);
                    ulong sourceOffset = srcSubresourceBase
                                         + srcDepthPitch * srcZ
                                         + srcRowPitch * compressedSrcY
                                         + blockSizeInBytes * compressedSrcX;

                    uint copyWidth = width > mipWidth && width <= blockSize
                        ? mipWidth
                        : width;

                    uint copyHeight = height > mipHeight && height <= blockSize
                        ? mipHeight
                        : height;

                    var sourceSize = new MTLSize(copyWidth, copyHeight, depth);
                    if (dstMtlTexture.Type != TextureType.Texture3D) srcDepthPitch = 0;
                    bce.copyFromBuffer(
                        srcBuffer,
                        (UIntPtr)sourceOffset,
                        srcRowPitch,
                        srcDepthPitch,
                        sourceSize,
                        dstTexture,
                        dstBaseArrayLayer + layer,
                        dstMipLevel,
                        new MTLOrigin(dstX, dstY, dstZ),
                        gd.MetalFeatures.IsMacOS);
                }
            }
            else if (srcIsStaging)
            {
                for (uint layer = 0; layer < layerCount; layer++)
                {
                    // Staging -> Staging
                    ulong srcSubresourceBase = Util.ComputeSubresourceOffset(
                        srcMtlTexture,
                        srcMipLevel,
                        layer + srcBaseArrayLayer);
                    srcMtlTexture.GetSubresourceLayout(
                        srcMipLevel,
                        srcBaseArrayLayer + layer,
                        out uint srcRowPitch,
                        out uint srcDepthPitch);

                    ulong dstSubresourceBase = Util.ComputeSubresourceOffset(
                        dstMtlTexture,
                        dstMipLevel,
                        layer + dstBaseArrayLayer);
                    dstMtlTexture.GetSubresourceLayout(
                        dstMipLevel,
                        dstBaseArrayLayer + layer,
                        out uint dstRowPitch,
                        out uint dstDepthPitch);

                    uint blockSize = FormatHelpers.IsCompressedFormat(dstMtlTexture.Format) ? 4u : 1u;

                    if (blockSize == 1)
                    {
                        uint pixelSize = FormatSizeHelpers.GetSizeInBytes(dstMtlTexture.Format);
                        uint copySize = width * pixelSize;

                        for (uint zz = 0; zz < depth; zz++)
                        {
                            for (uint yy = 0; yy < height; yy++)
                            {
                                ulong srcRowOffset = srcSubresourceBase
                                                     + srcDepthPitch * (zz + srcZ)
                                                     + srcRowPitch * (yy + srcY)
                                                     + pixelSize * srcX;
                                ulong dstRowOffset = dstSubresourceBase
                                                     + dstDepthPitch * (zz + dstZ)
                                                     + dstRowPitch * (yy + dstY)
                                                     + pixelSize * dstX;
                                bce.copy(
                                    srcMtlTexture.StagingBuffer,
                                    (UIntPtr)srcRowOffset,
                                    dstMtlTexture.StagingBuffer,
                                    (UIntPtr)dstRowOffset,
                                    copySize);
                            }
                        }
                    }
                    else // blockSize != 1
                    {
                        uint paddedWidth = Math.Max(blockSize, width);
                        uint paddedHeight = Math.Max(blockSize, height);
                        uint numRows = FormatHelpers.GetNumRows(paddedHeight, srcMtlTexture.Format);
                        uint rowPitch = FormatHelpers.GetRowPitch(paddedWidth, srcMtlTexture.Format);

                        uint compressedSrcX = srcX / 4;
                        uint compressedSrcY = srcY / 4;
                        uint compressedDstX = dstX / 4;
                        uint compressedDstY = dstY / 4;
                        uint blockSizeInBytes = FormatHelpers.GetBlockSizeInBytes(srcMtlTexture.Format);

                        for (uint zz = 0; zz < depth; zz++)
                        {
                            for (uint row = 0; row < numRows; row++)
                            {
                                ulong srcRowOffset = srcSubresourceBase
                                                     + srcDepthPitch * (zz + srcZ)
                                                     + srcRowPitch * (row + compressedSrcY)
                                                     + blockSizeInBytes * compressedSrcX;
                                ulong dstRowOffset = dstSubresourceBase
                                                     + dstDepthPitch * (zz + dstZ)
                                                     + dstRowPitch * (row + compressedDstY)
                                                     + blockSizeInBytes * compressedDstX;
                                bce.copy(
                                    srcMtlTexture.StagingBuffer,
                                    (UIntPtr)srcRowOffset,
                                    dstMtlTexture.StagingBuffer,
                                    (UIntPtr)dstRowOffset,
                                    rowPitch);
                            }
                        }
                    }
                }
            }
            else if (dstIsStaging)
            {
                // Normal -> Staging
                var srcOrigin = new MTLOrigin(srcX, srcY, srcZ);
                var srcSize = new MTLSize(width, height, depth);

                for (uint layer = 0; layer < layerCount; layer++)
                {
                    dstMtlTexture.GetSubresourceLayout(
                        dstMipLevel,
                        dstBaseArrayLayer + layer,
                        out uint dstBytesPerRow,
                        out uint dstBytesPerImage);

                    Util.GetMipDimensions(srcMtlTexture, dstMipLevel, out uint mipWidth, out uint mipHeight, out uint _);
                    uint blockSize = FormatHelpers.IsCompressedFormat(srcMtlTexture.Format) ? 4u : 1u;
                    uint bufferRowLength = Math.Max(mipWidth, blockSize);
                    uint bufferImageHeight = Math.Max(mipHeight, blockSize);
                    uint compressedDstX = dstX / blockSize;
                    uint compressedDstY = dstY / blockSize;
                    uint blockSizeInBytes = blockSize == 1
                        ? FormatSizeHelpers.GetSizeInBytes(srcMtlTexture.Format)
                        : FormatHelpers.GetBlockSizeInBytes(srcMtlTexture.Format);
                    uint rowPitch = FormatHelpers.GetRowPitch(bufferRowLength, srcMtlTexture.Format);
                    uint depthPitch = FormatHelpers.GetDepthPitch(rowPitch, bufferImageHeight, srcMtlTexture.Format);

                    ulong dstOffset = Util.ComputeSubresourceOffset(dstMtlTexture, dstMipLevel, dstBaseArrayLayer + layer)
                                      + dstZ * depthPitch
                                      + compressedDstY * rowPitch
                                      + compressedDstX * blockSizeInBytes;

                    bce.copyTextureToBuffer(
                        srcMtlTexture.DeviceTexture,
                        srcBaseArrayLayer + layer,
                        srcMipLevel,
                        srcOrigin,
                        srcSize,
                        dstMtlTexture.StagingBuffer,
                        (UIntPtr)dstOffset,
                        dstBytesPerRow,
                        dstBytesPerImage);
                }
            }
            else
            {
                // Normal -> Normal
                for (uint layer = 0; layer < layerCount; layer++)
                {
                    bce.copyFromTexture(
                        srcMtlTexture.DeviceTexture,
                        srcBaseArrayLayer + layer,
                        srcMipLevel,
                        new MTLOrigin(srcX, srcY, srcZ),
                        new MTLSize(width, height, depth),
                        dstMtlTexture.DeviceTexture,
                        dstBaseArrayLayer + layer,
                        dstMipLevel,
                        new MTLOrigin(dstX, dstY, dstZ),
                        gd.MetalFeatures.IsMacOS);
                }
            }
        }

        protected override void DispatchIndirectCore(DeviceBuffer indirectBuffer, uint offset)
        {
            var mtlBuffer = Util.AssertSubtype<DeviceBuffer, MtlBuffer>(indirectBuffer);
            preComputeCommand();
            cce.dispatchThreadgroupsWithIndirectBuffer(
                mtlBuffer.DeviceBuffer,
                offset,
                computePipeline.ThreadsPerThreadgroup);
        }

        protected override void DrawIndexedIndirectCore(DeviceBuffer indirectBuffer, uint offset, uint drawCount, uint stride)
        {
            if (preDrawCommand())
            {
                var mtlBuffer = Util.AssertSubtype<DeviceBuffer, MtlBuffer>(indirectBuffer);

                for (uint i = 0; i < drawCount; i++)
                {
                    uint currentOffset = i * stride + offset;
                    rce.drawIndexedPrimitives(
                        graphicsPipeline.PrimitiveType,
                        indexType,
                        indexBuffer.DeviceBuffer,
                        ibOffset,
                        mtlBuffer.DeviceBuffer,
                        currentOffset);
                }
            }
        }

        protected override void DrawIndirectCore(DeviceBuffer indirectBuffer, uint offset, uint drawCount, uint stride)
        {
            if (preDrawCommand())
            {
                var mtlBuffer = Util.AssertSubtype<DeviceBuffer, MtlBuffer>(indirectBuffer);

                for (uint i = 0; i < drawCount; i++)
                {
                    uint currentOffset = i * stride + offset;
                    rce.drawPrimitives(graphicsPipeline.PrimitiveType, mtlBuffer.DeviceBuffer, currentOffset);
                }
            }
        }

        protected override void ResolveTextureCore(Texture source, Texture destination)
        {
            // TODO: This approach destroys the contents of the source Texture (according to the docs).
            ensureNoBlitEncoder();
            ensureNoRenderPass();

            var mtlSrc = Util.AssertSubtype<Texture, MtlTexture>(source);
            var mtlDst = Util.AssertSubtype<Texture, MtlTexture>(destination);

            var rpDesc = MTLRenderPassDescriptor.New();
            var colorAttachment = rpDesc.colorAttachments[0];
            colorAttachment.texture = mtlSrc.DeviceTexture;
            colorAttachment.loadAction = MTLLoadAction.Load;
            colorAttachment.storeAction = MTLStoreAction.MultisampleResolve;
            colorAttachment.resolveTexture = mtlDst.DeviceTexture;

            using (NSAutoreleasePool.Begin())
            {
                var encoder = cb.renderCommandEncoderWithDescriptor(rpDesc);
                encoder.endEncoding();
            }

            ObjectiveCRuntime.release(rpDesc.NativePtr);
        }

        protected override void SetComputeResourceSetCore(uint slot, ResourceSet set, uint dynamicOffsetCount, ref uint dynamicOffsets)
        {
            if (!computeResourceSets[slot].Equals(set, dynamicOffsetCount, ref dynamicOffsets))
            {
                computeResourceSets[slot].Offsets.Dispose();
                computeResourceSets[slot] = new BoundResourceSetInfo(set, dynamicOffsetCount, ref dynamicOffsets);
                computeResourceSetsActive[slot] = false;
            }
        }

        protected override void SetFramebufferCore(Framebuffer fb)
        {
            if (!currentFramebufferEverActive && mtlFramebuffer != null)
            {
                // This ensures that any submitted clear values will be used even if nothing has been drawn.
                if (ensureRenderPass()) endCurrentRenderPass();
            }

            ensureNoRenderPass();
            mtlFramebuffer = Util.AssertSubtype<Framebuffer, MtlFramebuffer>(fb);
            viewportCount = Math.Max(1u, (uint)fb.ColorTargets.Count);
            Util.EnsureArrayMinimumSize(ref viewports, viewportCount);
            Util.ClearArray(viewports);
            Util.EnsureArrayMinimumSize(ref scissorRects, viewportCount);
            Util.ClearArray(scissorRects);
            Util.EnsureArrayMinimumSize(ref activeScissorRects, viewportCount);
            Util.ClearArray(activeScissorRects);
            Util.EnsureArrayMinimumSize(ref clearColors, (uint)fb.ColorTargets.Count);
            Util.ClearArray(clearColors);
            currentFramebufferEverActive = false;
        }

        protected override void SetGraphicsResourceSetCore(uint slot, ResourceSet rs, uint dynamicOffsetCount, ref uint dynamicOffsets)
        {
            if (!graphicsResourceSets[slot].Equals(rs, dynamicOffsetCount, ref dynamicOffsets))
            {
                graphicsResourceSets[slot].Offsets.Dispose();
                graphicsResourceSets[slot] = new BoundResourceSetInfo(rs, dynamicOffsetCount, ref dynamicOffsets);
                graphicsResourceSetsActive[slot] = false;
            }
        }

        private bool preDrawCommand()
        {
            if (ensureRenderPass())
            {
                if (viewportsChanged)
                {
                    flushViewports();
                    viewportsChanged = false;
                }

                if (graphicsPipeline.ScissorTestEnabled)
                    flushScissorRects();

                Debug.Assert(graphicsPipeline != null);

                if (graphicsPipeline.RenderPipelineState.NativePtr != lastGraphicsPipeline?.RenderPipelineState.NativePtr)
                    rce.setRenderPipelineState(graphicsPipeline.RenderPipelineState);

                if (graphicsPipeline.CullMode != lastGraphicsPipeline?.CullMode)
                    rce.setCullMode(graphicsPipeline.CullMode);

                if (graphicsPipeline.FrontFace != lastGraphicsPipeline?.FrontFace)
                    rce.setFrontFacing(graphicsPipeline.FrontFace);

                if (graphicsPipeline.FillMode != lastGraphicsPipeline?.FillMode)
                    rce.setTriangleFillMode(graphicsPipeline.FillMode);

                var blendColor = graphicsPipeline.BlendColor;
                if (blendColor != lastGraphicsPipeline?.BlendColor)
                    rce.setBlendColor(blendColor.R, blendColor.G, blendColor.B, blendColor.A);

                if (Framebuffer.DepthTarget != null)
                {
                    if (graphicsPipeline.DepthStencilState.NativePtr != lastGraphicsPipeline?.DepthStencilState.NativePtr)
                        rce.setDepthStencilState(graphicsPipeline.DepthStencilState);

                    if (graphicsPipeline.DepthClipMode != lastGraphicsPipeline?.DepthClipMode)
                        rce.setDepthClipMode(graphicsPipeline.DepthClipMode);

                    if (graphicsPipeline.StencilReference != lastGraphicsPipeline?.StencilReference)
                        rce.setStencilReferenceValue(graphicsPipeline.StencilReference);
                }

                lastGraphicsPipeline = graphicsPipeline;

                for (uint i = 0; i < graphicsResourceSetCount; i++)
                {
                    if (!graphicsResourceSetsActive[i])
                    {
                        activateGraphicsResourceSet(i, graphicsResourceSets[i]);
                        graphicsResourceSetsActive[i] = true;
                    }
                }

                for (uint i = 0; i < vertexBufferCount; i++)
                {
                    if (!vertexBuffersActive[i])
                    {
                        UIntPtr index = graphicsPipeline.ResourceBindingModel == ResourceBindingModel.Improved
                            ? nonVertexBufferCount + i
                            : i;
                        rce.setVertexBuffer(
                            vertexBuffers[i].DeviceBuffer,
                            vbOffsets[i],
                            index);

                        vertexBuffersActive[i] = true;
                        vbOffsetsActive[i] = true;
                    }

                    if (!vbOffsetsActive[i])
                    {
                        UIntPtr index = graphicsPipeline.ResourceBindingModel == ResourceBindingModel.Improved
                            ? nonVertexBufferCount + i
                            : i;

                        rce.setVertexBufferOffset(
                            vbOffsets[i],
                            index);

                        vbOffsetsActive[i] = true;
                    }
                }

                return true;
            }

            return false;
        }

        private void flushViewports()
        {
            if (gd.MetalFeatures.IsSupported(MTLFeatureSet.macOS_GPUFamily1_v3))
            {
                fixed (MTLViewport* viewportsPtr = &viewports[0])
                    rce.setViewports(viewportsPtr, viewportCount);
            }
            else
                rce.setViewport(viewports[0]);
        }

        private void flushScissorRects()
        {
            if (gd.MetalFeatures.IsSupported(MTLFeatureSet.macOS_GPUFamily1_v3))
            {
                bool scissorRectsChanged = false;

                for (int i = 0; i < scissorRects.Length; i++)
                {
                    scissorRectsChanged |= !scissorRects[i].Equals(activeScissorRects[i]);
                    activeScissorRects[i] = scissorRects[i];
                }

                if (scissorRectsChanged)
                {
                    fixed (MTLScissorRect* scissorRectsPtr = scissorRects)
                        rce.setScissorRects(scissorRectsPtr, viewportCount);
                }
            }
            else
            {
                if (!scissorRects[0].Equals(activeScissorRects[0]))
                    rce.setScissorRect(scissorRects[0]);

                activeScissorRects[0] = scissorRects[0];
            }
        }

        private void preComputeCommand()
        {
            ensureComputeEncoder();

            if (computePipeline.ComputePipelineState.NativePtr != lastComputePipeline?.ComputePipelineState.NativePtr)
                cce.setComputePipelineState(computePipeline.ComputePipelineState);

            lastComputePipeline = computePipeline;

            for (uint i = 0; i < computeResourceSetCount; i++)
            {
                if (!computeResourceSetsActive[i])
                {
                    activateComputeResourceSet(i, computeResourceSets[i]);
                    computeResourceSetsActive[i] = true;
                }
            }
        }

        private MtlBuffer getFreeStagingBuffer(uint sizeInBytes)
        {
            lock (submittedCommandsLock)
            {
                foreach (var buffer in availableStagingBuffers)
                {
                    if (buffer.SizeInBytes >= sizeInBytes)
                    {
                        availableStagingBuffers.Remove(buffer);
                        return buffer;
                    }
                }
            }

            var staging = gd.ResourceFactory.CreateBuffer(
                new BufferDescription(sizeInBytes, BufferUsage.Staging));

            return Util.AssertSubtype<DeviceBuffer, MtlBuffer>(staging);
        }

        private void activateGraphicsResourceSet(uint slot, BoundResourceSetInfo brsi)
        {
            Debug.Assert(renderEncoderActive);
            var mtlRs = Util.AssertSubtype<ResourceSet, MtlResourceSet>(brsi.Set);
            var layout = mtlRs.Layout;
            uint dynamicOffsetIndex = 0;

            for (int i = 0; i < mtlRs.Resources.Length; i++)
            {
                var bindingInfo = layout.GetBindingInfo(i);
                var resource = mtlRs.Resources[i];
                uint bufferOffset = 0;

                if (bindingInfo.DynamicBuffer)
                {
                    bufferOffset = brsi.Offsets.Get(dynamicOffsetIndex);
                    dynamicOffsetIndex += 1;
                }

                switch (bindingInfo.Kind)
                {
                    case ResourceKind.UniformBuffer:
                    {
                        var range = Util.GetBufferRange(resource, bufferOffset);
                        bindBuffer(range, slot, bindingInfo.Slot, bindingInfo.Stages);
                        break;
                    }

                    case ResourceKind.TextureReadOnly:
                        var texView = Util.GetTextureView(gd, resource);
                        var mtlTexView = Util.AssertSubtype<TextureView, MtlTextureView>(texView);
                        bindTexture(mtlTexView, slot, bindingInfo.Slot, bindingInfo.Stages);
                        break;

                    case ResourceKind.TextureReadWrite:
                        var texViewRw = Util.GetTextureView(gd, resource);
                        var mtlTexViewRw = Util.AssertSubtype<TextureView, MtlTextureView>(texViewRw);
                        bindTexture(mtlTexViewRw, slot, bindingInfo.Slot, bindingInfo.Stages);
                        break;

                    case ResourceKind.Sampler:
                        var mtlSampler = Util.AssertSubtype<IBindableResource, MtlSampler>(resource);
                        bindSampler(mtlSampler, slot, bindingInfo.Slot, bindingInfo.Stages);
                        break;

                    case ResourceKind.StructuredBufferReadOnly:
                    {
                        var range = Util.GetBufferRange(resource, bufferOffset);
                        bindBuffer(range, slot, bindingInfo.Slot, bindingInfo.Stages);
                        break;
                    }

                    case ResourceKind.StructuredBufferReadWrite:
                    {
                        var range = Util.GetBufferRange(resource, bufferOffset);
                        bindBuffer(range, slot, bindingInfo.Slot, bindingInfo.Stages);
                        break;
                    }

                    default:
                        throw Illegal.Value<ResourceKind>();
                }
            }
        }

        private void activateComputeResourceSet(uint slot, BoundResourceSetInfo brsi)
        {
            Debug.Assert(computeEncoderActive);
            var mtlRs = Util.AssertSubtype<ResourceSet, MtlResourceSet>(brsi.Set);
            var layout = mtlRs.Layout;
            uint dynamicOffsetIndex = 0;

            for (int i = 0; i < mtlRs.Resources.Length; i++)
            {
                var bindingInfo = layout.GetBindingInfo(i);
                var resource = mtlRs.Resources[i];
                uint bufferOffset = 0;

                if (bindingInfo.DynamicBuffer)
                {
                    bufferOffset = brsi.Offsets.Get(dynamicOffsetIndex);
                    dynamicOffsetIndex += 1;
                }

                switch (bindingInfo.Kind)
                {
                    case ResourceKind.UniformBuffer:
                    {
                        var range = Util.GetBufferRange(resource, bufferOffset);
                        bindBuffer(range, slot, bindingInfo.Slot, bindingInfo.Stages);
                        break;
                    }

                    case ResourceKind.TextureReadOnly:
                        var texView = Util.GetTextureView(gd, resource);
                        var mtlTexView = Util.AssertSubtype<TextureView, MtlTextureView>(texView);
                        bindTexture(mtlTexView, slot, bindingInfo.Slot, bindingInfo.Stages);
                        break;

                    case ResourceKind.TextureReadWrite:
                        var texViewRw = Util.GetTextureView(gd, resource);
                        var mtlTexViewRw = Util.AssertSubtype<TextureView, MtlTextureView>(texViewRw);
                        bindTexture(mtlTexViewRw, slot, bindingInfo.Slot, bindingInfo.Stages);
                        break;

                    case ResourceKind.Sampler:
                        var mtlSampler = Util.AssertSubtype<IBindableResource, MtlSampler>(resource);
                        bindSampler(mtlSampler, slot, bindingInfo.Slot, bindingInfo.Stages);
                        break;

                    case ResourceKind.StructuredBufferReadOnly:
                    {
                        var range = Util.GetBufferRange(resource, bufferOffset);
                        bindBuffer(range, slot, bindingInfo.Slot, bindingInfo.Stages);
                        break;
                    }

                    case ResourceKind.StructuredBufferReadWrite:
                    {
                        var range = Util.GetBufferRange(resource, bufferOffset);
                        bindBuffer(range, slot, bindingInfo.Slot, bindingInfo.Stages);
                        break;
                    }

                    default:
                        throw Illegal.Value<ResourceKind>();
                }
            }
        }

        private void bindBuffer(DeviceBufferRange range, uint set, uint slot, ShaderStages stages)
        {
            var mtlBuffer = Util.AssertSubtype<DeviceBuffer, MtlBuffer>(range.Buffer);
            uint baseBuffer = getBufferBase(set, stages != ShaderStages.Compute);

            if (stages == ShaderStages.Compute)
            {
                UIntPtr index = slot + baseBuffer;

                if (!boundComputeBuffers.TryGetValue(index, out var boundBuffer) || !range.Equals(boundBuffer))
                {
                    cce.setBuffer(mtlBuffer.DeviceBuffer, range.Offset, slot + baseBuffer);
                    boundComputeBuffers[index] = range;
                }
            }
            else
            {
                if ((stages & ShaderStages.Vertex) == ShaderStages.Vertex)
                {
                    UIntPtr index = graphicsPipeline.ResourceBindingModel == ResourceBindingModel.Improved
                        ? slot + baseBuffer
                        : slot + vertexBufferCount + baseBuffer;

                    if (!boundVertexBuffers.TryGetValue(index, out var boundBuffer) || boundBuffer.Buffer != range.Buffer)
                    {
                        rce.setVertexBuffer(mtlBuffer.DeviceBuffer, range.Offset, index);
                        boundVertexBuffers[index] = range;
                    }
                    else if (!range.Equals(boundBuffer))
                    {
                        rce.setVertexBufferOffset(range.Offset, index);
                        boundVertexBuffers[index] = range;
                    }
                }

                if ((stages & ShaderStages.Fragment) == ShaderStages.Fragment)
                {
                    UIntPtr index = slot + baseBuffer;

                    if (!boundFragmentBuffers.TryGetValue(index, out var boundBuffer) || boundBuffer.Buffer != range.Buffer)
                    {
                        rce.setFragmentBuffer(mtlBuffer.DeviceBuffer, range.Offset, slot + baseBuffer);
                        boundFragmentBuffers[index] = range;
                    }
                    else if (!range.Equals(boundBuffer))
                    {
                        rce.setFragmentBufferOffset(range.Offset, slot + baseBuffer);
                        boundFragmentBuffers[index] = range;
                    }
                }
            }
        }

        private void bindTexture(MtlTextureView mtlTexView, uint set, uint slot, ShaderStages stages)
        {
            uint baseTexture = getTextureBase(set, stages != ShaderStages.Compute);
            UIntPtr index = slot + baseTexture;

            if (stages == ShaderStages.Compute && (!boundComputeTextures.TryGetValue(index, out var computeTexture) || computeTexture.NativePtr != mtlTexView.TargetDeviceTexture.NativePtr))
            {
                cce.setTexture(mtlTexView.TargetDeviceTexture, index);
                boundComputeTextures[index] = mtlTexView.TargetDeviceTexture;
            }

            if ((stages & ShaderStages.Vertex) == ShaderStages.Vertex
                && (!boundVertexTextures.TryGetValue(index, out var vertexTexture) || vertexTexture.NativePtr != mtlTexView.TargetDeviceTexture.NativePtr))
            {
                rce.setVertexTexture(mtlTexView.TargetDeviceTexture, index);
                boundVertexTextures[index] = mtlTexView.TargetDeviceTexture;
            }

            if ((stages & ShaderStages.Fragment) == ShaderStages.Fragment
                && (!boundFragmentTextures.TryGetValue(index, out var fragmentTexture) || fragmentTexture.NativePtr != mtlTexView.TargetDeviceTexture.NativePtr))
            {
                rce.setFragmentTexture(mtlTexView.TargetDeviceTexture, index);
                boundFragmentTextures[index] = mtlTexView.TargetDeviceTexture;
            }
        }

        private void bindSampler(MtlSampler mtlSampler, uint set, uint slot, ShaderStages stages)
        {
            uint baseSampler = getSamplerBase(set, stages != ShaderStages.Compute);
            UIntPtr index = slot + baseSampler;

            if (stages == ShaderStages.Compute && (!boundComputeSamplers.TryGetValue(index, out var computeSampler) || computeSampler.NativePtr != mtlSampler.DeviceSampler.NativePtr))
            {
                cce.setSamplerState(mtlSampler.DeviceSampler, index);
                boundComputeSamplers[index] = mtlSampler.DeviceSampler;
            }

            if ((stages & ShaderStages.Vertex) == ShaderStages.Vertex
                && (!boundVertexSamplers.TryGetValue(index, out var vertexSampler) || vertexSampler.NativePtr != mtlSampler.DeviceSampler.NativePtr))
            {
                rce.setVertexSamplerState(mtlSampler.DeviceSampler, index);
                boundVertexSamplers[index] = mtlSampler.DeviceSampler;
            }

            if ((stages & ShaderStages.Fragment) == ShaderStages.Fragment
                && (!boundFragmentSamplers.TryGetValue(index, out var fragmentSampler) || fragmentSampler.NativePtr != mtlSampler.DeviceSampler.NativePtr))
            {
                rce.setFragmentSamplerState(mtlSampler.DeviceSampler, index);
                boundFragmentSamplers[index] = mtlSampler.DeviceSampler;
            }
        }

        private uint getBufferBase(uint set, bool graphics)
        {
            var layouts = graphics ? graphicsPipeline.ResourceLayouts : computePipeline.ResourceLayouts;
            uint ret = 0;

            for (int i = 0; i < set; i++)
            {
                Debug.Assert(layouts[i] != null);
                ret += layouts[i].BufferCount;
            }

            return ret;
        }

        private uint getTextureBase(uint set, bool graphics)
        {
            var layouts = graphics ? graphicsPipeline.ResourceLayouts : computePipeline.ResourceLayouts;
            uint ret = 0;

            for (int i = 0; i < set; i++)
            {
                Debug.Assert(layouts[i] != null);
                ret += layouts[i].TextureCount;
            }

            return ret;
        }

        private uint getSamplerBase(uint set, bool graphics)
        {
            var layouts = graphics ? graphicsPipeline.ResourceLayouts : computePipeline.ResourceLayouts;
            uint ret = 0;

            for (int i = 0; i < set; i++)
            {
                Debug.Assert(layouts[i] != null);
                ret += layouts[i].SamplerCount;
            }

            return ret;
        }

        private bool ensureRenderPass()
        {
            Debug.Assert(mtlFramebuffer != null);
            ensureNoBlitEncoder();
            ensureNoComputeEncoder();
            return renderEncoderActive || beginCurrentRenderPass();
        }

        private bool beginCurrentRenderPass()
        {
            if (mtlFramebuffer is MtlSwapchainFramebuffer swapchainFramebuffer && !swapchainFramebuffer.EnsureDrawableAvailable())
                return false;

            var rpDesc = mtlFramebuffer.CreateRenderPassDescriptor();

            for (uint i = 0; i < clearColors.Length; i++)
            {
                if (clearColors[i] != null)
                {
                    var attachment = rpDesc.colorAttachments[0];
                    attachment.loadAction = MTLLoadAction.Clear;
                    var c = clearColors[i].Value;
                    attachment.clearColor = new MTLClearColor(c.R, c.G, c.B, c.A);
                    clearColors[i] = null;
                }
            }

            if (clearDepth != null)
            {
                var depthAttachment = rpDesc.depthAttachment;
                depthAttachment.loadAction = MTLLoadAction.Clear;
                depthAttachment.clearDepth = clearDepth.Value.depth;

                if (mtlFramebuffer.DepthTarget != null && FormatHelpers.IsStencilFormat(mtlFramebuffer.DepthTarget.Value.Target.Format))
                {
                    var stencilAttachment = rpDesc.stencilAttachment;
                    stencilAttachment.loadAction = MTLLoadAction.Clear;
                    stencilAttachment.clearStencil = clearDepth.Value.stencil;
                }

                clearDepth = null;
            }

            using (NSAutoreleasePool.Begin())
            {
                rce = cb.renderCommandEncoderWithDescriptor(rpDesc);
                ObjectiveCRuntime.retain(rce.NativePtr);
            }

            ObjectiveCRuntime.release(rpDesc.NativePtr);
            currentFramebufferEverActive = true;

            return true;
        }

        private void ensureNoRenderPass()
        {
            if (renderEncoderActive) endCurrentRenderPass();

            Debug.Assert(!renderEncoderActive);
        }

        private void endCurrentRenderPass()
        {
            rce.endEncoding();
            ObjectiveCRuntime.release(rce.NativePtr);
            rce = default;

            lastGraphicsPipeline = null;
            boundVertexBuffers.Clear();
            boundVertexTextures.Clear();
            boundVertexSamplers.Clear();
            boundFragmentBuffers.Clear();
            boundFragmentTextures.Clear();
            boundFragmentSamplers.Clear();
            Util.ClearArray(graphicsResourceSetsActive);
            Util.ClearArray(vertexBuffersActive);
            Util.ClearArray(vbOffsetsActive);

            Util.ClearArray(activeScissorRects);

            viewportsChanged = true;
        }

        private void ensureBlitEncoder()
        {
            if (!blitEncoderActive)
            {
                ensureNoRenderPass();
                ensureNoComputeEncoder();

                using (NSAutoreleasePool.Begin())
                {
                    bce = cb.blitCommandEncoder();
                    ObjectiveCRuntime.retain(bce.NativePtr);
                }
            }

            Debug.Assert(blitEncoderActive);
            Debug.Assert(!renderEncoderActive);
            Debug.Assert(!computeEncoderActive);
        }

        private void ensureNoBlitEncoder()
        {
            if (blitEncoderActive)
            {
                bce.endEncoding();
                ObjectiveCRuntime.release(bce.NativePtr);
                bce = default;
            }

            Debug.Assert(!blitEncoderActive);
        }

        private void ensureComputeEncoder()
        {
            if (!computeEncoderActive)
            {
                ensureNoBlitEncoder();
                ensureNoRenderPass();

                using (NSAutoreleasePool.Begin())
                {
                    cce = cb.computeCommandEncoder();
                    ObjectiveCRuntime.retain(cce.NativePtr);
                }
            }

            Debug.Assert(computeEncoderActive);
            Debug.Assert(!renderEncoderActive);
            Debug.Assert(!blitEncoderActive);
        }

        private void ensureNoComputeEncoder()
        {
            if (computeEncoderActive)
            {
                cce.endEncoding();
                ObjectiveCRuntime.release(cce.NativePtr);
                cce = default;

                boundComputeBuffers.Clear();
                boundComputeTextures.Clear();
                boundComputeSamplers.Clear();
                lastComputePipeline = null;

                Util.ClearArray(computeResourceSetsActive);
            }

            Debug.Assert(!computeEncoderActive);
        }

        private protected override void ClearColorTargetCore(uint index, RgbaFloat clearColor)
        {
            ensureNoRenderPass();
            clearColors[index] = clearColor;
        }

        private protected override void ClearDepthStencilCore(float depth, byte stencil)
        {
            ensureNoRenderPass();
            clearDepth = (depth, stencil);
        }

        private protected override void DrawCore(uint vertexCount, uint instanceCount, uint vertexStart, uint instanceStart)
        {
            if (preDrawCommand())
            {
                if (instanceStart == 0)
                {
                    rce.drawPrimitives(
                        graphicsPipeline.PrimitiveType,
                        vertexStart,
                        vertexCount,
                        instanceCount);
                }
                else
                {
                    rce.drawPrimitives(
                        graphicsPipeline.PrimitiveType,
                        vertexStart,
                        vertexCount,
                        instanceCount,
                        instanceStart);
                }
            }
        }

        private protected override void DrawIndexedCore(uint indexCount, uint instanceCount, uint indexStart, int vertexOffset, uint instanceStart)
        {
            if (preDrawCommand())
            {
                uint indexSize = indexType == MTLIndexType.UInt16 ? 2u : 4u;
                uint indexBufferOffset = indexSize * indexStart + ibOffset;

                if (vertexOffset == 0 && instanceStart == 0)
                {
                    rce.drawIndexedPrimitives(
                        graphicsPipeline.PrimitiveType,
                        indexCount,
                        indexType,
                        indexBuffer.DeviceBuffer,
                        indexBufferOffset,
                        instanceCount);
                }
                else
                {
                    rce.drawIndexedPrimitives(
                        graphicsPipeline.PrimitiveType,
                        indexCount,
                        indexType,
                        indexBuffer.DeviceBuffer,
                        indexBufferOffset,
                        instanceCount,
                        vertexOffset,
                        instanceStart);
                }
            }
        }

        private protected override void SetPipelineCore(Pipeline pipeline)
        {
            if (pipeline.IsComputePipeline && computePipeline != pipeline)
            {
                computePipeline = Util.AssertSubtype<Pipeline, MtlPipeline>(pipeline);
                computeResourceSetCount = (uint)computePipeline.ResourceLayouts.Length;
                Util.EnsureArrayMinimumSize(ref computeResourceSets, computeResourceSetCount);
                Util.EnsureArrayMinimumSize(ref computeResourceSetsActive, computeResourceSetCount);
                Util.ClearArray(computeResourceSetsActive);
            }
            else if (!pipeline.IsComputePipeline && graphicsPipeline != pipeline)
            {
                graphicsPipeline = Util.AssertSubtype<Pipeline, MtlPipeline>(pipeline);
                graphicsResourceSetCount = (uint)graphicsPipeline.ResourceLayouts.Length;
                Util.EnsureArrayMinimumSize(ref graphicsResourceSets, graphicsResourceSetCount);
                Util.EnsureArrayMinimumSize(ref graphicsResourceSetsActive, graphicsResourceSetCount);
                Util.ClearArray(graphicsResourceSetsActive);

                nonVertexBufferCount = graphicsPipeline.NonVertexBufferCount;

                vertexBufferCount = graphicsPipeline.VertexBufferCount;
                Util.EnsureArrayMinimumSize(ref vertexBuffers, vertexBufferCount);
                Util.EnsureArrayMinimumSize(ref vbOffsets, vertexBufferCount);
                Util.EnsureArrayMinimumSize(ref vertexBuffersActive, vertexBufferCount);
                Util.EnsureArrayMinimumSize(ref vbOffsetsActive, vertexBufferCount);
                Util.ClearArray(vertexBuffersActive);
                Util.ClearArray(vbOffsetsActive);
            }
        }

        private protected override void UpdateBufferCore(DeviceBuffer buffer, uint bufferOffsetInBytes, IntPtr source, uint sizeInBytes)
        {
            bool useComputeCopy = bufferOffsetInBytes % 4 != 0
                                  || (sizeInBytes % 4 != 0 && bufferOffsetInBytes != 0 && sizeInBytes != buffer.SizeInBytes);

            var dstMtlBuffer = Util.AssertSubtype<DeviceBuffer, MtlBuffer>(buffer);
            var staging = getFreeStagingBuffer(sizeInBytes);

            gd.UpdateBuffer(staging, 0, source, sizeInBytes);

            if (useComputeCopy)
                CopyBufferCore(staging, 0, buffer, bufferOffsetInBytes, sizeInBytes);
            else
            {
                Debug.Assert(bufferOffsetInBytes % 4 == 0);
                uint sizeRoundFactor = (4 - sizeInBytes % 4) % 4;
                ensureBlitEncoder();
                bce.copy(
                    staging.DeviceBuffer, UIntPtr.Zero,
                    dstMtlBuffer.DeviceBuffer, bufferOffsetInBytes,
                    sizeInBytes + sizeRoundFactor);
            }

            lock (submittedCommandsLock)
            {
                if (!submittedStagingBuffers.TryGetValue(cb, out var bufferList))
                    submittedStagingBuffers.Add(cb, bufferList = new List<MtlBuffer>());

                bufferList.Add(staging);
            }
        }

        private protected override void GenerateMipmapsCore(Texture texture)
        {
            Debug.Assert(texture.MipLevels > 1);
            ensureBlitEncoder();
            var mtlTex = Util.AssertSubtype<Texture, MtlTexture>(texture);
            bce.generateMipmapsForTexture(mtlTex.DeviceTexture);
        }

        private protected override void SetIndexBufferCore(DeviceBuffer buffer, IndexFormat format, uint offset)
        {
            indexBuffer = Util.AssertSubtype<DeviceBuffer, MtlBuffer>(buffer);
            ibOffset = offset;
            indexType = MtlFormats.VdToMtlIndexFormat(format);
        }

        private protected override void SetVertexBufferCore(uint index, DeviceBuffer buffer, uint offset)
        {
            Util.EnsureArrayMinimumSize(ref vertexBuffers, index + 1);
            Util.EnsureArrayMinimumSize(ref vbOffsets, index + 1);
            Util.EnsureArrayMinimumSize(ref vertexBuffersActive, index + 1);
            Util.EnsureArrayMinimumSize(ref vbOffsetsActive, index + 1);

            if (vertexBuffers[index] != buffer)
            {
                var mtlBuffer = Util.AssertSubtype<DeviceBuffer, MtlBuffer>(buffer);
                vertexBuffers[index] = mtlBuffer;
                vertexBuffersActive[index] = false;
            }

            if (vbOffsets[index] != offset)
            {
                vbOffsets[index] = offset;
                vbOffsetsActive[index] = false;
            }
        }

        private protected override void PushDebugGroupCore(string name)
        {
            var nsName = NSString.New(name);
            if (!bce.IsNull)
                bce.pushDebugGroup(nsName);
            else if (!cce.IsNull)
                cce.pushDebugGroup(nsName);
            else if (!rce.IsNull) rce.pushDebugGroup(nsName);

            ObjectiveCRuntime.release(nsName);
        }

        private protected override void PopDebugGroupCore()
        {
            if (!bce.IsNull)
                bce.popDebugGroup();
            else if (!cce.IsNull)
                cce.popDebugGroup();
            else if (!rce.IsNull) rce.popDebugGroup();
        }

        private protected override void InsertDebugMarkerCore(string name)
        {
            var nsName = NSString.New(name);
            if (!bce.IsNull)
                bce.insertDebugSignpost(nsName);
            else if (!cce.IsNull)
                cce.insertDebugSignpost(nsName);
            else if (!rce.IsNull) rce.insertDebugSignpost(nsName);

            ObjectiveCRuntime.release(nsName);
        }
    }
}
