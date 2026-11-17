using System.Collections.Concurrent;
using System.Text.Json;
using System.Windows.Media.Imaging;

namespace Dota2ChatExtractor;

internal sealed record HeroInfo(int Id, string? Name)
{
    public string DisplayName => Name ?? UiText.Get("UnknownHero", Id);
    public BitmapSource? Icon => HeroCatalog.GetIcon(Id);
}

internal static class HeroCatalog
{
    private const string Prefix = "Dota2ChatExtractor.Heroes.";
    private static readonly IReadOnlyDictionary<int, HeroInfo> Names = LoadNames();
    private static readonly ConcurrentDictionary<int, BitmapSource?> Icons = new();

    public static HeroInfo? For(int? id) => id is > 0 ? Names.GetValueOrDefault(id.Value) ?? new HeroInfo(id.Value, null) : null;

    public static BitmapSource? GetIcon(int id) => Icons.GetOrAdd(id, static id =>
    {
        using var stream = typeof(HeroCatalog).Assembly.GetManifestResourceStream($"{Prefix}{id}.png");
        if (stream is null)
            return null;

        var icon = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        icon.Freeze();
        return icon;
    });

    private static IReadOnlyDictionary<int, HeroInfo> LoadNames()
    {
        using var stream = typeof(HeroCatalog).Assembly.GetManifestResourceStream($"{Prefix}index.json");
        return stream is not null ? (JsonSerializer.Deserialize<HeroInfo[]>(stream) ?? []).ToDictionary(hero => hero.Id)
            : new Dictionary<int, HeroInfo>();
    }
}
