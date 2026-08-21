using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using Vitals.Shared;

namespace Vitals.Settings;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<MetricRow> _rows;
    private Corner _position = Corner.BottomRight;
    private MetricRow? _colorEditRow;

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "Vitals";

    public MainWindow()
    {
        InitializeComponent();

        var config = VitalsConfig.Load();
        _rows = new ObservableCollection<MetricRow>(config.Metrics.Select(m =>
        {
            var (defWarn, defCrit) = MetricRow.DefaultThresholds(m.Key);
            return new MetricRow(m.Key, m.Enabled, m.Color, m.WarnThreshold ?? defWarn, m.CritThreshold ?? defCrit);
        }));
        MetricsList.ItemsSource = _rows;
        ThresholdsList.ItemsSource = _rows;
        PreviewMetrics.ItemsSource = _rows;

        ScaleSlider.Value = config.Scale;
        OpacitySlider.Value = config.Opacity;
        WidthSlider.Value = config.ColumnWidth;
        FontSlider.Value = config.FontScale;
        ChartsCheck.IsChecked = config.ShowCharts;
        SmoothCheck.IsChecked = config.SmoothTransitions;
        AlertCheck.IsChecked = config.AlertColors;
        AutoStartCheck.IsChecked = config.AutoStart;
        AutoHideCheck.IsChecked = config.AutoHideFullscreen;
        ClickThroughCheck.IsChecked = config.ClickThrough;
        UpdateScaleReadout();
        UpdateOpacityReadout();
        UpdateWidthReadout();
        UpdateFontReadout();

        _position = config.Position;
        (config.Position switch
        {
            Corner.TopLeft => PosTopLeft,
            Corner.Top => PosTop,
            Corner.TopRight => PosTopRight,
            Corner.Left => PosLeft,
            Corner.Center => PosCenter,
            Corner.Right => PosRight,
            Corner.BottomLeft => PosBottomLeft,
            Corner.Bottom => PosBottom,
            _ => PosBottomRight,
        }).IsChecked = true;

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = version is null ? "Versión —" : $"Versión {version.Major}.{version.Minor}.{version.Build}";
    }

    // ---- navegación ----

    private static readonly Dictionary<string, (string Title, string Subtitle)> PageInfo = new()
    {
        ["General"] = ("General", "Personaliza la apariencia y comportamiento de la píldora de Vitals."),
        ["Métricas"] = ("Métricas", "Qué mostrar y cómo."),
        ["Rendimiento"] = ("Rendimiento", "Gráficos e historial."),
        ["Posición"] = ("Posición", "Dónde vive la píldora en tu pantalla."),
        ["Alertas"] = ("Alertas", "Umbrales y colores de aviso por métrica."),
        ["Accesos rápidos"] = ("Accesos rápidos", "Teclas y atajos."),
        ["Acerca de"] = ("Acerca de", "Info de Vitals."),
    };

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        // NavGeneral trae IsChecked="True" desde el XAML, así que este evento
        // dispara durante el propio InitializeComponent() — antes de que los
        // paneles de página declarados más abajo existan. La visibilidad
        // inicial ya es correcta por los valores por defecto del XAML, así
        // que no hay nada que hacer todavía.
        if (PageGeneral is null) return;
        if (sender is not RadioButton rb || rb.Content is not string page) return;

        PageGeneral.Visibility = page == "General" ? Visibility.Visible : Visibility.Collapsed;
        PageMetricas.Visibility = page == "Métricas" ? Visibility.Visible : Visibility.Collapsed;
        PageRendimiento.Visibility = page == "Rendimiento" ? Visibility.Visible : Visibility.Collapsed;
        PagePosicion.Visibility = page == "Posición" ? Visibility.Visible : Visibility.Collapsed;
        PageAlertas.Visibility = page == "Alertas" ? Visibility.Visible : Visibility.Collapsed;
        PageAccesos.Visibility = page == "Accesos rápidos" ? Visibility.Visible : Visibility.Collapsed;
        PageAcerca.Visibility = page == "Acerca de" ? Visibility.Visible : Visibility.Collapsed;

        if (PageInfo.TryGetValue(page, out var info))
        {
            PageTitle.Text = info.Title;
            PageSubtitle.Text = info.Subtitle;
        }
    }

    // ---- posición ----

    private void Position_Checked(object sender, RoutedEventArgs e)
    {
        if (sender == PosTopLeft) _position = Corner.TopLeft;
        else if (sender == PosTop) _position = Corner.Top;
        else if (sender == PosTopRight) _position = Corner.TopRight;
        else if (sender == PosLeft) _position = Corner.Left;
        else if (sender == PosCenter) _position = Corner.Center;
        else if (sender == PosRight) _position = Corner.Right;
        else if (sender == PosBottomLeft) _position = Corner.BottomLeft;
        else if (sender == PosBottom) _position = Corner.Bottom;
        else if (sender == PosBottomRight) _position = Corner.BottomRight;
    }

    // ---- esquema de color ----

    private static readonly Dictionary<string, string[]> ColorSchemes = new()
    {
        // Orden: Cpu, Gpu, Ram, Up, Down, Battery.
        ["Neon"] = ["#4D8CFF", "#3DD67D", "#A87FFF", "#FF9F43", "#56C4FF", "#4CD964"],
        ["Classic"] = ["#6B8CAE", "#6BAE8C", "#9B8CAE", "#AE8C6B", "#6B9BAE", "#7CAE6B"],
        ["Pastel"] = ["#A8C5FF", "#A8F0C5", "#D4C2FF", "#FFCBA8", "#A8E5FF", "#B8F0A8"],
        ["Monochrome"] = ["#DDDDDD", "#DDDDDD", "#DDDDDD", "#DDDDDD", "#DDDDDD", "#DDDDDD"],
    };

    private static readonly MetricKey[] SchemeOrder =
        [MetricKey.Cpu, MetricKey.Gpu, MetricKey.Ram, MetricKey.Up, MetricKey.Down, MetricKey.Battery];

    private void SchemePreset_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not string schemeName) return;
        if (!ColorSchemes.TryGetValue(schemeName, out var palette)) return;

        for (int i = 0; i < SchemeOrder.Length; i++)
        {
            var row = _rows.FirstOrDefault(r => r.Key == SchemeOrder[i]);
            if (row is not null) row.ColorHex = palette[i];
        }
    }

    // ---- color por métrica ----

    private void ColorSwatch_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not MetricRow row) return;
        _colorEditRow = row;
        ColorPopup.PlacementTarget = (UIElement)sender;
        ColorPopup.IsOpen = true;
    }

    private void PaletteColor_Click(object sender, RoutedEventArgs e)
    {
        if (_colorEditRow is not null && ((FrameworkElement)sender).Tag is string hex)
            _colorEditRow.ColorHex = hex;
        ColorPopup.IsOpen = false;
    }

    private void ResetColor_Click(object sender, MouseButtonEventArgs e)
    {
        if (_colorEditRow is not null)
            _colorEditRow.ColorHex = null;
        ColorPopup.IsOpen = false;
    }

    // ---- umbrales de alerta ----

    private void ThresholdMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not MetricRow row) return;
        if (sender is not ComboBox combo) return;
        row.SetThresholdMode(combo.SelectedIndex == 1);
    }

    // ---- readouts ----

    private void FontSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateFontReadout();
    private void UpdateFontReadout()
    {
        if (FontReadout is not null) FontReadout.Text = $"{FontSlider.Value * 100:0}%";
    }

    private void WidthSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateWidthReadout();
    private void UpdateWidthReadout()
    {
        if (WidthReadout is not null) WidthReadout.Text = $"{WidthSlider.Value:0} px";
    }

    private void ScaleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateScaleReadout();
    private void UpdateScaleReadout()
    {
        if (ScaleReadout is not null) ScaleReadout.Text = $"{ScaleSlider.Value * 100:0}%";
    }

    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateOpacityReadout();
    private void UpdateOpacityReadout()
    {
        if (OpacityReadout is not null) OpacityReadout.Text = $"{OpacitySlider.Value * 100:0}%";
    }

    // ---- reordenar métricas ----

    private void MoveUp_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not MetricRow row) return;
        int i = _rows.IndexOf(row);
        if (i > 0) _rows.Move(i, i - 1);
    }

    private void MoveDown_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not MetricRow row) return;
        int i = _rows.IndexOf(row);
        if (i < _rows.Count - 1) _rows.Move(i, i + 1);
    }

    // ---- restablecer / cancelar / guardar ----

    private void ResetDefaults_Click(object sender, RoutedEventArgs e)
    {
        var defaults = new VitalsConfig();

        ScaleSlider.Value = defaults.Scale;
        OpacitySlider.Value = defaults.Opacity;
        WidthSlider.Value = defaults.ColumnWidth;
        FontSlider.Value = defaults.FontScale;
        ChartsCheck.IsChecked = defaults.ShowCharts;
        SmoothCheck.IsChecked = defaults.SmoothTransitions;
        AlertCheck.IsChecked = defaults.AlertColors;
        AutoStartCheck.IsChecked = defaults.AutoStart;
        AutoHideCheck.IsChecked = defaults.AutoHideFullscreen;
        ClickThroughCheck.IsChecked = defaults.ClickThrough;
        PosBottomRight.IsChecked = true;

        _rows.Clear();
        foreach (var entry in VitalsConfig.DefaultOrder())
        {
            var (defWarn, defCrit) = MetricRow.DefaultThresholds(entry.Key);
            _rows.Add(new MetricRow(entry.Key, entry.Enabled, null, defWarn, defCrit));
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private static void ApplyAutoStart(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        if (key is null) return;

        if (enabled)
        {
            string exePath = Path.Combine(AppContext.BaseDirectory, "Vitals.exe");
            key.SetValue(RunValueName, $"\"{exePath}\"");
        }
        else
        {
            key.DeleteValue(RunValueName, throwOnMissingValue: false);
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var config = new VitalsConfig
        {
            Metrics = _rows.Select(r => new MetricEntry
            {
                Key = r.Key,
                Enabled = r.Enabled,
                Color = r.ColorHex,
                WarnThreshold = r.WarnThreshold,
                CritThreshold = r.CritThreshold,
            }).ToList(),
            Scale = ScaleSlider.Value,
            Opacity = OpacitySlider.Value,
            ShowCharts = ChartsCheck.IsChecked == true,
            SmoothTransitions = SmoothCheck.IsChecked == true,
            AlertColors = AlertCheck.IsChecked == true,
            ColumnWidth = (int)WidthSlider.Value,
            FontScale = FontSlider.Value,
            Position = _position,
            AutoStart = AutoStartCheck.IsChecked == true,
            AutoHideFullscreen = AutoHideCheck.IsChecked == true,
            ClickThrough = ClickThroughCheck.IsChecked == true,
        };
        config.Save();
        ApplyAutoStart(config.AutoStart);

        // Vitals aplica los cambios en caliente (se redimensiona y se
        // reposiciona) — no hace falta matar el proceso ni relanzarlo.
        nint hwnd = NativeInterop.FindWindow("VitalsPillWindow", null);
        if (hwnd != 0)
        {
            NativeInterop.PostMessage(hwnd, NativeInterop.WM_RELOAD_CONFIG, 0, 0);
        }
        else
        {
            string vitalsExe = Path.Combine(AppContext.BaseDirectory, "Vitals.exe");
            if (File.Exists(vitalsExe))
                Process.Start(vitalsExe);
        }

        Close();
    }
}
