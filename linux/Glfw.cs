// Thin GLFW 3.3 binding. GLFW owns the Wayland/X11 connection, the OpenGL context and input.
using System;
using System.Runtime.InteropServices;

namespace ScreenWorm
{
    [StructLayout(LayoutKind.Sequential)]
    struct GLFWvidmode
    {
        public int Width, Height, RedBits, GreenBits, BlueBits, RefreshRate;
    }

    static class Glfw
    {
        const string Lib = "libglfw.so.3";

        public const int TRUE = 1, FALSE = 0;
        public const int PRESS = 1, RELEASE = 0, REPEAT = 2;
        public const int KEY_ESCAPE = 256, KEY_Q = 81, KEY_H = 72, KEY_C = 67, KEY_B = 66;
        public const int MOUSE_BUTTON_LEFT = 0;

        public const int CLIENT_API = 0x00022001, NO_API = 0;
        public const int CONTEXT_VERSION_MAJOR = 0x00022002, CONTEXT_VERSION_MINOR = 0x00022003;
        public const int OPENGL_PROFILE = 0x00022008, OPENGL_CORE_PROFILE = 0x00032001;
        public const int VISIBLE = 0x00020004, RESIZABLE = 0x00020003, DECORATED = 0x00020005, FLOATING = 0x00020007;
        public const int SCALE_TO_MONITOR = 0x0002200C, AUTO_ICONIFY = 0x00020006;
        public const int CURSOR = 0x00033001, CURSOR_NORMAL = 0x00034001, CURSOR_HIDDEN = 0x00034002;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void ErrorCallback(int code, IntPtr description);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void KeyCallback(IntPtr window, int key, int scancode, int action, int mods);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void MouseButtonCallback(IntPtr window, int button, int action, int mods);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void CursorPosCallback(IntPtr window, double x, double y);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void FramebufferSizeCallback(IntPtr window, int width, int height);

        [DllImport(Lib)] public static extern int glfwInit();
        [DllImport(Lib)] public static extern void glfwTerminate();
        [DllImport(Lib)] public static extern int glfwGetError(out IntPtr description);
        [DllImport(Lib)] public static extern void glfwSetErrorCallback(ErrorCallback callback);
        [DllImport(Lib)] public static extern void glfwWindowHint(int hint, int value);
        [DllImport(Lib)] public static extern void glfwDefaultWindowHints();
        [DllImport(Lib)] public static extern IntPtr glfwCreateWindow(int width, int height, string title, IntPtr monitor, IntPtr share);
        [DllImport(Lib)] public static extern void glfwDestroyWindow(IntPtr window);
        [DllImport(Lib)] public static extern int glfwWindowShouldClose(IntPtr window);
        [DllImport(Lib)] public static extern void glfwSetWindowShouldClose(IntPtr window, int value);
        [DllImport(Lib)] public static extern void glfwShowWindow(IntPtr window);
        [DllImport(Lib)] public static extern void glfwHideWindow(IntPtr window);
        [DllImport(Lib)] public static extern void glfwFocusWindow(IntPtr window);
        [DllImport(Lib)] public static extern void glfwGetWindowSize(IntPtr window, out int width, out int height);
        [DllImport(Lib)] public static extern void glfwGetWindowPos(IntPtr window, out int x, out int y);
        [DllImport(Lib)] public static extern void glfwSetWindowPos(IntPtr window, int x, int y);
        [DllImport(Lib)] public static extern void glfwSetWindowTitle(IntPtr window, string title);
        [DllImport(Lib)] public static extern void glfwGetFramebufferSize(IntPtr window, out int width, out int height);
        [DllImport(Lib)] public static extern void glfwMakeContextCurrent(IntPtr window);
        [DllImport(Lib)] public static extern IntPtr glfwGetCurrentContext();
        [DllImport(Lib)] public static extern void glfwSwapBuffers(IntPtr window);
        [DllImport(Lib)] public static extern void glfwSwapInterval(int interval);
        [DllImport(Lib)] public static extern void glfwPollEvents();
        [DllImport(Lib)] public static extern void glfwWaitEventsTimeout(double seconds);
        [DllImport(Lib)] public static extern double glfwGetTime();
        [DllImport(Lib)] public static extern IntPtr glfwGetProcAddress(string name);
        [DllImport(Lib)] public static extern IntPtr glfwGetMonitors(out int count);
        [DllImport(Lib)] public static extern IntPtr glfwGetPrimaryMonitor();
        [DllImport(Lib)] public static extern void glfwGetMonitorPos(IntPtr monitor, out int x, out int y);
        [DllImport(Lib)] public static extern void glfwGetMonitorWorkarea(IntPtr monitor, out int x, out int y, out int width, out int height);
        [DllImport(Lib)] public static extern void glfwGetMonitorContentScale(IntPtr monitor, out float xscale, out float yscale);
        [DllImport(Lib)] public static extern IntPtr glfwGetMonitorName(IntPtr monitor);
        [DllImport(Lib)] public static extern IntPtr glfwGetVideoMode(IntPtr monitor);
        [DllImport(Lib)] public static extern void glfwSetInputMode(IntPtr window, int mode, int value);
        [DllImport(Lib)] public static extern void glfwSetKeyCallback(IntPtr window, KeyCallback callback);
        [DllImport(Lib)] public static extern void glfwSetMouseButtonCallback(IntPtr window, MouseButtonCallback callback);
        [DllImport(Lib)] public static extern void glfwSetCursorPosCallback(IntPtr window, CursorPosCallback callback);
        [DllImport(Lib)] public static extern void glfwSetFramebufferSizeCallback(IntPtr window, FramebufferSizeCallback callback);


        public static void SetErrorCallback(ErrorCallback callback) { glfwSetErrorCallback(callback); }
        public static void DefaultWindowHints() { glfwDefaultWindowHints(); }
        public static void WindowHint(int hint, int value) { glfwWindowHint(hint, value); }
        public static IntPtr CreateWindow(int width, int height, string title, IntPtr monitor, IntPtr share) { return glfwCreateWindow(width, height, title, monitor, share); }
        public static void DestroyWindow(IntPtr window) { glfwDestroyWindow(window); }
        public static void ShowWindow(IntPtr window) { glfwShowWindow(window); }
        public static void FocusWindow(IntPtr window) { glfwFocusWindow(window); }
        public static void GetWindowSize(IntPtr window, out int width, out int height) { glfwGetWindowSize(window, out width, out height); }
        public static void GetFramebufferSize(IntPtr window, out int width, out int height) { glfwGetFramebufferSize(window, out width, out height); }
        public static void MakeContextCurrent(IntPtr window) { glfwMakeContextCurrent(window); }
        public static void SwapBuffers(IntPtr window) { glfwSwapBuffers(window); }
        public static bool WindowShouldClose(IntPtr window) { return glfwWindowShouldClose(window) != 0; }
        public static void SwapInterval(int interval) { glfwSwapInterval(interval); }
        public static void PollEvents() { glfwPollEvents(); }
        public static IntPtr GetMonitors(out int count) { return glfwGetMonitors(out count); }
        public static IntPtr GetPrimaryMonitor() { return glfwGetPrimaryMonitor(); }
        public static void GetMonitorPos(IntPtr monitor, out int x, out int y) { glfwGetMonitorPos(monitor, out x, out y); }
        public static void GetMonitorWorkarea(IntPtr monitor, out int x, out int y, out int width, out int height) { glfwGetMonitorWorkarea(monitor, out x, out y, out width, out height); }
        public static void GetMonitorContentScale(IntPtr monitor, out float xscale, out float yscale) { glfwGetMonitorContentScale(monitor, out xscale, out yscale); }
        public static void SetWindowPos(IntPtr window, int x, int y) { glfwSetWindowPos(window, x, y); }
        public static void SetInputMode(IntPtr window, int mode, int value) { glfwSetInputMode(window, mode, value); }
        public static void SetKeyCallback(IntPtr window, KeyCallback callback) { glfwSetKeyCallback(window, callback); }
        public static void SetMouseButtonCallback(IntPtr window, MouseButtonCallback callback) { glfwSetMouseButtonCallback(window, callback); }
        public static void SetCursorPosCallback(IntPtr window, CursorPosCallback callback) { glfwSetCursorPosCallback(window, callback); }
        public static void Terminate() { glfwTerminate(); }

        public static void Init()
        {
            IntPtr description;
            if (glfwInit() == 0)
            {
                glfwGetError(out description);
                throw new Exception("glfwInit failed: " + Describe(description));
            }
        }

        public static string Describe(IntPtr description)
        {
            return description == IntPtr.Zero ? "unknown error" : Marshal.PtrToStringAnsi(description);
        }

        public static string MonitorName(IntPtr monitor)
        {
            IntPtr p = glfwGetMonitorName(monitor);
            return p == IntPtr.Zero ? "" : Marshal.PtrToStringAnsi(p);
        }

        public static GLFWvidmode VideoMode(IntPtr monitor)
        {
            IntPtr p = glfwGetVideoMode(monitor);
            return p == IntPtr.Zero ? new GLFWvidmode() : (GLFWvidmode)Marshal.PtrToStructure(p, typeof(GLFWvidmode));
        }
    }
}
