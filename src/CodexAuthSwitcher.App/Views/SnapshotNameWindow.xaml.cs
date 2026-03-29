using System.Windows;

namespace CodexAuthSwitcher.App.Views;

public partial class SnapshotNameWindow : Window
{
    public SnapshotNameWindow(string suggestedName)
    {
        InitializeComponent();
        ProfileName = suggestedName;
        DataContext = this;
    }

    public string ProfileName { get; set; }

    private void NameBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element)
        {
            element.Focus();
            if (element is System.Windows.Controls.TextBox box)
            {
                box.SelectAll();
            }
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ProfileName))
        {
            MessageBox.Show(
                this,
                App.Localization["Dialog.Snapshot.ValidationMessage"],
                App.Localization["Dialog.Snapshot.ValidationTitle"],
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
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
}
