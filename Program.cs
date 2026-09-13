namespace Vitals;

internal static class Program
{
    private static void Main()
    {
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
