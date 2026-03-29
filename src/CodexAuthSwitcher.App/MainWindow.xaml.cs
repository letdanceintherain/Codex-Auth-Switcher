using System.Windows;
using CodexAuthSwitcher.App.Services;
using CodexAuthSwitcher.App.ViewModels;
using CodexAuthSwitcher.Core.Services;

namespace CodexAuthSwitcher.App;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;

    public MainWindow(string codexHome)
    {
        InitializeComponent();
        _viewModel = new MainWindowViewModel(
            new CodexAuthSwitcherService(codexHome),
            new CodexDesktopProcessService(),
            new WpfDialogService(this, App.Localization),
            App.Localization);
        DataContext = _viewModel;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await _viewModel.InitializeAsync();
    }
}
