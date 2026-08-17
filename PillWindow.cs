using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using Vitals.Shared;
using static Vitals.NativeMethods;

namespace Vitals;

internal enum IconKind { Cpu, Gpu, Ram, Up, Down, Battery }

internal sealed class RingBuffer(int capacity)
{
    private readonly double[] _values = new double[capacity];
    private int _count;
    private int _head;

    public void Push(double v)
    {
        _values[_head] = v;
        _head = (_head + 1) % _values.Length;
        if (_count < _values.Length) _count++;
    }

    public double[] Ordered()
    {
        var result = new double[_count];
        int start = (_head - _count + _values.Length) % _values.Length;
        for (int i = 0; i < _count; i++)
            result[i] = _values[(start + i) % _values.Length];
        return result;
    }

    public double Max() => _count == 0 ? 0 : Ordered().Max();
}

internal readonly record struct Accent(uint Stroke, uint Fill, uint Text);

internal static class PillWindow
{
    private const string ClassName = "VitalsPillWindow";
    private const int ColumnPixelWidth = 96;
    private const int PillHeight = 104;
    private const int ScreenMargin = 12;
    private const int CornerRadius = 24;
    private const int HistoryLength = 30;

    private static List<MetricEntry> _enabledMetrics = [];
    private static int _pillWidth;

    private const nint DataTimerId = 1;
    private const nint AnimTimerId = 2;
    private const int AnimFrameMs = 33;
    private const double AnimDurationMs = 350;

    private static readonly MetricsSampler Sampler = new();
    private static Snapshot _snapshot;
    private static Snapshot _animFrom;
    private static Snapshot _animTo;
    private static DateTime _animStart;
    private static bool _animRunning;

    private static readonly RingBuffer CpuHistory = new(HistoryLength);
    private static readonly RingBuffer GpuHistory = new(HistoryLength);
    private static readonly RingBuffer RamHistory = new(HistoryLength);
    private static readonly RingBuffer NetUpHistory = new(HistoryLength);
    private static readonly RingBuffer NetDownHistory = new(HistoryLength);

    // Colores fijos por métrica, como en la referencia — nada de recoloreo
    // por umbral: la propia gráfica ya comunica cuándo algo está cargado.
    private static readonly Accent CpuAccent = MakeAccent(77, 140, 255);
    private static readonly Accent GpuAccent = MakeAccent(61, 214, 125);
    private static readonly Accent RamAccent = MakeAccent(168, 127, 255);
    private static readonly Accent UpAccent = MakeAccent(255, 159, 67);
    private static readonly Accent DownAccent = MakeAccent(86, 196, 255);
    private static readonly Accent BatteryAccent = MakeAccent(76, 217, 100);

    private static Accent MakeAccent(byte r, byte g, byte b) => new(
        Stroke: Gdip.Argb(255, r, g, b),
        Fill: Gdip.Argb(46, r, g, b),
        Text: Gdip.Argb(255, r, g, b));

    private static NOTIFYICONDATA _trayIcon;
    private static nint _bgBrush;
    private static nint _labelBrush;
    private static nint _labelFont;
    private static nint _valueFont;
    private static nint _centerFormat;
    private static nint _leftFormat;

    public static unsafe void Run()
    {
        Gdip.Startup();

        var config = VitalsConfig.Load();
        _enabledMetrics = config.Metrics.Where(m => m.Enabled).ToList();
        if (_enabledMetrics.Count == 0) _enabledMetrics = VitalsConfig.DefaultOrder();
        _pillWidth = _enabledMetrics.Count * ColumnPixelWidth;

        nint hInstance = GetModuleHandle(null);
        var wndProcPtr = (nint)(delegate* unmanaged<nint, uint, nint, nint, nint>)&WndProc;

        var wndClass = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = wndProcPtr,
            hInstance = hInstance,
            hCursor = LoadCursor(0, (nint)32512), // IDC_ARROW
            hbrBackground = 0,
            lpszClassName = ClassName,
        };
        RegisterClassEx(ref wndClass);

        var workArea = new RECT();
        SystemParametersInfo(SPI_GETWORKAREA, 0, ref workArea, 0);
        int x = workArea.Right - _pillWidth - ScreenMargin;
        int y = workArea.Bottom - PillHeight - ScreenMargin;

        nint hwnd = CreateWindowEx(
            WS_EX_LAYERED | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            ClassName, "Vitals",
            WS_POPUP,
            x, y, _pillWidth, PillHeight,
            0, 0, hInstance, 0);

        if (hwnd == 0) return;

        SetWindowRgn(hwnd, CreateRoundRectRgn(0, 0, _pillWidth, PillHeight, CornerRadius * 2, CornerRadius * 2), true);
        SetLayeredWindowAttributes(hwnd, 0, 250, LWA_ALPHA);

        _bgBrush = 0;
        Gdip.GdipCreateSolidFill(Gdip.Argb(255, 16, 18, 26), out _bgBrush);
        Gdip.GdipCreateSolidFill(Gdip.Argb(255, 150, 160, 168), out _labelBrush);

        Gdip.GdipCreateFontFamilyFromName("Segoe UI", 0, out nint labelFamily);
        Gdip.GdipCreateFont(labelFamily, 12.5f, Gdip.FontStyleRegular, Gdip.UnitPixel, out _labelFont);
        Gdip.GdipCreateFontFamilyFromName("Cascadia Mono", 0, out nint valueFamily);
        Gdip.GdipCreateFont(valueFamily, 17f, Gdip.FontStyleBold, Gdip.UnitPixel, out _valueFont);

        Gdip.GdipCreateStringFormat(0, 0, out _centerFormat);
        Gdip.GdipSetStringFormatAlign(_centerFormat, Gdip.StringAlignCenter);
        Gdip.GdipSetStringFormatLineAlign(_centerFormat, Gdip.StringAlignCenter);

        Gdip.GdipCreateStringFormat(0, 0, out _leftFormat);
        Gdip.GdipSetStringFormatAlign(_leftFormat, Gdip.StringAlignNear);
        Gdip.GdipSetStringFormatLineAlign(_leftFormat, Gdip.StringAlignCenter);

        AddTrayIcon(hwnd);

        _snapshot = _animFrom = _animTo = Sampler.Sample();
        ShowWindow(hwnd, SW_SHOWNOACTIVATE);
        SetTimer(hwnd, DataTimerId, 1000, 0);

        while (GetMessage(out var msg, 0, 0, 0))
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        Shell_NotifyIcon(NIM_DELETE, ref _trayIcon);
    }

    private static unsafe void AddTrayIcon(nint hwnd)
    {
        _trayIcon = new NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = hwnd,
            uID = TrayIconId,
            uFlags = NIF_MESSAGE | NIF_ICON,
            uCallbackMessage = WM_TRAYICON,
            hIcon = LoadIcon(0, (nint)32512), // IDI_APPLICATION — ícono propio queda como pulido posterior
        };
        Shell_NotifyIcon(NIM_ADD, ref _trayIcon);
    }

    private static void ShowTrayMenu(nint hwnd)
    {
        GetCursorPos(out var pt);
        nint menu = CreatePopupMenu();
        AppendMenu(menu, MF_STRING, IdSettings, "Configuración...");
        AppendMenu(menu, MF_STRING, IdExit, "Salir");
        SetForegroundWindow(hwnd);
        TrackPopupMenu(menu, TPM_RIGHTBUTTON, pt.X, pt.Y, 0, hwnd, 0);
        DestroyMenu(menu);
    }

    private static void OpenSettings()
    {
        string settingsExe = Path.Combine(AppContext.BaseDirectory, "VitalsSettings.exe");
        if (File.Exists(settingsExe))
            Process.Start(settingsExe);
    }

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case WM_TIMER when wParam == DataTimerId:
                _animFrom = _snapshot;
                _animTo = Sampler.Sample();
                _animStart = DateTime.UtcNow;

                CpuHistory.Push(_animTo.CpuPercent);
                GpuHistory.Push(_animTo.GpuPercent ?? 0);
                RamHistory.Push(_animTo.RamPercent);
                NetUpHistory.Push(_animTo.NetUpBytesPerSec);
                NetDownHistory.Push(_animTo.NetDownBytesPerSec);

                if (!_animRunning)
                {
                    _animRunning = true;
                    SetTimer(hWnd, AnimTimerId, AnimFrameMs, 0);
                }
                return 0;

            case WM_TIMER when wParam == AnimTimerId:
                double t = (DateTime.UtcNow - _animStart).TotalMilliseconds / AnimDurationMs;
                if (t >= 1.0)
                {
                    _snapshot = _animTo;
                    KillTimer(hWnd, AnimTimerId);
                    _animRunning = false;
                }
                else
                {
                    _snapshot = Lerp(_animFrom, _animTo, EaseOutCubic(t));
                }
                InvalidateRect(hWnd, 0, false);
                return 0;

            case WM_PAINT:
                Paint(hWnd);
                return 0;

            case WM_NCHITTEST:
                return HTCAPTION;

            case WM_TRAYICON when lParam == (nint)WM_RBUTTONUP || lParam == (nint)WM_LBUTTONUP:
                ShowTrayMenu(hWnd);
                return 0;

            case WM_COMMAND when wParam == IdExit:
                PostQuitMessage(0);
                return 0;

            case WM_COMMAND when wParam == IdSettings:
                OpenSettings();
                return 0;

            case WM_DESTROY:
                PostQuitMessage(0);
                return 0;

            default:
                return DefWindowProc(hWnd, msg, wParam, lParam);
        }
    }

    private static void Paint(nint hWnd)
    {
        nint hdc = BeginPaint(hWnd, out var ps);
        GetClientRect(hWnd, out var client);

        nint memDc = CreateCompatibleDC(hdc);
        nint memBmp = CreateCompatibleBitmap(hdc, client.Width, client.Height);
        nint oldBmp = SelectObject(memDc, memBmp);

        Render(memDc, client);

        BitBlt(hdc, 0, 0, client.Width, client.Height, memDc, 0, 0, SRCCOPY);

        SelectObject(memDc, oldBmp);
        DeleteObject(memBmp);
        DeleteDC(memDc);
        EndPaint(hWnd, ref ps);
    }

    private static void Render(nint memDc, RECT client)
    {
        Gdip.GdipCreateFromHDC(memDc, out nint g);
        Gdip.GdipSetSmoothingMode(g, Gdip.SmoothingModeAntiAlias);
        Gdip.GdipSetTextRenderingHint(g, Gdip.TextRenderingHintAntiAliasGridFit);

        nint bgPath = Gdip.RoundRectPath(0, 0, client.Width, client.Height, CornerRadius);
        Gdip.GdipFillPath(g, _bgBrush, bgPath);
        Gdip.GdipDeletePath(bgPath);

        float colWidth = client.Width / (float)_enabledMetrics.Count;
        var s = _snapshot;

        for (int i = 0; i < _enabledMetrics.Count; i++)
            DrawMetricColumn(g, i, colWidth, _enabledMetrics[i].Key, s);

        Gdip.GdipDeleteGraphics(g);
    }

    private static void DrawMetricColumn(nint g, int index, float colWidth, MetricKey key, Snapshot s)
    {
        switch (key)
        {
            case MetricKey.Cpu:
                DrawColumn(g, index, colWidth, IconKind.Cpu, "CPU", CpuAccent,
                    (gr, x, y, w, h) => DrawLineChart(gr, CpuHistory, x, y, w, h, CpuAccent, 100),
                    $"{s.CpuPercent:0}%");
                break;

            case MetricKey.Gpu:
                DrawColumn(g, index, colWidth, IconKind.Gpu, "GPU", GpuAccent,
                    (gr, x, y, w, h) => DrawLineChart(gr, GpuHistory, x, y, w, h, GpuAccent, 100),
                    s.GpuPercent is { } gpu ? $"{gpu:0}%" : "—");
                break;

            case MetricKey.Ram:
                DrawColumn(g, index, colWidth, IconKind.Ram, "RAM", RamAccent,
                    (gr, x, y, w, h) => DrawBarChart(gr, RamHistory, x, y, w, h, RamAccent, 100),
                    $"{s.RamPercent}%");
                break;

            case MetricKey.Up:
                DrawColumn(g, index, colWidth, IconKind.Up, "Subida", UpAccent,
                    (gr, x, y, w, h) => DrawBarChart(gr, NetUpHistory, x, y, w, h, UpAccent, Math.Max(NetUpHistory.Max(), 20_000)),
                    MetricsSampler.FormatBps(s.NetUpBytesPerSec));
                break;

            case MetricKey.Down:
                DrawColumn(g, index, colWidth, IconKind.Down, "Bajada", DownAccent,
                    (gr, x, y, w, h) => DrawBarChart(gr, NetDownHistory, x, y, w, h, DownAccent, Math.Max(NetDownHistory.Max(), 20_000)),
                    MetricsSampler.FormatBps(s.NetDownBytesPerSec));
                break;

            case MetricKey.Battery:
                DrawColumn(g, index, colWidth, IconKind.Battery, "Batería", BatteryAccent, null,
                    s.BatteryPercent < 0 ? "—" : $"{s.BatteryPercent}%{(s.OnAc ? " ⚡" : "")}");
                break;
        }
    }

    private static void DrawColumn(nint g, int index, float colWidth, IconKind icon, string label, Accent accent,
        Action<nint, float, float, float, float>? chart, string value)
    {
        float colStart = index * colWidth;
        const float iconSize = 15, gap = 6, rowIconY = 13;

        var measureRect = new RectF(0, 0, 400, 24);
        Gdip.GdipMeasureString(g, label, label.Length, _labelFont, ref measureRect, _leftFormat, out var bbox, out _, out _);

        float comboWidth = iconSize + gap + bbox.Width;
        float startX = colStart + (colWidth - comboWidth) / 2f;

        Gdip.GdipCreatePen1(accent.Stroke, 1.6f, Gdip.UnitPixel, out nint pen);
        Gdip.GdipSetPenLineJoin(pen, Gdip.LineJoinRound);
        Gdip.GdipSetPenStartCap(pen, Gdip.LineCapRound);
        Gdip.GdipSetPenEndCap(pen, Gdip.LineCapRound);
        Gdip.GdipCreateSolidFill(accent.Stroke, out nint fillBrush);

        DrawIcon(g, icon, startX, rowIconY, iconSize, pen, fillBrush);

        var labelRect = new RectF(startX + iconSize + gap, rowIconY - 4, bbox.Width + 4, 22);
        Gdip.GdipDrawString(g, label, label.Length, _labelFont, ref labelRect, _leftFormat, _labelBrush);

        Gdip.GdipDeletePen(pen);
        Gdip.GdipDeleteBrush(fillBrush);

        if (chart is not null)
        {
            const float chartTop = 36, chartHeight = 38, chartPad = 10;
            chart(g, colStart + chartPad, chartTop, colWidth - chartPad * 2, chartHeight);
        }

        Gdip.GdipCreateSolidFill(accent.Text, out nint valueBrush);
        var valueRect = new RectF(colStart, 78, colWidth, 22);
        Gdip.GdipDrawString(g, value, value.Length, _valueFont, ref valueRect, _centerFormat, valueBrush);
        Gdip.GdipDeleteBrush(valueBrush);
    }

    private static void DrawIcon(nint g, IconKind kind, float x, float y, float size, nint pen, nint fillBrush)
    {
        switch (kind)
        {
            case IconKind.Cpu:
            case IconKind.Gpu:
            {
                float pad = size * 0.22f;
                float bx = x + pad, by = y + pad, bs = size - pad * 2;
                Gdip.GdipDrawRectangle(g, pen, bx, by, bs, bs);
                for (int i = 0; i < 3; i++)
                {
                    float px = bx + bs * (i + 1) / 4f;
                    Gdip.GdipDrawLine(g, pen, px, y, px, by);
                    Gdip.GdipDrawLine(g, pen, px, by + bs, px, y + size);
                }
                if (kind == IconKind.Gpu)
                {
                    float cx = bx + bs / 2, cy = by + bs / 2, r = bs * 0.24f;
                    Gdip.GdipCreatePath(Gdip.FillModeAlternate, out nint circle);
                    Gdip.GdipAddPathArc(circle, cx - r, cy - r, r * 2, r * 2, 0, 360);
                    Gdip.GdipDrawPath(g, pen, circle);
                    Gdip.GdipDeletePath(circle);
                }
                break;
            }
            case IconKind.Ram:
            {
                float bodyH = size * 0.55f;
                float by = y + size * 0.1f;
                Gdip.GdipDrawRectangle(g, pen, x, by, size, bodyH);
                float pinY = by + bodyH;
                for (int i = 0; i < 4; i++)
                {
                    float px = x + size * (i + 0.75f) / 4f;
                    Gdip.GdipDrawLine(g, pen, px, pinY, px, pinY + size * 0.25f);
                }
                break;
            }
            case IconKind.Up:
            case IconKind.Down:
            {
                float cx = x + size / 2;
                bool up = kind == IconKind.Up;
                float tip = up ? y : y + size;
                float dir = up ? 1 : -1;
                float arm = size * 0.34f;
                Gdip.GdipDrawLine(g, pen, cx, y, cx, y + size);
                Gdip.GdipDrawLine(g, pen, cx, tip, cx - arm, tip + arm * dir);
                Gdip.GdipDrawLine(g, pen, cx, tip, cx + arm, tip + arm * dir);
                break;
            }
            case IconKind.Battery:
            {
                float bodyW = size * 0.78f, bodyH = size * 0.5f;
                float bx = x, by = y + (size - bodyH) / 2;
                Gdip.GdipDrawRectangle(g, pen, bx, by, bodyW, bodyH);
                float nubW = size * 0.1f, nubH = bodyH * 0.4f;
                Gdip.GdipFillRectangle(g, fillBrush, bx + bodyW, by + (bodyH - nubH) / 2, nubW, nubH);
                break;
            }
        }
    }

    private static void DrawLineChart(nint g, RingBuffer history, float x, float y, float w, float h, Accent accent, double scale)
    {
        var values = history.Ordered();
        if (values.Length < 2) return;

        var points = new PointF[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            float px = x + w * i / (values.Length - 1);
            float py = y + h - h * (float)(Math.Clamp(values[i], 0, scale) / scale);
            points[i] = new PointF(px, py);
        }

        var fillPoints = new PointF[points.Length + 2];
        Array.Copy(points, fillPoints, points.Length);
        fillPoints[^2] = new PointF(x + w, y + h);
        fillPoints[^1] = new PointF(x, y + h);

        Gdip.GdipCreateSolidFill(accent.Fill, out nint fillBrush);
        Gdip.GdipCreatePath(Gdip.FillModeAlternate, out nint path);
        Gdip.GdipAddPathPolygon(path, fillPoints, fillPoints.Length);
        Gdip.GdipFillPath(g, fillBrush, path);
        Gdip.GdipDeletePath(path);
        Gdip.GdipDeleteBrush(fillBrush);

        Gdip.GdipCreatePen1(accent.Stroke, 1.8f, Gdip.UnitPixel, out nint pen);
        Gdip.GdipSetPenLineJoin(pen, Gdip.LineJoinRound);
        Gdip.GdipSetPenStartCap(pen, Gdip.LineCapRound);
        Gdip.GdipSetPenEndCap(pen, Gdip.LineCapRound);
        Gdip.GdipDrawLines(g, pen, points, points.Length);
        Gdip.GdipDeletePen(pen);
    }

    private static void DrawBarChart(nint g, RingBuffer history, float x, float y, float w, float h, Accent accent, double scale)
    {
        var values = history.Ordered();
        if (values.Length == 0) return;

        const float gap = 2f;
        float barW = (w - gap * (values.Length - 1)) / values.Length;
        if (barW < 1) barW = 1;

        Gdip.GdipCreateSolidFill(accent.Stroke, out nint brush);
        for (int i = 0; i < values.Length; i++)
        {
            double norm = Math.Clamp(values[i] / scale, 0, 1);
            float barH = Math.Max((float)(h * norm), 1.5f);
            float bx = x + i * (barW + gap);
            float by = y + h - barH;
            Gdip.GdipFillRectangle(g, brush, bx, by, barW, barH);
        }
        Gdip.GdipDeleteBrush(brush);
    }

    private static Snapshot Lerp(Snapshot a, Snapshot b, double t) => new()
    {
        CpuPercent = a.CpuPercent + (b.CpuPercent - a.CpuPercent) * t,
        GpuPercent = a.GpuPercent is { } ga && b.GpuPercent is { } gb ? ga + (gb - ga) * t : b.GpuPercent,
        RamPercent = (uint)(a.RamPercent + (b.RamPercent - a.RamPercent) * t),
        BatteryPercent = b.BatteryPercent,
        OnAc = b.OnAc,
        NetUpBytesPerSec = a.NetUpBytesPerSec + (b.NetUpBytesPerSec - a.NetUpBytesPerSec) * t,
        NetDownBytesPerSec = a.NetDownBytesPerSec + (b.NetDownBytesPerSec - a.NetDownBytesPerSec) * t,
    };

    private static double EaseOutCubic(double t) => 1 - Math.Pow(1 - t, 3);
}
