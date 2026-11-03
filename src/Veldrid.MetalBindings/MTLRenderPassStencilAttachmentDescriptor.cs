using System;
using static Veldrid.MetalBindings.ObjectiveCRuntime;

namespace Veldrid.MetalBindings
{
    public struct MTLRenderPassStencilAttachmentDescriptor
    {
        public readonly IntPtr NativePtr;

        public MTLTexture texture
        {
            get => objc_msgSend<MTLTexture>(NativePtr, Selectors.texture);
            set => objc_msgSend(NativePtr, Selectors.setTexture, value.NativePtr);
        }

        public MTLLoadAction loadAction
        {
            get => (MTLLoadAction)nuint_objc_msgSend(NativePtr, Selectors.loadAction);
            set => objc_msgSend(NativePtr, Selectors.setLoadAction, (nuint)value);
        }

        public MTLStoreAction storeAction
        {
            get => (MTLStoreAction)nuint_objc_msgSend(NativePtr, Selectors.storeAction);
            set => objc_msgSend(NativePtr, Selectors.setStoreAction, (nuint)value);
        }

        public uint clearStencil
        {
            get => uint_objc_msgSend(NativePtr, sel_clearStencil);
            set => objc_msgSend(NativePtr, sel_setClearStencil, value);
        }

        public UIntPtr slice
        {
            get => UIntPtr_objc_msgSend(NativePtr, Selectors.slice);
            set => objc_msgSend(NativePtr, Selectors.setSlice, value);
        }

        private static readonly Selector sel_clearStencil = "clearStencil";
        private static readonly Selector sel_setClearStencil = "setClearStencil:";
    }
}
