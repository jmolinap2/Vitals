using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Vitals.Shared;

namespace Vitals.Settings;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<MetricRow> _rows;

    public MainWindow()
    {
        InitializeComponent();

        var config = VitalsConfig.Load();
        _rows = new ObservableCollection<MetricRow>(
            config.Metrics.Select(m => new MetricRow(m.Key, m.Enabled)));
        MetricsList.ItemsSource = _rows;
    }

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

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var config = new VitalsConfig
        {
            Metrics = _rows.Select(r => new MetricEntry { Key = r.Key, Enabled = r.Enabled }).ToList(),
        };
        config.Save();

        foreach (var proc in Process.GetProcessesByName("Vitals"))
        {
            proc.Kill();
            proc.WaitForExit(2000);
        }

        string vitalsExe = Path.Combine(AppContext.BaseDirectory, "Vitals.exe");
        if (File.Exists(vitalsExe))
            Process.Start(vitalsExe);

        Close();
    }
}
