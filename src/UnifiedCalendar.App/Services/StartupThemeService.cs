using System.Windows;
using Microsoft.Win32;

namespace UnifiedCalendar.App.Services;

public sealed class StartupThemeService
{
    private static readonly Uri LightTheme = new(
        "/UnifiedCalendar.App;component/Resources/Themes/Light.xaml",
        UriKind.Relative);
    private static readonly Uri DarkTheme = new(
        "/UnifiedCalendar.App;component/Resources/Themes/Dark.xaml",
        UriKind.Relative);

    private readonly IThemePreferenceReader _preferenceReader;

    public StartupThemeService(IThemePreferenceReader preferenceReader)
    {
        _preferenceReader = preferenceReader ?? throw new ArgumentNullException(nameof(preferenceReader));
    }

    public bool IsDarkTheme() => _preferenceReader.IsDarkTheme();

    public void Apply(ResourceDictionary resources)
    {
        ArgumentNullException.ThrowIfNull(resources);
        Apply(resources, IsDarkTheme());
    }

    public void Apply(ResourceDictionary resources, bool useDarkTheme)
    {
        ArgumentNullException.ThrowIfNull(resources);
        var source = useDarkTheme ? DarkTheme : LightTheme;
        for (var index = resources.MergedDictionaries.Count - 1; index >= 0; index--)
        {
            if (IsThemeDictionary(resources.MergedDictionaries[index]))
            {
                resources.MergedDictionaries.RemoveAt(index);
            }
        }

        resources.MergedDictionaries.Add(new ResourceDictionary { Source = source });
    }

    internal static bool IsThemeDictionary(ResourceDictionary dictionary)
    {
        var source = dictionary.Source?.OriginalString;
        return source is not null
            && (source.EndsWith("/Resources/Themes/Light.xaml", StringComparison.OrdinalIgnoreCase)
                || source.EndsWith("/Resources/Themes/Dark.xaml", StringComparison.OrdinalIgnoreCase)
                || source.Equals("Resources/Themes/Light.xaml", StringComparison.OrdinalIgnoreCase)
                || source.Equals("Resources/Themes/Dark.xaml", StringComparison.OrdinalIgnoreCase));
    }

}

public interface IThemePreferenceReader
{
    bool IsDarkTheme();
}

public sealed class WindowsThemePreferenceReader : IThemePreferenceReader
{
    public bool IsDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch
        {
            return false;
        }
    }
}
