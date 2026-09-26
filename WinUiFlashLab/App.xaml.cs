using Microsoft.UI.Xaml;

namespace WinUiFlashLab;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
        // Dark unless the mode asks for light or for the Windows app mode (Program.Theme is null then).
        if (Program.Theme is { } theme) RequestedTheme = theme;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args) => Program.Launch();
}
