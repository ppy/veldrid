using System.Collections.Generic;
using System.Diagnostics;
using Veldrid.MetalBindings;

namespace Veldrid.MTL
{
    internal class MtlSwapchainFramebuffer : MtlFramebuffer
    {
        public override uint Width => colorTexture.Width;
        public override uint Height => colorTexture.Height;

        public override OutputDescription OutputDescription { get; }

        public override IReadOnlyList<FramebufferAttachment> ColorTargets => colorTargets;
        public override FramebufferAttachment? DepthTarget => depthTarget;
        private readonly MtlGraphicsDevice gd;
        private readonly MtlSwapchain parentSwapchain;
        private readonly PixelFormat colorFormat;

        private readonly PixelFormat? depthFormat;
        private readonly MtlSwapchainTexture colorTexture = new MtlSwapchainTexture();
        private MtlTexture depthTexture;

        private readonly FramebufferAttachment[] colorTargets;
        private FramebufferAttachment? depthTarget;

        public MtlSwapchainFramebuffer(
            MtlGraphicsDevice gd,
            MtlSwapchain parent,
            PixelFormat? depthFormat,
            PixelFormat colorFormat)
        {
            this.gd = gd;
            parentSwapchain = parent;
            this.colorFormat = colorFormat;

            OutputAttachmentDescription? depthAttachment = null;

            if (depthFormat != null)
            {
                this.depthFormat = depthFormat;
                depthAttachment = new OutputAttachmentDescription(depthFormat.Value);
            }

            var colorAttachment = new OutputAttachmentDescription(colorFormat);

            colorTargets = new[] { new FramebufferAttachment(colorTexture, 0) };

            OutputDescription = new OutputDescription(depthAttachment, colorAttachment);
        }

        #region Disposal

        public override void Dispose()
        {
            depthTexture?.Dispose();
            base.Dispose();
        }

        #endregion

        public void UpdateTextures(CAMetalDrawable drawable, CGSize size)
        {
            colorTexture.SetDrawable(drawable, size, colorFormat);

            if (depthFormat.HasValue && (size.width != depthTexture?.Width || size.height != depthTexture?.Height))
                recreateDepthTexture((uint)size.width, (uint)size.height);
        }

        public bool EnsureDrawableAvailable()
        {
            return parentSwapchain.EnsureDrawableAvailable();
        }

        private void recreateDepthTexture(uint width, uint height)
        {
            Debug.Assert(depthFormat.HasValue);
            depthTexture?.Dispose();

            depthTexture = Util.AssertSubtype<Texture, MtlTexture>(
                gd.ResourceFactory.CreateTexture(TextureDescription.Texture2D(
                    width, height, 1, 1, depthFormat.Value, TextureUsage.DepthStencil)));
            depthTarget = new FramebufferAttachment(depthTexture, 0);
        }
    }
}
