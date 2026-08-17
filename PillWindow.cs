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
    private readonly double[] _ordered = new double[capacity];
    private int _count;
    private int _head;

    public void Push(double v)
    {
        _values[_head] = v;
        _head = (_head + 1) % _values.Length;
        if (_count < _values.Length) _count++;
    }

    // Repinta un buffer propio en cada llamada en vez de asignar un array
    // nuevo — el repintado (animación incluida) ocurre varias veces por
    // segundo, y este es precisamente un monitor de recursos.
    public ReadOnlySpan<double> Ordered()
    {
        int start = (_head - _count + _values.Length) % _values.Length;
        for (int i = 0; i < _count; i++)
            _ordered[i] = _values[(start + i) % _values.Length];
        return _ordered.AsSpan(0, _count);
    }

    public double Max()
    {
        double max = 0;
        for (int i = 0; i < _count; i++)
            if (_values[i] > max) max = _values[i];
        return max;
    }
}

internal readonly record struct Accent(uint Stroke, uint FillTop, uint FillBottom, uint Text);

internal static class PillWindow
{
    private const string ClassName = "VitalsPillWindow";
    private static int _columnWidth = 96;
    private const int EdgePadding = 10;
    private const int HeightWithCharts = 104;
    private const int HeightCompact = 62;
    private static int _pillHeight = HeightWithCharts;
    private static bool _smoothTransitions = true;
    private static bool _showCharts = true;
    private static bool _alertColors = true;
    private const int ScreenMargin = 12;
    private const int CornerRadius = 24;
    private const int HistoryLength = 30;

    private static List<MetricEntry> _enabledMetrics = [];
    private static int _pillWidth; // ancho lógico (sin escalar) — GDI+ escala todo con una sola transformación
    private static double _scale = 1.0;
    private static double _fontScale = 1.0;
    private static byte _opacity = 242;
    private static nint _hwnd;

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
    private static readonly RingBuffer BatteryHistory = new(HistoryLength);

    // Cada métrica tiene su color de identidad; los umbrales de alerta solo
    // recolorean el número, para que el aviso se note sin volver ilegible
    // la fila de gráficas.
    private static readonly Accent CpuAccent = MakeAccent(77, 140, 255);
    private static readonly Accent GpuAccent = MakeAccent(61, 214, 125);
    private static readonly Accent RamAccent = MakeAccent(168, 127, 255);
    private static readonly Accent UpAccent = MakeAccent(255, 159, 67);
    private static readonly Accent DownAccent = MakeAccent(86, 196, 255);
    private static readonly Accent BatteryAccent = MakeAccent(76, 217, 100);

    private static Accent MakeAccent(byte r, byte g, byte b) => new(
        Stroke: Gdip.Argb(255, r, g, b),
        FillTop: Gdip.Argb(75, r, g, b),
        FillBottom: Gdip.Argb(0, r, g, b),
        Text: Gdip.Argb(255, r, g, b));

    private static NOTIFYICONDATA _trayIcon;
    private static nint _bgBrush;
    private static nint _labelBrush;
    private static nint _dividerBrush;
    private static nint _borderPen;
    private static nint _warnBrush;
    private static nint _critBrush;
    private static nint _labelFont;
    private static nint _valueFont;
    private static nint _centerFormat;
    private static nint _leftFormat;

    // Superficie ARGB persistente. Se dibuja aquí y se entrega entera a
    // UpdateLayeredWindow, que respeta el alfa de cada píxel.
    private static nint _backDc;
    private static nint _backBmp;
    private static nint _backBits;
    private static nint _gdipBitmap;
    private static nint _graphics;
    private static int _backWidth;
    private static int _backHeight;

    private enum ChartKind { Line, Bars }

    /// <summary>
    /// Todo lo que no cambia entre cuadros — posiciones, ancho de etiqueta ya
    /// medido, y los objetos GDI+ — se calcula una sola vez al iniciar. El
    /// bucle de render solo dibuja.
    /// </summary>
    private sealed class ColumnLayout
    {
        public required MetricKey Key;
        public required IconKind Icon;
        public required string Label;
        public required Accent Accent;
        public required RingBuffer History;
        public required ChartKind Chart;
        public required bool DynamicScale;

        public float IconX;
        public RectF LabelRect;
        public RectF ValueRect;
        public RectF ChartRect;
        public float DividerX;

        public nint Pen;
        public nint AccentBrush;
        public nint TextBrush;
        public nint GradientBrush;
    }

    private static ColumnLayout[] _columns = [];

    private static void ApplyConfig(VitalsConfig config)
    {
        _enabledMetrics = config.Metrics.Where(m => m.Enabled).ToList();
        if (_enabledMetrics.Count == 0) _enabledMetrics = VitalsConfig.DefaultOrder();
        _columnWidth = Math.Clamp(config.ColumnWidth, 62, 130);
        _pillWidth = EdgePadding * 2 + _enabledMetrics.Count * _columnWidth;
        _scale = Math.Clamp(config.Scale, 0.6, 2.0);
        _fontScale = Math.Clamp(config.FontScale, 0.7, 1.3);
        _opacity = (byte)Math.Round(Math.Clamp(config.Opacity, 0.25, 1.0) * 255);
        _smoothTransitions = config.SmoothTransitions;
        _showCharts = config.ShowCharts;
        _alertColors = config.AlertColors;
        _pillHeight = _showCharts ? HeightWithCharts : HeightCompact;
    }

    /// <summary>
    /// Ajustes guardó config.json y avisó por mensaje de ventana — no hay
    /// que matar el proceso: se reposiciona/redimensiona en caliente.
    /// </summary>
    private static void ReloadConfig()
    {
        ApplyConfig(VitalsConfig.Load());

        int deviceWidth = (int)Math.Round(_pillWidth * _scale);
        int deviceHeight = (int)Math.Round(_pillHeight * _scale);

        var workArea = new RECT();
        SystemParametersInfo(SPI_GETWORKAREA, 0, ref workArea, 0);
        int x = workArea.Right - deviceWidth - ScreenMargin;
        int y = workArea.Bottom - deviceHeight - ScreenMargin;
        SetWindowPos(_hwnd, 0, x, y, deviceWidth, deviceHeight, SWP_NOZORDER | SWP_NOACTIVATE);

        DisposeColumnLayout();
        BuildColumnLayout();

        DisposeBackBuffer();
        CreateBackBuffer(deviceWidth, deviceHeight);

        Redraw();
    }

    private static void DisposeColumnLayout()
    {
        foreach (var col in _columns)
        {
            Gdip.GdipDeletePen(col.Pen);
            Gdip.GdipDeleteBrush(col.AccentBrush);
            Gdip.GdipDeleteBrush(col.TextBrush);
            Gdip.GdipDeleteBrush(col.GradientBrush);
        }
    }

    private static void DisposeBackBuffer()
    {
        Gdip.GdipDeleteGraphics(_graphics);
        Gdip.GdipDisposeImage(_gdipBitmap);
        DeleteObject(_backBmp);
        DeleteDC(_backDc);
    }

    public static unsafe void Run()
    {
        Gdip.Startup();
        ApplyConfig(VitalsConfig.Load());

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

        int deviceWidth = (int)Math.Round(_pillWidth * _scale);
        int deviceHeight = (int)Math.Round(_pillHeight * _scale);
        int deviceCorner = (int)Math.Round(CornerRadius * _scale);

        var workArea = new RECT();
        SystemParametersInfo(SPI_GETWORKAREA, 0, ref workArea, 0);
        int x = workArea.Right - deviceWidth - ScreenMargin;
        int y = workArea.Bottom - deviceHeight - ScreenMargin;

        nint hwnd = CreateWindowEx(
            WS_EX_LAYERED | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            ClassName, "Vitals",
            WS_POPUP,
            x, y, deviceWidth, deviceHeight,
            0, 0, hInstance, 0);

        if (hwnd == 0) return;
        _hwnd = hwnd;

        // Sin SetWindowRgn ni SetLayeredWindowAttributes: la forma y la
        // opacidad las define ahora el alfa de la propia superficie.
        Gdip.GdipCreateSolidFill(Gdip.Argb(255, 0, 0, 0), out _bgBrush);
        Gdip.GdipCreateSolidFill(Gdip.Argb(255, 150, 160, 168), out _labelBrush);
        Gdip.GdipCreateSolidFill(Gdip.Argb(30, 255, 255, 255), out _dividerBrush);
        Gdip.GdipCreateSolidFill(Gdip.Argb(255, 255, 193, 84), out _warnBrush);
        Gdip.GdipCreateSolidFill(Gdip.Argb(255, 255, 99, 87), out _critBrush);
        // Borde: define el contorno de la píldora ahora que no hay región.
        Gdip.GdipCreatePen1(Gdip.Argb(60, 255, 255, 255), 1f, Gdip.UnitPixel, out _borderPen);

        CreateFonts();

        Gdip.GdipCreateStringFormat(0, 0, out _centerFormat);
        Gdip.GdipSetStringFormatAlign(_centerFormat, Gdip.StringAlignCenter);
        Gdip.GdipSetStringFormatLineAlign(_centerFormat, Gdip.StringAlignCenter);
        Gdip.GdipSetStringFormatFlags(_centerFormat, Gdip.StringFormatFlagsNoWrap);

        Gdip.GdipCreateStringFormat(0, 0, out _leftFormat);
        Gdip.GdipSetStringFormatAlign(_leftFormat, Gdip.StringAlignNear);
        Gdip.GdipSetStringFormatLineAlign(_leftFormat, Gdip.StringAlignCenter);
        Gdip.GdipSetStringFormatFlags(_leftFormat, Gdip.StringFormatFlagsNoWrap);

        BuildColumnLayout();
        CreateBackBuffer(deviceWidth, deviceHeight);
        AddTrayIcon(hwnd);

        _snapshot = _animFrom = _animTo = Sampler.Sample();
        Redraw();
        ShowWindow(hwnd, SW_SHOWNOACTIVATE);
        SetTimer(hwnd, DataTimerId, 1000, 0);

        while (GetMessage(out var msg, 0, 0, 0))
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        Shell_NotifyIcon(NIM_DELETE, ref _trayIcon);
    }

    private static void BuildColumnLayout()
    {
        // Un HDC de pantalla basta para medir texto; se libera enseguida.
        nint screenDc = GetDC(0);
        Gdip.GdipCreateFromHDC(screenDc, out nint measureG);

        const float iconSize = 15, gap = 6, rowIconY = 13, chartPad = 10;

        _columns = new ColumnLayout[_enabledMetrics.Count];
        for (int i = 0; i < _enabledMetrics.Count; i++)
        {
            var key = _enabledMetrics[i].Key;
            var col = DescribeMetric(key);
            float colStart = EdgePadding + i * _columnWidth;

            var probe = new RectF(0, 0, 400, 24);
            Gdip.GdipMeasureString(measureG, col.Label, col.Label.Length, _labelFont,
                ref probe, _leftFormat, out var bbox, out _, out _);

            float comboWidth = iconSize + gap + bbox.Width;
            col.IconX = colStart + (_columnWidth - comboWidth) / 2f;
            col.LabelRect = new RectF(col.IconX + iconSize + gap, rowIconY - 4, bbox.Width + 4, 22);
            col.ChartRect = new RectF(colStart + chartPad, 36, _columnWidth - chartPad * 2, 38);
            col.ValueRect = new RectF(colStart, _showCharts ? 78 : 33, _columnWidth, 22);
            col.DividerX = colStart;

            Gdip.GdipCreatePen1(col.Accent.Stroke, 1.6f, Gdip.UnitPixel, out col.Pen);
            Gdip.GdipSetPenLineJoin(col.Pen, Gdip.LineJoinRound);
            Gdip.GdipSetPenStartCap(col.Pen, Gdip.LineCapRound);
            Gdip.GdipSetPenEndCap(col.Pen, Gdip.LineCapRound);
            Gdip.GdipCreateSolidFill(col.Accent.Stroke, out col.AccentBrush);
            Gdip.GdipCreateSolidFill(col.Accent.Text, out col.TextBrush);

            var gradTop = new PointF(col.ChartRect.X, col.ChartRect.Y);
            var gradBottom = new PointF(col.ChartRect.X, col.ChartRect.Y + col.ChartRect.Height);
            Gdip.GdipCreateLineBrush(ref gradTop, ref gradBottom,
                col.Accent.FillTop, col.Accent.FillBottom, Gdip.WrapModeTile, out col.GradientBrush);

            _columns[i] = col;
        }

        Gdip.GdipDeleteGraphics(measureG);
        ReleaseDC(0, screenDc);
    }

    private static ColumnLayout DescribeMetric(MetricKey key) => key switch
    {
        MetricKey.Cpu => new ColumnLayout
        {
            Key = key, Icon = IconKind.Cpu, Label = "CPU", Accent = CpuAccent,
            History = CpuHistory, Chart = ChartKind.Line, DynamicScale = false,
        },
        MetricKey.Gpu => new ColumnLayout
        {
            Key = key, Icon = IconKind.Gpu, Label = "GPU", Accent = GpuAccent,
            History = GpuHistory, Chart = ChartKind.Line, DynamicScale = false,
        },
        MetricKey.Ram => new ColumnLayout
        {
            Key = key, Icon = IconKind.Ram, Label = "RAM", Accent = RamAccent,
            History = RamHistory, Chart = ChartKind.Bars, DynamicScale = false,
        },
        MetricKey.Up => new ColumnLayout
        {
            Key = key, Icon = IconKind.Up, Label = "Subida", Accent = UpAccent,
            History = NetUpHistory, Chart = ChartKind.Bars, DynamicScale = true,
        },
        MetricKey.Down => new ColumnLayout
        {
            Key = key, Icon = IconKind.Down, Label = "Bajada", Accent = DownAccent,
            History = NetDownHistory, Chart = ChartKind.Bars, DynamicScale = true,
        },
        _ => new ColumnLayout
        {
            Key = MetricKey.Battery, Icon = IconKind.Battery, Label = "Batería", Accent = BatteryAccent,
            History = BatteryHistory, Chart = ChartKind.Line, DynamicScale = false,
        },
    };

    private static void CreateBackBuffer(int width, int height)
    {
        nint screenDc = GetDC(0);
        _backDc = CreateCompatibleDC(screenDc);

        var header = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = -height, // negativo = filas de arriba hacia abajo
            biPlanes = 1,
            biBitCount = 32,
            biCompression = BI_RGB,
        };
        _backBmp = CreateDIBSection(screenDc, ref header, DIB_RGB_COLORS, out _backBits, 0, 0);
        SelectObject(_backDc, _backBmp);
        ReleaseDC(0, screenDc);

        // GDI+ dibuja directamente sobre los píxeles del DIB, en premultiplicado.
        Gdip.GdipCreateBitmapFromScan0(width, height, width * 4, Gdip.PixelFormat32bppPARGB, _backBits, out _gdipBitmap);
        Gdip.GdipGetImageGraphicsContext(_gdipBitmap, out _graphics);
        Gdip.GdipSetSmoothingMode(_graphics, Gdip.SmoothingModeAntiAlias);
        Gdip.GdipSetTextRenderingHint(_graphics, Gdip.TextRenderingHintAntiAlias);
        Gdip.GdipScaleWorldTransform(_graphics, (float)_scale, (float)_scale, Gdip.MatrixOrderPrepend);

        _backWidth = width;
        _backHeight = height;
    }

    private static unsafe void AddTrayIcon(nint hwnd)
    {
        ExtractIconEx(Environment.ProcessPath!, 0, out _, out nint smallIcon, 1);

        _trayIcon = new NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = hwnd,
            uID = TrayIconId,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAYICON,
            hIcon = smallIcon,
        };

        const string tip = "Vitals";
        for (int i = 0; i < tip.Length; i++)
            _trayIcon.szTip[i] = tip[i];

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
                BatteryHistory.Push(Math.Max(_animTo.BatteryPercent, 0));

                // Sin transiciones: un solo repintado por lectura, en vez de
                // los ~11 que cuesta interpolar durante 350 ms.
                if (!_smoothTransitions)
                {
                    _snapshot = _animTo;
                    Redraw();
                    return 0;
                }

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
                Redraw();
                return 0;

            case WM_RELOAD_CONFIG:
                ReloadConfig();
                return 0;

            case WM_NCHITTEST:
                return HTCAPTION;

            case WM_TRAYICON when lParam == (nint)WM_RBUTTONUP || lParam == (nint)WM_LBUTTONUP:
                ShowTrayMenu(hWnd);
                return 0;

            // Con NCHITTEST forzado a HTCAPTION el clic derecho sobre la
            // píldora llega como WM_NCRBUTTONUP: mismo menú, sin depender
            // de encontrar el ícono en la bandeja.
            case WM_NCRBUTTONUP:
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

    private static void Redraw()
    {
        nint g = _graphics;

        // Limpiar a totalmente transparente: fuera de la píldora no debe
        // quedar nada, y así el borde redondeado se compone con suavizado.
        Gdip.GdipGraphicsClear(g, 0x00000000);

        // Coordenadas lógicas (sin escalar): la transformación del contexto
        // ya las mapea al tamaño real de ventana.
        float radius = Math.Min(CornerRadius, _pillHeight / 2f - 1);
        nint bgPath = Gdip.RoundRectPath(0.5f, 0.5f, _pillWidth - 1, _pillHeight - 1, radius);
        Gdip.GdipFillPath(g, _bgBrush, bgPath);
        Gdip.GdipDrawPath(g, _borderPen, bgPath);
        Gdip.GdipDeletePath(bgPath);

        var s = _snapshot;
        for (int i = 0; i < _columns.Length; i++)
        {
            var col = _columns[i];
            if (i > 0) Gdip.GdipFillRectangle(g, _dividerBrush, col.DividerX, 14, 1, _pillHeight - 28);
            DrawColumn(g, col, s);
        }

        var size = new SIZE { cx = _backWidth, cy = _backHeight };
        var srcPoint = new POINT { X = 0, Y = 0 };
        var blend = new BLENDFUNCTION
        {
            BlendOp = AC_SRC_OVER,
            BlendFlags = 0,
            SourceConstantAlpha = _opacity,
            AlphaFormat = AC_SRC_ALPHA,
        };
        UpdateLayeredWindow(_hwnd, 0, 0, ref size, _backDc, ref srcPoint, 0, ref blend, ULW_ALPHA);
    }

    private static void DrawColumn(nint g, ColumnLayout col, Snapshot s)
    {
        const float iconSize = 15, rowIconY = 13;

        // Recorta al espacio propio de la columna: con anchos angostos el
        // ícono, la etiqueta o el valor pueden medir más que la columna, y
        // sin esto se dibujan encima de la columna vecina.
        Gdip.GdipSetClipRect(g, col.DividerX, 0, _columnWidth, _pillHeight, Gdip.CombineModeReplace);

        DrawIcon(g, col.Icon, col.IconX, rowIconY, iconSize, col.Pen, col.AccentBrush);

        var labelRect = col.LabelRect;
        Gdip.GdipDrawString(g, col.Label, col.Label.Length, _labelFont, ref labelRect, _leftFormat, _labelBrush);

        if (_showCharts)
        {
            double scale = col.DynamicScale ? Math.Max(col.History.Max(), 20_000) : 100;
            if (col.Chart == ChartKind.Line)
                DrawLineChart(g, col, scale);
            else
                DrawBarChart(g, col, scale);
        }

        string value = FormatValue(col.Key, s);
        var valueRect = col.ValueRect;
        nint brush = _alertColors
            ? AlertLevel(col.Key, s) switch { 2 => _critBrush, 1 => _warnBrush, _ => col.TextBrush }
            : col.TextBrush;
        Gdip.GdipDrawString(g, value, value.Length, _valueFont, ref valueRect, _centerFormat, brush);

        Gdip.GdipResetClip(g);
    }

    /// <summary>0 = normal, 1 = aviso, 2 = crítico. La red no tiene umbral: su
    /// valor "alto" es deseable, no un problema.</summary>
    private static int AlertLevel(MetricKey key, Snapshot s) => key switch
    {
        MetricKey.Cpu => Level(s.CpuPercent, 75, 90),
        MetricKey.Gpu => s.GpuPercent is { } g ? Level(g, 75, 90) : 0,
        MetricKey.Ram => Level(s.RamPercent, 80, 92),
        // Batería al revés: alarma cuando queda poca, y solo con el cargador
        // desconectado — enchufado, un 8% es normal, no una alerta.
        MetricKey.Battery => s.OnAc || s.BatteryPercent < 0 ? 0
            : s.BatteryPercent <= 10 ? 2
            : s.BatteryPercent <= 20 ? 1
            : 0,
        _ => 0,
    };

    private static int Level(double value, double warn, double crit) =>
        value >= crit ? 2 : value >= warn ? 1 : 0;

    private static string FormatValue(MetricKey key, Snapshot s) => key switch
    {
        MetricKey.Cpu => $"{s.CpuPercent:0}%",
        MetricKey.Gpu => s.GpuPercent is { } gpu ? $"{gpu:0}%" : "—",
        MetricKey.Ram => $"{s.RamPercent}%",
        MetricKey.Up => MetricsSampler.FormatBps(s.NetUpBytesPerSec),
        MetricKey.Down => MetricsSampler.FormatBps(s.NetDownBytesPerSec),
        _ => s.BatteryPercent < 0 ? "—" : $"{s.BatteryPercent}%{(s.OnAc ? " ⚡" : "")}",
    };

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

    // Compartidos entre CPU y GPU: un mismo pase de render consume cada
    // buffer por completo antes del siguiente, así que reusarlos es seguro
    // y evita un par de arrays nuevos en cada uno de los ~11 repintados
    // por segundo que dura la animación de transición.
    private static readonly PointF[] LinePointsBuffer = new PointF[HistoryLength];
    private static readonly PointF[] LineFillPointsBuffer = new PointF[HistoryLength + 2];

    private static void DrawLineChart(nint g, ColumnLayout col, double scale)
    {
        var values = col.History.Ordered();
        if (values.Length < 2) return;

        float x = col.ChartRect.X, y = col.ChartRect.Y;
        float w = col.ChartRect.Width, h = col.ChartRect.Height;

        for (int i = 0; i < values.Length; i++)
        {
            float px = x + w * i / (values.Length - 1);
            float py = y + h - h * (float)(Math.Clamp(values[i], 0, scale) / scale);
            LinePointsBuffer[i] = new PointF(px, py);
        }

        Array.Copy(LinePointsBuffer, LineFillPointsBuffer, values.Length);
        LineFillPointsBuffer[values.Length] = new PointF(x + w, y + h);
        LineFillPointsBuffer[values.Length + 1] = new PointF(x, y + h);

        Gdip.GdipCreatePath(Gdip.FillModeAlternate, out nint path);
        Gdip.GdipAddPathPolygon(path, LineFillPointsBuffer, values.Length + 2);
        Gdip.GdipFillPath(g, col.GradientBrush, path);
        Gdip.GdipDeletePath(path);

        Gdip.GdipDrawLines(g, col.Pen, LinePointsBuffer, values.Length);
    }

    private static void DrawBarChart(nint g, ColumnLayout col, double scale)
    {
        var values = col.History.Ordered();
        if (values.Length == 0) return;

        float x = col.ChartRect.X, y = col.ChartRect.Y;
        float w = col.ChartRect.Width, h = col.ChartRect.Height;

        const float gap = 2f;
        float barW = (w - gap * (values.Length - 1)) / values.Length;
        if (barW < 1) barW = 1;

        for (int i = 0; i < values.Length; i++)
        {
            double norm = Math.Clamp(values[i] / scale, 0, 1);
            float barH = Math.Max((float)(h * norm), 1.5f);
            Gdip.GdipFillRectangle(g, col.AccentBrush, x + i * (barW + gap), y + h - barH, barW, barH);
        }
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
