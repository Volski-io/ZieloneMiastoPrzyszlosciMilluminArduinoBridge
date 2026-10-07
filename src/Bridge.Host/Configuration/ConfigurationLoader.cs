using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bridge.Host.Configuration;

public static class ConfigurationLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task<AppOptions> LoadAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var options = await JsonSerializer.DeserializeAsync<AppOptions>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("Configuration file is empty.");
        options.Validate();
        return options;
    }
}
