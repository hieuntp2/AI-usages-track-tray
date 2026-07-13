using System.Windows;
using System.Windows.Controls;
using AiUsageTray.ViewModels;
using Button = System.Windows.Controls.Button;

namespace AiUsageTray.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    public void SelectTab(int index)
    {
        if (index >= 0 && index < MainTabControl.Items.Count)
        {
            MainTabControl.SelectedIndex = index;
        }
    }

    private async void OnSaveGitHubCredentialsClick(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        var tokenBox = (PasswordBox)button.Tag;
        var row = (ProviderSettingsRowViewModel)button.DataContext;

        await row.SaveGitHubCredentialsAsync(tokenBox.Password);
        tokenBox.Clear();
    }
}
