using System.Windows;

namespace Dota2ChatExtractor;

public partial class App : Application
{
    public bool IsDark { get; private set; } = true;

    public void ToggleTheme()
    {
        var dictionaries = Resources.MergedDictionaries;
        var currentPath = IsDark ? "Themes/Dark.xaml" : "Themes/Light.xaml";
        var previous = dictionaries.First(dictionary => dictionary.Source?.OriginalString.EndsWith(currentPath, StringComparison.Ordinal) == true);
        var next = IsDark ? "Themes/Light.xaml" : "Themes/Dark.xaml";
        dictionaries[dictionaries.IndexOf(previous)] = new ResourceDictionary
        {
            Source = new Uri($"/DOTA2ChatSpy;component/{next}", UriKind.Relative)
        };
        IsDark = !IsDark;
#pragma warning disable WPF0001 // ThemeMode is the WPF API for updating the Fluent controls and title bar.
        ThemeMode = IsDark ? ThemeMode.Dark : ThemeMode.Light;
#pragma warning restore WPF0001
    }
}
