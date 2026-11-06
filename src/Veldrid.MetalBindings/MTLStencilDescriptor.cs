using System;
using static Veldrid.MetalBindings.ObjectiveCRuntime;

namespace Veldrid.MetalBindings
{
    public struct MTLStencilDescriptor
    {
        public readonly IntPtr NativePtr;

        public MTLStencilOperation stencilFailureOperation
        {
            get => (MTLStencilOperation)nuint_objc_msgSend(NativePtr, sel_stencilFailureOperation);
            set => objc_msgSend(NativePtr, sel_setStencilFailureOperation, (nuint)value);
        }

        public MTLStencilOperation depthFailureOperation
        {
            get => (MTLStencilOperation)nuint_objc_msgSend(NativePtr, sel_depthFailureOperation);
            set => objc_msgSend(NativePtr, sel_setDepthFailureOperation, (nuint)value);
        }

        public MTLStencilOperation depthStencilPassOperation
        {
            get => (MTLStencilOperation)nuint_objc_msgSend(NativePtr, sel_depthStencilPassOperation);
            set => objc_msgSend(NativePtr, sel_setDepthStencilPassOperation, (nuint)value);
        }

        public MTLCompareFunction stencilCompareFunction
        {
            get => (MTLCompareFunction)nuint_objc_msgSend(NativePtr, sel_stencilCompareFunction);
            set => objc_msgSend(NativePtr, sel_setStencilCompareFunction, (nuint)value);
        }

        public uint readMask
        {
            get => uint_objc_msgSend(NativePtr, sel_readMask);
            set => objc_msgSend(NativePtr, sel_setReadMask, value);
        }

        public uint writeMask
        {
            get => uint_objc_msgSend(NativePtr, sel_writeMask);
            set => objc_msgSend(NativePtr, sel_setWriteMask, value);
        }

        private static readonly Selector sel_depthFailureOperation = "depthFailureOperation";
        private static readonly Selector sel_stencilFailureOperation = "stencilFailureOperation";
        private static readonly Selector sel_setStencilFailureOperation = "setStencilFailureOperation:";
        private static readonly Selector sel_setDepthFailureOperation = "setDepthFailureOperation:";
        private static readonly Selector sel_depthStencilPassOperation = "depthStencilPassOperation";
        private static readonly Selector sel_setDepthStencilPassOperation = "setDepthStencilPassOperation:";
        private static readonly Selector sel_stencilCompareFunction = "stencilCompareFunction";
        private static readonly Selector sel_setStencilCompareFunction = "setStencilCompareFunction:";
        private static readonly Selector sel_readMask = "readMask";
        private static readonly Selector sel_setReadMask = "setReadMask:";
        private static readonly Selector sel_writeMask = "writeMask";
        private static readonly Selector sel_setWriteMask = "setWriteMask:";
    }
}
