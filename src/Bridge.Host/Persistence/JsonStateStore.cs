using System.Text.Json;
using Bridge.Core.Abstractions;
using Bridge.Core.Bridge;

namespace Bridge.Host.Persistence;

public sealed class JsonStateStore(string path, IBridgeLog log) : IStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path = ExpandHome(path);

    public async ValueTask<BridgeState> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return new BridgeState();
        }

        try
        {
            await using var stream = File.OpenRead(_path);
            return await JsonSerializer.DeserializeAsync<BridgeState>(stream, JsonOptions, cancellationToken)
                ?? new BridgeState();
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            log.Warning($"Cannot load state file '{_path}': {exception.Message}. Starting with empty state.");
            return new BridgeState();
        }
    }

    public async ValueTask SaveAsync(BridgeState state, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException("State path has no directory.");
        Directory.CreateDirectory(directory);

        var temporaryPath = $"{_path}.{Environment.ProcessId}.tmp";
        await using (var stream = new FileStream(
            temporaryPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, state, JsonOptions, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        File.Move(temporaryPath, _path, overwrite: true);
    }

    private static string ExpandHome(string path)
    {
        if (!path.StartsWith('~'))
        {
            return Path.GetFullPath(path);
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, path[1..].TrimStart('/', '\\'));
    }
}
