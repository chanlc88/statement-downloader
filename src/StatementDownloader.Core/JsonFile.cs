using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StatementDownloader.Core;

/// <summary>Small helper for the JSON state files: atomic writes, one lock per file.</summary>
public static class JsonFile
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(StringComparer.OrdinalIgnoreCase);

    public static async Task<T?> ReadAsync<T>(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path)) return default;
        // Share Delete too, so a write can replace the file while it's being read; and retry, since
        // Windows refuses to open it for the moment the replace takes.
        return await RetryAsync(async () =>
        {
            try
            {
                await using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return await JsonSerializer.DeserializeAsync<T>(f, Options, ct);
            }
            catch (FileNotFoundException) { return default; }
        }, ct);
    }

    public static async Task WriteAsync<T>(string path, T value, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = path + ".tmp";
        await using (var f = File.Create(tmp)) await JsonSerializer.SerializeAsync(f, value, Options, ct);
        await RetryAsync(() => { File.Move(tmp, path, overwrite: true); return Task.FromResult(true); }, ct);
    }

    /// <summary>
    /// Retries for about two seconds while the file is busy: being replaced by a write, or held by
    /// something else for a moment (a reader in another process, a virus scanner).
    /// </summary>
    private static async Task<TResult> RetryAsync<TResult>(Func<Task<TResult>> attempt, CancellationToken ct)
    {
        for (var i = 1; ; i++)
        {
            try { return await attempt(); }
            catch (Exception ex) when (i < 20 && ex is UnauthorizedAccessException or IOException
                and not FileNotFoundException and not DirectoryNotFoundException and not EndOfStreamException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(10 * i, 200)), ct);
            }
        }
    }

    /// <summary>Read-modify-write under the file's lock.</summary>
    public static async Task<TResult> UpdateAsync<T, TResult>(string path, Func<T, TResult> mutate, CancellationToken ct = default) where T : new()
    {
        var gate = Locks.GetOrAdd(Path.GetFullPath(path), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var value = await ReadAsync<T>(path, ct) ?? new T();
            var result = mutate(value);
            await WriteAsync(path, value, ct);
            return result;
        }
        finally { gate.Release(); }
    }

    public static Task UpdateAsync<T>(string path, Action<T> mutate, CancellationToken ct = default) where T : new() =>
        UpdateAsync<T, bool>(path, v => { mutate(v); return true; }, ct);
}
