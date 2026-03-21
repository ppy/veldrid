using System;
using System.Collections.Generic;
using System.Diagnostics;
using Veldrid.MetalBindings;

namespace Veldrid.MTL
{
    internal class MtlPipeline : Pipeline
    {
        public MTLRenderPipelineState RenderPipelineState { get; }
        public MTLComputePipelineState ComputePipelineState { get; }
        public MTLPrimitiveType PrimitiveType { get; }
        public new MtlResourceLayout[] ResourceLayouts { get; }
        public ResourceBindingModel ResourceBindingModel { get; }
        public uint VertexBufferCount { get; }
        public uint NonVertexBufferCount { get; }
        public MTLCullMode CullMode { get; }
        public MTLWinding FrontFace { get; }
        public MTLTriangleFillMode FillMode { get; }
        public MTLDepthStencilState DepthStencilState { get; }
        public MTLDepthClipMode DepthClipMode { get; }
        public override bool IsComputePipeline { get; }
        public bool ScissorTestEnabled { get; }
        public MTLSize ThreadsPerThreadgroup { get; } = new MTLSize(1, 1, 1);
        public bool HasStencil { get; }
        public uint StencilReference { get; }
        public RgbaFloat BlendColor { get; }
        public override bool IsDisposed => disposed;
        public override string Name { get; set; }

        private bool disposed;
        private List<MTLFunction> specializedFunctions;

        public MtlPipeline(ref GraphicsPipelineDescription description, MtlGraphicsDevice gd)
            : base(ref description)
        {
            PrimitiveType = MtlFormats.VdToMtlPrimitiveTopology(description.PrimitiveTopology);
            ResourceLayouts = new MtlResourceLayout[description.ResourceLayouts.Length];
            NonVertexBufferCount = 0;

            for (int i = 0; i < ResourceLayouts.Length; i++)
            {
                ResourceLayouts[i] = Util.AssertSubtype<ResourceLayout, MtlResourceLayout>(description.ResourceLayouts[i]);
                NonVertexBufferCount += ResourceLayouts[i].BufferCount;
            }

            ResourceBindingModel = description.ResourceBindingModel ?? gd.ResourceBindingModel;

            CullMode = MtlFormats.VdToMtlCullMode(description.RasterizerState.CullMode);
            FrontFace = MtlFormats.VdVoMtlFrontFace(description.RasterizerState.FrontFace);
            FillMode = MtlFormats.VdToMtlFillMode(description.RasterizerState.FillMode);
            ScissorTestEnabled = description.RasterizerState.ScissorTestEnabled;

            var mtlDesc = MTLRenderPipelineDescriptor.New();

            foreach (var shader in description.ShaderSet.Shaders)
            {
                var mtlShader = Util.AssertSubtype<Shader, MtlShader>(shader);
                MTLFunction specializedFunction;

                if (mtlShader.HasFunctionConstants)
                {
                    // Need to create specialized MTLFunction.
                    var constantValues = createConstantValues(description.ShaderSet.Specializations);
                    specializedFunction = mtlShader.Library.newFunctionWithNameConstantValues(mtlShader.EntryPoint, constantValues);
                    addSpecializedFunction(specializedFunction);
                    ObjectiveCRuntime.release(constantValues.NativePtr);

                    Debug.Assert(specializedFunction.NativePtr != IntPtr.Zero, "Failed to create specialized MTLFunction");
                }
                else
                    specializedFunction = mtlShader.Function;

                if (shader.Stage == ShaderStages.Vertex)
                    mtlDesc.vertexFunction = specializedFunction;
                else if (shader.Stage == ShaderStages.Fragment) mtlDesc.fragmentFunction = specializedFunction;
            }

            // Vertex layouts
            var vdVertexLayouts = description.ShaderSet.VertexLayouts;
            var vertexDescriptor = mtlDesc.vertexDescriptor;

            for (uint i = 0; i < vdVertexLayouts.Length; i++)
            {
                uint layoutIndex = ResourceBindingModel == ResourceBindingModel.Improved
                    ? NonVertexBufferCount + i
                    : i;
                var mtlLayout = vertexDescriptor.layouts[layoutIndex];
                mtlLayout.stride = vdVertexLayouts[i].Stride;
                uint stepRate = vdVertexLayouts[i].InstanceStepRate;
                mtlLayout.stepFunction = stepRate == 0 ? MTLVertexStepFunction.PerVertex : MTLVertexStepFunction.PerInstance;
                mtlLayout.stepRate = Math.Max(1, stepRate);
            }

            uint element = 0;

            for (uint i = 0; i < vdVertexLayouts.Length; i++)
            {
                uint offset = 0;
                var vdDesc = vdVertexLayouts[i];

                for (uint j = 0; j < vdDesc.Elements.Length; j++)
                {
                    var elementDesc = vdDesc.Elements[j];
                    var mtlAttribute = vertexDescriptor.attributes[element];
                    mtlAttribute.bufferIndex = ResourceBindingModel == ResourceBindingModel.Improved
                        ? NonVertexBufferCount + i
                        : i;
                    mtlAttribute.format = MtlFormats.VdToMtlVertexFormat(elementDesc.Format);
                    mtlAttribute.offset = elementDesc.Offset != 0 ? elementDesc.Offset : (UIntPtr)offset;
                    offset += FormatSizeHelpers.GetSizeInBytes(elementDesc.Format);
                    element += 1;
                }
            }

            VertexBufferCount = (uint)vdVertexLayouts.Length;

            // Outputs
            var outputs = description.Outputs;
            var blendStateDesc = description.BlendState;
            BlendColor = blendStateDesc.BlendFactor;

            if (outputs.SampleCount != TextureSampleCount.Count1) mtlDesc.sampleCount = FormatHelpers.GetSampleCountUInt32(outputs.SampleCount);

            if (outputs.DepthAttachment != null)
            {
                var depthFormat = outputs.DepthAttachment.Value.Format;
                var mtlDepthFormat = MtlFormats.VdToMtlPixelFormat(depthFormat, true);
                mtlDesc.depthAttachmentPixelFormat = mtlDepthFormat;

                if (FormatHelpers.IsStencilFormat(depthFormat))
                {
                    HasStencil = true;
                    mtlDesc.stencilAttachmentPixelFormat = mtlDepthFormat;
                }
            }

            for (uint i = 0; i < outputs.ColorAttachments.Length; i++)
            {
                var attachmentBlendDesc = blendStateDesc.AttachmentStates[i];
                var colorDesc = mtlDesc.colorAttachments[i];
                colorDesc.pixelFormat = MtlFormats.VdToMtlPixelFormat(outputs.ColorAttachments[i].Format, false);
                colorDesc.blendingEnabled = attachmentBlendDesc.BlendEnabled;
                colorDesc.writeMask = MtlFormats.VdToMtlColorWriteMask(attachmentBlendDesc.ColorWriteMask.GetOrDefault());
                colorDesc.alphaBlendOperation = MtlFormats.VdToMtlBlendOp(attachmentBlendDesc.AlphaFunction);
                colorDesc.sourceAlphaBlendFactor = MtlFormats.VdToMtlBlendFactor(attachmentBlendDesc.SourceAlphaFactor);
                colorDesc.destinationAlphaBlendFactor = MtlFormats.VdToMtlBlendFactor(attachmentBlendDesc.DestinationAlphaFactor);

                colorDesc.rgbBlendOperation = MtlFormats.VdToMtlBlendOp(attachmentBlendDesc.ColorFunction);
                colorDesc.sourceRGBBlendFactor = MtlFormats.VdToMtlBlendFactor(attachmentBlendDesc.SourceColorFactor);
                colorDesc.destinationRGBBlendFactor = MtlFormats.VdToMtlBlendFactor(attachmentBlendDesc.DestinationColorFactor);
            }

            mtlDesc.alphaToCoverageEnabled = blendStateDesc.AlphaToCoverageEnabled;

            RenderPipelineState = gd.Device.newRenderPipelineStateWithDescriptor(mtlDesc);
            ObjectiveCRuntime.release(mtlDesc.NativePtr);

            if (description.Outputs.DepthAttachment != null)
            {
                var depthDescriptor = MTLUtil.AllocInit<MTLDepthStencilDescriptor>(
                    nameof(MTLDepthStencilDescriptor));
                // Metal has no explicit depth-test enable flag on the depth-stencil descriptor.
                // When depth testing is disabled we must force "always" to avoid unintentionally
                // rejecting all fragments if DepthComparison is left at its default (Never).
                depthDescriptor.depthCompareFunction = MtlFormats.VdToMtlCompareFunction(
                    description.DepthStencilState.DepthTestEnabled
                        ? description.DepthStencilState.DepthComparison
                        : ComparisonKind.Always);
                depthDescriptor.depthWriteEnabled = description.DepthStencilState.DepthWriteEnabled;

                bool stencilEnabled = description.DepthStencilState.StencilTestEnabled;

                if (stencilEnabled)
                {
                    StencilReference = description.DepthStencilState.StencilReference;

                    var vdFrontDesc = description.DepthStencilState.StencilFront;
                    var front = MTLUtil.AllocInit<MTLStencilDescriptor>(nameof(MTLStencilDescriptor));
                    front.readMask = description.DepthStencilState.StencilReadMask;
                    front.writeMask = description.DepthStencilState.StencilWriteMask;
                    front.depthFailureOperation = MtlFormats.VdToMtlStencilOperation(vdFrontDesc.DepthFail);
                    front.stencilFailureOperation = MtlFormats.VdToMtlStencilOperation(vdFrontDesc.Fail);
                    front.depthStencilPassOperation = MtlFormats.VdToMtlStencilOperation(vdFrontDesc.Pass);
                    front.stencilCompareFunction = MtlFormats.VdToMtlCompareFunction(vdFrontDesc.Comparison);
                    depthDescriptor.frontFaceStencil = front;

                    var vdBackDesc = description.DepthStencilState.StencilBack;
                    var back = MTLUtil.AllocInit<MTLStencilDescriptor>(nameof(MTLStencilDescriptor));
                    back.readMask = description.DepthStencilState.StencilReadMask;
                    back.writeMask = description.DepthStencilState.StencilWriteMask;
                    back.depthFailureOperation = MtlFormats.VdToMtlStencilOperation(vdBackDesc.DepthFail);
                    back.stencilFailureOperation = MtlFormats.VdToMtlStencilOperation(vdBackDesc.Fail);
                    back.depthStencilPassOperation = MtlFormats.VdToMtlStencilOperation(vdBackDesc.Pass);
                    back.stencilCompareFunction = MtlFormats.VdToMtlCompareFunction(vdBackDesc.Comparison);
                    depthDescriptor.backFaceStencil = back;

                    ObjectiveCRuntime.release(front.NativePtr);
                    ObjectiveCRuntime.release(back.NativePtr);
                }

                DepthStencilState = gd.Device.newDepthStencilStateWithDescriptor(depthDescriptor);
                ObjectiveCRuntime.release(depthDescriptor.NativePtr);
            }

            DepthClipMode = description.DepthStencilState.DepthTestEnabled ? MTLDepthClipMode.Clip : MTLDepthClipMode.Clamp;
        }

        public MtlPipeline(ref ComputePipelineDescription description, MtlGraphicsDevice gd)
            : base(ref description)
        {
            IsComputePipeline = true;
            ResourceLayouts = new MtlResourceLayout[description.ResourceLayouts.Length];

            for (int i = 0; i < ResourceLayouts.Length; i++) ResourceLayouts[i] = Util.AssertSubtype<ResourceLayout, MtlResourceLayout>(description.ResourceLayouts[i]);

            ThreadsPerThreadgroup = new MTLSize(
                description.ThreadGroupSizeX,
                description.ThreadGroupSizeY,
                description.ThreadGroupSizeZ);

            var mtlDesc = MTLUtil.AllocInit<MTLComputePipelineDescriptor>(
                nameof(MTLComputePipelineDescriptor));
            var mtlShader = Util.AssertSubtype<Shader, MtlShader>(description.ComputeShader);
            MTLFunction specializedFunction;

            if (mtlShader.HasFunctionConstants)
            {
                // Need to create specialized MTLFunction.
                var constantValues = createConstantValues(description.Specializations);
                specializedFunction = mtlShader.Library.newFunctionWithNameConstantValues(mtlShader.EntryPoint, constantValues);
                addSpecializedFunction(specializedFunction);
                ObjectiveCRuntime.release(constantValues.NativePtr);

                Debug.Assert(specializedFunction.NativePtr != IntPtr.Zero, "Failed to create specialized MTLFunction");
            }
            else
                specializedFunction = mtlShader.Function;

            mtlDesc.computeFunction = specializedFunction;
            var buffers = mtlDesc.buffers;
            uint bufferIndex = 0;

            foreach (var layout in ResourceLayouts)
            {
                foreach (var rle in layout.Description.Elements)
                {
                    var kind = rle.Kind;

                    if (kind == ResourceKind.UniformBuffer
                        || kind == ResourceKind.StructuredBufferReadOnly)
                    {
                        var bufferDesc = buffers[bufferIndex];
                        bufferDesc.mutability = MTLMutability.Immutable;
                        bufferIndex += 1;
                    }
                    else if (kind == ResourceKind.StructuredBufferReadWrite)
                    {
                        var bufferDesc = buffers[bufferIndex];
                        bufferDesc.mutability = MTLMutability.Mutable;
                        bufferIndex += 1;
                    }
                }
            }

            ComputePipelineState = gd.Device.newComputePipelineStateWithDescriptor(mtlDesc);
            ObjectiveCRuntime.release(mtlDesc.NativePtr);
        }

        #region Disposal

        public override void Dispose()
        {
            if (!disposed)
            {
                if (RenderPipelineState.NativePtr != IntPtr.Zero)
                    ObjectiveCRuntime.release(RenderPipelineState.NativePtr);

                if (DepthStencilState.NativePtr != IntPtr.Zero)
                    ObjectiveCRuntime.release(DepthStencilState.NativePtr);

                if (ComputePipelineState.NativePtr != IntPtr.Zero)
                    ObjectiveCRuntime.release(ComputePipelineState.NativePtr);

                if (specializedFunctions != null)
                {
                    foreach (var function in specializedFunctions) ObjectiveCRuntime.release(function.NativePtr);

                    specializedFunctions.Clear();
                }

                disposed = true;
            }
        }

        #endregion

        private unsafe MTLFunctionConstantValues createConstantValues(SpecializationConstant[] specializations)
        {
            var ret = MTLFunctionConstantValues.New();

            if (specializations != null)
            {
                foreach (var sc in specializations)
                {
                    var mtlType = MtlFormats.VdVoMtlShaderConstantType(sc.Type);
                    ret.setConstantValuetypeatIndex(&sc.Data, mtlType, sc.ID);
                }
            }

            return ret;
        }

        private void addSpecializedFunction(MTLFunction function)
        {
            specializedFunctions ??= new List<MTLFunction>();
            specializedFunctions.Add(function);
        }
    }
}
