using CodexAuthSwitcher.App.Services;
using CodexAuthSwitcher.Core.Models;

namespace CodexAuthSwitcher.App.ViewModels;

public sealed class ProfileItemViewModel : ViewModelBase, IDisposable
{
    private readonly LocalizationService _localizationService;

    public ProfileItemViewModel(ProfileSummary summary, bool isCurrent, LocalizationService localizationService)
    {
        Summary = summary;
        IsCurrent = isCurrent;
        _localizationService = localizationService;
        _localizationService.LanguageChanged += OnLanguageChanged;
    }

    public ProfileSummary Summary { get; }
    public bool IsCurrent { get; }

    public string Name => Summary.Name;

    public string KindLabel => Summary.Kind == ProfileKind.ChatGptSnapshot
        ? _localizationService["ProfileKind.ChatGpt"]
        : _localizationService["ProfileKind.Api"];

    public string Subtitle
    {
        get
        {
            if (Summary.Kind == ProfileKind.ChatGptSnapshot)
            {
                if (!string.IsNullOrWhiteSpace(Summary.Email))
                {
                    return _localizationService.Format("Profile.Subtitle.ChatGpt.Email", Summary.Email);
                }

                if (!string.IsNullOrWhiteSpace(Summary.AccountId))
                {
                    return _localizationService.Format("Profile.Subtitle.ChatGpt.Account", Summary.AccountId);
                }

                return _localizationService["Profile.Subtitle.ChatGpt.Generic"];
            }

            return _localizationService.Format(
                "Profile.Subtitle.Api",
                Summary.Provider ?? _localizationService["Common.Unknown"],
                Summary.ApiHost ?? Summary.BaseUrl ?? _localizationService["Common.Unknown"]);
        }
    }

    public string Detail
    {
        get
        {
            if (Summary.Kind == ProfileKind.ChatGptSnapshot)
            {
                var timestamp = Summary.UpdatedAt.LocalDateTime.ToString("g");
                return Summary.IsAutoCaptured
                    ? _localizationService.Format("Profile.Detail.ChatGpt.AutoCaptured", timestamp)
                    : _localizationService.Format("Profile.Detail.ChatGpt", timestamp);
            }

            return _localizationService.Format(
                "Profile.Detail.Api",
                Summary.Model ?? _localizationService["Common.Unknown"],
                Summary.ApiKeyMask ?? _localizationService["Common.None"]);
        }
    }

    public string CurrentLabel => _localizationService["Common.Current"];

    public void RefreshLocalizedProperties()
    {
        OnPropertyChanged(nameof(KindLabel));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(Detail));
        OnPropertyChanged(nameof(CurrentLabel));
    }

    public void Dispose()
    {
        _localizationService.LanguageChanged -= OnLanguageChanged;
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        RefreshLocalizedProperties();
    }
}
