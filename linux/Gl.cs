// OpenGL 3.3 over libGL + GLFW's proc loader. Legacy entry points are exported by libGL;
// everything newer is resolved with glfwGetProcAddress once a context is current.
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace ScreenWorm
{
    static class GL
    {
        const string Lib = "libGL.so.1";

        public const uint COLOR_BUFFER_BIT = 0x4000, POINTS = 0, TRIANGLES = 4, TRIANGLE_STRIP = 5, BLEND = 0xBE2, ONE = 1, ZERO = 0, SRC_ALPHA = 0x302, ONE_MINUS_SRC_ALPHA = 0x303,
            TEXTURE_2D = 0xDE1, TEXTURE_MIN_FILTER = 0x2801, TEXTURE_MAG_FILTER = 0x2800, TEXTURE_WRAP_S = 0x2802, TEXTURE_WRAP_T = 0x2803, LINEAR = 0x2601, NEAREST = 0x2600, CLAMP_TO_EDGE = 0x812F,
            RGBA = 0x1908, RGBA8 = 0x8058, RGBA16F = 0x881A, RGBA32F = 0x8814, UNSIGNED_BYTE = 0x1401, FLOAT = 0x1406, HALF_FLOAT = 0x140B, BGRA = 0x80E1,
            FRAMEBUFFER = 0x8D40, COLOR_ATTACHMENT0 = 0x8CE0, FRAMEBUFFER_COMPLETE = 0x8CD5, ARRAY_BUFFER = 0x8892, STATIC_DRAW = 0x88E4, STREAM_DRAW = 0x88E0, DYNAMIC_COPY = 0x88EA,
            VERTEX_SHADER = 0x8B31, FRAGMENT_SHADER = 0x8B30, COMPILE_STATUS = 0x8B81, LINK_STATUS = 0x8B82, TEXTURE0 = 0x84C0, TEXTURE1 = 0x84C1, TRANSFORM_FEEDBACK = 0x8E22, TRANSFORM_FEEDBACK_BUFFER = 0x8C8E,
            SEPARATE_ATTRIBS = 0x8C8D, RASTERIZER_DISCARD = 0x8C89, PROGRAM_POINT_SIZE = 0x8642, POINT_SPRITE = 0x8861, VERSION = 0x1F02, RENDERER = 0x1F01, UNPACK_ALIGNMENT = 0xCF5, PACK_ALIGNMENT = 0xD05,
            MAX_TEXTURE_SIZE = 0xD33, POINT_SIZE_RANGE = 0xB12, MAJOR_VERSION = 0x821B, MINOR_VERSION = 0x821C;

        [DllImport(Lib)] public static extern void glClear(uint mask);
        [DllImport(Lib)] public static extern void glClearColor(float r, float g, float b, float a);
        [DllImport(Lib)] public static extern void glViewport(int x, int y, int w, int h);
        [DllImport(Lib)] public static extern void glEnable(uint cap);
        [DllImport(Lib)] public static extern void glDisable(uint cap);
        [DllImport(Lib)] public static extern void glBlendFunc(uint s, uint d);
        [DllImport(Lib)] public static extern void glDrawArrays(uint mode, int first, int count);
        [DllImport(Lib)] public static extern uint glGetError();
        [DllImport(Lib, EntryPoint = "glGetString")] static extern IntPtr glGetStringPtr(uint name);
        public static string glGetString(uint name) { IntPtr p = glGetStringPtr(name); return p == IntPtr.Zero ? "" : Marshal.PtrToStringAnsi(p); }
        [DllImport(Lib)] public static extern void glGetIntegerv(uint pname, int[] data);
        [DllImport(Lib)] public static extern void glGetFloatv(uint pname, float[] data);
        [DllImport(Lib)] public static extern void glReadPixels(int x, int y, int w, int h, uint format, uint type, IntPtr data);
        [DllImport(Lib)] public static extern void glTexParameteri(uint target, uint pname, int param);
        [DllImport(Lib)] public static extern void glTexImage2D(uint target, int level, int internalFormat, int w, int h, int border, uint format, uint type, IntPtr data);
        [DllImport(Lib)] public static extern void glTexSubImage2D(uint target, int level, int x, int y, int w, int h, uint format, uint type, IntPtr data);
        [DllImport(Lib, EntryPoint = "glTexSubImage2D")] public static extern void glTexSubImage2Df(uint target, int level, int x, int y, int w, int h, uint format, uint type, float[] data);
        [DllImport(Lib)] public static extern void glBindTexture(uint target, uint tex);
        [DllImport(Lib)] public static extern void glGenTextures(int n, out uint tex);
        [DllImport(Lib)] public static extern void glDeleteTextures(int n, ref uint tex);
        [DllImport(Lib)] public static extern void glPixelStorei(uint pname, int param);
        [DllImport(Lib)] public static extern void glFinish();
        [DllImport(Lib)] public static extern void glFlush();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate uint D_CreateShader(uint type);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void D_ShaderSource(uint shader, int count, IntPtr[] src, int[] len);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void D_Uint(uint a);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void D_GetIv(uint obj, uint pname, out int v);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void D_GetInfoLog(uint obj, int max, out int len, StringBuilder log);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate uint D_CreateProgram();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void D_UintUint(uint a, uint b);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int D_GetUniformLocation(uint prog, string name);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void D_Uniform1f(int loc, float v);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void D_Uniform2f(int loc, float a, float b);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void D_Uniform3f(int loc, float a, float b, float c);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void D_Uniform4f(int loc, float a, float b, float c, float d);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void D_Uniform1i(int loc, int v);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void D_Uniform1ui(int loc, uint v);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void D_Gen(int n, out uint id);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void D_Del(int n, ref uint id);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void D_BufferData(uint target, IntPtr size, IntPtr data, uint usage);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void D_BufferDataF(uint target, IntPtr size, float[] data, uint usage);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void D_BufferSubDataF(uint target, IntPtr offset, IntPtr size, float[] data);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void D_VertexAttribPointer(uint index, int size, uint type, byte normalized, int stride, IntPtr offset);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void D_FramebufferTexture2D(uint target, uint attachment, uint textarget, uint tex, int level);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate uint D_CheckFramebufferStatus(uint target);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void D_TransformFeedbackVaryings(uint prog, int count, IntPtr[] names, uint mode);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void D_BindBufferBase(uint target, uint index, uint buffer);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void D_Void();

        public static D_CreateShader glCreateShader; public static D_ShaderSource glShaderSource; public static D_Uint glCompileShader, glLinkProgram, glUseProgram, glDeleteShader, glDeleteProgram, glBindVertexArray, glEnableVertexAttribArray, glActiveTexture, glBeginTransformFeedback;
        public static D_GetIv glGetShaderiv, glGetProgramiv; public static D_GetInfoLog glGetShaderInfoLog, glGetProgramInfoLog; public static D_CreateProgram glCreateProgram;
        public static D_UintUint glAttachShader, glBindBuffer, glBindFramebuffer, glBindTransformFeedback; public static D_GetUniformLocation glGetUniformLocation;
        public static D_Uniform1f glUniform1f; public static D_Uniform2f glUniform2f; public static D_Uniform3f glUniform3f; public static D_Uniform4f glUniform4f; public static D_Uniform1i glUniform1i; public static D_Uniform1ui glUniform1ui;
        public static D_Gen glGenBuffers, glGenVertexArrays, glGenFramebuffers, glGenTransformFeedbacks; public static D_Del glDeleteBuffers, glDeleteVertexArrays, glDeleteFramebuffers, glDeleteTransformFeedbacks;
        public static D_BufferData glBufferData; public static D_BufferDataF glBufferDataF; public static D_BufferSubDataF glBufferSubDataF; public static D_VertexAttribPointer glVertexAttribPointer;
        public static D_FramebufferTexture2D glFramebufferTexture2D; public static D_CheckFramebufferStatus glCheckFramebufferStatus; public static D_TransformFeedbackVaryings glTransformFeedbackVaryings;
        public static D_BindBufferBase glBindBufferBase; public static D_Void glEndTransformFeedback;

        public static void Load()
        {
            glCreateShader = Get<D_CreateShader>("glCreateShader"); glShaderSource = Get<D_ShaderSource>("glShaderSource");
            glCompileShader = Get<D_Uint>("glCompileShader"); glLinkProgram = Get<D_Uint>("glLinkProgram"); glUseProgram = Get<D_Uint>("glUseProgram");
            glDeleteShader = Get<D_Uint>("glDeleteShader"); glDeleteProgram = Get<D_Uint>("glDeleteProgram"); glBindVertexArray = Get<D_Uint>("glBindVertexArray");
            glEnableVertexAttribArray = Get<D_Uint>("glEnableVertexAttribArray"); glActiveTexture = Get<D_Uint>("glActiveTexture"); glBeginTransformFeedback = Get<D_Uint>("glBeginTransformFeedback");
            glGetShaderiv = Get<D_GetIv>("glGetShaderiv"); glGetProgramiv = Get<D_GetIv>("glGetProgramiv");
            glGetShaderInfoLog = Get<D_GetInfoLog>("glGetShaderInfoLog"); glGetProgramInfoLog = Get<D_GetInfoLog>("glGetProgramInfoLog");
            glCreateProgram = Get<D_CreateProgram>("glCreateProgram"); glAttachShader = Get<D_UintUint>("glAttachShader");
            glBindBuffer = Get<D_UintUint>("glBindBuffer"); glBindFramebuffer = Get<D_UintUint>("glBindFramebuffer"); glBindTransformFeedback = Get<D_UintUint>("glBindTransformFeedback");
            glGetUniformLocation = Get<D_GetUniformLocation>("glGetUniformLocation");
            glUniform1f = Get<D_Uniform1f>("glUniform1f"); glUniform2f = Get<D_Uniform2f>("glUniform2f"); glUniform3f = Get<D_Uniform3f>("glUniform3f"); glUniform4f = Get<D_Uniform4f>("glUniform4f");
            glUniform1i = Get<D_Uniform1i>("glUniform1i"); glUniform1ui = Get<D_Uniform1ui>("glUniform1ui");
            glGenBuffers = Get<D_Gen>("glGenBuffers"); glGenVertexArrays = Get<D_Gen>("glGenVertexArrays"); glGenFramebuffers = Get<D_Gen>("glGenFramebuffers"); glGenTransformFeedbacks = Get<D_Gen>("glGenTransformFeedbacks");
            glDeleteBuffers = Get<D_Del>("glDeleteBuffers"); glDeleteVertexArrays = Get<D_Del>("glDeleteVertexArrays"); glDeleteFramebuffers = Get<D_Del>("glDeleteFramebuffers"); glDeleteTransformFeedbacks = Get<D_Del>("glDeleteTransformFeedbacks");
            glBufferData = Get<D_BufferData>("glBufferData"); glBufferDataF = Get<D_BufferDataF>("glBufferData"); glBufferSubDataF = Get<D_BufferSubDataF>("glBufferSubData");
            glVertexAttribPointer = Get<D_VertexAttribPointer>("glVertexAttribPointer"); glFramebufferTexture2D = Get<D_FramebufferTexture2D>("glFramebufferTexture2D");
            glCheckFramebufferStatus = Get<D_CheckFramebufferStatus>("glCheckFramebufferStatus"); glTransformFeedbackVaryings = Get<D_TransformFeedbackVaryings>("glTransformFeedbackVaryings");
            glBindBufferBase = Get<D_BindBufferBase>("glBindBufferBase"); glEndTransformFeedback = Get<D_Void>("glEndTransformFeedback");
        }

        static T Get<T>(string name) where T : class
        {
            IntPtr p = Glfw.glfwGetProcAddress(name);
            if (p == IntPtr.Zero) throw new Exception("OpenGL 3.3 function missing: " + name + " (driver too old?)");
            return (T)(object)Marshal.GetDelegateForFunctionPointer(p, typeof(T));
        }

        public static uint Compile(uint type, string src, string label)
        {
            uint sh = glCreateShader(type);
            IntPtr p = Marshal.StringToHGlobalAnsi(src);
            try { glShaderSource(sh, 1, new[] { p }, new[] { src.Length }); } finally { Marshal.FreeHGlobal(p); }
            glCompileShader(sh);
            int ok; glGetShaderiv(sh, COMPILE_STATUS, out ok);
            if (ok == 0) { var sb = new StringBuilder(16384); int len; glGetShaderInfoLog(sh, 16384, out len, sb); throw new Exception("shader '" + label + "' failed:\n" + sb); }
            return sh;
        }

        public static uint Link(string vs, string fs, string label, string[] varyings)
        {
            uint prog = glCreateProgram();
            uint v = Compile(VERTEX_SHADER, vs, label + " vs"), f = Compile(FRAGMENT_SHADER, fs, label + " fs");
            glAttachShader(prog, v); glAttachShader(prog, f);
            if (varyings != null)
            {
                var ptrs = new IntPtr[varyings.Length];
                for (int i = 0; i < ptrs.Length; i++) ptrs[i] = Marshal.StringToHGlobalAnsi(varyings[i]);
                try { glTransformFeedbackVaryings(prog, ptrs.Length, ptrs, SEPARATE_ATTRIBS); }
                finally { foreach (var p in ptrs) Marshal.FreeHGlobal(p); }
            }
            glLinkProgram(prog);
            int ok; glGetProgramiv(prog, LINK_STATUS, out ok);
            if (ok == 0) { var sb = new StringBuilder(16384); int len; glGetProgramInfoLog(prog, 16384, out len, sb); throw new Exception("program '" + label + "' failed to link:\n" + sb); }
            glDeleteShader(v); glDeleteShader(f);
            return prog;
        }

        public static void Check(string where)
        {
            uint e = glGetError();
            if (e != 0) Log.Write("GL error 0x" + e.ToString("x") + " at " + where);
        }
    }
}
