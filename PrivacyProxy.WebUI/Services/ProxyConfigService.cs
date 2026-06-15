using System.Text.Json;
using System.Text.Json.Nodes;
using PrivacyProxy.Core.Configuration;

namespace PrivacyProxy.WebUI.Services;

/// <summary>
/// Reads and writes the shared configuration file (<see cref="ProxyConfigFile"/>) that the API
/// hot-reloads. Each section is read/written independently, so editing one area (e.g. LLM) never
/// clobbers the others or sections still governed by appsettings/.env.
/// </summary>
public interface IProxyConfigService
{
    /// <summary>Absolute path of the shared config file (for display in the UI).</summary>
    string FilePath { get; }

    /// <summary>Current LLM settings from the file, or defaults if not set yet.</summary>
    LlmOptions GetLlm();

    /// <summary>Persists the LLM settings (merged into the file, written atomically).</summary>
    Task SaveLlmAsync(LlmOptions value, CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class ProxyConfigService : IProxyConfigService
{
    private const string LlmSection = "Llm";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public string FilePath { get; } = ProxyConfigFile.ResolvePath();

    public LlmOptions GetLlm() => GetSection<LlmOptions>(LlmSection);

    public Task SaveLlmAsync(LlmOptions value, CancellationToken ct = default)
        => SaveSectionAsync(LlmSection, value, ct);

    private T GetSection<T>(string section) where T : new()
    {
        var node = ReadRoot()?[section];
        return node is null ? new T() : node.Deserialize<T>(SerializerOptions) ?? new T();
    }

    private async Task SaveSectionAsync<T>(string section, T value, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            var root = ReadRoot() ?? new JsonObject();
            root[section] = JsonSerializer.SerializeToNode(value, SerializerOptions);

            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

            // Atomic write: write to a temp file, then replace the target.
            var tempPath = FilePath + ".tmp";
            await File.WriteAllTextAsync(tempPath, root.ToJsonString(SerializerOptions), ct);
            File.Move(tempPath, FilePath, overwrite: true);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private JsonObject? ReadRoot()
    {
        if (!File.Exists(FilePath))
            return null;

        var text = File.ReadAllText(FilePath);
        return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text) as JsonObject;
    }
}
