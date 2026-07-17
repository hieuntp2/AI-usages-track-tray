using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;

namespace AiUsageTray.Views;

/// <summary>Maps a "Normal"/"Warn"/"Error" level string to the matching themed brush.</summary>
public sealed class LevelToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = (value as string) switch
        {
            "Warn" => "Brush.Warning",
            "Error" => "Brush.Error",
            "Danger" => "Brush.ProgressDanger",
            // "Normal" == healthy/connected: a green dot reads as "OK" at a glance.
            _ => "Brush.Success",
        };

        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Chooses the progress bar fill color based on the row's computed usage level.</summary>
public sealed class ProgressLevelToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = (value as string) switch
        {
            "Danger" => "Brush.ProgressDanger",
            "Warn" => "Brush.ProgressWarn",
            _ => "Brush.ProgressFill",
        };

        return Application.Current.TryFindResource(key) as Brush ?? Brushes.SteelBlue;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class StringNullOrEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>True (an error/auth problem) → the themed error brush; false → the normal accent brush.</summary>
public sealed class ErrorBoolToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value is true ? "Brush.Error" : "Brush.SecondaryText";
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
