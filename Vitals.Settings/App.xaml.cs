using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace Vitals.Settings;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        EventManager.RegisterClassHandler(
            typeof(RadioButton),
            ToggleButton.CheckedEvent,
            new RoutedEventHandler(OnRadioButtonChecked));
    }

    private static void OnRadioButtonChecked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Content: string content } button || content != "Accesos rápidos")
            return;

        var owner = Window.GetWindow(button);
        if (owner is null) return;

        owner.Dispatcher.BeginInvoke(() =>
        {
            var dialog = new QuickAccessManagerWindow { Owner = owner };
            dialog.ShowDialog();

            if (owner.FindName("NavGeneral") is RadioButton general)
                general.IsChecked = true;
        }, DispatcherPriority.Background);
    }
}
