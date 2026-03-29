using System.Windows;
using CodexAuthSwitcher.App.Services;

namespace CodexAuthSwitcher.App.Views;

public partial class RunningCodexWindow : Window
{
    public RunningCodexWindow()
    {
        InitializeComponent();
        Decision = RunningCodexDecision.Cancel;
        DataContext = this;
    }

    public RunningCodexDecision Decision { get; private set; }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Decision = RunningCodexDecision.Cancel;
        DialogResult = false;
        Close();
    }

    private void ManualClose_Click(object sender, RoutedEventArgs e)
    {
        Decision = RunningCodexDecision.ManualClose;
        DialogResult = true;
        Close();
    }

    private void AutoClose_Click(object sender, RoutedEventArgs e)
    {
        Decision = RunningCodexDecision.AutoCloseAndRestart;
        DialogResult = true;
        Close();
    }
}