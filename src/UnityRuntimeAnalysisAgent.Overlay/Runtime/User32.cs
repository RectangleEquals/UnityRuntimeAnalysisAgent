using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace UnityRuntimeAnalysisAgent.Overlay.Runtime;

/// <summary>
/// The overlay's calls into Windows' <c>user32.dll</c>, bound to the system's own copy. <c>[DllImport("user32.dll")]</c>
/// isn't used: Mono looks in the game's <c>Plugins</c> folders first, and a game that ships a file named
/// <c>user32.dll</c> there makes every such import fail (<c>DllNotFoundException</c>). The functions are looked up in
/// the user32 Windows already loaded into the process (or in System32), and called through delegates.
/// </summary>
internal static class User32
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi, SetLastError = true)]
    public delegate IntPtr SetWindowsHookExFn(int idHook, IntPtr proc, IntPtr module, uint threadId);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate bool UnhookWindowsHookExFn(IntPtr hook);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate int GetMessageFn(IntPtr message, IntPtr window, uint first, uint last);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate bool PostThreadMessageFn(uint threadId, uint message, IntPtr wParam, IntPtr lParam);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate IntPtr CallNextHookExFn(IntPtr hook, int code, IntPtr message, IntPtr data);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate IntPtr GetForegroundWindowFn();

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate uint GetWindowThreadProcessIdFn(IntPtr window, out uint processId);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate short GetKeyStateFn(int key);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate IntPtr GetKeyboardLayoutFn(uint threadId);

    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode)]
    public delegate int ToUnicodeExFn(uint key, uint scan, byte[] state, StringBuilder buffer, int size, uint flags, IntPtr layout);

    private static IntPtr s_module;

    private static SetWindowsHookExFn? s_setWindowsHookEx;
    private static UnhookWindowsHookExFn? s_unhookWindowsHookEx;
    private static GetMessageFn? s_getMessage;
    private static PostThreadMessageFn? s_postThreadMessage;
    private static CallNextHookExFn? s_callNextHookEx;
    private static GetForegroundWindowFn? s_getForegroundWindow;
    private static GetWindowThreadProcessIdFn? s_getWindowThreadProcessId;
    private static GetKeyStateFn? s_getAsyncKeyState;
    private static GetKeyStateFn? s_getKeyState;
    private static GetKeyboardLayoutFn? s_getKeyboardLayout;
    private static ToUnicodeExFn? s_toUnicodeEx;

    public static SetWindowsHookExFn SetWindowsHookEx => s_setWindowsHookEx ??= Bind<SetWindowsHookExFn>("SetWindowsHookExW");

    public static UnhookWindowsHookExFn UnhookWindowsHookEx => s_unhookWindowsHookEx ??= Bind<UnhookWindowsHookExFn>("UnhookWindowsHookEx");

    public static GetMessageFn GetMessage => s_getMessage ??= Bind<GetMessageFn>("GetMessageW");

    public static PostThreadMessageFn PostThreadMessage => s_postThreadMessage ??= Bind<PostThreadMessageFn>("PostThreadMessageW");

    public static CallNextHookExFn CallNextHookEx => s_callNextHookEx ??= Bind<CallNextHookExFn>("CallNextHookEx");

    public static GetForegroundWindowFn GetForegroundWindow => s_getForegroundWindow ??= Bind<GetForegroundWindowFn>("GetForegroundWindow");

    public static GetWindowThreadProcessIdFn GetWindowThreadProcessId => s_getWindowThreadProcessId ??= Bind<GetWindowThreadProcessIdFn>("GetWindowThreadProcessId");

    public static GetKeyStateFn GetAsyncKeyState => s_getAsyncKeyState ??= Bind<GetKeyStateFn>("GetAsyncKeyState");

    public static GetKeyStateFn GetKeyState => s_getKeyState ??= Bind<GetKeyStateFn>("GetKeyState");

    public static GetKeyboardLayoutFn GetKeyboardLayout => s_getKeyboardLayout ??= Bind<GetKeyboardLayoutFn>("GetKeyboardLayout");

    public static ToUnicodeExFn ToUnicodeEx => s_toUnicodeEx ??= Bind<ToUnicodeExFn>("ToUnicodeEx");

    // One function of the system user32, as a delegate; DllNotFoundException / EntryPointNotFoundException like an import.
    private static T Bind<T>(string name)
        where T : Delegate
    {
        if (s_module == IntPtr.Zero)
        {
            var systemCopy = Path.Combine(Environment.SystemDirectory, "user32.dll");
            var module = GetModuleHandleW(systemCopy);
            if (module == IntPtr.Zero)
            {
                module = LoadLibraryExW("user32.dll", IntPtr.Zero, LoadLibrarySearchSystem32);
            }

            s_module = module != IntPtr.Zero ? module : throw new DllNotFoundException("user32.dll (system copy) couldn't be loaded.");
        }

        var address = GetProcAddress(s_module, name);
        return address != IntPtr.Zero
            ? (T)Marshal.GetDelegateForFunctionPointer(address, typeof(T))
            : throw new EntryPointNotFoundException("user32.dll has no " + name + ".");
    }

    private const uint LoadLibrarySearchSystem32 = 0x800;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandleW(string name);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryExW(string name, IntPtr file, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern IntPtr GetProcAddress(IntPtr module, string name);
}
