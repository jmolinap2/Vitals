using System.Diagnostics;
using System.Runtime.InteropServices;
using Vitals.Shared;

namespace Vitals;

/// <summary>
/// Launcher visual acoplado a la cápsula. Mantiene su propia ventana Win32 para
/// no mezclar el render de métricas con el grid de accesos directos.
/// </summary>
internal static unsafe class CapsuleQuickLauncher
{
    private const string ToggleClass = "VitalsLauncherToggle";
    private const string PanelClass = "VitalsLauncherPanel";
    private const int ToggleSize = 28;
    private const int Gap = 5;
    private const int CellWidth = 92;
    private const int CellHeightLabels = 76;
    private const int CellHeightCompact = 56;
    private const int Padding = 10;
    private const nint AnimationTimer = 31;
    private const uint WM_PAINT = 0x000F;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_MOUSEMOVE = 0x0200;
    private const uint WM_TIMER = 0x0113;
    private const uint WM_DESTROY = 0x0002;
    private const uint WS_POPUP = 0x80000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int SW_HIDE = 0;
    private const int SW_SHOWNOACTIVATE = 4;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOZORDER = 0x0004;
    private const int COLOR_WINDOW = 5;
    private const uint DT_CENTER = 0x00000001;
    private const uint DT_VCENTER = 0x00000004;
    private const uint DT_SINGLELINE = 0x00000020;
    private const uint DT_END_ELLIPSIS = 0x00008000;
    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_LARGEICON = 0x000000000;
    private const int DI_NORMAL = 0x0003;
    private const int TRANSPARENT = 1;

    private static nint _pill;
    private static nint _toggle;
    private static nint _panel;
    private static nint _instance;
    private static bool _registered;
    private static bool _expanded;
    private static bool _opening;
    private static DateTime _animationStart;
    private static int _panelX;
    private static int _panelY;
    private static int _panelWidth;
    private static int _panelHeight;
    private static bool _opensUp;
    private static QuickLauncherConfig _config = new();
    private static List<QuickAccessItem> _items = [];
    private static readonly List<nint> Icons = [];

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASSEX
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
        public nint lpszMenuName;
        public nint lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; public int Width => Right - Left; public int Height => Bottom - Top; }
    [StructLayout(LayoutKind.Sequential)] private struct PAINTSTRUCT { public nint hdc; public int fErase; public RECT rcPaint; public int fRestore; public int fIncUpdate; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO { public nint hIcon; public int iIcon; public uint dwAttributes; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public uint cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WNDCLASSEX cls);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowEx(int exStyle, string cls, string name, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")] private static extern nint DefWindowProc(nint hwnd, uint msg, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern nint SetTimer(nint hwnd, nint id, uint ms, nint callback);
    [DllImport("user32.dll")] private static extern bool KillTimer(nint hwnd, nint id);
    [DllImport("user32.dll")] private static extern bool InvalidateRect(nint hwnd, nint rect, bool erase);
    [DllImport("user32.dll")] private static extern nint BeginPaint(nint hwnd, out PAINTSTRUCT ps);
    [DllImport("user32.dll")] private static extern bool EndPaint(nint hwnd, ref PAINTSTRUCT ps);
    [DllImport("user32.dll")] private static extern int FillRect(nint hdc, ref RECT rect, nint brush);
    [DllImport("gdi32.dll")] private static extern nint CreateSolidBrush(uint color);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] private static extern uint SetTextColor(nint hdc, uint color);
    [DllImport("gdi32.dll")] private static extern int SetBkMode(nint hdc, int mode);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int DrawText(nint hdc, string text, int count, ref RECT rect, uint format);
    [DllImport("user32.dll")] private static extern bool DrawIconEx(nint hdc, int x, int y, nint icon, int cx, int cy, uint step, nint brush, int flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHGetFileInfoW")] private static extern nint SHGetFileInfo(string path, uint attrs, ref SHFILEINFO info, uint size, uint flags);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(nint monitor, ref MONITORINFO info);
    [DllImport("user32.dll")] private static extern bool SetWindowRgn(nint hwnd, nint region, bool redraw);
    [DllImport("gdi32.dll")] private static extern nint CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);

    public static void Attach(nint pill)
    {
        _pill = pill;
        Reload();
        EnsureWindows();
        Reposition();
    }

    public static void Reload()
    {
        _config = QuickLauncherStore.Load();
        _config.Columns = Math.Clamp(_config.Columns, 1, 8);
        _config.IconSize = Math.Clamp(_config.IconSize, 20, 48);
        _config.AnimationMs = Math.Clamp(_config.AnimationMs, 80, 400);
        _items = QuickAccessStore.Load().Where(x => x.Enabled && !string.IsNullOrWhiteSpace(x.Target)).OrderBy(x => x.Order).ToList();
        ReloadIcons();
        if (!_config.Enabled || _items.Count == 0) Collapse(immediate: true);
        Reposition();
        if (_toggle != 0) InvalidateRect(_toggle, 0, true);
        if (_panel != 0) InvalidateRect(_panel, 0, true);
    }

    public static void Reposition()
    {
        if (_pill == 0 || _toggle == 0 || !GetWindowRect(_pill, out var pill)) return;

        int toggleX = pill.Right - ToggleSize - 6;
        int toggleY = pill.Top + (pill.Height - ToggleSize) / 2;
        SetWindowPos(_toggle, 0, toggleX, toggleY, ToggleSize, ToggleSize, SWP_NOACTIVATE | SWP_NOZORDER);

        if (!_config.Enabled || _items.Count == 0)
        {
            ShowWindow(_toggle, SW_HIDE);
            return;
        }
        ShowWindow(_toggle, SW_SHOWNOACTIVATE);

        int columns = Math.Min(_config.Columns, Math.Max(1, _items.Count));
        int rows = (_items.Count + columns - 1) / columns;
        int cellHeight = _config.ShowLabels ? CellHeightLabels : CellHeightCompact;
        _panelWidth = Padding * 2 + columns * CellWidth;
        _panelHeight = Padding * 2 + rows * cellHeight;

        nint monitor = MonitorFromWindow(_pill, 2);
        var mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(monitor, ref mi);

        bool roomBelow = pill.Bottom + Gap + _panelHeight <= mi.rcWork.Bottom;
        _opensUp = _config.Direction == QuickLauncherDirection.Up || (_config.Direction == QuickLauncherDirection.Auto && !roomBelow);

        _panelX = Math.Clamp(pill.Right - _panelWidth, mi.rcWork.Left, Math.Max(mi.rcWork.Left, mi.rcWork.Right - _panelWidth));
        _panelY = _opensUp ? pill.Top - Gap - _panelHeight : pill.Bottom + Gap;

        if (_expanded && _panel != 0)
            SetWindowPos(_panel, 0, _panelX, _panelY, _panelWidth, _panelHeight, SWP_NOACTIVATE | SWP_NOZORDER);
    }

    public static void Toggle()
    {
        if (!_config.Enabled || _items.Count == 0) return;
        if (_expanded) Collapse(false); else Expand();
    }

    public static void Collapse(bool immediate)
    {
        if (_panel == 0) return;
        if (immediate)
        {
            KillTimer(_panel, AnimationTimer);
            _expanded = false;
            ShowWindow(_panel, SW_HIDE);
            InvalidateRect(_toggle, 0, true);
            return;
        }
        if (!_expanded) return;
        _opening = false;
        _animationStart = DateTime.UtcNow;
        SetTimer(_panel, AnimationTimer, 16, 0);
    }

    public static void Dispose()
    {
        ReloadIcons(clearOnly: true);
        _pill = 0;
    }

    private static void Expand()
    {
        EnsureWindows();
        Reposition();
        _expanded = true;
        _opening = true;
        _animationStart = DateTime.UtcNow;
        ShowWindow(_panel, SW_SHOWNOACTIVATE);
        SetWindowPos(_panel, 0, _panelX, _opensUp ? _panelY + _panelHeight - 1 : _panelY, _panelWidth, 1, SWP_NOACTIVATE | SWP_NOZORDER);
        SetTimer(_panel, AnimationTimer, 16, 0);
        InvalidateRect(_toggle, 0, true);
    }

    private static void EnsureWindows()
    {
        if (!_registered)
        {
            _instance = GetModuleHandle(null);
            Register(ToggleClass, (nint)(delegate* unmanaged<nint, uint, nint, nint, nint>)&ToggleProc);
            Register(PanelClass, (nint)(delegate* unmanaged<nint, uint, nint, nint, nint>)&PanelProc);
            _registered = true;
        }
        if (_toggle == 0)
            _toggle = CreateWindowEx(WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE, ToggleClass, "", WS_POPUP, 0, 0, ToggleSize, ToggleSize, _pill, 0, _instance, 0);
        if (_panel == 0)
            _panel = CreateWindowEx(WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE, PanelClass, "", WS_POPUP, 0, 0, 1, 1, _pill, 0, _instance, 0);
    }

    private static void Register(string className, nint proc)
    {
        var cls = new WNDCLASSEX { cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(), lpfnWndProc = proc, hInstance = _instance, hCursor = NativeMethods.LoadCursor(0, (nint)32512) };
        nint p = Marshal.StringToHGlobalUni(className);
        try { cls.lpszClassName = p; RegisterClassEx(ref cls); }
        finally { Marshal.FreeHGlobal(p); }
    }

    [UnmanagedCallersOnly]
    private static nint ToggleProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        try
        {
            if (msg == WM_LBUTTONUP) { Toggle(); return 0; }
            if (msg == WM_PAINT) { PaintToggle(hwnd); return 0; }
            if (msg == WM_DESTROY) { _toggle = 0; return 0; }
        }
        catch { }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    [UnmanagedCallersOnly]
    private static nint PanelProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        try
        {
            if (msg == WM_PAINT) { PaintPanel(hwnd); return 0; }
            if (msg == WM_LBUTTONUP) { LaunchAt(lParam); return 0; }
            if (msg == WM_TIMER && wParam == AnimationTimer) { Animate(); return 0; }
            if (msg == WM_DESTROY) { _panel = 0; return 0; }
        }
        catch { }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private static void Animate()
    {
        double duration = Math.Max(80, _config.AnimationMs);
        double t = Math.Clamp((DateTime.UtcNow - _animationStart).TotalMilliseconds / duration, 0, 1);
        double eased = 1 - Math.Pow(1 - t, 3);
        double factor = _opening ? eased : 1 - eased;
        int h = Math.Max(1, (int)Math.Round(_panelHeight * factor));
        int y = _opensUp ? _panelY + _panelHeight - h : _panelY;
        SetWindowPos(_panel, 0, _panelX, y, _panelWidth, h, SWP_NOACTIVATE | SWP_NOZORDER);
        nint rgn = CreateRoundRectRgn(0, 0, _panelWidth + 1, h + 1, 18, 18);
        SetWindowRgn(_panel, rgn, true);
        if (t < 1) return;
        KillTimer(_panel, AnimationTimer);
        if (!_opening)
        {
            _expanded = false;
            ShowWindow(_panel, SW_HIDE);
            InvalidateRect(_toggle, 0, true);
        }
    }

    private static void PaintToggle(nint hwnd)
    {
        nint dc = BeginPaint(hwnd, out var ps);
        var rect = new RECT { Left = 0, Top = 0, Right = ToggleSize, Bottom = ToggleSize };
        nint bg = CreateSolidBrush(0x00201D1B);
        FillRect(dc, ref rect, bg);
        DeleteObject(bg);
        SetBkMode(dc, TRANSPARENT);
        SetTextColor(dc, 0x00BED12A);
        string glyph = _expanded ? "⌃" : "⌄";
        DrawText(dc, glyph, glyph.Length, ref rect, DT_CENTER | DT_VCENTER | DT_SINGLELINE);
        EndPaint(hwnd, ref ps);
    }

    private static void PaintPanel(nint hwnd)
    {
        nint dc = BeginPaint(hwnd, out var ps);
        GetWindowRect(hwnd, out var wr);
        var bgRect = new RECT { Left = 0, Top = 0, Right = wr.Width, Bottom = wr.Height };
        nint bg = CreateSolidBrush(0x0013100F);
        FillRect(dc, ref bgRect, bg);
        DeleteObject(bg);
        SetBkMode(dc, TRANSPARENT);

        int columns = Math.Min(_config.Columns, Math.Max(1, _items.Count));
        int cellHeight = _config.ShowLabels ? CellHeightLabels : CellHeightCompact;
        for (int i = 0; i < _items.Count; i++)
        {
            int col = i % columns;
            int row = i / columns;
            int left = Padding + col * CellWidth;
            int top = Padding + row * cellHeight;
            int iconSize = _config.IconSize;
            int iconX = left + (CellWidth - iconSize) / 2;
            int iconY = top + 4;
            if (i < Icons.Count && Icons[i] != 0)
                DrawIconEx(dc, iconX, iconY, Icons[i], iconSize, iconSize, 0, 0, DI_NORMAL);

            if (_config.ShowLabels)
            {
                var text = new RECT { Left = left + 3, Top = iconY + iconSize + 5, Right = left + CellWidth - 3, Bottom = top + cellHeight - 3 };
                SetTextColor(dc, 0x00F5F0EA);
                DrawText(dc, _items[i].Name, _items[i].Name.Length, ref text, DT_CENTER | DT_VCENTER | DT_SINGLELINE | DT_END_ELLIPSIS);
            }
        }
        EndPaint(hwnd, ref ps);
    }

    private static void LaunchAt(nint lParam)
    {
        int x = unchecked((short)((long)lParam & 0xFFFF));
        int y = unchecked((short)(((long)lParam >> 16) & 0xFFFF));
        int columns = Math.Min(_config.Columns, Math.Max(1, _items.Count));
        int cellHeight = _config.ShowLabels ? CellHeightLabels : CellHeightCompact;
        int col = (x - Padding) / CellWidth;
        int row = (y - Padding) / cellHeight;
        if (x < Padding || y < Padding || col < 0 || col >= columns || row < 0) return;
        int index = row * columns + col;
        if ((uint)index >= (uint)_items.Count) return;

        var item = _items[index];
        try
        {
            string target = Environment.ExpandEnvironmentVariables(item.Target);
            string args = Environment.ExpandEnvironmentVariables(item.Arguments ?? string.Empty);
            var start = new ProcessStartInfo
            {
                FileName = target,
                Arguments = args,
                WorkingDirectory = string.IsNullOrWhiteSpace(item.WorkingDirectory) ? ResolveWorkingDirectory(target) : Environment.ExpandEnvironmentVariables(item.WorkingDirectory),
                UseShellExecute = true,
            };
            Process.Start(start);
            if (_config.CollapseOnLaunch) Collapse(false);
        }
        catch { }
    }

    private static string ResolveWorkingDirectory(string target)
    {
        if (File.Exists(target)) return Path.GetDirectoryName(target) ?? string.Empty;
        return string.Empty;
    }

    private static void ReloadIcons(bool clearOnly = false)
    {
        foreach (nint icon in Icons) if (icon != 0) DestroyIcon(icon);
        Icons.Clear();
        if (clearOnly) return;
        foreach (var item in _items)
        {
            string source = item.IconMode == QuickAccessIconMode.Custom && !string.IsNullOrWhiteSpace(item.IconPath) ? item.IconPath : item.Target;
            source = Environment.ExpandEnvironmentVariables(source);
            var info = new SHFILEINFO();
            nint ok = SHGetFileInfo(source, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_LARGEICON);
            Icons.Add(ok == 0 ? 0 : info.hIcon);
        }
    }
}
