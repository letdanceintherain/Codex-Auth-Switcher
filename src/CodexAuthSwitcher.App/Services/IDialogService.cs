using CodexAuthSwitcher.Core.Models;

namespace CodexAuthSwitcher.App.Services;

public interface IDialogService
{
    string? PromptSnapshotName(string suggestedName);
    ApiProfileSpec? EditApiProfile(ApiProfileSpec profile, bool isEditMode);
    bool ConfirmDelete(string profileName);
    RunningCodexDecision PromptRunningCodex();
    void ShowInfo(string title, string message);
    void ShowError(string title, string message);
}
