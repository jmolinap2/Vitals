using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vitals.Shared;

public enum MetricKey { Cpu, Gpu, Ram, Up, Down, Battery }

/// <summary>Las 9 combinaciones de una grilla de 3x3 en el área de trabajo.</summary>
public enum Corner
{
    TopLeft, Top, TopRight,
    Left, Center, Right,
    BottomLeft, Bottom, BottomRight,
}

public enum QuickAccessIconMode
{
    Automatic,
    Custom,
}

public enum QuickLauncherDirection
{
    Auto,
    Down,
    Up,
    Right,
    Left,
}

public enum QuickLauncherLayout
{
    Grid,
    Row,
    Column,
}

public sealed class QuickAccessItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public string? Arguments { get; set; }
    public string? WorkingDirectory { get; set; }
    public QuickAccessIconMode IconMode { get; set; } = QuickAccessIconMode.Automatic;
    public string? IconPath { get; set; }
    public bool Enabled { get; set; } = true;
    public int Order { get; set; }
}

public sealed class QuickLauncherConfig
{
    public bool Enabled { get; set; } = true;
    public int Columns { get; set; } = 4;
    public int IconSize { get; set; } = 32;
    public bool ShowLabels { get; set; } = true;
    public int AnimationMs { get; set; } = 160;
    public bool CollapseOnLaunch { get; set; } = true;
    public QuickLauncherDirection Direction { get; set; } = QuickLauncherDirection.Auto;
    public QuickLauncherLayout Layout { get; set; } = QuickLauncherLayout.Grid;
}

public static class QuickAccessStore
{
    public static string Path => System.IO.Path.Combine(VitalsConfig.InstallDir, "quick-access.json");

    public static List<QuickAccessItem> Load()
    {
        if (!File.Exists(Path)) return [];
        string json = File.ReadAllText(Path);
        return JsonSerializer.Deserialize(json, VitalsJsonContext.Default.ListQuickAccessItem) ?? [];
    }

    public static void Save(IEnumerable<QuickAccessItem> items)
    {
        Directory.CreateDirectory(VitalsConfig.InstallDir);
        string json = JsonSerializer.Serialize(items.ToList(), VitalsJsonContext.Default.ListQuickAccessItem);
        File.WriteAllText(Path, json);
    }
}

public static class QuickLauncherStore
{
    public static string Path => System.IO.Path.Combine(VitalsConfig.InstallDir, "quick-launcher.json");

    public static QuickLauncherConfig Load()
    {
        if (!File.Exists(Path)) return new QuickLauncherConfig();
        string json = File.ReadAllText(Path);
        return JsonSerializer.Deserialize(json, VitalsJsonContext.Default.QuickLauncherConfig) ?? new QuickLauncherConfig();
    }

    public static void Save(QuickLauncherConfig config)
    {
        Directory.CreateDirectory(VitalsConfig.InstallDir);
        string json = JsonSerializer.Serialize(config, VitalsJsonContext.Default.QuickLauncherConfig);
        File.WriteAllText(Path, json);
    }
}

public sealed class MetricEntry
{
    public MetricKey Key { get; set; }
    public bool Enabled { get; set; } = true;

    /// <summary>Color de acento en hex "#RRGGBB". Null = color por defecto de la métrica.</summary>
    public string? Color { get; set; }

    /// <summary>Umbral de aviso (ámbar), 0-100. Null = valor por defecto de la métrica.</summary>
    public double? WarnThreshold { get; set; }

    /// <summary>Umbral crítico (rojo), 0-100. Null = valor por defecto de la métrica.</summary>
    public double? CritThreshold { get; set; }
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

    /// <summary>Multiplicador sobre el tamaño base de etiquetas y valores — independiente de Scale.</summary>
    public double FontScale { get; set; } = 1.0;

    /// <summary>Posición en la grilla de 3x3 del área de trabajo donde vive la píldora.</summary>
    public Corner Position { get; set; } = Corner.BottomRight;

    /// <summary>Se agrega a la carpeta de inicio de Windows del usuario actual.</summary>
    public bool AutoStart { get; set; }

    /// <summary>Oculta la píldora mientras la ventana en primer plano ocupa toda la pantalla.</summary>
    public bool AutoHideFullscreen { get; set; }

    /// <summary>Los clics atraviesan la píldora hacia la ventana debajo (WS_EX_TRANSPARENT).</summary>
    public bool ClickThrough { get; set; }

    public static List<MetricEntry> DefaultOrder() =>
    [
        new() { Key = MetricKey.Cpu },
        new() { Key = MetricKey.Gpu },
        new() { Key = MetricKey.Ram },
        new() { Key = MetricKey.Up },
        new() { Key = MetricKey.Down },
        new() { Key = MetricKey.Battery },
    ];

    public static string InstallDir => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vitals");

    public static string ConfigPath => System.IO.Path.Combine(InstallDir, "config.json");

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
[JsonSerializable(typeof(List<QuickAccessItem>))]
[JsonSerializable(typeof(QuickLauncherConfig))]
public partial class VitalsJsonContext : JsonSerializerContext;
