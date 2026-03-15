using System.Collections;
using System.Collections.Generic;

namespace Veldrid.OpenGL
{
    internal class OpenGLExtensions : IReadOnlyCollection<string>
    {
        public int Count => extensions.Count;

        public readonly bool ArbDirectStateAccess;
        public readonly bool ArbMultiBind;
        public readonly bool ArbTextureView;
        public readonly bool ArbDebugOutput;
        public readonly bool KhrDebug;
        public readonly bool ArbViewportArray;
        public readonly bool ArbClipControl;
        public readonly bool ExtSRGBWriteControl;
        public readonly bool ExtDebugMarker;
        public readonly bool ArbGpuShaderFp64;
        public readonly bool ArbUniformBufferObject;

        // Differs between GL / GLES
        public readonly bool TextureStorage;
        public readonly bool TextureStorageMultisample;

        public readonly bool CopyImage;
        public readonly bool ComputeShaders;
        public readonly bool TessellationShader;
        public readonly bool GeometryShader;
        public readonly bool DrawElementsBaseVertex;
        public readonly bool IndependentBlend;
        public readonly bool DrawIndirect;
        public readonly bool MultiDrawIndirect;
        public readonly bool StorageBuffers;
        public readonly bool AnisotropicFilter;
        public readonly bool OesPackedDepthStencil;
        private readonly HashSet<string> extensions;
        private readonly GraphicsBackend backend;
        private readonly int major;
        private readonly int minor;

        internal OpenGLExtensions(HashSet<string> extensions, GraphicsBackend backend, int major, int minor)
        {
            this.extensions = extensions;
            this.backend = backend;
            this.major = major;
            this.minor = minor;

            TextureStorage = IsExtensionSupported("GL_ARB_texture_storage") // OpenGL 4.2 / 4.3 (multisampled)
                             || GLESVersion(3, 0);
            TextureStorageMultisample = IsExtensionSupported("GL_ARB_texture_storage_multisample")
                                        || GLESVersion(3, 1);
            ArbDirectStateAccess = IsExtensionSupported("GL_ARB_direct_state_access");
            ArbMultiBind = IsExtensionSupported("GL_ARB_multi_bind");
            ArbTextureView = GLVersion(4, 3) || IsExtensionSupported("GL_ARB_texture_view") // OpenGL 4.3
                                             || IsExtensionSupported("GL_OES_texture_view");
            CopyImage = IsExtensionSupported("GL_ARB_copy_image")
                        || GLESVersion(3, 2)
                        || IsExtensionSupported("GL_OES_copy_image")
                        || IsExtensionSupported("GL_EXT_copy_image");
            ArbDebugOutput = IsExtensionSupported("GL_ARB_debug_output");
            KhrDebug = IsExtensionSupported("GL_KHR_debug");

            ComputeShaders = IsExtensionSupported("GL_ARB_compute_shader") || GLESVersion(3, 1);

            ArbViewportArray = IsExtensionSupported("GL_ARB_viewport_array") || GLVersion(4, 1);
            TessellationShader = IsExtensionSupported("GL_ARB_tessellation_shader") || GLVersion(4, 0)
                                                                                    || IsExtensionSupported("GL_OES_tessellation_shader");
            GeometryShader = IsExtensionSupported("GL_ARB_geometry_shader4") || GLVersion(3, 2)
                                                                             || IsExtensionSupported("OES_geometry_shader");
            DrawElementsBaseVertex = GLVersion(3, 2)
                                     || IsExtensionSupported("GL_ARB_draw_elements_base_vertex")
                                     || GLESVersion(3, 2)
                                     || IsExtensionSupported("GL_OES_draw_elements_base_vertex");
            IndependentBlend = GLVersion(4, 0) || GLESVersion(3, 2);

            DrawIndirect = GLVersion(4, 0) || IsExtensionSupported("GL_ARB_draw_indirect")
                                           || GLESVersion(3, 1);
            MultiDrawIndirect = GLVersion(4, 3) || IsExtensionSupported("GL_ARB_multi_draw_indirect")
                                                || IsExtensionSupported("GL_EXT_multi_draw_indirect");

            StorageBuffers = GLVersion(4, 3) || IsExtensionSupported("GL_ARB_shader_storage_buffer_object")
                                             || GLESVersion(3, 1);

            ArbClipControl = GLVersion(4, 5) || IsExtensionSupported("GL_ARB_clip_control");
            ExtSRGBWriteControl = this.backend == GraphicsBackend.OpenGLES && IsExtensionSupported("GL_EXT_sRGB_write_control");
            ExtDebugMarker = this.backend == GraphicsBackend.OpenGLES && IsExtensionSupported("GL_EXT_debug_marker");

            ArbGpuShaderFp64 = GLVersion(4, 0) || IsExtensionSupported("GL_ARB_gpu_shader_fp64");

            ArbUniformBufferObject = IsExtensionSupported("GL_ARB_uniform_buffer_object");

            AnisotropicFilter = IsExtensionSupported("GL_EXT_texture_filter_anisotropic") || IsExtensionSupported("GL_ARB_texture_filter_anisotropic");

            // D24_UNorm_S8_UInt (GL_DEPTH24_STENCIL8) is core in GL 3.0+ and GLES 3.0+.
            // On desktop GL 2.x, GL_ARB_framebuffer_object provides it.
            // On GLES 2.x, it requires the GL_OES_packed_depth_stencil extension.
            OesPackedDepthStencil = GLVersion(3, 0) || GLESVersion(3, 0)
                                    || IsExtensionSupported("GL_ARB_framebuffer_object")
                                    || IsExtensionSupported("GL_OES_packed_depth_stencil");
        }

        /// <summary>
        ///     Returns a value indicating whether the given extension is supported.
        /// </summary>
        /// <param name="extension">The name of the extensions. For example, "</param>
        /// <returns></returns>
        public bool IsExtensionSupported(string extension)
        {
            return extensions.Contains(extension);
        }

        public bool GLVersion(int major, int minor)
        {
            if (backend == GraphicsBackend.OpenGL)
            {
                if (this.major > major)
                    return true;

                return this.major == major && this.minor >= minor;
            }

            return false;
        }

        public bool GLESVersion(int major, int minor)
        {
            if (backend == GraphicsBackend.OpenGLES)
            {
                if (this.major > major)
                    return true;

                return this.major == major && this.minor >= minor;
            }

            return false;
        }

        public IEnumerator<string> GetEnumerator()
        {
            return extensions.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }
}
