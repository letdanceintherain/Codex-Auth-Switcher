using System.Collections.ObjectModel;
using CodexAuthSwitcher.App.Models;
using CodexAuthSwitcher.App.Services;
using CodexAuthSwitcher.Core.Models;
using CodexAuthSwitcher.Core.Services;

namespace CodexAuthSwitcher.App.ViewModels;

public sealed class MainWindowViewModel : ViewModelBase
{
    private readonly CodexAuthSwitcherService _switcherService;
    private readonly CodexDesktopProcessService _desktopProcessService;
    private readonly IDialogService _dialogService;
    private readonly LocalizationService _localizationService;

    private string _codexHomePath = string.Empty;
    private string _currentAuthMode = "unknown";
    private string _modelProvider = "(default)";
    private string _model = "(default)";
    private string? _currentProfileName;
    private ProfileKind? _currentProfileKind;
    private LiveAuthIdentity? _liveIdentity;
    private bool _isCodexRunning;
    private string _statusMessage = string.Empty;
    private string _statusKey = "Status.Ready";
    private object[] _statusArgs = [];
    private bool _isBusy;
    private ProfileItemViewModel? _selectedProfile;
    private string? _desktopAppPath;

    private sealed class RefreshSnapshot
    {
        public required CodexEnvironment Environment { get; init; }
        public required DesktopAppState ProcessState { get; init; }
        public required IReadOnlyList<ProfileItemViewModel> Profiles { get; init; }
        public required IdentitySyncResult SyncResult { get; init; }
    }

    private sealed class SwitchExecutionResult
    {
        public required SwitchResult SwitchResult { get; init; }
        public required RestartMethod RestartMethod { get; init; }
    }

    public MainWindowViewModel(
        CodexAuthSwitcherService switcherService,
        CodexDesktopProcessService desktopProcessService,
        IDialogService dialogService,
        LocalizationService localizationService)
    {
        _switcherService = switcherService;
        _desktopProcessService = desktopProcessService;
        _dialogService = dialogService;
        _localizationService = localizationService;
        _localizationService.LanguageChanged += OnLanguageChanged;

        Profiles = new ObservableCollection<ProfileItemViewModel>();
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsBusy);
        CaptureCurrentCommand = new AsyncRelayCommand(CaptureCurrentAsync, () => !IsBusy && string.Equals(CurrentAuthMode, "chatgpt", StringComparison.OrdinalIgnoreCase));
        NewApiProfileCommand = new AsyncRelayCommand(CreateApiProfileAsync, () => !IsBusy);
        EditSelectedProfileCommand = new AsyncRelayCommand(EditSelectedProfileAsync, () => !IsBusy && SelectedProfile?.Summary.Kind == ProfileKind.ApiKey);
        DeleteSelectedProfileCommand = new AsyncRelayCommand(DeleteSelectedProfileAsync, () => !IsBusy && SelectedProfile is not null);
        SwitchSelectedProfileCommand = new AsyncRelayCommand(SwitchSelectedProfileAsync, () => !IsBusy && SelectedProfile is not null);
        SetChineseCommand = new RelayCommand(() => SetLanguage(AppLanguage.ZhCn), () => !_localizationService.IsChineseSelected);
        SetEnglishCommand = new RelayCommand(() => SetLanguage(AppLanguage.EnUs), () => !_localizationService.IsEnglishSelected);

        SetStatus("Status.Ready");
    }

    public ObservableCollection<ProfileItemViewModel> Profiles { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand CaptureCurrentCommand { get; }
    public AsyncRelayCommand NewApiProfileCommand { get; }
    public AsyncRelayCommand EditSelectedProfileCommand { get; }
    public AsyncRelayCommand DeleteSelectedProfileCommand { get; }
    public AsyncRelayCommand SwitchSelectedProfileCommand { get; }
    public RelayCommand SetChineseCommand { get; }
    public RelayCommand SetEnglishCommand { get; }

    public string CodexHomePath
    {
        get => _codexHomePath;
        private set => SetProperty(ref _codexHomePath, value);
    }

    public string CurrentAuthMode
    {
        get => _currentAuthMode;
        private set
        {
            if (SetProperty(ref _currentAuthMode, value))
            {
                OnPropertyChanged(nameof(CurrentAuthDisplayText));
            }
        }
    }

    public string ModelProvider
    {
        get => _modelProvider;
        private set
        {
            if (SetProperty(ref _modelProvider, value))
            {
                OnPropertyChanged(nameof(ModelProviderDisplayText));
            }
        }
    }

    public string Model
    {
        get => _model;
        private set
        {
            if (SetProperty(ref _model, value))
            {
                OnPropertyChanged(nameof(ModelDisplayText));
            }
        }
    }

    public bool IsCodexRunning
    {
        get => _isCodexRunning;
        private set
        {
            if (SetProperty(ref _isCodexRunning, value))
            {
                OnPropertyChanged(nameof(DesktopCodexStatusText));
            }
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RaiseCommandStateChanged();
            }
        }
    }

    public ProfileItemViewModel? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (SetProperty(ref _selectedProfile, value))
            {
                RaiseSelectedProfileChanged();
                RaiseCommandStateChanged();
            }
        }
    }

    public bool IsChineseSelected => _localizationService.IsChineseSelected;
    public bool IsEnglishSelected => _localizationService.IsEnglishSelected;
    public string CurrentAuthDisplayText => _localizationService[$"AuthMode.{CurrentAuthMode.ToLowerInvariant()}"];
    public string DesktopCodexStatusText => _localizationService.Format("Card.Running", IsCodexRunning);
    public string ModelProviderDisplayText => _localizationService.Format("Card.Provider", ModelProvider);
    public string ModelDisplayText => _localizationService.Format("Card.Model", Model);
    public string CurrentProfileDisplayName => string.IsNullOrWhiteSpace(_currentProfileName)
        ? _localizationService["Common.Untracked"]
        : _currentProfileName;
    public string CurrentProfileKindDisplayText => _currentProfileKind switch
    {
        ProfileKind.ChatGptSnapshot => _localizationService["ProfileKind.ChatGpt"],
        ProfileKind.ApiKey => _localizationService["ProfileKind.Api"],
        _ => _localizationService["Common.Untracked"]
    };

    public string CurrentIdentityPrimaryText => BuildLiveIdentityPrimary(_liveIdentity);
    public string CurrentIdentitySecondaryText => BuildLiveIdentitySecondary(_liveIdentity);
    public string SelectedProfileDisplayName => SelectedProfile?.Name ?? _localizationService["Common.None"];
    public string SelectedProfileKindText => SelectedProfile?.KindLabel ?? _localizationService["Common.None"];
    public string SelectedProfileIdentityText => BuildSelectedProfileIdentity(SelectedProfile?.Summary);
    public string SelectedProfileDetailText => SelectedProfile?.Detail ?? _localizationService["Common.None"];

    public async Task InitializeAsync()
    {
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        var snapshot = await RunBusyAsync(() => Task.Run(LoadRefreshSnapshot));
        if (snapshot is null)
        {
            return;
        }

        ApplyRefreshSnapshot(snapshot);
        if (snapshot.SyncResult.CreatedProfile && !string.IsNullOrWhiteSpace(snapshot.SyncResult.MatchedProfileName))
        {
            SetStatus("Status.AutoCapturedChatGpt", snapshot.SyncResult.MatchedProfileName);
            return;
        }

        if (snapshot.SyncResult.LiveIdentity?.Kind == ProfileKind.ApiKey && !snapshot.SyncResult.IsManaged)
        {
            SetStatus("Status.ApiUnmanaged");
            return;
        }

        SetStatus("Status.LoadedProfiles", Profiles.Count);
    }

    private async Task CaptureCurrentAsync()
    {
        var suggestedName = !string.IsNullOrWhiteSpace(_currentProfileName) ? _currentProfileName : "chatgpt";
        var profileName = _dialogService.PromptSnapshotName(suggestedName);
        if (string.IsNullOrWhiteSpace(profileName))
        {
            return;
        }

        var completed = await RunBusyAsync(() => Task.Run(() =>
        {
            _switcherService.CaptureCurrentChatGptSnapshot(profileName);
            return true;
        }));
        if (completed != true)
        {
            return;
        }

        await RefreshAsync();
        SetStatus("Status.SavedChatGpt", profileName);
    }

    private async Task CreateApiProfileAsync()
    {
        var profile = _dialogService.EditApiProfile(new ApiProfileSpec(), isEditMode: false);
        if (profile is null)
        {
            return;
        }

        var completed = await RunBusyAsync(() => Task.Run(() =>
        {
            _switcherService.SaveApiProfile(profile);
            return true;
        }));
        if (completed != true)
        {
            return;
        }

        await RefreshAsync();
        SetStatus("Status.SavedApi", profile.Name);
    }

    private async Task EditSelectedProfileAsync()
    {
        if (SelectedProfile?.Summary.Kind != ProfileKind.ApiKey)
        {
            return;
        }

        var existing = _switcherService.LoadApiProfile(SelectedProfile.Name);
        if (existing is null)
        {
            _dialogService.ShowError(
                _localizationService["Dialog.EditProfileError.Title"],
                _localizationService["Dialog.EditProfileError.Message"]);
            return;
        }

        var updated = _dialogService.EditApiProfile(existing, isEditMode: true);
        if (updated is null)
        {
            return;
        }

        var completed = await RunBusyAsync(() => Task.Run(() =>
        {
            _switcherService.SaveApiProfile(updated);
            return true;
        }));
        if (completed != true)
        {
            return;
        }

        await RefreshAsync();
        SetStatus("Status.UpdatedApi", updated.Name);
    }

    private async Task DeleteSelectedProfileAsync()
    {
        if (SelectedProfile is null)
        {
            return;
        }

        if (!_dialogService.ConfirmDelete(SelectedProfile.Name))
        {
            return;
        }

        var targetName = SelectedProfile.Name;
        var completed = await RunBusyAsync(() => Task.Run(() =>
        {
            _switcherService.DeleteProfile(targetName);
            return true;
        }));
        if (completed != true)
        {
            return;
        }

        await RefreshAsync();
        SetStatus("Status.DeletedProfile", targetName);
    }

    private async Task SwitchSelectedProfileAsync()
    {
        if (SelectedProfile is null)
        {
            return;
        }

        if (IsCodexRunning)
        {
            var decision = _dialogService.PromptRunningCodex();
            if (decision == RunningCodexDecision.Cancel)
            {
                return;
            }

            if (decision == RunningCodexDecision.ManualClose)
            {
                _dialogService.ShowInfo(
                    _localizationService["Dialog.CodexRunning.Title"],
                    _localizationService["Dialog.CodexRunning.Info"]);
                return;
            }

            var closed = await RunBusyAsync(() => Task.Run(() =>
            {
                var success = _desktopProcessService.CloseDesktopApp(TimeSpan.FromSeconds(8));
                if (!success)
                {
                    throw new InvalidOperationException("Unable to close the desktop Codex app automatically. Please close it manually first.");
                }

                return true;
            }));
            if (closed != true)
            {
                return;
            }
        }

        var result = await RunBusyAsync(() => Task.Run(() =>
        {
            var switchResult = _switcherService.SwitchProfile(SelectedProfile.Name);
            var restartMethod = _desktopProcessService.RestartDesktopApp(_desktopAppPath);
            return new SwitchExecutionResult
            {
                SwitchResult = switchResult,
                RestartMethod = restartMethod
            };
        }));
        if (result is null)
        {
            return;
        }

        if (result.RestartMethod == RestartMethod.None)
        {
            _dialogService.ShowError(
                _localizationService["Dialog.RestartFailed.Title"],
                _localizationService["Dialog.RestartFailed.Message"]);
        }

        await RefreshAsync();
        if (result.RestartMethod == RestartMethod.None)
        {
            SetStatus("Status.SwitchedRestartFailed", result.SwitchResult.AppliedProfileName);
        }
        else
        {
            SetStatus("Status.SwitchedRestarted", result.SwitchResult.AppliedProfileName, result.RestartMethod);
        }
    }

    private async Task<T?> RunBusyAsync<T>(Func<Task<T>> action)
    {
        try
        {
            IsBusy = true;
            return await action();
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            _dialogService.ShowError(_localizationService["Dialog.Error.Title"], ex.Message);
            return default;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private RefreshSnapshot LoadRefreshSnapshot()
    {
        var syncResult = _switcherService.EnsureCurrentIdentityTracked();
        var environment = _switcherService.GetEnvironment();
        var currentProfileName = environment.CurrentProfile?.ProfileName;
        var processState = _desktopProcessService.GetDesktopAppState();
        var profileItems = _switcherService.GetProfiles()
            .OrderByDescending(item => string.Equals(item.Name, currentProfileName, StringComparison.OrdinalIgnoreCase))
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Select(item => new ProfileItemViewModel(item, string.Equals(item.Name, currentProfileName, StringComparison.OrdinalIgnoreCase), _localizationService))
            .ToList();

        return new RefreshSnapshot
        {
            Environment = environment,
            ProcessState = processState,
            Profiles = profileItems,
            SyncResult = syncResult
        };
    }

    private void ApplyRefreshSnapshot(RefreshSnapshot snapshot)
    {
        var previousSelectionName = SelectedProfile?.Name;
        DisposeProfiles();

        CodexHomePath = snapshot.Environment.CodexHomePath;
        CurrentAuthMode = snapshot.Environment.CurrentAuthMode;
        ModelProvider = snapshot.Environment.ModelProvider;
        Model = snapshot.Environment.Model;
        _currentProfileName = snapshot.Environment.CurrentProfile?.ProfileName;
        _currentProfileKind = snapshot.Environment.CurrentProfile?.Kind;
        _liveIdentity = snapshot.Environment.LiveIdentity;
        OnPropertyChanged(nameof(CurrentProfileDisplayName));
        OnPropertyChanged(nameof(CurrentProfileKindDisplayText));
        OnPropertyChanged(nameof(CurrentIdentityPrimaryText));
        OnPropertyChanged(nameof(CurrentIdentitySecondaryText));

        IsCodexRunning = snapshot.ProcessState.IsRunning;
        _desktopAppPath = snapshot.ProcessState.DesktopAppPath;

        Profiles.Clear();
        foreach (var profile in snapshot.Profiles)
        {
            Profiles.Add(profile);
        }

        SelectedProfile = Profiles.FirstOrDefault(item => string.Equals(item.Name, previousSelectionName, StringComparison.OrdinalIgnoreCase))
            ?? Profiles.FirstOrDefault(item => item.IsCurrent)
            ?? Profiles.FirstOrDefault();

        RaiseCommandStateChanged();
    }

    private void SetLanguage(AppLanguage language)
    {
        _localizationService.SetLanguage(language);
    }

    private void SetStatus(string key, params object[] args)
    {
        _statusKey = key;
        _statusArgs = args;
        StatusMessage = _localizationService.Format(key, args);
    }

    private void RefreshLocalizedComputedProperties()
    {
        OnPropertyChanged(nameof(IsChineseSelected));
        OnPropertyChanged(nameof(IsEnglishSelected));
        OnPropertyChanged(nameof(CurrentAuthDisplayText));
        OnPropertyChanged(nameof(DesktopCodexStatusText));
        OnPropertyChanged(nameof(ModelProviderDisplayText));
        OnPropertyChanged(nameof(ModelDisplayText));
        OnPropertyChanged(nameof(CurrentProfileDisplayName));
        OnPropertyChanged(nameof(CurrentProfileKindDisplayText));
        OnPropertyChanged(nameof(CurrentIdentityPrimaryText));
        OnPropertyChanged(nameof(CurrentIdentitySecondaryText));
        RaiseSelectedProfileChanged();

        foreach (var profile in Profiles)
        {
            profile.RefreshLocalizedProperties();
        }

        StatusMessage = _localizationService.Format(_statusKey, _statusArgs);
        RaiseCommandStateChanged();
    }

    private void RaiseSelectedProfileChanged()
    {
        OnPropertyChanged(nameof(SelectedProfileDisplayName));
        OnPropertyChanged(nameof(SelectedProfileKindText));
        OnPropertyChanged(nameof(SelectedProfileIdentityText));
        OnPropertyChanged(nameof(SelectedProfileDetailText));
    }

    private void DisposeProfiles()
    {
        foreach (var profile in Profiles)
        {
            profile.Dispose();
        }
    }

    private string BuildLiveIdentityPrimary(LiveAuthIdentity? identity)
    {
        if (identity is null)
        {
            return _localizationService["Common.Untracked"];
        }

        return identity.Kind switch
        {
            ProfileKind.ChatGptSnapshot => !string.IsNullOrWhiteSpace(identity.Email)
                ? identity.Email
                : _localizationService["LiveIdentity.ChatGpt"],
            ProfileKind.ApiKey => !string.IsNullOrWhiteSpace(identity.Provider) && !string.IsNullOrWhiteSpace(identity.ApiHost)
                ? $"{identity.Provider} @ {identity.ApiHost}"
                : _localizationService["LiveIdentity.Api"],
            _ => _localizationService["Common.Unknown"]
        };
    }

    private string BuildLiveIdentitySecondary(LiveAuthIdentity? identity)
    {
        if (identity is null)
        {
            return string.Empty;
        }

        return identity.Kind switch
        {
            ProfileKind.ChatGptSnapshot when !string.IsNullOrWhiteSpace(identity.AccountId)
                => _localizationService.Format("LiveIdentity.ChatGpt.Secondary", identity.AccountId),
            ProfileKind.ApiKey => _localizationService.Format(
                "LiveIdentity.Api.Secondary",
                identity.Model ?? _localizationService["Common.Unknown"],
                identity.ApiKeyMask ?? _localizationService["Common.None"]),
            _ => string.Empty
        };
    }

    private string BuildSelectedProfileIdentity(ProfileSummary? summary)
    {
        if (summary is null)
        {
            return _localizationService["Common.None"];
        }

        if (summary.Kind == ProfileKind.ChatGptSnapshot)
        {
            if (!string.IsNullOrWhiteSpace(summary.Email))
            {
                return _localizationService.Format("Profile.Subtitle.ChatGpt.Email", summary.Email);
            }

            if (!string.IsNullOrWhiteSpace(summary.AccountId))
            {
                return _localizationService.Format("Profile.Subtitle.ChatGpt.Account", summary.AccountId);
            }

            return _localizationService["Profile.Subtitle.ChatGpt.Generic"];
        }

        return _localizationService.Format(
            "Profile.Subtitle.Api",
            summary.Provider ?? _localizationService["Common.Unknown"],
            summary.ApiHost ?? summary.BaseUrl ?? _localizationService["Common.Unknown"]);
    }

    private void RaiseCommandStateChanged()
    {
        RefreshCommand.RaiseCanExecuteChanged();
        CaptureCurrentCommand.RaiseCanExecuteChanged();
        NewApiProfileCommand.RaiseCanExecuteChanged();
        EditSelectedProfileCommand.RaiseCanExecuteChanged();
        DeleteSelectedProfileCommand.RaiseCanExecuteChanged();
        SwitchSelectedProfileCommand.RaiseCanExecuteChanged();
        SetChineseCommand.RaiseCanExecuteChanged();
        SetEnglishCommand.RaiseCanExecuteChanged();
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        RefreshLocalizedComputedProperties();
    }
}
