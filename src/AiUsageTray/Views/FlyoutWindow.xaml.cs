using System.Windows;
using AiUsageTray.ViewModels;

namespace AiUsageTray.Views;

public partial class FlyoutWindow : Window
{
    public FlyoutWindow(FlyoutViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void OnDeactivated(object? sender, EventArgs e) => Hide();

    /// <summary>
    /// Positions the flyout just above the system tray, anchored to whichever screen edge the
    /// taskbar occupies (bottom in the common case). Windows does not expose the exact pixel
    /// location of a NotifyIcon without Shell32 interop, so this uses the conventional
    /// "corner nearest the tray, above the taskbar" placement used by most tray apps.
    /// </summary>
    public void ShowNearTray()
    {
        UpdateLayout();

        var workArea = SystemParameters.WorkArea;
        const double margin = 10;

        Left = workArea.Right - Width - margin;
        Top = workArea.Bottom - ActualHeight - margin;

        if (Top < workArea.Top)
        {
            Top = workArea.Top + margin;
        }

        Show();
        Activate();
    }
}
