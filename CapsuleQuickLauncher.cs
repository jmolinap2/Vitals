using System.Diagnostics;
using System.Runtime.InteropServices;
using Vitals.Shared;

namespace Vitals;

/// <summary>
/// Launcher visual acoplado a la cápsula. El panel vive en su propia ventana
/// Win32, pero se comporta y se dibuja como un drawer de la cápsula.
/// </summary>
internal static unsafe class CapsuleQuickLauncher
{
    private const string PanelClass = "VitalsLauncherPanel";

    private const int PanelGap = 3;
    private const int CellWidth = 88;
    private const int PanelPadding = 10;
    private const int ItemInset = 4;
    private const int PanelRadius = 18;

    private const nint AnimationTimer = 31;
    private const uint WM_PAINT = 0x000F;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_MOUSEMOVE = 0x0200;
    private const uint WM_MOUSELEAVE = 0x02A3;
    private const uint WM_TIMER = 0x0113;
    private const uint WM_DESTROY = 0x0002;
    private const uint WS_POPUP = 0x80000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int SW_HIDE = 0;
    private const int SW_SHOWNOACTIVATE = 4;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint DT_CENTER = 0x00000001;
    private const uint DT_VCENTER = 0x00000004;
    private const uint DT_SINGLELINE = 0x00000020;
    private const uint DT_END_ELLIPSIS = 0x00008000;
    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_LARGEICON = 0x000000000;
    private const int DI_NORMAL = 0x0003;
    private const int TRANSPARENT = 1;
    private const uint LWA_ALPHA = 0x00000002;
    private const uint TME_LEAVE = 0x00000002;
    private const int DEFAULT_GUI_FONT = 17;
    private const int NULL_PEN = 8;
    private const int PS_SOLID = 0;

    private static readonly uint Background = Rgb(7, 9, 12);
    private static readonly uint Border = Rgb(48, 54, 64);
    private static readonly uint Hover = Rgb(27, 32, 39);
    private static readonly uint Text = Rgb(236, 240, 244);
    private static readonly uint Accent = Rgb(42, 209, 190);

    private static nint _pill;
    private static nint _panel;
    private static nint _instance;
    private static bool _registered;
    private static bool _expanded;
    private static bool _opening;
    private static bool _trackingMouse;
    private static DateTime _animationStart;
    private static int _panelX;
    private static int _panelY;
    private static int _panelWidth;
    private static int _panelHeight;
    private static bool _opensUp;
    private static bool _opensHorizontally;
    private static bool _opensRight;
    private static int _hoverIndex = -1;
    private static double _animationFactor;
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
    [StructLayout(LayoutKind.Sequential)]
    private struct TRACKMOUSEEVENT { public uint cbSize; public uint dwFlags; public nint hwndTrack; public uint dwHoverTime; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WNDCLASSEX cls);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowEx(int exStyle, string cls, string name, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")] private static extern nint DefWindowProc(nint hwnd, uint msg, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern nint SetTimer(nint hwnd, nint id, uint ms, nint callback);
    [DllImport("user32.dll")] private static extern bool KillTimer(nint hwnd, nint id);
    [DllImport("user32.dll")] private static extern bool InvalidateRect(nint hwnd, nint rect, bool erase);
    [DllImport("user32.dll")] private static extern nint BeginPaint(nint hwnd, out PAINTSTRUCT ps);
    [DllImport("user32.dll")] private static extern bool EndPaint(nint hwnd, ref PAINTSTRUCT ps);
    [DllImport("user32.dll")] private static extern int FillRect(nint hdc, ref RECT rect, nint brush);
    [DllImport("user32.dll")] private static extern bool DrawIconEx(nint hdc, int x, int y, nint icon, int cx, int cy, uint step, nint brush, int flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int DrawText(nint hdc, string text, int count, ref RECT rect, uint format);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(nint monitor, ref MONITORINFO info);
    [DllImport("user32.dll")] private static extern bool SetWindowRgn(nint hwnd, nint region, bool redraw);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(nint hwnd, uint colorKey, byte alpha, uint flags);
    [DllImport("user32.dll")] private static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT trackEvent);

    [DllImport("gdi32.dll")] private static extern nint CreateSolidBrush(uint color);
    [DllImport("gdi32.dll")] private static extern nint CreatePen(int style, int width, uint color);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] private static extern nint GetStockObject(int objectId);
    [DllImport("gdi32.dll")] private static extern uint SetTextColor(nint hdc, uint color);
    [DllImport("gdi32.dll")] private static extern int SetBkMode(nint hdc, int mode);
    [DllImport("gdi32.dll")] private static extern bool RoundRect(nint hdc, int left, int top, int right, int bottom, int width, int height);
    [DllImport("gdi32.dll")] private static extern nint CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHGetFileInfoW")] private static extern nint SHGetFileInfo(string path, uint attrs, ref SHFILEINFO info, uint size, uint flags);

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
        _items = QuickAccessStore.Load()
            .Where(x => x.Enabled && !string.IsNullOrWhiteSpace(x.Target))
            .OrderBy(x => x.Order)
            .ToList();

        ReloadIcons();
        _hoverIndex = -1;
        if (!_config.Enabled || _items.Count == 0) Collapse(immediate: true);
        Reposition();
        if (_panel != 0) InvalidateRect(_panel, 0, false);
    }

    public static void Reposition()
    {
        if (_pill == 0 || !GetWindowRect(_pill, out var pill)) return;

        if (!_config.Enabled || _items.Count == 0)
        {
            Collapse(immediate: true);
            return;
        }

        GetGrid(out int columns, out int rows);
        int cellHeight = CellHeight();
        int naturalWidth = PanelPadding * 2 + columns * CellWidth;

        // El panel toma el tamaño de sus accesos. Un único acceso no debe
        // convertirse en una franja del ancho completo de la cápsula.
        _panelWidth = naturalWidth;
        _panelHeight = PanelPadding * 2 + rows * cellHeight;

        nint monitor = MonitorFromWindow(_pill, 2);
        var mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(monitor, ref mi);

        bool roomBelow = pill.Bottom + PanelGap + _panelHeight <= mi.rcWork.Bottom;
        _opensUp = _config.Direction == QuickLauncherDirection.Up
            || (_config.Direction == QuickLauncherDirection.Auto && !roomBelow);
        _opensHorizontally = _config.Direction is QuickLauncherDirection.Left or QuickLauncherDirection.Right;
        _opensRight = _config.Direction == QuickLauncherDirection.Right;

        _panelWidth = Math.Min(_panelWidth, mi.rcWork.Width);
        _panelHeight = Math.Min(_panelHeight, mi.rcWork.Height);
        if (_opensHorizontally)
        {
            _panelX = _opensRight ? pill.Right + PanelGap : pill.Left - PanelGap - _panelWidth;
            _panelY = pill.Bottom - _panelHeight;
        }
        else
        {
            _panelX = pill.Right - _panelWidth;
            _panelY = _opensUp
                ? pill.Top - PanelGap - _panelHeight
                : pill.Bottom + PanelGap;
        }
        _panelX = Math.Clamp(_panelX, mi.rcWork.Left, Math.Max(mi.rcWork.Left, mi.rcWork.Right - _panelWidth));
        _panelY = Math.Clamp(_panelY, mi.rcWork.Top, Math.Max(mi.rcWork.Top, mi.rcWork.Bottom - _panelHeight));

        if (_expanded && _panel != 0)
            SetWindowPos(_panel, 0, _panelX, _panelY, _panelWidth, _panelHeight,
                SWP_NOACTIVATE | SWP_NOZORDER);
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
            _animationFactor = 0;
            _hoverIndex = -1;
            ShowWindow(_panel, SW_HIDE);
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
        _animationFactor = 0;
        _animationStart = DateTime.UtcNow;
        _hoverIndex = -1;

        SetLayeredWindowAttributes(_panel, 0, 0, LWA_ALPHA);
        ShowWindow(_panel, SW_SHOWNOACTIVATE);
        int x = _opensHorizontally && !_opensRight ? _panelX + _panelWidth - 1 : _panelX;
        int y = !_opensHorizontally && _opensUp ? _panelY + _panelHeight - 1 : _panelY;
        int width = _opensHorizontally ? 1 : _panelWidth;
        int height = _opensHorizontally ? _panelHeight : 1;
        SetWindowPos(_panel, 0, x, y, width, height, SWP_NOACTIVATE | SWP_NOZORDER);
        SetTimer(_panel, AnimationTimer, 16, 0);
    }

    private static void EnsureWindows()
    {
        if (!_registered)
        {
            _instance = GetModuleHandle(null);
            Register(PanelClass, (nint)(delegate* unmanaged<nint, uint, nint, nint, nint>)&PanelProc);
            _registered = true;
        }

        if (_panel == 0)
            _panel = CreateWindowEx(
                WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE | WS_EX_LAYERED,
                PanelClass, "", WS_POPUP, 0, 0, 1, 1,
                _pill, 0, _instance, 0);
    }

    private static void Register(string className, nint proc)
    {
        var cls = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = proc,
            hInstance = _instance,
            hCursor = NativeMethods.LoadCursor(0, (nint)32512),
        };
        nint p = Marshal.StringToHGlobalUni(className);
        try
        {
            cls.lpszClassName = p;
            RegisterClassEx(ref cls);
        }
        finally
        {
            Marshal.FreeHGlobal(p);
        }
    }

    [UnmanagedCallersOnly]
    private static nint PanelProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        try
        {
            if (msg == WM_PAINT) { PaintPanel(hwnd); return 0; }
            if (msg == WM_LBUTTONUP) { LaunchAt(lParam); return 0; }
            if (msg == WM_MOUSEMOVE) { HandleMouseMove(hwnd, lParam); return 0; }
            if (msg == WM_MOUSELEAVE) { HandleMouseLeave(); return 0; }
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
        _animationFactor = _opening ? eased : 1 - eased;

        int width = _opensHorizontally ? Math.Max(1, (int)Math.Round(_panelWidth * _animationFactor)) : _panelWidth;
        int height = _opensHorizontally ? _panelHeight : Math.Max(1, (int)Math.Round(_panelHeight * _animationFactor));
        int x = _opensHorizontally && !_opensRight ? _panelX + _panelWidth - width : _panelX;
        int y = !_opensHorizontally && _opensUp ? _panelY + _panelHeight - height : _panelY;
        SetWindowPos(_panel, 0, x, y, width, height,
            SWP_NOACTIVATE | SWP_NOZORDER);

        nint region = CreateRoundRectRgn(0, 0, width + 1, height + 1,
            PanelRadius, PanelRadius);
        SetWindowRgn(_panel, region, true);

        byte alpha = (byte)Math.Clamp(120 + 135 * _animationFactor, 0, 255);
        SetLayeredWindowAttributes(_panel, 0, alpha, LWA_ALPHA);
        if (t < 1) return;
        KillTimer(_panel, AnimationTimer);

        if (!_opening)
        {
            _expanded = false;
            _animationFactor = 0;
            _hoverIndex = -1;
            ShowWindow(_panel, SW_HIDE);
        }
        else
        {
            _animationFactor = 1;
            SetLayeredWindowAttributes(_panel, 0, 255, LWA_ALPHA);
        }
    }

    private static void PaintPanel(nint hwnd)
    {
        nint dc = BeginPaint(hwnd, out var ps);
        GetClientRect(hwnd, out var client);

        nint bg = CreateSolidBrush(Background);
        FillRect(dc, ref client, bg);
        DeleteObject(bg);

        // Borde sutil: el panel conserva el mismo lenguaje visual de la cápsula.
        nint borderPen = CreatePen(PS_SOLID, 1, Border);
        nint nullBrush = GetStockObject(5); // NULL_BRUSH
        nint oldBrush = SelectObject(dc, nullBrush);
        nint oldPen = SelectObject(dc, borderPen);
        RoundRect(dc, 0, 0, Math.Max(1, client.Right - 1), Math.Max(1, client.Bottom - 1),
            PanelRadius, PanelRadius);
        SelectObject(dc, oldPen);
        SelectObject(dc, oldBrush);
        DeleteObject(borderPen);

        SetBkMode(dc, TRANSPARENT);
        SelectObject(dc, GetStockObject(DEFAULT_GUI_FONT));

        GetGrid(out int columns, out int rows);
        int cellHeight = CellHeight();

        for (int row = 0; row < rows; row++)
        {
            int first = row * columns;
            int rowCount = Math.Min(columns, _items.Count - first);
            int rowLeft = (_panelWidth - rowCount * CellWidth) / 2;

            for (int col = 0; col < rowCount; col++)
            {
                int i = first + col;
                int left = rowLeft + col * CellWidth;
                int top = PanelPadding + row * cellHeight;

                if (i == _hoverIndex)
                    DrawHover(dc, left + ItemInset, top + ItemInset,
                        CellWidth - ItemInset * 2, cellHeight - ItemInset * 2);

                int iconSize = _config.IconSize;
                int iconX = left + (CellWidth - iconSize) / 2;
                int iconY = top + (_config.ShowLabels ? 7 : (cellHeight - iconSize) / 2);
                if (i < Icons.Count && Icons[i] != 0)
                    DrawIconEx(dc, iconX, iconY, Icons[i], iconSize, iconSize, 0, 0, DI_NORMAL);

                if (_config.ShowLabels)
                {
                    var textRect = new RECT
                    {
                        Left = left + 5,
                        Top = iconY + iconSize + 4,
                        Right = left + CellWidth - 5,
                        Bottom = top + cellHeight - 4,
                    };
                    SetTextColor(dc, Text);
                    DrawText(dc, _items[i].Name, _items[i].Name.Length, ref textRect,
                        DT_CENTER | DT_VCENTER | DT_SINGLELINE | DT_END_ELLIPSIS);
                }
            }
        }

        EndPaint(hwnd, ref ps);
    }

    private static void DrawHover(nint dc, int x, int y, int width, int height)
    {
        nint brush = CreateSolidBrush(Hover);
        nint oldBrush = SelectObject(dc, brush);
        nint oldPen = SelectObject(dc, GetStockObject(NULL_PEN));
        RoundRect(dc, x, y, x + width, y + height, 12, 12);
        SelectObject(dc, oldPen);
        SelectObject(dc, oldBrush);
        DeleteObject(brush);
    }

    private static void HandleMouseMove(nint hwnd, nint lParam)
    {
        if (!_trackingMouse)
        {
            var track = new TRACKMOUSEEVENT
            {
                cbSize = (uint)Marshal.SizeOf<TRACKMOUSEEVENT>(),
                dwFlags = TME_LEAVE,
                hwndTrack = hwnd,
            };
            TrackMouseEvent(ref track);
            _trackingMouse = true;
        }

        int x = unchecked((short)((long)lParam & 0xFFFF));
        int y = unchecked((short)(((long)lParam >> 16) & 0xFFFF));
        int next = HitTest(x, y);
        if (next == _hoverIndex) return;
        _hoverIndex = next;
        InvalidateRect(_panel, 0, false);
    }

    private static void HandleMouseLeave()
    {
        _trackingMouse = false;
        if (_hoverIndex == -1) return;
        _hoverIndex = -1;
        if (_panel != 0) InvalidateRect(_panel, 0, false);
    }

    private static int HitTest(int x, int y)
    {
        if (y < PanelPadding) return -1;
        GetGrid(out int columns, out _);
        int cellHeight = CellHeight();
        int row = (y - PanelPadding) / cellHeight;
        if (row < 0) return -1;

        int first = row * columns;
        if (first >= _items.Count) return -1;
        int rowCount = Math.Min(columns, _items.Count - first);
        int rowLeft = (_panelWidth - rowCount * CellWidth) / 2;
        if (x < rowLeft || x >= rowLeft + rowCount * CellWidth) return -1;

        int col = (x - rowLeft) / CellWidth;
        int index = first + col;
        return (uint)index < (uint)_items.Count ? index : -1;
    }

    private static void LaunchAt(nint lParam)
    {
        int x = unchecked((short)((long)lParam & 0xFFFF));
        int y = unchecked((short)(((long)lParam >> 16) & 0xFFFF));
        int index = HitTest(x, y);
        if (index < 0) return;

        var item = _items[index];
        try
        {
            string target = Environment.ExpandEnvironmentVariables(item.Target);
            string args = Environment.ExpandEnvironmentVariables(item.Arguments ?? string.Empty);
            var start = new ProcessStartInfo
            {
                FileName = target,
                Arguments = args,
                WorkingDirectory = string.IsNullOrWhiteSpace(item.WorkingDirectory)
                    ? ResolveWorkingDirectory(target)
                    : Environment.ExpandEnvironmentVariables(item.WorkingDirectory),
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

    private static int CellHeight() =>
        _config.ShowLabels ? Math.Max(62, _config.IconSize + 31) : Math.Max(50, _config.IconSize + 14);

    private static void GetGrid(out int columns, out int rows)
    {
        int count = Math.Max(1, _items.Count);
        columns = _config.Layout switch
        {
            QuickLauncherLayout.Row => count,
            QuickLauncherLayout.Column => 1,
            _ => Math.Min(_config.Columns, count),
        };
        rows = (count + columns - 1) / columns;
    }

    private static void ReloadIcons(bool clearOnly = false)
    {
        foreach (nint icon in Icons)
            if (icon != 0) DestroyIcon(icon);
        Icons.Clear();
        if (clearOnly) return;

        foreach (var item in _items)
        {
            string source = item.IconMode == QuickAccessIconMode.Custom && !string.IsNullOrWhiteSpace(item.IconPath)
                ? item.IconPath
                : item.Target;
            source = Environment.ExpandEnvironmentVariables(source);
            var info = new SHFILEINFO();
            nint ok = SHGetFileInfo(source, 0, ref info,
                (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_LARGEICON);
            Icons.Add(ok == 0 ? 0 : info.hIcon);
        }
    }

    private static uint Rgb(byte r, byte g, byte b) =>
        (uint)(r | (g << 8) | (b << 16));
}
