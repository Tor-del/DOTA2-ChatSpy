using System.ComponentModel;
using System.Globalization;

namespace Dota2ChatExtractor;

internal sealed class MatchView(MatchSummary summary) : INotifyPropertyChanged
{
    private MatchChat? _match;
    private bool _failed;
    private Dictionary<int, PlayerTimeline> _timelines = [];

    public long Id => summary.MatchId;
    public string Title => UiText.Get("MatchTitle", Id);
    public string Date => DateTimeOffset.FromUnixTimeSeconds(summary.StartTime).ToLocalTime()
        .ToString("dd.MM.yyyy · HH:mm", CultureInfo.InvariantCulture);
    public string Details => $"{Date}  ·  {FormatClock(summary.Duration)}";
    public bool? IsWin => summary.RadiantWin is null || summary.PlayerSlot is null
        ? null
        : summary.RadiantWin == (summary.PlayerSlot < 128);
    public string Result => IsWin switch
    {
        true => UiText.Get("Win"),
        false => UiText.Get("Loss"),
        null => ""
    };

    public bool IsLoaded => _match is not null;
    public bool Failed => _failed;
    public bool NotFound { get; private set; }
    public bool HasChat => _match?.Chat is not null;
    public bool NeedsTimeline => HasChat && _match!.CacheVersion < MatchChat.TimelineCacheVersion;
    public bool IsParsed => (_match?.Version ?? summary.Version) is > 0;
    public IReadOnlyList<MessageView> Messages { get; private set; } = [];
    public int TargetCount { get; private set; }

    public string ChatStatus => _failed ? UiText.Get("MatchLoadError") : _match switch
    {
        null => IsParsed ? UiText.Get("ChatNotLoaded") : UiText.Get("ParseUnconfirmed"),
        { Chat: null } => IsParsed ? UiText.Get("ChatUnavailable") : UiText.Get("NoReplayParse"),
        _ => UiText.Get("MatchChatCount", TargetCount, Messages.Count)
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    public void RefreshLanguage() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

    public void SetChat(MatchChat match, uint account)
    {
        var players = (match.Players ?? []).Where(player => player.PlayerSlot is not null)
            .ToLookup(player => player.PlayerSlot);
        var identifiedPlayer = match.Players?.FirstOrDefault(player => player.AccountId == account && player.PlayerSlot is not null);
        // The player history also identifies the slot when match participants are anonymized.
        var targetSlot = identifiedPlayer?.PlayerSlot ?? summary.PlayerSlot;

        Messages = (match.Chat ?? [])
            .Where(item => item.Type == "chat" && !string.IsNullOrWhiteSpace(item.Key))
            .OrderBy(item => item.Time)
            .Select(item =>
            {
                var player = players[item.PlayerSlot].FirstOrDefault();
                var target = item.PlayerSlot is not null && item.PlayerSlot == targetSlot;
                var author = string.IsNullOrWhiteSpace(item.Unit) ? player?.Personaname : item.Unit;
                author = string.IsNullOrWhiteSpace(author) ? null : author;
                var hero = HeroCatalog.For(player?.HeroId ?? (target ? summary.HeroId : null));
                return new MessageView(item.Time, author, item.Key!, target, item.PlayerSlot, Hero: hero);
            })
            .ToArray();
        TargetCount = Messages.Count(message => message.IsTarget);
        _timelines = players.ToDictionary(group => group.Key!.Value, group => new PlayerTimeline(group.First()));
        _match = match;
        SetFailed(false);
    }

    public IReadOnlyList<MessageView> GetRows(bool allAuthors, bool showScore)
    {
        var messages = allAuthors ? Messages : Messages.Where(message => message.IsTarget).ToArray();
        if (!showScore || messages.Count == 0)
            return messages;

        var authors = messages.Where(message => message.PlayerSlot.HasValue).DistinctBy(message => message.PlayerSlot)
            .Where(message => _timelines.ContainsKey(message.PlayerSlot!.Value))
            .Select(message => (message.Author, message.PlayerSlot, Deaths: _timelines[message.PlayerSlot!.Value].Deaths)).ToArray();
        var rows = new List<MessageView>();
        for (var i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            if (i > 0)
            {
                var previous = messages[i - 1].Time;
                var changes = authors.Select(author => (author.Author, author.PlayerSlot, author.Deaths.Complete,
                    Count: author.Deaths.CountAt(message.Time) - author.Deaths.CountAt(previous)))
                    .Where(change => change.Count > 0).ToArray();
                if (changes.Length > 0)
                {
                    var parts = new List<TranscriptPart> { new(UiText.Get("BetweenMessages")) };
                    foreach (var change in changes)
                    {
                        parts.Add(new TranscriptPart(" · "));
                        parts.Add(new TranscriptPart(change.Author, change.PlayerSlot));
                        parts.Add(new TranscriptPart(UiText.Get("AdditionalDeaths", FormatDeaths(change.Count, change.Complete))));
                    }
                    rows.Add(new MessageView(message.Time, "", "", false, null, Kind: MessageKind.Interlude, InterludeParts: parts));
                }
            }

            rows.Add(message with
            {
                Score = message.PlayerSlot is { } slot && _timelines.TryGetValue(slot, out var timeline)
                    ? timeline.ScoreAt(message.Time) : "— / — / —"
            });
        }

        rows.Add(new MessageView(null, UiText.Get("FinalKda"), UiText.Get("KdaDescription"), false, null,
            Kind: MessageKind.FinalHeading));
        var names = Messages.Where(message => message.PlayerSlot.HasValue && message.AuthorName is not null)
            .ToLookup(message => message.PlayerSlot);
        foreach (var (slot, timeline) in _timelines.OrderByDescending(entry => entry.Value.FinalRatio).ThenBy(entry => entry.Key))
        {
            var name = names[slot].FirstOrDefault()?.Author ?? timeline.Personaname;
            if (string.IsNullOrWhiteSpace(name))
                name = slot < 128 ? $"Radiant {slot + 1}" : $"Dire {slot - 127}";
            var hero = names[slot].FirstOrDefault()?.Hero ?? timeline.Hero;
            rows.Add(new MessageView(null, name, "", false, slot, timeline.FinalScore, MessageKind.FinalScore, hero));
        }
        if (_timelines.Count == 0)
            rows.Add(new MessageView(null, UiText.Get("PlayersUnavailable"), "", false, null, Kind: MessageKind.FinalScore));
        return rows;
    }

    private static string FormatDeaths(int count, bool complete)
    {
        var pluralNumber = UiText.Culture.TwoLetterISOLanguageName == "ru"
            ? (count % 100 is >= 11 and <= 14 ? 0 : count % 10)
            : count;
        var noun = pluralNumber switch
        {
            1 => UiText.Get("DeathSingular"),
            >= 2 and <= 4 => UiText.Get("DeathFew"),
            _ => UiText.Get("DeathPlural")
        };
        return $"{(complete ? "" : "≥")}{count} {noun}";
    }

    public void SetFailed(bool failed, bool notFound = false)
    {
        _failed = failed;
        NotFound = notFound;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ChatStatus)));
    }

    internal static string FormatClock(int seconds)
    {
        var absolute = Math.Abs((long)seconds);
        return $"{(seconds < 0 ? "−" : "")}{absolute / 60:00}:{absolute % 60:00}";
    }
}

internal enum MessageKind { Chat, Interlude, FinalHeading, FinalScore }

internal sealed record MessageView(int? Time, string? AuthorName, string Text, bool IsTarget, int? PlayerSlot,
    string? Score = null, MessageKind Kind = MessageKind.Chat, HeroInfo? Hero = null,
    IReadOnlyList<TranscriptPart>? InterludeParts = null)
{
    public string Author => AuthorName ?? UiText.Get("UnknownAuthor");
    public string Clock => Time is { } time ? MatchView.FormatClock(time) : "";
    public IReadOnlyList<TranscriptPart> AuthorParts => Hero is null ? [new(Author, PlayerSlot)]
        : [new(Author, PlayerSlot), new($" · {Hero.DisplayName}")];
    public IReadOnlyList<TranscriptPart> BodyParts => InterludeParts ?? [new(Text)];
}

internal sealed record Transcript(long MatchId, bool AllAuthors, string Title, string Metadata,
    IReadOnlyList<MessageView> Messages);

internal sealed class PlayerTimeline(MatchPlayer player)
{
    private readonly Counter _kills = new(player.Kills, player.KillsLog?.Where(entry =>
        entry.Key?.StartsWith("npc_dota_hero_", StringComparison.Ordinal) == true));
    public Counter Deaths { get; } = new(player.Deaths, player.DeathsLog);
    private readonly Counter _assists = new(player.Assists, player.AssistsLog);

    public string? Personaname => player.Personaname;
    public HeroInfo? Hero => HeroCatalog.For(player.HeroId);
    public decimal? FinalRatio { get; } = CalculateRatio(player);
    public string FinalScore => $"{_kills.Final} / {Deaths.Final} / {_assists.Final}  ·  {FormatRatio(FinalRatio)}";
    public string ScoreAt(int? time) => $"{_kills.At(time)} / {Deaths.At(time)} / {_assists.At(time)}";

    private static decimal? CalculateRatio(MatchPlayer player)
    {
        if (player.Kills is not { } kills || player.Assists is not { } assists || player.Deaths is not { } deaths)
            return null;

        var contribution = (decimal)kills + assists;
        if (deaths > 0)
            return contribution / deaths;
        return contribution > 0 ? decimal.MaxValue : null;
    }

    private static string FormatRatio(decimal? ratio) => ratio switch
    {
        null => "—",
        decimal.MaxValue => "∞",
        _ => ratio.Value.ToString("0.##", UiText.Culture)
    };

    internal sealed class Counter
    {
        private readonly int[] _times;
        public bool Complete { get; }
        public string Final { get; }

        public Counter(int? final, IEnumerable<CombatEvent>? events)
        {
            _times = events?.Where(entry => entry.Time.HasValue).Select(entry => entry.Time!.Value).Order().ToArray() ?? [];
            Complete = final == _times.Length && (events is not null || final == 0);
            Final = final?.ToString(CultureInfo.InvariantCulture) ?? "—";
        }

        public int CountAt(int? time) => _times.TakeWhile(eventTime => eventTime <= time).Count();
        public string At(int? time) => Complete ? CountAt(time).ToString(CultureInfo.InvariantCulture) : "—";
    }
}
