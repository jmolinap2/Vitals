using System.ComponentModel;
using System.Globalization;
using System.Windows.Media;
using Vitals.Shared;

namespace Vitals.Settings;

public sealed class MetricRow(MetricKey key, bool enabled, string? colorHex, double warnThreshold, double critThreshold) : INotifyPropertyChanged
{
    public MetricKey Key { get; } = key;

    public string DisplayName => Key switch
    {
        MetricKey.Cpu => "CPU",
        MetricKey.Gpu => "GPU",
        MetricKey.Ram => "RAM",
        MetricKey.Up => "Subida",
        MetricKey.Down => "Bajada",
        MetricKey.Battery => "Batería",
        _ => Key.ToString(),
    };

    // Valor ilustrativo para la vista previa — Ajustes no tiene datos reales
    // del sistema, solo los recibe el proceso de la píldora.
    public string DemoValue => Key switch
    {
        MetricKey.Cpu => "21%",
        MetricKey.Gpu => "42%",
        MetricKey.Ram => "68%",
        MetricKey.Up => "1.2 Mbps",
        MetricKey.Down => "3.4 Mbps",
        _ => "92%",
    };

    // Mismos acentos que usa la píldora por defecto, para que Ajustes y
    // widget se lean como una sola app cuando no hay color personalizado.
    public static Color DefaultColor(MetricKey key) => key switch
    {
        MetricKey.Cpu => Color.FromRgb(77, 140, 255),
        MetricKey.Gpu => Color.FromRgb(61, 214, 125),
        MetricKey.Ram => Color.FromRgb(168, 127, 255),
        MetricKey.Up => Color.FromRgb(255, 159, 67),
        MetricKey.Down => Color.FromRgb(86, 196, 255),
        MetricKey.Battery => Color.FromRgb(76, 217, 100),
        _ => Colors.White,
    };

    // Mismos valores por defecto que PillWindow.DefaultThresholds — Subida y
    // Bajada no tienen tope absoluto natural, así que su % se interpreta
    // como % de la escala dinámica reciente de su propia gráfica.
    public static (double warn, double crit) DefaultThresholds(MetricKey key) => key switch
    {
        MetricKey.Cpu => (75, 90),
        MetricKey.Gpu => (75, 90),
        MetricKey.Ram => (80, 92),
        MetricKey.Up => (90, 97),
        MetricKey.Down => (90, 97),
        _ => (20, 10), // Batería: invertido (alarma con poca carga).
    };

    public Geometry IconGeometry => Geometry.Parse(Key switch
    {
        // Chip: cuerpo + patas.
        MetricKey.Cpu or MetricKey.Gpu =>
            "M4,4 H14 V14 H4 Z M7,1 V4 M11,1 V4 M7,14 V17 M11,14 V17 M1,7 H4 M1,11 H4 M14,7 H17 M14,11 H17",
        // Módulo de memoria con muescas.
        MetricKey.Ram =>
            "M1,4 H17 V12 H1 Z M4,12 V15 M8,12 V15 M12,12 V15 M15,12 V15",
        MetricKey.Up => "M9,16 V2 M3.5,7.5 L9,2 L14.5,7.5",
        MetricKey.Down => "M9,2 V16 M3.5,10.5 L9,16 L14.5,10.5",
        MetricKey.Battery => "M1,6 H14 V13 H1 Z M15.5,8.5 V10.5",
        _ => "M0,0",
    });

    private bool _enabled = enabled;
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Enabled)));
        }
    }

    private string? _colorHex = colorHex;

    /// <summary>Color de acento en hex, o null para usar el color por defecto de la métrica.</summary>
    public string? ColorHex
    {
        get => _colorHex;
        set
        {
            if (_colorHex == value) return;
            _colorHex = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ColorHex)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AccentColor)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AccentBrush)));
        }
    }

    public Color AccentColor => TryParseHex(ColorHex, out var c) ? c : DefaultColor(Key);

    public Brush AccentBrush => new SolidColorBrush(AccentColor);

    private double _warnThreshold = warnThreshold;
    public double WarnThreshold
    {
        get => _warnThreshold;
        set
        {
            if (_warnThreshold == value) return;
            _warnThreshold = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(WarnThreshold)));
            if (!EditingCritThreshold)
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ThresholdEditValue)));
        }
    }

    private double _critThreshold = critThreshold;
    public double CritThreshold
    {
        get => _critThreshold;
        set
        {
            if (_critThreshold == value) return;
            _critThreshold = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CritThreshold)));
            if (EditingCritThreshold)
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ThresholdEditValue)));
        }
    }

    /// <summary>false = el campo de umbral edita Advertencia; true = edita Crítico.</summary>
    public bool EditingCritThreshold { get; private set; }

    public void SetThresholdMode(bool editingCrit)
    {
        if (EditingCritThreshold == editingCrit) return;
        EditingCritThreshold = editingCrit;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ThresholdEditValue)));
    }

    /// <summary>El campo de umbral en pantalla lee/escribe este valor — cuál de
    /// los dos (Warn/Crit) depende de EditingCritThreshold.</summary>
    public double ThresholdEditValue
    {
        get => EditingCritThreshold ? CritThreshold : WarnThreshold;
        set
        {
            if (EditingCritThreshold) CritThreshold = value;
            else WarnThreshold = value;
        }
    }

    private static bool TryParseHex(string? hex, out Color color)
    {
        color = default;
        if (hex is not { Length: 7 } || hex[0] != '#') return false;
        if (!byte.TryParse(hex.AsSpan(1, 2), NumberStyles.HexNumber, null, out byte r)) return false;
        if (!byte.TryParse(hex.AsSpan(3, 2), NumberStyles.HexNumber, null, out byte g)) return false;
        if (!byte.TryParse(hex.AsSpan(5, 2), NumberStyles.HexNumber, null, out byte b)) return false;
        color = Color.FromRgb(r, g, b);
        return true;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
