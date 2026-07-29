using System.Windows;
using CodexAuthSwitcher.App.Services;
using CodexAuthSwitcher.Core.Services;

namespace CodexAuthSwitcher.App;

public partial class App : Application
{
    public static LocalizationService Localization { get; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var codexHome = CodexPathResolver.ResolveCodexHome();
        Localization.Initialize(codexHome);

        var window = new MainWindow(codexHome);
        MainWindow = window;
        window.Show();
    }
}
