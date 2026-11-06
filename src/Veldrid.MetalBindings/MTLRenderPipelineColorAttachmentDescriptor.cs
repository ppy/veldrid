using System;
using System.Runtime.InteropServices;
using static Veldrid.MetalBindings.ObjectiveCRuntime;

namespace Veldrid.MetalBindings
{
    [StructLayout(LayoutKind.Sequential)]
    public struct MTLRenderPipelineColorAttachmentDescriptor
    {
        public readonly IntPtr NativePtr;

        public MTLRenderPipelineColorAttachmentDescriptor(IntPtr ptr) => NativePtr = ptr;

        public MTLPixelFormat pixelFormat
        {
            get => (MTLPixelFormat)nuint_objc_msgSend(NativePtr, Selectors.pixelFormat);
            set => objc_msgSend(NativePtr, Selectors.setPixelFormat, (nuint)value);
        }

        public MTLColorWriteMask writeMask
        {
            get => (MTLColorWriteMask)nuint_objc_msgSend(NativePtr, sel_writeMask);
            set => objc_msgSend(NativePtr, sel_setWriteMask, (nuint)value);
        }

        public Bool8 blendingEnabled
        {
            get => bool8_objc_msgSend(NativePtr, sel_isBlendingEnabled);
            set => objc_msgSend(NativePtr, sel_setBlendingEnabled, value);
        }

        public MTLBlendOperation alphaBlendOperation
        {
            get => (MTLBlendOperation)nuint_objc_msgSend(NativePtr, sel_alphaBlendOperation);
            set => objc_msgSend(NativePtr, sel_setAlphaBlendOperation, (nuint)value);
        }

        public MTLBlendOperation rgbBlendOperation
        {
            get => (MTLBlendOperation)nuint_objc_msgSend(NativePtr, sel_rgbBlendOperation);
            set => objc_msgSend(NativePtr, sel_setRGBBlendOperation, (nuint)value);
        }

        public MTLBlendFactor destinationAlphaBlendFactor
        {
            get => (MTLBlendFactor)nuint_objc_msgSend(NativePtr, sel_destinationAlphaBlendFactor);
            set => objc_msgSend(NativePtr, sel_setDestinationAlphaBlendFactor, (nuint)value);
        }

        public MTLBlendFactor destinationRGBBlendFactor
        {
            get => (MTLBlendFactor)nuint_objc_msgSend(NativePtr, sel_destinationRGBBlendFactor);
            set => objc_msgSend(NativePtr, sel_setDestinationRGBBlendFactor, (nuint)value);
        }

        public MTLBlendFactor sourceAlphaBlendFactor
        {
            get => (MTLBlendFactor)nuint_objc_msgSend(NativePtr, sel_sourceAlphaBlendFactor);
            set => objc_msgSend(NativePtr, sel_setSourceAlphaBlendFactor, (nuint)value);
        }

        public MTLBlendFactor sourceRGBBlendFactor
        {
            get => (MTLBlendFactor)nuint_objc_msgSend(NativePtr, sel_sourceRGBBlendFactor);
            set => objc_msgSend(NativePtr, sel_setSourceRGBBlendFactor, (nuint)value);
        }

        private static readonly Selector sel_isBlendingEnabled = "isBlendingEnabled";
        private static readonly Selector sel_setBlendingEnabled = "setBlendingEnabled:";
        private static readonly Selector sel_writeMask = "writeMask";
        private static readonly Selector sel_setWriteMask = "setWriteMask:";
        private static readonly Selector sel_alphaBlendOperation = "alphaBlendOperation";
        private static readonly Selector sel_setAlphaBlendOperation = "setAlphaBlendOperation:";
        private static readonly Selector sel_rgbBlendOperation = "rgbBlendOperation";
        private static readonly Selector sel_setRGBBlendOperation = "setRgbBlendOperation:";
        private static readonly Selector sel_destinationAlphaBlendFactor = "destinationAlphaBlendFactor";
        private static readonly Selector sel_setDestinationAlphaBlendFactor = "setDestinationAlphaBlendFactor:";
        private static readonly Selector sel_destinationRGBBlendFactor = "destinationRGBBlendFactor";
        private static readonly Selector sel_setDestinationRGBBlendFactor = "setDestinationRGBBlendFactor:";
        private static readonly Selector sel_sourceAlphaBlendFactor = "sourceAlphaBlendFactor";
        private static readonly Selector sel_setSourceAlphaBlendFactor = "setSourceAlphaBlendFactor:";
        private static readonly Selector sel_sourceRGBBlendFactor = "sourceRGBBlendFactor";
        private static readonly Selector sel_setSourceRGBBlendFactor = "setSourceRGBBlendFactor:";
    }
}
