using System.Collections;
using System.Globalization;
using System.Resources;
using System.Windows;

namespace Dota2ChatExtractor;

internal static class UiText
{
    private static readonly ResourceManager Strings = new("Dota2ChatExtractor.UiStrings", typeof(UiText).Assembly);
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    private static ResourceDictionary? _dictionary;

    public static CultureInfo Culture { get; private set; } = English;

    public static string Get(string key) =>
        Strings.GetString(key, Culture) ?? throw new MissingManifestResourceException(key);

    public static string Get(string key, params object[] args) => string.Format(Culture, Get(key), args);

    public static void ApplyLanguage(bool russian)
    {
        Culture = russian ? CultureInfo.GetCultureInfo("ru-RU") : English;
        var next = new ResourceDictionary();
        var keys = Strings.GetResourceSet(CultureInfo.InvariantCulture, createIfNotExists: true, tryParents: false)!;
        foreach (DictionaryEntry entry in keys)
        {
            var key = (string)entry.Key;
            next.Add("Ui." + key, Get(key));
        }

        var dictionaries = Application.Current.Resources.MergedDictionaries;
        if (_dictionary is null)
            dictionaries.Add(next);
        else
            dictionaries[dictionaries.IndexOf(_dictionary)] = next;
        _dictionary = next;
    }
}
