using System.Windows;
using CodexAuthSwitcher.App.Views;
using CodexAuthSwitcher.Core.Models;

namespace CodexAuthSwitcher.App.Services;

public sealed class WpfDialogService : IDialogService
{
    private readonly Window _owner;
    private readonly LocalizationService _localizationService;

    public WpfDialogService(Window owner, LocalizationService localizationService)
    {
        _owner = owner;
        _localizationService = localizationService;
    }

    public string? PromptSnapshotName(string suggestedName)
    {
        var window = new SnapshotNameWindow(suggestedName)
        {
            Owner = _owner
        };
        return window.ShowDialog() == true ? window.ProfileName : null;
    }

    public ApiProfileSpec? EditApiProfile(ApiProfileSpec profile, bool isEditMode)
    {
        var window = new ApiProfileEditorWindow(profile.Clone(), isEditMode)
        {
            Owner = _owner
        };
        return window.ShowDialog() == true ? window.Profile : null;
    }

    public bool ConfirmDelete(string profileName)
    {
        return MessageBox.Show(
                   _owner,
                   _localizationService.Format("Dialog.Delete.Message", profileName),
                   _localizationService["Dialog.Delete.Title"],
                   MessageBoxButton.YesNo,
                   MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }

    public RunningCodexDecision PromptRunningCodex()
    {
        var window = new RunningCodexWindow
        {
            Owner = _owner
        };
        return window.ShowDialog() == true ? window.Decision : RunningCodexDecision.Cancel;
    }

    public void ShowInfo(string title, string message)
    {
        MessageBox.Show(_owner, message, title, MessageBoxButton.OK, MessageBoxImage.Information);
    }

    public void ShowError(string title, string message)
    {
        MessageBox.Show(_owner, message, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
