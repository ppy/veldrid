using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Vulkan;
using static Vulkan.VulkanNative;
using static Veldrid.Vk.VulkanUtil;
using static Vulkan.RawConstants;

namespace Veldrid.Vk
{
    internal unsafe class VkCommandList : CommandList
    {
        public VkCommandPool CommandPool => pool;
        public VkCommandBuffer CommandBuffer { get; private set; }

        public ResourceRefCount RefCount { get; }

        public override bool IsDisposed => destroyed;

        public override string Name
        {
            get => name;
            set
            {
                name = value;
                gd.SetResourceName(this, value);
            }
        }

        private readonly VkGraphicsDevice gd;
        private readonly List<VkTexture> preDrawSampledImages = new List<VkTexture>();

        private readonly object commandBufferListLock = new object();
        private readonly Queue<VkCommandBuffer> availableCommandBuffers = new Queue<VkCommandBuffer>();
        private readonly List<VkCommandBuffer> submittedCommandBuffers = new List<VkCommandBuffer>();
        private readonly object stagingLock = new object();
        private readonly Dictionary<VkCommandBuffer, StagingResourceInfo> submittedStagingInfos = new Dictionary<VkCommandBuffer, StagingResourceInfo>();
        private readonly List<StagingResourceInfo> availableStagingInfos = new List<StagingResourceInfo>();
        private readonly List<VkBuffer> availableStagingBuffers = new List<VkBuffer>();
        private readonly VkCommandPool pool;
        private bool destroyed;

        private bool commandBufferBegun;
        private bool commandBufferEnded;
        private VkRect2D[] scissorRects = Array.Empty<VkRect2D>();

        private VkClearValue[] clearValues = Array.Empty<VkClearValue>();
        private bool[] validColorClearValues = Array.Empty<bool>();
        private VkClearValue? depthClearValue;

        // Graphics State
        private VkFramebufferBase currentFramebuffer;
        private bool currentFramebufferEverActive;
        private VkRenderPass activeRenderPass;
        private VkPipeline currentGraphicsPipeline;
        private BoundResourceSetInfo[] currentGraphicsResourceSets = Array.Empty<BoundResourceSetInfo>();
        private bool[] graphicsResourceSetsChanged;

        private bool newFramebuffer; // Render pass cycle state

        // Compute State
        private VkPipeline currentComputePipeline;
        private BoundResourceSetInfo[] currentComputeResourceSets = Array.Empty<BoundResourceSetInfo>();
        private bool[] computeResourceSetsChanged;
        private string name;

        private StagingResourceInfo currentStagingInfo;

        public VkCommandList(VkGraphicsDevice gd, ref CommandListDescription description)
            : base(ref description, gd.Features, gd.UniformBufferMinOffsetAlignment, gd.StructuredBufferMinOffsetAlignment)
        {
            this.gd = gd;
            var poolCi = VkCommandPoolCreateInfo.New();
            poolCi.flags = VkCommandPoolCreateFlags.ResetCommandBuffer;
            poolCi.queueFamilyIndex = gd.GraphicsQueueIndex;
            var result = vkCreateCommandPool(this.gd.Device, ref poolCi, null, out pool);
            CheckResult(result);

            CommandBuffer = getNextCommandBuffer();
            RefCount = new ResourceRefCount(disposeCore);
        }

        #region Disposal

        public override void Dispose()
        {
            RefCount.Decrement();
        }

        #endregion

        public void CommandBufferSubmitted(VkCommandBuffer cb)
        {
            RefCount.Increment();
            foreach (var rrc in currentStagingInfo.Resources) rrc.Increment();

            submittedStagingInfos.Add(cb, currentStagingInfo);
            currentStagingInfo = null;
        }

        public void CommandBufferCompleted(VkCommandBuffer completedCb)
        {
            lock (commandBufferListLock)
            {
                for (int i = 0; i < submittedCommandBuffers.Count; i++)
                {
                    var submittedCb = submittedCommandBuffers[i];

                    if (submittedCb == completedCb)
                    {
                        availableCommandBuffers.Enqueue(completedCb);
                        submittedCommandBuffers.RemoveAt(i);
                        i -= 1;
                    }
                }
            }

            lock (stagingLock)
            {
                if (submittedStagingInfos.TryGetValue(completedCb, out var info))
                {
                    recycleStagingInfo(info);
                    submittedStagingInfos.Remove(completedCb);
                }
            }

            RefCount.Decrement();
        }

        public override void Begin()
        {
            if (commandBufferBegun)
            {
                throw new VeldridException(
                    "CommandList must be in its initial state, or End() must have been called, for Begin() to be valid to call.");
            }

            if (commandBufferEnded)
            {
                commandBufferEnded = false;
                CommandBuffer = getNextCommandBuffer();
                if (currentStagingInfo != null) recycleStagingInfo(currentStagingInfo);
            }

            currentStagingInfo = getStagingResourceInfo();

            var beginInfo = VkCommandBufferBeginInfo.New();
            beginInfo.flags = VkCommandBufferUsageFlags.OneTimeSubmit;
            vkBeginCommandBuffer(CommandBuffer, ref beginInfo);
            commandBufferBegun = true;

            ClearCachedState();
            currentFramebuffer = null;
            currentGraphicsPipeline = null;
            clearSets(currentGraphicsResourceSets);
            Util.ClearArray(scissorRects);

            currentComputePipeline = null;
            clearSets(currentComputeResourceSets);
        }

        public override void Dispatch(uint groupCountX, uint groupCountY, uint groupCountZ)
        {
            preDispatchCommand();

            vkCmdDispatch(CommandBuffer, groupCountX, groupCountY, groupCountZ);
        }

        public override void End()
        {
            if (!commandBufferBegun) throw new VeldridException("CommandBuffer must have been started before End() may be called.");

            commandBufferBegun = false;
            commandBufferEnded = true;

            if (!currentFramebufferEverActive && currentFramebuffer != null)
                beginCurrentRenderPass();

            if (activeRenderPass != VkRenderPass.Null)
            {
                endCurrentRenderPass();
                currentFramebuffer!.TransitionToFinalLayout(CommandBuffer);
            }

            vkEndCommandBuffer(CommandBuffer);
            submittedCommandBuffers.Add(CommandBuffer);
        }

        public override void SetScissorRect(uint index, uint x, uint y, uint width, uint height)
        {
            if (index == 0 || gd.Features.MultipleViewports)
            {
                var scissor = new VkRect2D((int)x, (int)y, (int)width, (int)height);

                if (scissorRects[index] != scissor)
                {
                    scissorRects[index] = scissor;
                    vkCmdSetScissor(CommandBuffer, index, 1, ref scissor);
                }
            }
        }

        public override void SetViewport(uint index, ref Viewport viewport)
        {
            if (index == 0 || gd.Features.MultipleViewports)
            {
                float vpY = gd.IsClipSpaceYInverted
                    ? viewport.Y
                    : viewport.Height + viewport.Y;
                float vpHeight = gd.IsClipSpaceYInverted
                    ? viewport.Height
                    : -viewport.Height;

                var vkViewport = new VkViewport
                {
                    x = viewport.X,
                    y = vpY,
                    width = viewport.Width,
                    height = vpHeight,
                    minDepth = viewport.MinDepth,
                    maxDepth = viewport.MaxDepth
                };

                vkCmdSetViewport(CommandBuffer, index, 1, ref vkViewport);
            }
        }

        private protected override void UpdateBufferCore(DeviceBuffer buffer, uint bufferOffsetInBytes, IntPtr source, uint sizeInBytes)
        {
            VkBuffer stagingBuffer = getStagingBuffer(sizeInBytes);
            gd.UpdateBuffer(stagingBuffer, 0, source, sizeInBytes);
            CopyBuffer(stagingBuffer, 0, buffer, bufferOffsetInBytes, sizeInBytes);
        }

        protected override void CopyBufferCore(
            DeviceBuffer source,
            uint sourceOffset,
            DeviceBuffer destination,
            uint destinationOffset,
            uint sizeInBytes)
        {
            ensureNoRenderPass();

            VkBuffer srcVkBuffer = Util.AssertSubtype<DeviceBuffer, VkBuffer>(source);
            currentStagingInfo.Resources.Add(srcVkBuffer.RefCount);
            VkBuffer dstVkBuffer = Util.AssertSubtype<DeviceBuffer, VkBuffer>(destination);
            currentStagingInfo.Resources.Add(dstVkBuffer.RefCount);

            VkBufferCopy region = new VkBufferCopy
            {
                srcOffset = sourceOffset,
                dstOffset = destinationOffset,
                size = sizeInBytes
            };

            vkCmdCopyBuffer(CommandBuffer, srcVkBuffer.DeviceBuffer, dstVkBuffer.DeviceBuffer, 1, ref region);

            bool needToProtectUniform = destination.Usage.HasFlag(BufferUsage.UniformBuffer);

            VkMemoryBarrier barrier;
            barrier.sType = VkStructureType.MemoryBarrier;
            barrier.srcAccessMask = VkAccessFlags.TransferWrite;
            barrier.dstAccessMask = needToProtectUniform ? VkAccessFlags.UniformRead : VkAccessFlags.VertexAttributeRead;
            barrier.pNext = null;
            vkCmdPipelineBarrier(
                CommandBuffer,
                VkPipelineStageFlags.Transfer, needToProtectUniform
                    ? VkPipelineStageFlags.VertexShader | VkPipelineStageFlags.ComputeShader |
                      VkPipelineStageFlags.FragmentShader | VkPipelineStageFlags.GeometryShader |
                      VkPipelineStageFlags.TessellationControlShader | VkPipelineStageFlags.TessellationEvaluationShader
                    : VkPipelineStageFlags.VertexInput,
                VkDependencyFlags.None,
                1, ref barrier,
                0, null,
                0, null);
        }

        protected override void CopyTextureCore(
            Texture source,
            uint srcX, uint srcY, uint srcZ,
            uint srcMipLevel,
            uint srcBaseArrayLayer,
            Texture destination,
            uint dstX, uint dstY, uint dstZ,
            uint dstMipLevel,
            uint dstBaseArrayLayer,
            uint width, uint height, uint depth,
            uint layerCount)
        {
            ensureNoRenderPass();
            CopyTextureCore_VkCommandBuffer(
                CommandBuffer,
                source, srcX, srcY, srcZ, srcMipLevel, srcBaseArrayLayer,
                destination, dstX, dstY, dstZ, dstMipLevel, dstBaseArrayLayer,
                width, height, depth, layerCount);

            VkTexture srcVkTexture = Util.AssertSubtype<Texture, VkTexture>(source);
            currentStagingInfo.Resources.Add(srcVkTexture.RefCount);
            VkTexture dstVkTexture = Util.AssertSubtype<Texture, VkTexture>(destination);
            currentStagingInfo.Resources.Add(dstVkTexture.RefCount);
        }

        internal static void CopyTextureCore_VkCommandBuffer(
            VkCommandBuffer cb,
            Texture source,
            uint srcX, uint srcY, uint srcZ,
            uint srcMipLevel,
            uint srcBaseArrayLayer,
            Texture destination,
            uint dstX, uint dstY, uint dstZ,
            uint dstMipLevel,
            uint dstBaseArrayLayer,
            uint width, uint height, uint depth,
            uint layerCount)
        {
            var srcVkTexture = Util.AssertSubtype<Texture, VkTexture>(source);
            var dstVkTexture = Util.AssertSubtype<Texture, VkTexture>(destination);

            bool sourceIsStaging = (source.Usage & TextureUsage.Staging) == TextureUsage.Staging;
            bool destIsStaging = (destination.Usage & TextureUsage.Staging) == TextureUsage.Staging;

            if (!sourceIsStaging && !destIsStaging)
            {
                var srcSubresource = new VkImageSubresourceLayers
                {
                    aspectMask = VkImageAspectFlags.Color,
                    layerCount = layerCount,
                    mipLevel = srcMipLevel,
                    baseArrayLayer = srcBaseArrayLayer
                };

                var dstSubresource = new VkImageSubresourceLayers
                {
                    aspectMask = VkImageAspectFlags.Color,
                    layerCount = layerCount,
                    mipLevel = dstMipLevel,
                    baseArrayLayer = dstBaseArrayLayer
                };

                var region = new VkImageCopy
                {
                    srcOffset = new VkOffset3D { x = (int)srcX, y = (int)srcY, z = (int)srcZ },
                    dstOffset = new VkOffset3D { x = (int)dstX, y = (int)dstY, z = (int)dstZ },
                    srcSubresource = srcSubresource,
                    dstSubresource = dstSubresource,
                    extent = new VkExtent3D { width = width, height = height, depth = depth }
                };

                srcVkTexture.TransitionImageLayout(
                    cb,
                    srcMipLevel,
                    1,
                    srcBaseArrayLayer,
                    layerCount,
                    VkImageLayout.TransferSrcOptimal);

                dstVkTexture.TransitionImageLayout(
                    cb,
                    dstMipLevel,
                    1,
                    dstBaseArrayLayer,
                    layerCount,
                    VkImageLayout.TransferDstOptimal);

                vkCmdCopyImage(
                    cb,
                    srcVkTexture.OptimalDeviceImage,
                    VkImageLayout.TransferSrcOptimal,
                    dstVkTexture.OptimalDeviceImage,
                    VkImageLayout.TransferDstOptimal,
                    1,
                    ref region);

                if ((srcVkTexture.Usage & TextureUsage.Sampled) != 0)
                {
                    srcVkTexture.TransitionImageLayout(
                        cb,
                        srcMipLevel,
                        1,
                        srcBaseArrayLayer,
                        layerCount,
                        VkImageLayout.ShaderReadOnlyOptimal);
                }

                if ((dstVkTexture.Usage & TextureUsage.Sampled) != 0)
                {
                    dstVkTexture.TransitionImageLayout(
                        cb,
                        dstMipLevel,
                        1,
                        dstBaseArrayLayer,
                        layerCount,
                        VkImageLayout.ShaderReadOnlyOptimal);
                }
            }
            else if (sourceIsStaging && !destIsStaging)
            {
                var srcBuffer = srcVkTexture.StagingBuffer;
                var srcLayout = srcVkTexture.GetSubresourceLayout(
                    srcVkTexture.CalculateSubresource(srcMipLevel, srcBaseArrayLayer));
                var dstImage = dstVkTexture.OptimalDeviceImage;
                dstVkTexture.TransitionImageLayout(
                    cb,
                    dstMipLevel,
                    1,
                    dstBaseArrayLayer,
                    layerCount,
                    VkImageLayout.TransferDstOptimal);

                var dstSubresource = new VkImageSubresourceLayers
                {
                    aspectMask = VkImageAspectFlags.Color,
                    layerCount = layerCount,
                    mipLevel = dstMipLevel,
                    baseArrayLayer = dstBaseArrayLayer
                };

                Util.GetMipDimensions(srcVkTexture, srcMipLevel, out uint mipWidth, out uint mipHeight, out uint _);
                uint blockSize = FormatHelpers.IsCompressedFormat(srcVkTexture.Format) ? 4u : 1u;
                uint bufferRowLength = Math.Max(mipWidth, blockSize);
                uint bufferImageHeight = Math.Max(mipHeight, blockSize);
                uint compressedX = srcX / blockSize;
                uint compressedY = srcY / blockSize;
                uint blockSizeInBytes = blockSize == 1
                    ? FormatSizeHelpers.GetSizeInBytes(srcVkTexture.Format)
                    : FormatHelpers.GetBlockSizeInBytes(srcVkTexture.Format);
                uint rowPitch = FormatHelpers.GetRowPitch(bufferRowLength, srcVkTexture.Format);
                uint depthPitch = FormatHelpers.GetDepthPitch(rowPitch, bufferImageHeight, srcVkTexture.Format);

                uint copyWidth = Math.Min(width, mipWidth);
                uint copyheight = Math.Min(height, mipHeight);

                var regions = new VkBufferImageCopy
                {
                    bufferOffset = srcLayout.offset
                                   + srcZ * depthPitch
                                   + compressedY * rowPitch
                                   + compressedX * blockSizeInBytes,
                    bufferRowLength = bufferRowLength,
                    bufferImageHeight = bufferImageHeight,
                    imageExtent = new VkExtent3D { width = copyWidth, height = copyheight, depth = depth },
                    imageOffset = new VkOffset3D { x = (int)dstX, y = (int)dstY, z = (int)dstZ },
                    imageSubresource = dstSubresource
                };

                vkCmdCopyBufferToImage(cb, srcBuffer, dstImage, VkImageLayout.TransferDstOptimal, 1, ref regions);

                if ((dstVkTexture.Usage & TextureUsage.Sampled) != 0)
                {
                    dstVkTexture.TransitionImageLayout(
                        cb,
                        dstMipLevel,
                        1,
                        dstBaseArrayLayer,
                        layerCount,
                        VkImageLayout.ShaderReadOnlyOptimal);
                }
            }
            else if (!sourceIsStaging)
            {
                var srcImage = srcVkTexture.OptimalDeviceImage;
                srcVkTexture.TransitionImageLayout(
                    cb,
                    srcMipLevel,
                    1,
                    srcBaseArrayLayer,
                    layerCount,
                    VkImageLayout.TransferSrcOptimal);

                var dstBuffer = dstVkTexture.StagingBuffer;

                var aspect = (srcVkTexture.Usage & TextureUsage.DepthStencil) != 0
                    ? VkImageAspectFlags.Depth
                    : VkImageAspectFlags.Color;

                Util.GetMipDimensions(dstVkTexture, dstMipLevel, out uint mipWidth, out uint mipHeight, out uint _);
                uint blockSize = FormatHelpers.IsCompressedFormat(srcVkTexture.Format) ? 4u : 1u;
                uint bufferRowLength = Math.Max(mipWidth, blockSize);
                uint bufferImageHeight = Math.Max(mipHeight, blockSize);
                uint compressedDstX = dstX / blockSize;
                uint compressedDstY = dstY / blockSize;
                uint blockSizeInBytes = blockSize == 1
                    ? FormatSizeHelpers.GetSizeInBytes(dstVkTexture.Format)
                    : FormatHelpers.GetBlockSizeInBytes(dstVkTexture.Format);
                uint rowPitch = FormatHelpers.GetRowPitch(bufferRowLength, dstVkTexture.Format);
                uint depthPitch = FormatHelpers.GetDepthPitch(rowPitch, bufferImageHeight, dstVkTexture.Format);

                var layers = stackalloc VkBufferImageCopy[(int)layerCount];

                for (uint layer = 0; layer < layerCount; layer++)
                {
                    var dstLayout = dstVkTexture.GetSubresourceLayout(
                        dstVkTexture.CalculateSubresource(dstMipLevel, dstBaseArrayLayer + layer));

                    var srcSubresource = new VkImageSubresourceLayers
                    {
                        aspectMask = aspect,
                        layerCount = 1,
                        mipLevel = srcMipLevel,
                        baseArrayLayer = srcBaseArrayLayer + layer
                    };

                    var region = new VkBufferImageCopy
                    {
                        bufferRowLength = bufferRowLength,
                        bufferImageHeight = bufferImageHeight,
                        bufferOffset = dstLayout.offset
                                       + dstZ * depthPitch
                                       + compressedDstY * rowPitch
                                       + compressedDstX * blockSizeInBytes,
                        imageExtent = new VkExtent3D { width = width, height = height, depth = depth },
                        imageOffset = new VkOffset3D { x = (int)srcX, y = (int)srcY, z = (int)srcZ },
                        imageSubresource = srcSubresource
                    };

                    layers[layer] = region;
                }

                vkCmdCopyImageToBuffer(cb, srcImage, VkImageLayout.TransferSrcOptimal, dstBuffer, layerCount, layers);

                if ((srcVkTexture.Usage & TextureUsage.Sampled) != 0)
                {
                    srcVkTexture.TransitionImageLayout(
                        cb,
                        srcMipLevel,
                        1,
                        srcBaseArrayLayer,
                        layerCount,
                        VkImageLayout.ShaderReadOnlyOptimal);
                }
            }
            else
            {
                Debug.Assert(sourceIsStaging && destIsStaging);
                var srcBuffer = srcVkTexture.StagingBuffer;
                var srcLayout = srcVkTexture.GetSubresourceLayout(
                    srcVkTexture.CalculateSubresource(srcMipLevel, srcBaseArrayLayer));
                var dstBuffer = dstVkTexture.StagingBuffer;
                var dstLayout = dstVkTexture.GetSubresourceLayout(
                    dstVkTexture.CalculateSubresource(dstMipLevel, dstBaseArrayLayer));

                uint zLimit = Math.Max(depth, layerCount);

                if (!FormatHelpers.IsCompressedFormat(source.Format))
                {
                    uint pixelSize = FormatSizeHelpers.GetSizeInBytes(srcVkTexture.Format);

                    for (uint zz = 0; zz < zLimit; zz++)
                    {
                        for (uint yy = 0; yy < height; yy++)
                        {
                            var region = new VkBufferCopy
                            {
                                srcOffset = srcLayout.offset
                                            + srcLayout.depthPitch * (zz + srcZ)
                                            + srcLayout.rowPitch * (yy + srcY)
                                            + pixelSize * srcX,
                                dstOffset = dstLayout.offset
                                            + dstLayout.depthPitch * (zz + dstZ)
                                            + dstLayout.rowPitch * (yy + dstY)
                                            + pixelSize * dstX,
                                size = width * pixelSize
                            };

                            vkCmdCopyBuffer(cb, srcBuffer, dstBuffer, 1, ref region);
                        }
                    }
                }
                else // IsCompressedFormat
                {
                    uint denseRowSize = FormatHelpers.GetRowPitch(width, source.Format);
                    uint numRows = FormatHelpers.GetNumRows(height, source.Format);
                    uint compressedSrcX = srcX / 4;
                    uint compressedSrcY = srcY / 4;
                    uint compressedDstX = dstX / 4;
                    uint compressedDstY = dstY / 4;
                    uint blockSizeInBytes = FormatHelpers.GetBlockSizeInBytes(source.Format);

                    for (uint zz = 0; zz < zLimit; zz++)
                    {
                        for (uint row = 0; row < numRows; row++)
                        {
                            var region = new VkBufferCopy
                            {
                                srcOffset = srcLayout.offset
                                            + srcLayout.depthPitch * (zz + srcZ)
                                            + srcLayout.rowPitch * (row + compressedSrcY)
                                            + blockSizeInBytes * compressedSrcX,
                                dstOffset = dstLayout.offset
                                            + dstLayout.depthPitch * (zz + dstZ)
                                            + dstLayout.rowPitch * (row + compressedDstY)
                                            + blockSizeInBytes * compressedDstX,
                                size = denseRowSize
                            };

                            vkCmdCopyBuffer(cb, srcBuffer, dstBuffer, 1, ref region);
                        }
                    }
                }
            }
        }

        protected override void DrawIndirectCore(DeviceBuffer indirectBuffer, uint offset, uint drawCount, uint stride)
        {
            preDrawCommand();
            var vkBuffer = Util.AssertSubtype<DeviceBuffer, VkBuffer>(indirectBuffer);
            currentStagingInfo.Resources.Add(vkBuffer.RefCount);
            vkCmdDrawIndirect(CommandBuffer, vkBuffer.DeviceBuffer, offset, drawCount, stride);
        }

        protected override void DrawIndexedIndirectCore(DeviceBuffer indirectBuffer, uint offset, uint drawCount, uint stride)
        {
            preDrawCommand();
            var vkBuffer = Util.AssertSubtype<DeviceBuffer, VkBuffer>(indirectBuffer);
            currentStagingInfo.Resources.Add(vkBuffer.RefCount);
            vkCmdDrawIndexedIndirect(CommandBuffer, vkBuffer.DeviceBuffer, offset, drawCount, stride);
        }

        protected override void DispatchIndirectCore(DeviceBuffer indirectBuffer, uint offset)
        {
            preDispatchCommand();

            var vkBuffer = Util.AssertSubtype<DeviceBuffer, VkBuffer>(indirectBuffer);
            currentStagingInfo.Resources.Add(vkBuffer.RefCount);
            vkCmdDispatchIndirect(CommandBuffer, vkBuffer.DeviceBuffer, offset);
        }

        protected override void ResolveTextureCore(Texture source, Texture destination)
        {
            if (activeRenderPass != VkRenderPass.Null) endCurrentRenderPass();

            var vkSource = Util.AssertSubtype<Texture, VkTexture>(source);
            currentStagingInfo.Resources.Add(vkSource.RefCount);
            var vkDestination = Util.AssertSubtype<Texture, VkTexture>(destination);
            currentStagingInfo.Resources.Add(vkDestination.RefCount);
            var aspectFlags = (source.Usage & TextureUsage.DepthStencil) == TextureUsage.DepthStencil
                ? VkImageAspectFlags.Depth | VkImageAspectFlags.Stencil
                : VkImageAspectFlags.Color;
            var region = new VkImageResolve
            {
                extent = new VkExtent3D { width = source.Width, height = source.Height, depth = source.Depth },
                srcSubresource = new VkImageSubresourceLayers { layerCount = 1, aspectMask = aspectFlags },
                dstSubresource = new VkImageSubresourceLayers { layerCount = 1, aspectMask = aspectFlags }
            };

            vkSource.TransitionImageLayout(CommandBuffer, 0, 1, 0, 1, VkImageLayout.TransferSrcOptimal);
            vkDestination.TransitionImageLayout(CommandBuffer, 0, 1, 0, 1, VkImageLayout.TransferDstOptimal);

            vkCmdResolveImage(
                CommandBuffer,
                vkSource.OptimalDeviceImage,
                VkImageLayout.TransferSrcOptimal,
                vkDestination.OptimalDeviceImage,
                VkImageLayout.TransferDstOptimal,
                1,
                ref region);

            if ((vkDestination.Usage & TextureUsage.Sampled) != 0) vkDestination.TransitionImageLayout(CommandBuffer, 0, 1, 0, 1, VkImageLayout.ShaderReadOnlyOptimal);
        }

        protected override void SetFramebufferCore(Framebuffer fb)
        {
            if (activeRenderPass.Handle != VkRenderPass.Null)
                endCurrentRenderPass();
            else if (!currentFramebufferEverActive && currentFramebuffer != null)
            {
                // This forces any queued up texture clears to be emitted.
                beginCurrentRenderPass();
                endCurrentRenderPass();
            }

            currentFramebuffer?.TransitionToFinalLayout(CommandBuffer);

            var vkFb = Util.AssertSubtype<Framebuffer, VkFramebufferBase>(fb);
            currentFramebuffer = vkFb;
            currentFramebufferEverActive = false;
            newFramebuffer = true;
            Util.EnsureArrayMinimumSize(ref scissorRects, Math.Max(1, (uint)vkFb.ColorTargets.Count));
            uint clearValueCount = (uint)vkFb.ColorTargets.Count;
            Util.EnsureArrayMinimumSize(ref clearValues, clearValueCount + 1); // Leave an extra space for the depth value (tracked separately).
            Util.ClearArray(validColorClearValues);
            Util.EnsureArrayMinimumSize(ref validColorClearValues, clearValueCount);
            currentStagingInfo.Resources.Add(vkFb.RefCount);

            if (fb is VkSwapchainFramebuffer scFb) currentStagingInfo.Resources.Add(scFb.Swapchain.RefCount);
        }

        protected override void SetGraphicsResourceSetCore(uint slot, ResourceSet rs, uint dynamicOffsetsCount, ref uint dynamicOffsets)
        {
            if (!currentGraphicsResourceSets[slot].Equals(rs, dynamicOffsetsCount, ref dynamicOffsets))
            {
                currentGraphicsResourceSets[slot].Offsets.Dispose();
                currentGraphicsResourceSets[slot] = new BoundResourceSetInfo(rs, dynamicOffsetsCount, ref dynamicOffsets);
                graphicsResourceSetsChanged[slot] = true;
                Util.AssertSubtype<ResourceSet, VkResourceSet>(rs);
            }
        }

        protected override void SetComputeResourceSetCore(uint slot, ResourceSet rs, uint dynamicOffsetsCount, ref uint dynamicOffsets)
        {
            if (!currentComputeResourceSets[slot].Equals(rs, dynamicOffsetsCount, ref dynamicOffsets))
            {
                currentComputeResourceSets[slot].Offsets.Dispose();
                currentComputeResourceSets[slot] = new BoundResourceSetInfo(rs, dynamicOffsetsCount, ref dynamicOffsets);
                computeResourceSetsChanged[slot] = true;
                Util.AssertSubtype<ResourceSet, VkResourceSet>(rs);
            }
        }

        private VkCommandBuffer getNextCommandBuffer()
        {
            lock (commandBufferListLock)
            {
                if (availableCommandBuffers.Count > 0)
                {
                    var cachedCb = availableCommandBuffers.Dequeue();
                    var resetResult = vkResetCommandBuffer(cachedCb, VkCommandBufferResetFlags.None);
                    CheckResult(resetResult);
                    return cachedCb;
                }
            }

            var cbAi = VkCommandBufferAllocateInfo.New();
            cbAi.commandPool = pool;
            cbAi.commandBufferCount = 1;
            cbAi.level = VkCommandBufferLevel.Primary;
            var result = vkAllocateCommandBuffers(gd.Device, ref cbAi, out var cb);
            CheckResult(result);
            return cb;
        }

        private void preDrawCommand()
        {
            transitionImages(preDrawSampledImages, VkImageLayout.ShaderReadOnlyOptimal);
            preDrawSampledImages.Clear();

            ensureRenderPassActive();

            flushNewResourceSets(
                currentGraphicsResourceSets,
                graphicsResourceSetsChanged,
                currentGraphicsPipeline.ResourceSetCount,
                VkPipelineBindPoint.Graphics,
                currentGraphicsPipeline.PipelineLayout);
        }

        private void flushNewResourceSets(
            BoundResourceSetInfo[] resourceSets,
            bool[] resourceSetsChanged,
            uint resourceSetCount,
            VkPipelineBindPoint bindPoint,
            VkPipelineLayout pipelineLayout)
        {
            var pipeline = bindPoint == VkPipelineBindPoint.Graphics ? currentGraphicsPipeline : currentComputePipeline;

            var descriptorSets = stackalloc VkDescriptorSet[(int)resourceSetCount];
            uint* dynamicOffsets = stackalloc uint[(int)pipeline.DynamicOffsetsCount];
            uint currentBatchCount = 0;
            uint currentBatchFirstSet = 0;
            uint currentBatchDynamicOffsetCount = 0;

            for (uint currentSlot = 0; currentSlot < resourceSetCount; currentSlot++)
            {
                bool batchEnded = !resourceSetsChanged[currentSlot] || currentSlot == resourceSetCount - 1;

                if (resourceSetsChanged[currentSlot])
                {
                    resourceSetsChanged[currentSlot] = false;
                    var vkSet = Util.AssertSubtype<ResourceSet, VkResourceSet>(resourceSets[currentSlot].Set);
                    descriptorSets[currentBatchCount] = vkSet.DescriptorSet;
                    currentBatchCount += 1;

                    ref var curSetOffsets = ref resourceSets[currentSlot].Offsets;

                    for (uint i = 0; i < curSetOffsets.Count; i++)
                    {
                        dynamicOffsets[currentBatchDynamicOffsetCount] = curSetOffsets.Get(i);
                        currentBatchDynamicOffsetCount += 1;
                    }

                    // Increment ref count on first use of a set.
                    currentStagingInfo.Resources.Add(vkSet.RefCount);
                    for (int i = 0; i < vkSet.RefCounts.Count; i++) currentStagingInfo.Resources.Add(vkSet.RefCounts[i]);
                }

                if (batchEnded)
                {
                    if (currentBatchCount != 0)
                    {
                        // Flush current batch.
                        vkCmdBindDescriptorSets(
                            CommandBuffer,
                            bindPoint,
                            pipelineLayout,
                            currentBatchFirstSet,
                            currentBatchCount,
                            descriptorSets,
                            currentBatchDynamicOffsetCount,
                            dynamicOffsets);
                    }

                    currentBatchCount = 0;
                    currentBatchFirstSet = currentSlot + 1;
                }
            }
        }

        private void transitionImages(List<VkTexture> sampledTextures, VkImageLayout layout)
        {
            for (int i = 0; i < sampledTextures.Count; i++)
            {
                var tex = sampledTextures[i];
                tex.TransitionImageLayout(CommandBuffer, 0, tex.MipLevels, 0, tex.ActualArrayLayers, layout);
            }
        }

        private void preDispatchCommand()
        {
            ensureNoRenderPass();

            for (uint currentSlot = 0; currentSlot < currentComputePipeline.ResourceSetCount; currentSlot++)
            {
                var vkSet = Util.AssertSubtype<ResourceSet, VkResourceSet>(
                    currentComputeResourceSets[currentSlot].Set);

                transitionImages(vkSet.SampledTextures, VkImageLayout.ShaderReadOnlyOptimal);
                transitionImages(vkSet.StorageTextures, VkImageLayout.General);

                for (int texIdx = 0; texIdx < vkSet.StorageTextures.Count; texIdx++)
                {
                    var storageTex = vkSet.StorageTextures[texIdx];
                    if ((storageTex.Usage & TextureUsage.Sampled) != 0) preDrawSampledImages.Add(storageTex);
                }
            }

            flushNewResourceSets(
                currentComputeResourceSets,
                computeResourceSetsChanged,
                currentComputePipeline.ResourceSetCount,
                VkPipelineBindPoint.Compute,
                currentComputePipeline.PipelineLayout);
        }

        private void ensureRenderPassActive()
        {
            if (activeRenderPass == VkRenderPass.Null) beginCurrentRenderPass();
        }

        private void ensureNoRenderPass()
        {
            if (activeRenderPass != VkRenderPass.Null) endCurrentRenderPass();
        }

        private void beginCurrentRenderPass()
        {
            Debug.Assert(activeRenderPass == VkRenderPass.Null);
            Debug.Assert(currentFramebuffer != null);
            currentFramebufferEverActive = true;

            uint attachmentCount = currentFramebuffer.AttachmentCount;
            bool haveAnyAttachments = currentFramebuffer.ColorTargets.Count > 0 || currentFramebuffer.DepthTarget != null;
            bool haveAllClearValues = depthClearValue.HasValue || currentFramebuffer.DepthTarget == null;
            bool haveAnyClearValues = depthClearValue.HasValue;

            for (int i = 0; i < currentFramebuffer.ColorTargets.Count; i++)
            {
                if (!validColorClearValues[i])
                    haveAllClearValues = false;
                else
                    haveAnyClearValues = true;
            }

            var renderPassBi = VkRenderPassBeginInfo.New();
            renderPassBi.renderArea = new VkRect2D(currentFramebuffer.RenderableWidth, currentFramebuffer.RenderableHeight);
            renderPassBi.framebuffer = currentFramebuffer.CurrentFramebuffer;

            if (!haveAnyAttachments || !haveAllClearValues)
            {
                renderPassBi.renderPass = newFramebuffer
                    ? currentFramebuffer.RenderPassNoClearInit
                    : currentFramebuffer.RenderPassNoClearLoad;
                vkCmdBeginRenderPass(CommandBuffer, ref renderPassBi, VkSubpassContents.Inline);
                activeRenderPass = renderPassBi.renderPass;

                if (haveAnyClearValues)
                {
                    if (depthClearValue.HasValue)
                    {
                        ClearDepthStencilCore(depthClearValue.Value.depthStencil.depth, (byte)depthClearValue.Value.depthStencil.stencil);
                        depthClearValue = null;
                    }

                    for (uint i = 0; i < currentFramebuffer.ColorTargets.Count; i++)
                    {
                        if (validColorClearValues[i])
                        {
                            validColorClearValues[i] = false;
                            var vkClearValue = clearValues[i];
                            var clearColor = new RgbaFloat(
                                vkClearValue.color.float32_0,
                                vkClearValue.color.float32_1,
                                vkClearValue.color.float32_2,
                                vkClearValue.color.float32_3);
                            ClearColorTarget(i, clearColor);
                        }
                    }
                }
            }
            else
            {
                // We have clear values for every attachment.
                renderPassBi.renderPass = currentFramebuffer.RenderPassClear;

                fixed (VkClearValue* clearValuesPtr = &clearValues[0])
                {
                    renderPassBi.clearValueCount = attachmentCount;
                    renderPassBi.pClearValues = clearValuesPtr;

                    if (depthClearValue.HasValue)
                    {
                        clearValues[currentFramebuffer.ColorTargets.Count] = depthClearValue.Value;
                        depthClearValue = null;
                    }

                    vkCmdBeginRenderPass(CommandBuffer, ref renderPassBi, VkSubpassContents.Inline);
                    activeRenderPass = currentFramebuffer.RenderPassClear;
                    Util.ClearArray(validColorClearValues);
                }
            }

            newFramebuffer = false;
        }

        private void endCurrentRenderPass()
        {
            Debug.Assert(activeRenderPass != VkRenderPass.Null);
            vkCmdEndRenderPass(CommandBuffer);
            currentFramebuffer.TransitionToIntermediateLayout(CommandBuffer);
            activeRenderPass = VkRenderPass.Null;

            // Place a barrier between RenderPasses, so that color / depth outputs
            // can be read in subsequent passes.
            vkCmdPipelineBarrier(
                CommandBuffer,
                VkPipelineStageFlags.BottomOfPipe,
                VkPipelineStageFlags.TopOfPipe,
                VkDependencyFlags.None,
                0,
                null,
                0,
                null,
                0,
                null);
        }

        private void clearSets(BoundResourceSetInfo[] boundSets)
        {
            foreach (var boundSetInfo in boundSets) boundSetInfo.Offsets.Dispose();
            Util.ClearArray(boundSets);
        }

        [Conditional("DEBUG")]
        private void debugFullPipelineBarrier()
        {
            var memoryBarrier = VkMemoryBarrier.New();
            memoryBarrier.srcAccessMask = VK_ACCESS_INDIRECT_COMMAND_READ_BIT |
                                          VK_ACCESS_INDEX_READ_BIT |
                                          VK_ACCESS_VERTEX_ATTRIBUTE_READ_BIT |
                                          VK_ACCESS_UNIFORM_READ_BIT |
                                          VK_ACCESS_INPUT_ATTACHMENT_READ_BIT |
                                          VK_ACCESS_SHADER_READ_BIT |
                                          VK_ACCESS_SHADER_WRITE_BIT |
                                          VK_ACCESS_COLOR_ATTACHMENT_READ_BIT |
                                          VK_ACCESS_COLOR_ATTACHMENT_WRITE_BIT |
                                          VK_ACCESS_DEPTH_STENCIL_ATTACHMENT_READ_BIT |
                                          VK_ACCESS_DEPTH_STENCIL_ATTACHMENT_WRITE_BIT |
                                          VK_ACCESS_TRANSFER_READ_BIT |
                                          VK_ACCESS_TRANSFER_WRITE_BIT |
                                          VK_ACCESS_HOST_READ_BIT |
                                          VK_ACCESS_HOST_WRITE_BIT;
            memoryBarrier.dstAccessMask = VK_ACCESS_INDIRECT_COMMAND_READ_BIT |
                                          VK_ACCESS_INDEX_READ_BIT |
                                          VK_ACCESS_VERTEX_ATTRIBUTE_READ_BIT |
                                          VK_ACCESS_UNIFORM_READ_BIT |
                                          VK_ACCESS_INPUT_ATTACHMENT_READ_BIT |
                                          VK_ACCESS_SHADER_READ_BIT |
                                          VK_ACCESS_SHADER_WRITE_BIT |
                                          VK_ACCESS_COLOR_ATTACHMENT_READ_BIT |
                                          VK_ACCESS_COLOR_ATTACHMENT_WRITE_BIT |
                                          VK_ACCESS_DEPTH_STENCIL_ATTACHMENT_READ_BIT |
                                          VK_ACCESS_DEPTH_STENCIL_ATTACHMENT_WRITE_BIT |
                                          VK_ACCESS_TRANSFER_READ_BIT |
                                          VK_ACCESS_TRANSFER_WRITE_BIT |
                                          VK_ACCESS_HOST_READ_BIT |
                                          VK_ACCESS_HOST_WRITE_BIT;

            vkCmdPipelineBarrier(
                CommandBuffer,
                VK_PIPELINE_STAGE_ALL_COMMANDS_BIT, // srcStageMask
                VK_PIPELINE_STAGE_ALL_COMMANDS_BIT, // dstStageMask
                VkDependencyFlags.None,
                1, // memoryBarrierCount
                &memoryBarrier, // pMemoryBarriers
                0, null,
                0, null);
        }

        private VkBuffer getStagingBuffer(uint size)
        {
            lock (stagingLock)
            {
                VkBuffer ret = null;

                foreach (var buffer in availableStagingBuffers)
                {
                    if (buffer.SizeInBytes >= size)
                    {
                        ret = buffer;
                        availableStagingBuffers.Remove(buffer);
                        break;
                    }
                }

                if (ret == null)
                {
                    ret = (VkBuffer)gd.ResourceFactory.CreateBuffer(new BufferDescription(size, BufferUsage.Staging));
                    ret.Name = $"Staging Buffer (CommandList {name})";
                }

                currentStagingInfo.BuffersUsed.Add(ret);
                return ret;
            }
        }

        private void disposeCore()
        {
            if (!destroyed)
            {
                destroyed = true;
                vkDestroyCommandPool(gd.Device, pool, null);

                Debug.Assert(submittedStagingInfos.Count == 0);

                foreach (var buffer in availableStagingBuffers) buffer.Dispose();
            }
        }

        private StagingResourceInfo getStagingResourceInfo()
        {
            lock (stagingLock)
            {
                StagingResourceInfo ret;
                int availableCount = availableStagingInfos.Count;

                if (availableCount > 0)
                {
                    ret = availableStagingInfos[availableCount - 1];
                    availableStagingInfos.RemoveAt(availableCount - 1);
                }
                else
                    ret = new StagingResourceInfo();

                return ret;
            }
        }

        private void recycleStagingInfo(StagingResourceInfo info)
        {
            lock (stagingLock)
            {
                foreach (var buffer in info.BuffersUsed) availableStagingBuffers.Add(buffer);

                foreach (var rrc in info.Resources) rrc.Decrement();

                info.Clear();

                availableStagingInfos.Add(info);
            }
        }

        private protected override void ClearColorTargetCore(uint index, RgbaFloat clearColor)
        {
            var clearValue = new VkClearValue
            {
                color = new VkClearColorValue(clearColor.R, clearColor.G, clearColor.B, clearColor.A)
            };

            if (activeRenderPass != VkRenderPass.Null)
            {
                var clearAttachment = new VkClearAttachment
                {
                    colorAttachment = index,
                    aspectMask = VkImageAspectFlags.Color,
                    clearValue = clearValue
                };

                var colorTex = currentFramebuffer.ColorTargets[(int)index].Target;
                var clearRect = new VkClearRect
                {
                    baseArrayLayer = 0,
                    layerCount = 1,
                    rect = new VkRect2D(0, 0, colorTex.Width, colorTex.Height)
                };

                vkCmdClearAttachments(CommandBuffer, 1, ref clearAttachment, 1, ref clearRect);
            }
            else
            {
                // Queue up the clear value for the next RenderPass.
                clearValues[index] = clearValue;
                validColorClearValues[index] = true;
            }
        }

        private protected override void ClearDepthStencilCore(float depth, byte stencil)
        {
            var clearValue = new VkClearValue { depthStencil = new VkClearDepthStencilValue(depth, stencil) };

            if (activeRenderPass != VkRenderPass.Null)
            {
                var aspect = currentFramebuffer.DepthTarget is FramebufferAttachment depthAttachment && FormatHelpers.IsStencilFormat(depthAttachment.Target.Format)
                    ? VkImageAspectFlags.Depth | VkImageAspectFlags.Stencil
                    : VkImageAspectFlags.Depth;

                var clearAttachment = new VkClearAttachment
                {
                    aspectMask = aspect,
                    clearValue = clearValue
                };

                uint renderableWidth = currentFramebuffer.RenderableWidth;
                uint renderableHeight = currentFramebuffer.RenderableHeight;

                if (renderableWidth > 0 && renderableHeight > 0)
                {
                    var clearRect = new VkClearRect
                    {
                        baseArrayLayer = 0,
                        layerCount = 1,
                        rect = new VkRect2D(0, 0, renderableWidth, renderableHeight)
                    };

                    vkCmdClearAttachments(CommandBuffer, 1, ref clearAttachment, 1, ref clearRect);
                }
            }
            else
            {
                // Queue up the clear value for the next RenderPass.
                depthClearValue = clearValue;
            }
        }

        private protected override void DrawCore(uint vertexCount, uint instanceCount, uint vertexStart, uint instanceStart)
        {
            preDrawCommand();
            vkCmdDraw(CommandBuffer, vertexCount, instanceCount, vertexStart, instanceStart);
        }

        private protected override void DrawIndexedCore(uint indexCount, uint instanceCount, uint indexStart, int vertexOffset, uint instanceStart)
        {
            preDrawCommand();
            vkCmdDrawIndexed(CommandBuffer, indexCount, instanceCount, indexStart, vertexOffset, instanceStart);
        }

        private protected override void SetVertexBufferCore(uint index, DeviceBuffer buffer, uint offset)
        {
            var vkBuffer = Util.AssertSubtype<DeviceBuffer, VkBuffer>(buffer);
            var deviceBuffer = vkBuffer.DeviceBuffer;
            ulong offset64 = offset;
            vkCmdBindVertexBuffers(CommandBuffer, index, 1, ref deviceBuffer, ref offset64);
            currentStagingInfo.Resources.Add(vkBuffer.RefCount);
        }

        private protected override void SetIndexBufferCore(DeviceBuffer buffer, IndexFormat format, uint offset)
        {
            var vkBuffer = Util.AssertSubtype<DeviceBuffer, VkBuffer>(buffer);
            vkCmdBindIndexBuffer(CommandBuffer, vkBuffer.DeviceBuffer, offset, VkFormats.VdToVkIndexFormat(format));
            currentStagingInfo.Resources.Add(vkBuffer.RefCount);
        }

        private protected override void SetPipelineCore(Pipeline pipeline)
        {
            var vkPipeline = Util.AssertSubtype<Pipeline, VkPipeline>(pipeline);

            if (!pipeline.IsComputePipeline && currentGraphicsPipeline != pipeline)
            {
                Util.EnsureArrayMinimumSize(ref currentGraphicsResourceSets, vkPipeline.ResourceSetCount);
                clearSets(currentGraphicsResourceSets);
                Util.EnsureArrayMinimumSize(ref graphicsResourceSetsChanged, vkPipeline.ResourceSetCount);
                vkCmdBindPipeline(CommandBuffer, VkPipelineBindPoint.Graphics, vkPipeline.DevicePipeline);
                currentGraphicsPipeline = vkPipeline;
            }
            else if (pipeline.IsComputePipeline && currentComputePipeline != pipeline)
            {
                Util.EnsureArrayMinimumSize(ref currentComputeResourceSets, vkPipeline.ResourceSetCount);
                clearSets(currentComputeResourceSets);
                Util.EnsureArrayMinimumSize(ref computeResourceSetsChanged, vkPipeline.ResourceSetCount);
                vkCmdBindPipeline(CommandBuffer, VkPipelineBindPoint.Compute, vkPipeline.DevicePipeline);
                currentComputePipeline = vkPipeline;
            }

            currentStagingInfo.Resources.Add(vkPipeline.RefCount);
        }

        private protected override void GenerateMipmapsCore(Texture texture)
        {
            ensureNoRenderPass();
            var vkTex = Util.AssertSubtype<Texture, VkTexture>(texture);
            currentStagingInfo.Resources.Add(vkTex.RefCount);

            uint layerCount = vkTex.ArrayLayers;
            if ((vkTex.Usage & TextureUsage.Cubemap) != 0) layerCount *= 6;

            uint width = vkTex.Width;
            uint height = vkTex.Height;
            uint depth = vkTex.Depth;

            for (uint level = 1; level < vkTex.MipLevels; level++)
            {
                vkTex.TransitionImageLayoutNonmatching(CommandBuffer, level - 1, 1, 0, layerCount, VkImageLayout.TransferSrcOptimal);
                vkTex.TransitionImageLayoutNonmatching(CommandBuffer, level, 1, 0, layerCount, VkImageLayout.TransferDstOptimal);

                var deviceImage = vkTex.OptimalDeviceImage;
                uint mipWidth = Math.Max(width >> 1, 1);
                uint mipHeight = Math.Max(height >> 1, 1);
                uint mipDepth = Math.Max(depth >> 1, 1);

                VkImageBlit region = new VkImageBlit
                {
                    srcSubresource = new VkImageSubresourceLayers
                    {
                        aspectMask = VkImageAspectFlags.Color,
                        baseArrayLayer = 0,
                        layerCount = layerCount,
                        mipLevel = level - 1
                    },
                    srcOffsets_0 = new VkOffset3D(),
                    srcOffsets_1 = new VkOffset3D { x = (int)width, y = (int)height, z = (int)depth },
                    dstOffsets_0 = new VkOffset3D(),
                    dstSubresource = new VkImageSubresourceLayers
                    {
                        aspectMask = VkImageAspectFlags.Color,
                        baseArrayLayer = 0,
                        layerCount = layerCount,
                        mipLevel = level
                    },
                    dstOffsets_1 = new VkOffset3D { x = (int)mipWidth, y = (int)mipHeight, z = (int)mipDepth }
                };

                vkCmdBlitImage(
                    CommandBuffer,
                    deviceImage, VkImageLayout.TransferSrcOptimal,
                    deviceImage, VkImageLayout.TransferDstOptimal,
                    1, &region,
                    gd.GetFormatFilter(vkTex.VkFormat));

                width = mipWidth;
                height = mipHeight;
                depth = mipDepth;
            }

            if ((vkTex.Usage & TextureUsage.Sampled) != 0) vkTex.TransitionImageLayoutNonmatching(CommandBuffer, 0, vkTex.MipLevels, 0, layerCount, VkImageLayout.ShaderReadOnlyOptimal);
        }

        private protected override void PushDebugGroupCore(string name)
        {
            var func = gd.MarkerBegin;
            if (func == null) return;

            var markerInfo = VkDebugMarkerMarkerInfoEXT.New();

            int byteCount = Encoding.UTF8.GetByteCount(name);
            byte* utf8Ptr = stackalloc byte[byteCount + 1];
            fixed (char* namePtr = name) Encoding.UTF8.GetBytes(namePtr, name.Length, utf8Ptr, byteCount);
            utf8Ptr[byteCount] = 0;

            markerInfo.pMarkerName = utf8Ptr;

            func(CommandBuffer, &markerInfo);
        }

        private protected override void PopDebugGroupCore()
        {
            var func = gd.MarkerEnd;

            func?.Invoke(CommandBuffer);
        }

        private protected override void InsertDebugMarkerCore(string name)
        {
            var func = gd.MarkerInsert;
            if (func == null) return;

            var markerInfo = VkDebugMarkerMarkerInfoEXT.New();

            int byteCount = Encoding.UTF8.GetByteCount(name);
            byte* utf8Ptr = stackalloc byte[byteCount + 1];
            fixed (char* namePtr = name) Encoding.UTF8.GetBytes(namePtr, name.Length, utf8Ptr, byteCount);
            utf8Ptr[byteCount] = 0;

            markerInfo.pMarkerName = utf8Ptr;

            func(CommandBuffer, &markerInfo);
        }

        private class StagingResourceInfo
        {
            public List<VkBuffer> BuffersUsed { get; } = new List<VkBuffer>();
            public HashSet<ResourceRefCount> Resources { get; } = new HashSet<ResourceRefCount>();

            public void Clear()
            {
                BuffersUsed.Clear();
                Resources.Clear();
            }
        }
    }
}
