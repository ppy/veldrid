using System;
using Veldrid.MetalBindings;

namespace Veldrid.MTL
{
    internal class MtlSwapchain : Swapchain
    {
        public override Framebuffer Framebuffer => framebuffer;

        public override bool IsDisposed => disposed;

        public CAMetalDrawable CurrentDrawable => drawable;

        public override bool SyncToVerticalBlank
        {
            get => syncToVerticalBlank;
            set
            {
                if (syncToVerticalBlank != value) setSyncToVerticalBlank(value);
            }
        }

        public override string Name { get; set; }
        private readonly MtlSwapchainFramebuffer framebuffer;
        private readonly MtlGraphicsDevice gd;
        private CAMetalLayer metalLayer;
        private UIView uiView; // Valid only when a UIViewSwapchainSource is used.
        private bool syncToVerticalBlank;
        private bool disposed;

        private CAMetalDrawable drawable;

        public MtlSwapchain(MtlGraphicsDevice gd, ref SwapchainDescription description)
        {
            this.gd = gd;
            syncToVerticalBlank = description.SyncToVerticalBlank;

            uint width;
            uint height;

            var source = description.Source;

            if (source is NSWindowSwapchainSource nsWindowSource)
            {
                var nswindow = new NSWindow(nsWindowSource.NSWindow);
                var contentView = nswindow.contentView;
                var windowContentSize = contentView.frame.size;
                width = (uint)windowContentSize.width;
                height = (uint)windowContentSize.height;

                if (!CAMetalLayer.TryCast(contentView.layer, out metalLayer))
                {
                    metalLayer = CAMetalLayer.New();
                    contentView.wantsLayer = true;
                    contentView.layer = metalLayer.NativePtr;
                }
            }
            else if (source is NSViewSwapchainSource nsViewSource)
            {
                var contentView = new NSView(nsViewSource.NSView);
                var windowContentSize = contentView.frame.size;
                width = (uint)windowContentSize.width;
                height = (uint)windowContentSize.height;

                if (!CAMetalLayer.TryCast(contentView.layer, out metalLayer))
                {
                    metalLayer = CAMetalLayer.New();
                    contentView.wantsLayer = true;
                    contentView.layer = metalLayer.NativePtr;
                }
            }
            else if (source is UIViewSwapchainSource uiViewSource)
            {
                uiView = new UIView(uiViewSource.UIView);
                var viewSize = uiView.frame.size;
                width = (uint)viewSize.width;
                height = (uint)viewSize.height;

                if (!CAMetalLayer.TryCast(uiView.layer, out metalLayer))
                {
                    metalLayer = CAMetalLayer.New();
                    metalLayer.frame = uiView.frame;
                    metalLayer.opaque = true;
                    uiView.layer.addSublayer(metalLayer.NativePtr);
                }
            }
            else
                throw new VeldridException("A Metal Swapchain can only be created from an NSWindow, NSView, or UIView.");

            var format = description.ColorSrgb
                ? PixelFormat.B8G8R8A8UNormSRgb
                : PixelFormat.B8G8R8A8UNorm;

            metalLayer.device = this.gd.Device;
            metalLayer.pixelFormat = MtlFormats.VdToMtlPixelFormat(format, false);
            metalLayer.framebufferOnly = true;
            metalLayer.drawableSize = new CGSize(width, height);

            // Prefer triple buffering on iOS for maximum performance, and double buffering on macOS for minimum latency.
            metalLayer.maximumDrawableCount = gd.MetalFeatures.IsMacOS ? 2u : 3u;

            setSyncToVerticalBlank(syncToVerticalBlank);

            framebuffer = new MtlSwapchainFramebuffer(
                gd,
                this,
                description.DepthFormat,
                format);

            getNextDrawable();
        }

        #region Disposal

        public override void Dispose()
        {
            if (drawable.NativePtr != IntPtr.Zero) ObjectiveCRuntime.release(drawable.NativePtr);
            framebuffer.Dispose();
            ObjectiveCRuntime.release(metalLayer.NativePtr);

            disposed = true;
        }

        #endregion

        public override void Resize(uint width, uint height)
        {
            if (uiView.NativePtr != IntPtr.Zero)
                metalLayer.frame = uiView.frame;

            metalLayer.drawableSize = new CGSize(width, height);

            getNextDrawable();
        }

        public bool EnsureDrawableAvailable()
        {
            return !drawable.IsNull || getNextDrawable();
        }

        public void InvalidateDrawable()
        {
            ObjectiveCRuntime.release(drawable.NativePtr);
            drawable = default;
        }

        private bool getNextDrawable()
        {
            if (!drawable.IsNull) ObjectiveCRuntime.release(drawable.NativePtr);

            using (NSAutoreleasePool.Begin())
            {
                drawable = metalLayer.nextDrawable();

                if (!drawable.IsNull)
                {
                    ObjectiveCRuntime.retain(drawable.NativePtr);
                    framebuffer.UpdateTextures(drawable, metalLayer.drawableSize);
                    return true;
                }

                return false;
            }
        }

        private void setSyncToVerticalBlank(bool value)
        {
            syncToVerticalBlank = value;

            if (gd.MetalFeatures.MaxFeatureSet == MTLFeatureSet.macOS_GPUFamily1_v3
                || gd.MetalFeatures.MaxFeatureSet == MTLFeatureSet.macOS_GPUFamily1_v4
                || gd.MetalFeatures.MaxFeatureSet == MTLFeatureSet.macOS_GPUFamily2_v1)
                metalLayer.displaySyncEnabled = value;
        }
    }
}
