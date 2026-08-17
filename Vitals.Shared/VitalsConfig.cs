using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vitals.Shared;

public enum MetricKey { Cpu, Gpu, Ram, Up, Down, Battery }

public sealed class MetricEntry
{
    public MetricKey Key { get; set; }
    public bool Enabled { get; set; } = true;
}

public sealed class VitalsConfig
{
    public List<MetricEntry> Metrics { get; set; } = DefaultOrder();
    public double Scale { get; set; } = 1.0;
    public double Opacity { get; set; } = 0.95;

    /// <summary>Interpola los valores entre lecturas (~11 repintados/s en vez de 1).</summary>
    public bool SmoothTransitions { get; set; } = true;

    /// <summary>Dibuja el historial de cada métrica. Al desactivarlo la píldora se vuelve compacta.</summary>
    public bool ShowCharts { get; set; } = true;

    /// <summary>Ancho en píxeles de cada columna: controla lo compacta que queda la píldora.</summary>
    public int ColumnWidth { get; set; } = 96;

    /// <summary>Pinta el valor en ámbar/rojo al superar los umbrales de alerta.</summary>
    public bool AlertColors { get; set; } = true;

    public static List<MetricEntry> DefaultOrder() =>
    [
        new() { Key = MetricKey.Cpu },
        new() { Key = MetricKey.Gpu },
        new() { Key = MetricKey.Ram },
        new() { Key = MetricKey.Up },
        new() { Key = MetricKey.Down },
        new() { Key = MetricKey.Battery },
    ];

    public static string InstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vitals");

    public static string ConfigPath => Path.Combine(InstallDir, "config.json");

    // Sin config.json todavía es el estado normal de un primer arranque, no
    // un error — se resuelve con los valores por defecto, no con un catch-all.
    public static VitalsConfig Load()
    {
        string path = ConfigPath;
        if (!File.Exists(path)) return new VitalsConfig();

        string json = File.ReadAllText(path);
        return JsonSerializer.Deserialize(json, VitalsJsonContext.Default.VitalsConfig) is { Metrics.Count: > 0 } config
            ? config
            : new VitalsConfig();
    }

    public void Save()
    {
        Directory.CreateDirectory(InstallDir);
        string json = JsonSerializer.Serialize(this, VitalsJsonContext.Default.VitalsConfig);
        File.WriteAllText(ConfigPath, json);
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(VitalsConfig))]
public partial class VitalsJsonContext : JsonSerializerContext;
