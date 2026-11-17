using System.IO;
using System.Text.Json;

namespace Dota2ChatExtractor;

internal static class LocalData
{
    public static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Dota2ChatExtractor");

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public static async Task<T?> ReadAsync<T>(string path, TimeSpan lifetime, CancellationToken token) where T : class
    {
        try
        {
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > lifetime)
                return null;
            await using var input = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<T>(input, JsonOptions, token);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    public static async Task<bool> WriteAsync<T>(string path, T value, CancellationToken token)
    {
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using (var output = File.Create(temporary))
                await JsonSerializer.SerializeAsync(output, value, JsonOptions, token);
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
