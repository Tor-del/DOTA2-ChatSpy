using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace Dota2ChatExtractor;

internal sealed record MatchSummary(long MatchId, int? PlayerSlot, bool? RadiantWin, int Duration,
    long StartTime, int? Version, int? HeroId = null);

internal sealed record MatchPlayer(uint? AccountId, int? PlayerSlot, string? Personaname,
    int? Kills, int? Deaths, int? Assists, CombatEvent[]? KillsLog, CombatEvent[]? DeathsLog, CombatEvent[]? AssistsLog,
    int? HeroId = null);

internal sealed record CombatEvent(int? Time, string? Key);

internal sealed record ChatEvent(int Time, string? Type, string? Key, int? PlayerSlot, string? Unit);

internal sealed record MatchChat(long MatchId, int? Version, MatchPlayer[]? Players, ChatEvent[]? Chat, int CacheVersion)
{
    public const int TimelineCacheVersion = 1;
    public const int HeroCacheVersion = 2;
}

internal sealed class OpenDotaClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private readonly HttpClient _http = new()
    {
        BaseAddress = new Uri("https://api.opendota.com/api/"),
        Timeout = TimeSpan.FromSeconds(20)
    };

    private readonly string _cache = Path.Combine(LocalData.DirectoryPath, "matches");

    private DateTimeOffset _nextRequest;

    public async Task<MatchSummary[]> GetMatchesAsync(uint account, int limit, int offset, CancellationToken token)
    {
        var path = Path.Combine(_cache, $"history-{account}-{limit}-{offset}.json");
        var cached = await LocalData.ReadAsync<MatchSummary[]>(path, TimeSpan.FromMinutes(5), token);
        if (cached is not null)
            return cached;

        var page = await GetAsync<MatchSummary[]>($"players/{account}/matches?limit={limit}&offset={offset}", token);
        await LocalData.WriteAsync(path, page, token);
        return page;
    }

    public async Task<MatchChat> GetMatchAsync(long matchId, CancellationToken token, bool requireTimeline = false)
    {
        var path = Path.Combine(_cache, $"{matchId}.json");
        var saved = await LocalData.ReadAsync<MatchChat>(path, TimeSpan.FromDays(30), token);
        var validCache = saved?.MatchId == matchId &&
            (saved.Chat is not null || File.GetLastWriteTimeUtc(path) > DateTime.UtcNow.AddMinutes(-10));
        if (validCache && (saved!.CacheVersion >= MatchChat.HeroCacheVersion || saved.Chat is not { Length: > 0 }))
            return saved;

        MatchChat match;
        try
        {
            match = await GetAsync<MatchChat>($"matches/{matchId}", token);
        }
        catch (Exception ex) when (validCache && !token.IsCancellationRequested &&
            (!requireTimeline || saved!.CacheVersion >= MatchChat.TimelineCacheVersion) &&
            ex is HttpRequestException or OperationCanceledException or JsonException)
        {
            return saved!;
        }
        if (match.MatchId != matchId)
            throw new JsonException("OpenDota returned data for a different match.");
        if (match.Chat is null && saved is { Chat: not null } && saved.MatchId == matchId)
            return saved;

        match = match with
        {
            Chat = match.Chat?.Where(item => item.Type == "chat").ToArray(),
            CacheVersion = MatchChat.HeroCacheVersion
        };
        await LocalData.WriteAsync(path, match, token);
        return match;
    }

    private async Task<T> GetAsync<T>(string route, CancellationToken token)
    {
        for (var attempt = 0; ; attempt++)
        {
            var delay = _nextRequest - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, token);

            _nextRequest = DateTimeOffset.UtcNow.AddSeconds(1.5);
            // Buffer the body so HttpClient's timeout covers downloading it as well as the headers.
            using var response = await _http.GetAsync(route, token);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var retry = response.Headers.RetryAfter;
                var now = DateTimeOffset.UtcNow;
                var until = retry?.Date ?? now.Add(retry?.Delta ?? TimeSpan.FromMinutes(1));
                _nextRequest = until > _nextRequest ? until : _nextRequest;
                throw new HttpRequestException("OpenDota rate limit exceeded.",
                    null, response.StatusCode);
            }

            if (attempt == 0 && response.StatusCode is (HttpStatusCode.BadGateway or
                HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout))
            {
                _nextRequest = DateTimeOffset.UtcNow.AddSeconds(4);
                continue;
            }

            response.EnsureSuccessStatusCode();
            await using var body = await response.Content.ReadAsStreamAsync(token);
            return await JsonSerializer.DeserializeAsync<T>(body, JsonOptions, token)
                ?? throw new JsonException("OpenDota returned an empty response.");
        }
    }

    public Task<int> ClearCacheAsync(CancellationToken token) => Task.Run(() =>
    {
        var count = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(_cache, "*.json"))
            {
                token.ThrowIfCancellationRequested();
                File.Delete(file);
                count++;
            }
        }
        catch (DirectoryNotFoundException) { }
        return count;
    }, token);

    public void Dispose() => _http.Dispose();
}
