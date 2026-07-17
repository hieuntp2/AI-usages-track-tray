using System.Windows;
using AiUsageTray.ViewModels;

namespace AiUsageTray.Views;

public partial class FlyoutWindow : Window
{
    public FlyoutWindow(FlyoutViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        // Bottom-anchored + SizeToContent="Height": whenever content changes the height (cards
        // appearing after a refresh, rows expanding), the window grows *downward* past the work
        // area unless the bottom edge is re-pinned. This also finalizes the position on the very
        // first Show, where any pre-show height is only an estimate.
        SizeChanged += (_, _) =>
        {
            if (IsVisible)
            {
                AnchorToTrayCorner();
            }
        };
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
        // Before the window has ever been shown, no layout pass has run and ActualHeight is 0 -
        // positioning with it would hang the whole flyout below the screen. Measure against the
        // work area (MaxHeight still applies) to get a real content height for the first Show.
        if (ActualHeight == 0)
        {
            Measure(new System.Windows.Size(Width, SystemParameters.WorkArea.Height));
        }

        AnchorToTrayCorner();
        Show();
        Activate();
    }

    private void AnchorToTrayCorner()
    {
        var workArea = SystemParameters.WorkArea;
        const double margin = 10;
        var height = ActualHeight > 0 ? ActualHeight : DesiredSize.Height;

        Left = workArea.Right - Width - margin;
        Top = workArea.Bottom - height - margin;

        if (Top < workArea.Top)
        {
            Top = workArea.Top + margin;
        }
    }
}
