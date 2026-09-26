using System.Windows;

#pragma warning disable WPF0001 // ThemeMode is experimental in .NET 10.

namespace DarkStartupMinimal;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var application = new Application
        {
            ShutdownMode = ShutdownMode.OnMainWindowClose,
            ThemeMode = ThemeMode.Dark
        };

        var window = new MainWindow();
        // --no-cloak reproduces the original light flash for comparison.
        if (!args.Contains("--no-cloak")) FirstFrameCloak.Attach(window);
        // --no-activate opens the window the way a background (tray) process does: without activation.
        if (args.Contains("--no-activate")) window.ShowActivated = false;
        application.Run(window);
    }
}
