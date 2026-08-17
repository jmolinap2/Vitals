using System.Runtime.InteropServices;

namespace Vitals;

internal static class NativeMethods
{
    public const int WS_POPUP = unchecked((int)0x80000000);

    public const int WS_EX_LAYERED = 0x00080000;
    public const int WS_EX_TOPMOST = 0x00000008;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_NOACTIVATE = 0x08000000;

    public const int SW_SHOWNOACTIVATE = 4;

    public const int LWA_ALPHA = 0x2;

    public const uint WM_DESTROY = 0x0002;
    public const uint WM_PAINT = 0x000F;
    public const uint WM_TIMER = 0x0113;
    public const uint WM_RBUTTONUP = 0x0205;
    public const uint WM_NCHITTEST = 0x0084;
    public const uint WM_NCRBUTTONUP = 0x00A5;
    public const uint WM_LBUTTONUP = 0x0202;
    public const uint WM_COMMAND = 0x0111;
    public const uint WM_TRAYICON = 0x8000 + 1; // WM_APP + 1

    public const uint NIM_ADD = 0;
    public const uint NIM_DELETE = 2;
    public const uint NIF_MESSAGE = 0x1;
    public const uint NIF_ICON = 0x2;
    public const uint MF_STRING = 0x0;
    public const uint TPM_RIGHTBUTTON = 0x0002;
    public const uint TrayIconId = 1;
    public const nint IdExit = 1001;
    public const nint IdSettings = 1002;

    public const nint HTCAPTION = 2;

    public const int SPI_GETWORKAREA = 0x0030;

    public const uint SRCCOPY = 0x00CC0020;

    public const uint DT_LEFT = 0x0000;
    public const uint DT_CENTER = 0x0001;
    public const uint DT_VCENTER = 0x0004;
    public const uint DT_SINGLELINE = 0x0020;
    public const uint DT_NOCLIP = 0x0100;

    public const int TRANSPARENT_BK = 1;

    public const int FW_NORMAL = 400;
    public const int FW_SEMIBOLD = 600;
    public const uint DEFAULT_CHARSET = 1;
    public const uint OUT_DEFAULT_PRECIS = 0;
    public const uint CLIP_DEFAULT_PRECIS = 0;
    public const uint CLEARTYPE_QUALITY = 5;
    public const uint DEFAULT_PITCH = 0;
    public const uint FF_DONTCARE = 0;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public nint hIconSm;
    }

    public unsafe struct PAINTSTRUCT
    {
        public nint hdc;
        public int fErase;
        public RECT rcPaint;
        public int fRestore;
        public int fIncUpdate;
        public fixed byte rgbReserved[32];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;

        public readonly ulong Ticks => ((ulong)dwHighDateTime << 32) | dwLowDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    // Real desktop blur-behind (Acrylic), applied via the undocumented but
    // stable SetWindowCompositionAttribute — this is what gives a genuine
    // frosted-glass backdrop instead of a flat translucent fill.
    public const int WCA_ACCENT_POLICY = 19;
    public const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;

    [StructLayout(LayoutKind.Sequential)]
    public struct ACCENT_POLICY
    {
        public int AccentState;
        public int AccentFlags;
        public uint GradientColor; // 0xAABBGGRR
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WINCOMPATTRDATA
    {
        public int Attribute;
        public nint Data;
        public int SizeOfData;
    }

    // GPU% no existe como llamada Win32 directa; hay que leer el contador
    // de rendimiento "GPU Engine" (el mismo que usa el Administrador de tareas).
    public const uint PDH_FMT_DOUBLE = 0x00000200;
    public const uint PDH_MORE_DATA = 0x800007D2;

    [StructLayout(LayoutKind.Sequential)]
    public struct PDH_FMT_COUNTERVALUE_ITEM_W
    {
        public nint szName;
        public uint CStatus;
        public double doubleValue;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    public static extern uint PdhOpenQuery(string? szDataSource, nint dwUserData, out nint phQuery);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    public static extern uint PdhAddEnglishCounter(nint hQuery, string szFullCounterPath, nint dwUserData, out nint phCounter);

    [DllImport("pdh.dll")]
    public static extern uint PdhCollectQueryData(nint hQuery);

    [DllImport("pdh.dll")]
    public static extern uint PdhGetFormattedCounterArrayW(nint hCounter, uint dwFormat, ref uint lpdwBufferSize, ref uint lpdwItemCount, nint itemBuffer);

    // ---- bandeja del sistema ----

    public unsafe struct NOTIFYICONDATA
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;
        public fixed char szTip[128];
    }

    [DllImport("shell32.dll")]
    public static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll")]
    public static extern nint LoadIcon(nint hInstance, nint lpIconName);

    [DllImport("user32.dll")]
    public static extern nint CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool AppendMenu(nint hMenu, uint uFlags, nint uIDNewItem, string lpNewItem);

    [DllImport("user32.dll")]
    public static extern bool TrackPopupMenu(nint hMenu, uint uFlags, int x, int y, int nReserved, nint hWnd, nint prcRect);

    [DllImport("user32.dll")]
    public static extern bool DestroyMenu(nint hMenu);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT lpPoint);

    // ---- user32.dll ----

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern nint CreateWindowEx(int dwExStyle, string lpClassName, string lpWindowName,
        int dwStyle, int x, int y, int nWidth, int nHeight,
        nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

    [DllImport("user32.dll")]
    public static extern nint DefWindowProc(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    public static extern bool GetMessage(out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    public static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    public static extern nint DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    public static extern void PostQuitMessage(int nExitCode);

    [DllImport("user32.dll")]
    public static extern nint SetTimer(nint hWnd, nint nIDEvent, uint uElapse, nint lpTimerFunc);

    [DllImport("user32.dll")]
    public static extern bool KillTimer(nint hWnd, nint nIDEvent);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern int SetWindowRgn(nint hWnd, nint hRgn, bool bRedraw);

    [DllImport("user32.dll")]
    public static extern bool SetLayeredWindowAttributes(nint hwnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("user32.dll")]
    public static extern nint BeginPaint(nint hWnd, out PAINTSTRUCT lpPaint);

    [DllImport("user32.dll")]
    public static extern bool EndPaint(nint hWnd, ref PAINTSTRUCT lpPaint);

    [DllImport("user32.dll")]
    public static extern bool GetClientRect(nint hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern bool InvalidateRect(nint hWnd, nint lpRect, bool bErase);

    [DllImport("user32.dll")]
    public static extern nint LoadCursor(nint hInstance, nint lpCursorName);

    [DllImport("user32.dll")]
    public static extern bool SystemParametersInfo(int uiAction, int uiParam, ref RECT pvParam, int fWinIni);

    [DllImport("user32.dll")]
    public static extern int FillRect(nint hDC, ref RECT lprc, nint hbr);

    [DllImport("user32.dll")]
    public static extern int FrameRect(nint hDC, ref RECT lprc, nint hbr);

    [DllImport("user32.dll")]
    public static extern int SetWindowCompositionAttribute(nint hwnd, ref WINCOMPATTRDATA data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int DrawText(nint hDC, string lpchText, int nCount, ref RECT lpRect, uint uFormat);

    // ---- gdi32.dll ----

    [DllImport("gdi32.dll")]
    public static extern nint CreateRoundRectRgn(int x1, int y1, int x2, int y2, int cx, int cy);

    [DllImport("gdi32.dll")]
    public static extern nint CreateCompatibleDC(nint hdc);

    [DllImport("gdi32.dll")]
    public static extern nint CreateCompatibleBitmap(nint hdc, int cx, int cy);

    [DllImport("gdi32.dll")]
    public static extern nint SelectObject(nint hdc, nint h);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteDC(nint hdc);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(nint ho);

    [DllImport("gdi32.dll")]
    public static extern nint CreateSolidBrush(uint crColor);

    [DllImport("gdi32.dll")]
    public static extern uint SetTextColor(nint hdc, uint crColor);

    [DllImport("gdi32.dll")]
    public static extern int SetBkMode(nint hdc, int mode);

    [DllImport("gdi32.dll")]
    public static extern bool BitBlt(nint hdcDest, int xDest, int yDest, int w, int h, nint hdcSrc, int xSrc, int ySrc, uint rop);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    public static extern nint CreateFont(int h, int w, int esc, int orient, int weight, uint italic, uint underline,
        uint strikeout, uint charset, uint outPrec, uint clipPrec, uint quality, uint pitchAndFamily, string face);

    // ---- kernel32.dll ----

    [DllImport("kernel32.dll")]
    public static extern nint GetModuleHandle(string? lpModuleName);

    [DllImport("kernel32.dll")]
    public static extern bool GetSystemTimes(out FILETIME lpIdleTime, out FILETIME lpKernelTime, out FILETIME lpUserTime);

    [DllImport("kernel32.dll")]
    public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [DllImport("kernel32.dll")]
    public static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);

    public static uint Rgb(byte r, byte g, byte b) => (uint)(r | (g << 8) | (b << 16));
}
