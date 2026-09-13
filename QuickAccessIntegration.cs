using System.Runtime.InteropServices;
using static Vitals.NativeMethods;

namespace Vitals;

/// <summary>
/// Puente liviano entre la cápsula nativa existente y el launcher desplegable.
/// Observa únicamente los mensajes de VitalsPillWindow; no modifica el menú de
/// bandeja ni el render de métricas.
/// </summary>
internal static class QuickAccessIntegration
{
    private const int WhCallWndProc = 4;
    private const uint WmWindowPosChanged = 0x0047;
    private const uint WmShowWindow = 0x0018;
    private static nint _hook;
    private static nint _pill;

    [StructLayout(LayoutKind.Sequential)]
    private struct CWPSTRUCT
    {
        public nint lParam;
        public nint wParam;
        public uint message;
        public nint hwnd;
    }

    [DllImport("user32.dll")]
    private static extern unsafe nint SetWindowsHookEx(int idHook, delegate* unmanaged<int, nint, nint, nint> lpfn,
        nint hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint hwnd, char[] className, int maxCount);

    public static unsafe void InstallForCurrentThread()
    {
        if (_hook != 0) return;
        var callback = (delegate* unmanaged<int, nint, nint, nint>)&HookProc;
        _hook = SetWindowsHookEx(WhCallWndProc, callback, 0, GetCurrentThreadId());
    }

    public static void Uninstall()
    {
        CapsuleQuickLauncher.Dispose();
        _pill = 0;
        if (_hook == 0) return;
        UnhookWindowsHookEx(_hook);
        _hook = 0;
    }

    [UnmanagedCallersOnly]
    private static nint HookProc(int nCode, nint wParam, nint lParam)
    {
        try
        {
            if (nCode >= 0 && lParam != 0)
            {
                var message = Marshal.PtrToStructure<CWPSTRUCT>(lParam);
                if (IsPill(message.hwnd))
                {
                    if (_pill == 0)
                    {
                        _pill = message.hwnd;
                        CapsuleQuickLauncher.Attach(_pill);
                    }

                    if (message.message == WmWindowPosChanged || message.message == WmShowWindow)
                        CapsuleQuickLauncher.Reposition();
                    else if (message.message == WM_RELOAD_CONFIG)
                        CapsuleQuickLauncher.Reload();
                    else if (message.message == WM_DESTROY)
                    {
                        CapsuleQuickLauncher.Dispose();
                        _pill = 0;
                    }
                }
            }
        }
        catch
        {
            // Nunca propagar excepciones a través del callback Win32.
        }

        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private static bool IsPill(nint hwnd)
    {
        if (hwnd == 0) return false;
        if (_pill != 0) return hwnd == _pill;
        var buffer = new char[64];
        int len = GetClassName(hwnd, buffer, buffer.Length);
        return len > 0 && new string(buffer, 0, len) == "VitalsPillWindow";
    }
}
