using System.ComponentModel;
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
