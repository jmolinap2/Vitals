namespace Vitals;

internal static class Program
{
    private static void Main()
    {
        // El bridge mantiene el launcher desacoplado del render principal de la cápsula.
        QuickAccessIntegration.InstallForCurrentThread();
        try
        {
            PillWindow.Run();
        }
        finally
        {
            QuickAccessIntegration.Uninstall();
        }
    }
}
