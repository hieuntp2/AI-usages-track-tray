using Microsoft.Win32;

namespace AiUsageTray.Infrastructure;

/// <summary>Reads and watches the Windows 10/11 "Apps use light/dark mode" personalization setting.</summary>
public static class ThemeDetector
{
    private const string PersonalizeKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string ValueName = "AppsUseLightTheme";

    public static event Action? SystemThemeChanged;

    private static bool _subscribed;

    public static bool IsSystemDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKeyPath, writable: false);
            var value = key?.GetValue(ValueName);
            return value is int intValue && intValue == 0;
        }
        catch
        {
            return false;
        }
    }

    public static void EnsureSubscribed()
    {
        if (_subscribed)
        {
            return;
        }

        _subscribed = true;
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.General)
            {
                SystemThemeChanged?.Invoke();
            }
        };
    }
}
