using System.Windows;
using CodexAuthSwitcher.Core.Models;

namespace CodexAuthSwitcher.App.Views;

public partial class ApiProfileEditorWindow : Window
{
    public ApiProfileEditorWindow(ApiProfileSpec profile, bool isEditMode)
    {
        Profile = profile;
        IsEditMode = isEditMode;
        InitializeComponent();
        DataContext = this;

        if (!string.IsNullOrWhiteSpace(Profile.ApiKey))
        {
            ApiKeyBox.Password = Profile.ApiKey;
        }
    }

    public ApiProfileSpec Profile { get; }
    public bool IsEditMode { get; }
    public string WindowTitle => IsEditMode
        ? App.Localization["Window.ApiEditor.EditTitle"]
        : App.Localization["Window.ApiEditor.CreateTitle"];
    public string HeaderTitle => WindowTitle;
    public string HeaderSubtitle => IsEditMode
        ? App.Localization["Dialog.ApiEditor.EditHeader"]
        : App.Localization["Dialog.ApiEditor.CreateHeader"];
    public string ConfirmButtonText => IsEditMode
        ? App.Localization["Common.Update"]
        : App.Localization["Common.Create"];

    private void ApiKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        Profile.ApiKey = ApiKeyBox.Password;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (!TryNormalizeProfile())
        {
            return;
        }

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private bool TryNormalizeProfile()
    {
        Profile.Name = Profile.Name?.Trim() ?? string.Empty;
        Profile.Provider = string.IsNullOrWhiteSpace(Profile.Provider) ? "crs" : Profile.Provider.Trim();
        Profile.BaseUrl = string.IsNullOrWhiteSpace(Profile.BaseUrl) ? "https://api.funai.vip" : Profile.BaseUrl.Trim();
        Profile.Model = string.IsNullOrWhiteSpace(Profile.Model) ? "gpt-5.4" : Profile.Model.Trim();
        Profile.ReasoningEffort = string.IsNullOrWhiteSpace(Profile.ReasoningEffort) ? "xhigh" : Profile.ReasoningEffort.Trim();
        Profile.WireApi = string.IsNullOrWhiteSpace(Profile.WireApi) ? "responses" : Profile.WireApi.Trim();

        if (string.IsNullOrWhiteSpace(Profile.Name))
        {
            MessageBox.Show(this, App.Localization["Dialog.ApiEditor.ValidationName"], App.Localization["Dialog.ApiEditor.ValidationTitle"], MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        if (string.IsNullOrWhiteSpace(Profile.ApiKey))
        {
            MessageBox.Show(this, App.Localization["Dialog.ApiEditor.ValidationApiKey"], App.Localization["Dialog.ApiEditor.ValidationTitle"], MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        if (!int.TryParse(Profile.ModelAutoCompactTokenLimit.ToString(), out var parsedLimit) || parsedLimit <= 0)
        {
            MessageBox.Show(this, App.Localization["Dialog.ApiEditor.ValidationTokenLimit"], App.Localization["Dialog.ApiEditor.ValidationTitle"], MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        Profile.ModelAutoCompactTokenLimit = parsedLimit;
        return true;
    }
}
