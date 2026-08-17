using System.ComponentModel;
using System.Windows.Media;
using Vitals.Shared;

namespace Vitals.Settings;

public sealed class MetricRow(MetricKey key, bool enabled) : INotifyPropertyChanged
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

    // Mismos acentos que usa la píldora, para que Ajustes y widget se lean
    // como una sola app.
    public Brush AccentBrush => new SolidColorBrush(Key switch
    {
        MetricKey.Cpu => Color.FromRgb(77, 140, 255),
        MetricKey.Gpu => Color.FromRgb(61, 214, 125),
        MetricKey.Ram => Color.FromRgb(168, 127, 255),
        MetricKey.Up => Color.FromRgb(255, 159, 67),
        MetricKey.Down => Color.FromRgb(86, 196, 255),
        MetricKey.Battery => Color.FromRgb(76, 217, 100),
        _ => Colors.White,
    });

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

    public event PropertyChangedEventHandler? PropertyChanged;
}
