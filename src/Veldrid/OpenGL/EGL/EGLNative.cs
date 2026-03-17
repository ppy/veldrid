using System;
using System.Runtime.InteropServices;

namespace Veldrid.OpenGL.EGL
{
    internal static unsafe class EglNative
    {
        public const int EGL_DRAW = 0x3059;
        public const int EGL_READ = 0x305A;
        public const int EGL_RED_SIZE = 0x3024;
        public const int EGL_GREEN_SIZE = 0x3023;
        public const int EGL_BLUE_SIZE = 0x3022;
        public const int EGL_ALPHA_SIZE = 0x3021;
        public const int EGL_DEPTH_SIZE = 0x3025;
        public const int EGL_STENCIL_SIZE = 0x3026;
        public const int EGL_SURFACE_TYPE = 0x3033;
        public const int EGL_WINDOW_BIT = 0x0004;
        public const int EGL_OPENGL_ES_BIT = 0x0001;
        public const int EGL_OPENGL_ES2_BIT = 0x0004;
        public const int EGL_OPENGL_ES3_BIT = 0x00000040;
        public const int EGL_RENDERABLE_TYPE = 0x3040;
        public const int EGL_NONE = 0x3038;
        public const int EGL_NATIVE_VISUAL_ID = 0x302E;
        public const int EGL_CONTEXT_CLIENT_VERSION = 0x3098;
        private const string lib_name = "libEGL.so";

        [DllImport(lib_name)]
        public static extern EglError eglGetError();

        [DllImport(lib_name)]
        public static extern IntPtr eglGetCurrentContext();

        [DllImport(lib_name)]
        public static extern int eglDestroyContext(IntPtr display, IntPtr context);

        [DllImport(lib_name)]
        public static extern int eglMakeCurrent(IntPtr display, IntPtr draw, IntPtr read, IntPtr context);

        [DllImport(lib_name)]
        public static extern int eglChooseConfig(IntPtr display, int* attribList, IntPtr* configs, int configSize, int* numConfig);

        [DllImport(lib_name)]
        public static extern IntPtr eglGetProcAddress(string name);

        [DllImport(lib_name)]
        public static extern IntPtr eglGetCurrentDisplay();

        [DllImport(lib_name)]
        public static extern IntPtr eglGetDisplay(IntPtr nativeDisplay);

        [DllImport(lib_name)]
        public static extern IntPtr eglGetCurrentSurface(int readdraw);

        [DllImport(lib_name)]
        public static extern int eglInitialize(IntPtr display, int* major, int* minor);

        [DllImport(lib_name)]
        public static extern IntPtr eglCreateWindowSurface(
            IntPtr display,
            IntPtr config,
            IntPtr nativeWindow,
            int* attribList);

        [DllImport(lib_name)]
        public static extern IntPtr eglCreateContext(IntPtr display,
                                                     IntPtr config,
                                                     IntPtr shareContext,
                                                     int* attribList);

        [DllImport(lib_name)]
        public static extern int eglSwapBuffers(IntPtr display, IntPtr surface);

        [DllImport(lib_name)]
        public static extern int eglSwapInterval(IntPtr display, int value);

        [DllImport(lib_name)]
        public static extern int eglGetConfigAttrib(IntPtr display, IntPtr config, int attribute, int* value);
    }

    internal enum EglError
    {
        Success = 0x3000,
        NotInitialized = 0x3001,
        BadAccess = 0x3002,
        BadAlloc = 0x3003,
        BadAttribute = 0x3004,
        BadConfig = 0x3005,
        BadContext = 0x3006,
        BadCurrentSurface = 0x3007,
        BadDisplay = 0x3008,
        BadMatch = 0x3009,
        BadNativePixmap = 0x300A,
        BadNativeWindow = 0x300B,
        BadParameter = 0x300C,
        BadSurface = 0x300D,
        ContextLost = 0x300E
    }
}
