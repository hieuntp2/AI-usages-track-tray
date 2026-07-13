using System.Reflection;
using System.Windows.Forms;
using AiUsageTray.Models;

namespace AiUsageTray.Services;

/// <summary>
/// Owns the Win32 NotifyIcon: its context menu, click behavior, tooltip text, and the optional
/// icon-swap when any provider crosses the configured alert threshold.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _normalIcon;
    private readonly Icon _alertIcon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripSeparator _providerSectionStart;
    private readonly ToolStripSeparator _providerSectionEnd;
    private bool _alertActive;

    public event Action? OpenRequested;

    public event Action? RefreshRequested;

    public event Action? SettingsRequested;

    public event Action? AboutRequested;

    public event Action? ExitRequested;

    public event Action<bool>? StartWithWindowsToggled;

    /// <summary>Raised when the user clicks an already-added provider's row (e.g. to fix an auth error).</summary>
    public event Action<string>? ProviderRowClicked;

    /// <summary>Raised when the user clicks "+ Add AI service..." to enable/authenticate a new provider.</summary>
    public event Action? AddProviderRequested;

    private readonly ToolStripMenuItem _startWithWindowsItem;

    public TrayIconService(bool startWithWindowsInitiallyChecked)
    {
        _normalIcon = LoadEmbeddedIcon("Resources.tray.ico");
        _alertIcon = LoadEmbeddedIcon("Resources.tray-alert.ico");

        _startWithWindowsItem = new ToolStripMenuItem("Start with Windows") { CheckOnClick = true, Checked = startWithWindowsInitiallyChecked };
        _startWithWindowsItem.CheckedChanged += (_, _) => StartWithWindowsToggled?.Invoke(_startWithWindowsItem.Checked);

        _providerSectionStart = new ToolStripSeparator();
        _providerSectionEnd = new ToolStripSeparator();

        _menu = new ContextMenuStrip { ShowItemToolTips = true };
        _menu.Items.Add("Open", null, (_, _) => OpenRequested?.Invoke());
        _menu.Items.Add("Refresh now", null, (_, _) => RefreshRequested?.Invoke());
        _menu.Items.Add(_providerSectionStart);
        _menu.Items.Add(_providerSectionEnd);
        _menu.Items.Add("Settings", null, (_, _) => SettingsRequested?.Invoke());
        _menu.Items.Add(_startWithWindowsItem);
        _menu.Items.Add("About", null, (_, _) => AboutRequested?.Invoke());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Exit", null, (_, _) => ExitRequested?.Invoke());

        _notifyIcon = new NotifyIcon
        {
            Icon = _normalIcon,
            Visible = true,
            Text = "AI Usage Tray",
            ContextMenuStrip = _menu,
        };

        _notifyIcon.MouseUp += OnMouseUp;
        _notifyIcon.DoubleClick += (_, _) => OpenRequested?.Invoke();
    }

    /// <summary>
    /// Rebuilds the "which AI services are added / authenticated" section of the context menu
    /// between Refresh and Settings: one row per enabled provider (with its live auth status and,
    /// on error, the error message inline), plus a trailing "+ Add AI service..." row.
    /// </summary>
    public void UpdateProviderMenu(IReadOnlyList<ProviderState> states, AppSettings settings)
    {
        var insertIndex = _menu.Items.IndexOf(_providerSectionStart) + 1;
        var endIndex = _menu.Items.IndexOf(_providerSectionEnd);
        while (endIndex > insertIndex)
        {
            _menu.Items.RemoveAt(insertIndex);
            endIndex--;
        }

        foreach (var state in states)
        {
            if (!settings.GetOrAddProvider(state.Provider.Id).Enabled)
            {
                continue;
            }

            var (prefix, text, isError) = DescribeAuthStatus(state);
            var providerId = state.Provider.Id;
            var item = new ToolStripMenuItem($"{prefix} {state.Provider.DisplayName} — {text}")
            {
                ToolTipText = isError ? text : string.Empty,
            };
            item.Click += (_, _) => ProviderRowClicked?.Invoke(providerId);
            _menu.Items.Insert(insertIndex++, item);
        }

        var addItem = new ToolStripMenuItem("+ Add AI service...");
        addItem.Click += (_, _) => AddProviderRequested?.Invoke();
        _menu.Items.Insert(insertIndex, addItem);
    }

    private static (string Prefix, string Text, bool IsError) DescribeAuthStatus(ProviderState state)
    {
        var snapshot = state.LastSnapshot;
        if (snapshot is null)
        {
            return state.Detection is { IsInstalled: false }
                ? ("✗", state.Detection.Message ?? "Not installed", true)
                : ("…", "Not yet checked", false);
        }

        return snapshot.Status switch
        {
            ProviderConnectionStatus.Available => ("✓", "Connected", false),
            ProviderConnectionStatus.Refreshing => ("…", "Refreshing…", false),
            ProviderConnectionStatus.NotAuthenticated => ("✗", snapshot.Message ?? "Not authenticated", true),
            ProviderConnectionStatus.SetupRequired => ("⚠", snapshot.Message ?? "Setup required", true),
            ProviderConnectionStatus.Stale => ("⚠", "Stale data", true),
            ProviderConnectionStatus.UnsupportedVersion => ("⚠", snapshot.Message ?? "Unsupported version", true),
            ProviderConnectionStatus.NotInstalled => ("✗", snapshot.Message ?? "Not installed", true),
            ProviderConnectionStatus.Error => ("✗", snapshot.Message ?? "Error", true),
            _ => ("…", "Unknown", false),
        };
    }

    private void OnMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            OpenRequested?.Invoke();
        }
    }

    public void SetStartWithWindowsChecked(bool value) => _startWithWindowsItem.Checked = value;

    /// <summary>Summarizes the single highest currently-used quota window per provider into the tooltip.</summary>
    public void UpdateTooltip(IReadOnlyList<ProviderState> states, double alertThresholdPercent, bool alertEnabled)
    {
        var parts = new List<string>();
        var anyOverThreshold = false;

        foreach (var state in states)
        {
            var snapshot = state.LastSnapshot;
            if (snapshot?.Windows is not { Count: > 0 } windows)
            {
                continue;
            }

            var highest = windows.Where(w => w.UsedPercent is not null).OrderByDescending(w => w.UsedPercent).FirstOrDefault();
            if (highest is null)
            {
                continue;
            }

            parts.Add($"{snapshot.ProviderName} {highest.UsedPercent:0}%");

            if ((double)highest.UsedPercent!.Value >= alertThresholdPercent)
            {
                anyOverThreshold = true;
            }
        }

        var tooltip = parts.Count == 0 ? "AI Usage — no data yet" : $"AI Usage — {string.Join(", ", parts)}";
        _notifyIcon.Text = tooltip.Length > 127 ? tooltip[..127] : tooltip;

        if (alertEnabled && anyOverThreshold != _alertActive)
        {
            _alertActive = anyOverThreshold;
            _notifyIcon.Icon = _alertActive ? _alertIcon : _normalIcon;
        }
    }

    public void ShowBalloon(string title, string message) =>
        _notifyIcon.ShowBalloonTip(6000, title, message, ToolTipIcon.Info);

    private static Icon LoadEmbeddedIcon(string suffix)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames().First(n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        return new Icon(stream);
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _normalIcon.Dispose();
        _alertIcon.Dispose();
    }
}
