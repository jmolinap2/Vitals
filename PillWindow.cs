using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
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
    // Grosor mínimo del trazo de los íconos, ya en píxeles de pantalla. El
    // pen se crea en unidades lógicas (se re-escala con el resto), así que a
    // escalas chicas hay que engrosarlo para compensar y que no quede
    // filiforme — ver BuildColumnLayout().
    private const float MinIconStrokeDevicePx = 1.4f;
    private static Corner _position = Corner.BottomRight;
    private static bool _clickThrough;
    private static bool _autoHideFullscreen;
    private static bool _isHidden;
    private static POINT _pillClickStart;
    private static bool _pillClickCandidate;

    private static List<MetricEntry> _enabledMetrics = [];
    private static int _pillWidth; // ancho lógico (sin escalar) — GDI+ escala todo con una sola transformación
    private static double _scale = 1.0;
    private static double _fontScale = 1.0;
    private static byte _opacity = 242;
    private static nint _hwnd;
    private static Mutex? _singleInstanceMutex;

    private const nint DataTimerId = 1;
    private const nint AnimTimerId = 2;
    private const int AnimFrameMs = 33;
    private const double AnimDurationMs = 350;

    private static MetricsSampler _sampler = null!;
    private static bool _samplerHasGpu;
    private static bool _samplerHasNet;
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
    // Mismas fuentes que _labelFont/_valueFont pero ya en tamaño de
    // dispositivo (× _scale) — ver CreateFonts() y DrawDeviceText().
    private static nint _labelFontDevice;
    private static nint _valueFontDevice;
    // Subida/Bajada/Batería se dibujan con glifos reales de Segoe Fluent
    // Icons en vez de formas dibujadas a mano — no existe un glifo dedicado
    // para CPU/GPU/RAM en esa fuente, así que esos tres siguen siendo
    // vectoriales (ver DrawIcon).
    private static nint _iconFont;
    private static nint _centerFormat;
    private static nint _leftFormat;

    // Superficie ARGB persistente. Se dibuja aquí y se entrega entera a
    // UpdateLayeredWindow, que respeta el alfa de cada píxel.
    private static nint _backDc;
    private static nint _backBmp;
    private static nint _backOldBmp;
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

        public double WarnThreshold;
        public double CritThreshold;
    }

    private static ColumnLayout[] _columns = [];

    private static void ApplyConfig(VitalsConfig config)
    {
        _enabledMetrics = config.Metrics.Where(m => m.Enabled).ToList();
        if (_enabledMetrics.Count == 0) _enabledMetrics = VitalsConfig.DefaultOrder();
        // El mínimo antes era 62px, pero a esa altura valores largos como
        // "3.4Mbps" o "100%⚡" no entraban en la columna y el recorte por
        // columna los truncaba a la mitad — de ahí el aspecto poco pulido.
        _columnWidth = Math.Clamp(config.ColumnWidth, 78, 130);
        _pillWidth = EdgePadding * 2 + _enabledMetrics.Count * _columnWidth;
        _scale = Math.Clamp(config.Scale, 0.6, 2.0);
        _fontScale = Math.Clamp(config.FontScale, 0.7, 1.3);
        _opacity = (byte)Math.Round(Math.Clamp(config.Opacity, 0.25, 1.0) * 255);
        _smoothTransitions = config.SmoothTransitions;
        _showCharts = config.ShowCharts;
        _alertColors = config.AlertColors;
        _pillHeight = _showCharts ? HeightWithCharts : HeightCompact;
        _position = config.Position;
        _clickThrough = config.ClickThrough;
        _autoHideFullscreen = config.AutoHideFullscreen;
    }

    // Grilla de 3x3 sobre el área de trabajo: cada eje se resuelve por
    // separado (izquierda/centro/derecha, arriba/centro/abajo) y las 9
    // posiciones de Corner son las combinaciones de esos dos ejes.
    private static (int X, int Y) ComputePosition(RECT workArea, int deviceWidth, int deviceHeight)
    {
        int x = _position switch
        {
            Corner.TopLeft or Corner.Left or Corner.BottomLeft => workArea.Left + ScreenMargin,
            Corner.Top or Corner.Center or Corner.Bottom => workArea.Left + (workArea.Width - deviceWidth) / 2,
            _ => workArea.Right - deviceWidth - ScreenMargin,
        };
        int y = _position switch
        {
            Corner.TopLeft or Corner.Top or Corner.TopRight => workArea.Top + ScreenMargin,
            Corner.Left or Corner.Center or Corner.Right => workArea.Top + (workArea.Height - deviceHeight) / 2,
            _ => workArea.Bottom - deviceHeight - ScreenMargin,
        };
        return (x, y);
    }

    private static void ApplyClickThrough()
    {
        int exStyle = GetWindowLong(_hwnd, GWL_EXSTYLE);
        exStyle = _clickThrough ? exStyle | WS_EX_TRANSPARENT : exStyle & ~WS_EX_TRANSPARENT;
        SetWindowLong(_hwnd, GWL_EXSTYLE, exStyle);
    }

    // Compara la ventana en primer plano contra los límites completos de su
    // monitor (no el área de trabajo, que excluye la barra de tareas): si la
    // cubre entera, es una app a pantalla completa.
    private static bool IsForegroundFullscreen()
    {
        nint fg = GetForegroundWindow();
        if (fg == 0 || fg == _hwnd) return false;
        if (!GetWindowRect(fg, out var winRect)) return false;

        nint monitor = MonitorFromWindow(fg, MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref mi)) return false;

        return winRect.Left <= mi.rcMonitor.Left && winRect.Top <= mi.rcMonitor.Top
            && winRect.Right >= mi.rcMonitor.Right && winRect.Bottom >= mi.rcMonitor.Bottom;
    }

    // Solo abre los contadores PDH (GPU / red) que realmente hacen falta —
    // una configuración minimalista (por ejemplo, solo CPU y RAM) no debe
    // pagar el costo de inicializar infraestructura que nunca se muestra.
    // Si el conjunto de métricas habilitadas cambia (incluida una recarga en
    // caliente desde Ajustes), se recrea con las banderas correctas.
    private static void EnsureSampler()
    {
        bool needGpu = _enabledMetrics.Any(m => m.Key == MetricKey.Gpu);
        bool needNet = _enabledMetrics.Any(m => m.Key is MetricKey.Up or MetricKey.Down);

        if (_sampler is not null && needGpu == _samplerHasGpu && needNet == _samplerHasNet)
            return;

        _sampler?.Dispose();
        _sampler = new MetricsSampler(needGpu, needNet);
        _samplerHasGpu = needGpu;
        _samplerHasNet = needNet;
    }

    /// <summary>
    /// Ajustes guardó config.json y avisó por mensaje de ventana — no hay
    /// que matar el proceso: se reposiciona/redimensiona en caliente.
    /// </summary>
    private static void ReloadConfig()
    {
        ApplyConfig(VitalsConfig.Load());
        EnsureSampler();

        int deviceWidth = (int)Math.Round(_pillWidth * _scale);
        int deviceHeight = (int)Math.Round(_pillHeight * _scale);

        var workArea = new RECT();
        SystemParametersInfo(SPI_GETWORKAREA, 0, ref workArea, 0);
        var (x, y) = ComputePosition(workArea, deviceWidth, deviceHeight);
        SetWindowPos(_hwnd, 0, x, y, deviceWidth, deviceHeight, SWP_NOZORDER | SWP_NOACTIVATE);
        ApplyClickThrough();

        CreateFonts();

        DisposeColumnLayout();
        BuildColumnLayout();

        DisposeBackBuffer();
        CreateBackBuffer(deviceWidth, deviceHeight);

        Redraw();
    }

    private const float BaseLabelSize = 12.5f;
    private const float BaseValueSize = 17f;
    private const float IconGlyphSize = 17f;

    private static void CreateFonts()
    {
        if (_labelFont != 0) Gdip.GdipDeleteFont(_labelFont);
        if (_valueFont != 0) Gdip.GdipDeleteFont(_valueFont);
        if (_labelFontDevice != 0) Gdip.GdipDeleteFont(_labelFontDevice);
        if (_valueFontDevice != 0) Gdip.GdipDeleteFont(_valueFontDevice);

        // _labelFont/_valueFont quedan en tamaño lógico: BuildColumnLayout()
        // los usa para medir texto sobre un HDC sin transformación, y esa
        // medida es la que ubica íconos y rects (que sí se escalan luego con
        // el mundo). Las variantes *Device van directo en tamaño final de
        // píxel, para dibujar el texto nítido — ver DrawDeviceText().
        Gdip.GdipCreateFontFamilyFromName("Segoe UI", 0, out nint labelFamily);
        Gdip.GdipCreateFont(labelFamily, BaseLabelSize * (float)_fontScale, Gdip.FontStyleRegular, Gdip.UnitPixel, out _labelFont);
        Gdip.GdipCreateFont(labelFamily, BaseLabelSize * (float)_fontScale * (float)_scale, Gdip.FontStyleRegular, Gdip.UnitPixel, out _labelFontDevice);
        Gdip.GdipDeleteFontFamily(labelFamily);

        Gdip.GdipCreateFontFamilyFromName("Cascadia Mono", 0, out nint valueFamily);
        Gdip.GdipCreateFont(valueFamily, BaseValueSize * (float)_fontScale, Gdip.FontStyleBold, Gdip.UnitPixel, out _valueFont);
        Gdip.GdipCreateFont(valueFamily, BaseValueSize * (float)_fontScale * (float)_scale, Gdip.FontStyleBold, Gdip.UnitPixel, out _valueFontDevice);
        Gdip.GdipDeleteFontFamily(valueFamily);

        if (_iconFont != 0) Gdip.GdipDeleteFont(_iconFont);
        Gdip.GdipCreateFontFamilyFromName("Segoe Fluent Icons", 0, out nint iconFamily);
        Gdip.GdipCreateFont(iconFamily, IconGlyphSize * (float)_scale, Gdip.FontStyleRegular, Gdip.UnitPixel, out _iconFont);
        Gdip.GdipDeleteFontFamily(iconFamily);
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
        // Hay que devolver el DC a su bitmap original antes de borrar el
        // nuestro — borrar un bitmap todavía seleccionado en un DC es un uso
        // indebido de GDI y puede filtrar el objeto.
        SelectObject(_backDc, _backOldBmp);
        DeleteObject(_backBmp);
        DeleteDC(_backDc);
    }

    public static unsafe void Run()
    {
        // Evita dos píldoras corriendo a la vez (doble clic accidental sobre
        // el exe, arranque automático solapado con uno manual, etc.).
        _singleInstanceMutex = new Mutex(true, "Global\\VitalsPillWindow_SingleInstance", out bool createdNew);
        if (!createdNew) return;

        Gdip.Startup();
        ApplyConfig(VitalsConfig.Load());
        EnsureSampler();

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
        var (x, y) = ComputePosition(workArea, deviceWidth, deviceHeight);

        nint hwnd = CreateWindowEx(
            WS_EX_LAYERED | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            ClassName, "Vitals",
            WS_POPUP,
            x, y, deviceWidth, deviceHeight,
            0, 0, hInstance, 0);

        if (hwnd == 0) return;
        _hwnd = hwnd;
        ApplyClickThrough();

        // Sin SetWindowRgn ni SetLayeredWindowAttributes: la forma y la
        // opacidad las define ahora el alfa de la propia superficie.
        Gdip.GdipCreateSolidFill(Gdip.Argb(255, 0, 0, 0), out _bgBrush);
        Gdip.GdipCreateSolidFill(Gdip.Argb(255, 255, 255, 255), out _labelBrush);
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

        _snapshot = _animFrom = _animTo = _sampler.Sample();
        Redraw();
        ShowWindow(hwnd, SW_SHOWNOACTIVATE);
        SetTimer(hwnd, DataTimerId, 1000, 0);

        while (GetMessage(out var msg, 0, 0, 0))
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        Cleanup();
    }

    // Único punto de salida del message loop ("Salir" y un WM_DESTROY del
    // sistema terminan igual, en PostQuitMessage): todo lo que Run() abrió
    // se libera aquí, en orden inverso a como se creó.
    private static void Cleanup()
    {
        Shell_NotifyIcon(NIM_DELETE, ref _trayIcon);

        _sampler?.Dispose();

        DisposeColumnLayout();
        DisposeBackBuffer();

        Gdip.GdipDeleteBrush(_bgBrush);
        Gdip.GdipDeleteBrush(_labelBrush);
        Gdip.GdipDeleteBrush(_dividerBrush);
        Gdip.GdipDeleteBrush(_warnBrush);
        Gdip.GdipDeleteBrush(_critBrush);
        Gdip.GdipDeletePen(_borderPen);
        Gdip.GdipDeleteFont(_labelFont);
        Gdip.GdipDeleteFont(_valueFont);
        Gdip.GdipDeleteFont(_labelFontDevice);
        Gdip.GdipDeleteFont(_valueFontDevice);
        Gdip.GdipDeleteFont(_iconFont);
        Gdip.GdipDeleteStringFormat(_centerFormat);
        Gdip.GdipDeleteStringFormat(_leftFormat);

        // GdiplusShutdown debe llamarse después de liberar todo objeto GDI+
        // creado bajo este token, nunca antes.
        Gdip.Shutdown();

        _singleInstanceMutex?.Dispose();
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
            var entry = _enabledMetrics[i];
            var col = DescribeMetric(entry);
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

            float penWidth = Math.Max(1.6f, MinIconStrokeDevicePx / (float)_scale);
            Gdip.GdipCreatePen1(col.Accent.Stroke, penWidth, Gdip.UnitPixel, out col.Pen);
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

    // Valores por defecto cuando el usuario no fijó umbrales propios en
    // Ajustes. Subida/Bajada no tienen un tope absoluto natural (no hay
    // "100% de la red"), así que su umbral se interpreta como % de la
    // escala dinámica de su propia gráfica — ver AlertLevel().
    private static (double warn, double crit) DefaultThresholds(MetricKey key) => key switch
    {
        MetricKey.Cpu => (75, 90),
        MetricKey.Gpu => (75, 90),
        MetricKey.Ram => (80, 92),
        MetricKey.Up => (90, 97),
        MetricKey.Down => (90, 97),
        _ => (20, 10), // Batería: invertido, ver AlertLevel()
    };

    private static ColumnLayout DescribeMetric(MetricEntry entry)
    {
        var key = entry.Key;
        var col = key switch
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

        if (TryParseHexColor(entry.Color, out byte r, out byte g, out byte b))
            col.Accent = MakeAccent(r, g, b);

        var (defWarn, defCrit) = DefaultThresholds(key);
        col.WarnThreshold = entry.WarnThreshold ?? defWarn;
        col.CritThreshold = entry.CritThreshold ?? defCrit;

        return col;
    }

    private static bool TryParseHexColor(string? hex, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        if (hex is not { Length: 7 } || hex[0] != '#') return false;
        return byte.TryParse(hex.AsSpan(1, 2), System.Globalization.NumberStyles.HexNumber, null, out r)
            && byte.TryParse(hex.AsSpan(3, 2), System.Globalization.NumberStyles.HexNumber, null, out g)
            && byte.TryParse(hex.AsSpan(5, 2), System.Globalization.NumberStyles.HexNumber, null, out b);
    }

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
        _backOldBmp = SelectObject(_backDc, _backBmp);
        ReleaseDC(0, screenDc);

        // GDI+ dibuja directamente sobre los píxeles del DIB, en premultiplicado.
        Gdip.GdipCreateBitmapFromScan0(width, height, width * 4, Gdip.PixelFormat32bppPARGB, _backBits, out _gdipBitmap);
        Gdip.GdipGetImageGraphicsContext(_gdipBitmap, out _graphics);
        Gdip.GdipSetSmoothingMode(_graphics, Gdip.SmoothingModeAntiAlias);
        Gdip.GdipSetTextRenderingHint(_graphics, Gdip.TextRenderingHintAntiAliasGridFit);
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
                if (_autoHideFullscreen)
                {
                    bool shouldHide = IsForegroundFullscreen();
                    if (shouldHide != _isHidden)
                    {
                        _isHidden = shouldHide;
                        ShowWindow(hWnd, shouldHide ? SW_HIDE : SW_SHOWNOACTIVATE);
                    }
                }
                else if (_isHidden)
                {
                    // Se apagó la opción mientras estaba oculta por una app a
                    // pantalla completa — hay que volver a mostrarla.
                    _isHidden = false;
                    ShowWindow(hWnd, SW_SHOWNOACTIVATE);
                }

                _animFrom = _snapshot;
                _animTo = _sampler.Sample();
                _animStart = DateTime.UtcNow;

                // Solo alimenta el historial de las columnas visibles: sin
                // gráficas (_showCharts = false) nadie llega a leerlo.
                if (_showCharts) PushHistory(_animTo);

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
                CapsuleQuickLauncher.Reload();
                return 0;

            case WM_NCHITTEST:
                return HTCAPTION;

            // La cápsula conserva el arrastre de una barra de título. Si no
            // hubo desplazamiento, el mismo gesto es un clic para abrir o
            // cerrar los accesos, sin añadir una flecha ni botón externo.
            case WM_NCLBUTTONDOWN when wParam == HTCAPTION:
                GetCursorPos(out _pillClickStart);
                _pillClickCandidate = true;
                return DefWindowProc(hWnd, msg, wParam, lParam);

            case WM_NCLBUTTONUP when wParam == HTCAPTION:
                if (_pillClickCandidate)
                {
                    GetCursorPos(out var clickEnd);
                    int dx = clickEnd.X - _pillClickStart.X;
                    int dy = clickEnd.Y - _pillClickStart.Y;
                    if (dx * dx + dy * dy <= 25)
                        CapsuleQuickLauncher.Toggle();
                }
                _pillClickCandidate = false;
                return 0;

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

    private static void PushHistory(Snapshot s)
    {
        foreach (var col in _columns)
        {
            double value = col.Key switch
            {
                MetricKey.Cpu => s.CpuPercent,
                MetricKey.Gpu => s.GpuPercent ?? 0,
                MetricKey.Ram => s.RamPercent,
                MetricKey.Up => s.NetUpBytesPerSec,
                MetricKey.Down => s.NetDownBytesPerSec,
                _ => Math.Max(s.BatteryPercent, 0),
            };
            col.History.Push(value);
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

        DrawIcon(g, col, col.IconX, rowIconY, iconSize, s);

        DrawDeviceText(g, col.Label, _labelFontDevice, col.LabelRect, _leftFormat, _labelBrush);

        if (_showCharts)
        {
            double scale = col.DynamicScale ? Math.Max(col.History.Max(), 20_000) : 100;
            if (col.Chart == ChartKind.Line)
                DrawLineChart(g, col, scale);
            else
                DrawBarChart(g, col, scale);
        }

        string value = FormatValue(col.Key, s);
        nint brush = _alertColors
            ? AlertLevel(col, s) switch { 2 => _critBrush, 1 => _warnBrush, _ => col.TextBrush }
            : col.TextBrush;
        DrawDeviceText(g, value, _valueFontDevice, col.ValueRect, _centerFormat, brush);

        Gdip.GdipResetClip(g);
    }

    // Con la transformación de escala activa, GDI+ hintea el glifo al
    // tamaño lógico de la fuente y recién después reescala el resultado —
    // a escalas chicas eso deja el texto borroso y con poco contraste
    // (el trazo termina más delgado que un píxel). Acá se resetea la
    // transformación, se dibuja con un rect y una fuente ya en tamaño de
    // dispositivo, y se restaura la escala para lo que siga dibujándose.
    private static void DrawDeviceText(nint g, string text, nint font, RectF logicalRect, nint format, nint brush)
    {
        Gdip.GdipResetWorldTransform(g);

        var deviceRect = new RectF(
            logicalRect.X * (float)_scale,
            logicalRect.Y * (float)_scale,
            logicalRect.Width * (float)_scale,
            logicalRect.Height * (float)_scale);
        Gdip.GdipDrawString(g, text, text.Length, font, ref deviceRect, format, brush);

        Gdip.GdipScaleWorldTransform(g, (float)_scale, (float)_scale, Gdip.MatrixOrderPrepend);
    }

    /// <summary>0 = normal, 1 = aviso, 2 = crítico.</summary>
    private static int AlertLevel(ColumnLayout col, Snapshot s)
    {
        if (col.Key == MetricKey.Battery)
        {
            // Al revés: alarma cuando queda poca, y solo con el cargador
            // desconectado — enchufado, un 8% es normal, no una alerta.
            if (s.OnAc || s.BatteryPercent < 0) return 0;
            return s.BatteryPercent <= col.CritThreshold ? 2
                : s.BatteryPercent <= col.WarnThreshold ? 1
                : 0;
        }

        double? value = col.Key switch
        {
            MetricKey.Cpu => s.CpuPercent,
            MetricKey.Gpu => s.GpuPercent,
            MetricKey.Ram => s.RamPercent,
            // Sin tope absoluto natural: se compara contra la misma escala
            // dinámica que usa la gráfica (máximo reciente, mínimo 20 KB/s).
            MetricKey.Up => PercentOfDynamicScale(s.NetUpBytesPerSec, col),
            MetricKey.Down => PercentOfDynamicScale(s.NetDownBytesPerSec, col),
            _ => null,
        };

        return value is { } v ? Level(v, col.WarnThreshold, col.CritThreshold) : 0;
    }

    private static double PercentOfDynamicScale(double value, ColumnLayout col) =>
        value / Math.Max(col.History.Max(), 20_000) * 100;

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

    // Glifos de Segoe Fluent Icons construidos por código de punto, nunca
    // tipeados como caracteres literales: son de Área de Uso Privado (no
    // tienen representación visible propia) y se corrompen fácilmente al
    // pasar por texto/portapapeles.
    private static readonly string UploadGlyph = ((char)0xE898).ToString();
    private static readonly string DownloadGlyph = ((char)0xE896).ToString();
    private static readonly string BatteryUnknownGlyph = ((char)0xE996).ToString();
    private static readonly string BatteryFullGlyph = ((char)0xE83F).ToString();

    private static void DrawIcon(nint g, ColumnLayout col, float x, float y, float size, Snapshot s)
    {
        nint pen = col.Pen;
        switch (col.Icon)
        {
            case IconKind.Cpu:
            case IconKind.Gpu:
            {
                float pad = size * 0.22f;
                float bx = x + pad, by = y + pad, bs = size - pad * 2;
                float r = bs * 0.2f;
                nint chip = Gdip.RoundRectPath(bx, by, bs, bs, r);
                Gdip.GdipDrawPath(g, pen, chip);
                Gdip.GdipDeletePath(chip);
                for (int i = 0; i < 3; i++)
                {
                    float px = bx + bs * (i + 1) / 4f;
                    Gdip.GdipDrawLine(g, pen, px, y, px, by);
                    Gdip.GdipDrawLine(g, pen, px, by + bs, px, y + size);
                }
                if (col.Icon == IconKind.Gpu)
                {
                    float cx = bx + bs / 2, cy = by + bs / 2, cr = bs * 0.24f;
                    Gdip.GdipCreatePath(Gdip.FillModeAlternate, out nint circle);
                    Gdip.GdipAddPathArc(circle, cx - cr, cy - cr, cr * 2, cr * 2, 0, 360);
                    Gdip.GdipDrawPath(g, pen, circle);
                    Gdip.GdipDeletePath(circle);
                }
                break;
            }
            case IconKind.Ram:
            {
                float bodyH = size * 0.55f;
                float by = y + size * 0.1f;
                float r = bodyH * 0.22f;
                nint body = Gdip.RoundRectPath(x, by, size, bodyH, r);
                Gdip.GdipDrawPath(g, pen, body);
                Gdip.GdipDeletePath(body);
                float pinY = by + bodyH;
                for (int i = 0; i < 4; i++)
                {
                    float px = x + size * (i + 0.75f) / 4f;
                    Gdip.GdipDrawLine(g, pen, px, pinY, px, pinY + size * 0.25f);
                }
                break;
            }
            case IconKind.Up:
                DrawIconGlyph(g, UploadGlyph, x, y, size, col.AccentBrush);
                break;
            case IconKind.Down:
                DrawIconGlyph(g, DownloadGlyph, x, y, size, col.AccentBrush);
                break;
            case IconKind.Battery:
                DrawIconGlyph(g, BatteryGlyph(s.BatteryPercent, s.OnAc), x, y, size, col.AccentBrush);
                break;
        }
    }

    // Glifos reales de Segoe Fluent Icons en vez de formas dibujadas a mano
    // — se dibujan igual que el texto (ver DrawDeviceText): transformación
    // reseteada y rect/fuente ya en tamaño de dispositivo, para que salgan
    // tan nítidos como el resto del texto.
    private static void DrawIconGlyph(nint g, string glyph, float x, float y, float size, nint brush)
    {
        Gdip.GdipResetWorldTransform(g);

        var deviceRect = new RectF(x * (float)_scale, y * (float)_scale, size * (float)_scale, size * (float)_scale);
        Gdip.GdipDrawString(g, glyph, glyph.Length, _iconFont, ref deviceRect, _centerFormat, brush);

        Gdip.GdipScaleWorldTransform(g, (float)_scale, (float)_scale, Gdip.MatrixOrderPrepend);
    }

    // Battery0-9 + Battery10 cubren 11 tramos de carga; BatteryCharging0-8,
    // 9 tramos mientras está enchufada — así el ícono refleja el nivel real
    // en vez de ser siempre el mismo dibujo fijo.
    private static string BatteryGlyph(int percent, bool onAc)
    {
        if (percent < 0) return BatteryUnknownGlyph;
        if (onAc)
        {
            int level = Math.Clamp((int)Math.Round(percent / 100.0 * 8), 0, 8);
            return ((char)(0xE85A + level)).ToString();
        }
        int lvl = Math.Clamp((int)Math.Round(percent / 100.0 * 10), 0, 10);
        return lvl == 10 ? BatteryFullGlyph : ((char)(0xE850 + lvl)).ToString();
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
