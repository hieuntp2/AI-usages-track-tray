using System.Reflection;
using System.Windows;

namespace AiUsageTray.Views;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.1.0";
        VersionText.Text = $"Version {version}";
    }
}
