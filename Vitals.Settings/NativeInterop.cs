using System.Runtime.InteropServices;

namespace Vitals.Settings;

internal static class NativeInterop
{
    public const uint WM_RELOAD_CONFIG = 0x8000 + 2; // WM_APP + 2

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern nint FindWindow(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    public static extern bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam);
}
