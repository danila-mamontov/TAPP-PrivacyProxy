using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PrivacyProxy.Api.Models.Interfaces;
using Serilog;

namespace PrivacyProxy.Api.Services;

/// <summary>
/// Represents a store for managing mappings between original values and their pseudonymized placeholders.
/// Provides functionality for creating and retrieving placeholders as well as depseudonymizing text by replacing placeholders
/// with their corresponding original values.
/// </summary>
/// <remarks>
/// The store is registered per request (scoped), so it lives only for a single chat-completion
/// round-trip and needs no expiration: it is discarded when the request ends, and nothing bleeds
/// between requests.
/// </remarks>
public partial class MappingStore : IMappingStore
{
    /// <summary>
    /// Maps an original value to its placeholder.
    /// </summary>
    /// <remarks>
    /// Matching is exact: each distinct value gets its own placeholder, so it is restored with its
    /// exact original casing and spacing (the proxy stays transparent). The placeholder combines an
    /// entity type with a hash of the value.
    /// </remarks>
    private readonly Dictionary<string, string> _originalToPlaceholder = new();

    /// <summary>
    /// Reverse lookup used during depseudonymization: maps a placeholder back to its original value.
    /// </summary>
    private readonly Dictionary<string, string> _placeholderToOriginal = new();

    /// <summary>
    /// A compiled regular expression designed to match placeholders within a specific format.
    /// The placeholders correspond to strings formatted as "[TYPE_HASH]",
    /// where TYPE is an uppercase alphanumeric identifier, and HASH is a 16-character hexadecimal string.
    /// </summary>
    /// <returns>A Regex object configured to match the specified placeholder format.</returns>
    [GeneratedRegex(@"\[(?<type>[A-Z0-9_]+)_(?<hash>[0-9a-f]{16})\]",
                       RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex TYPE_HASH_REGEX();

    /// <summary>
    /// Represents a regular expression used to identify and parse placeholders within text.
    /// </summary>
    /// <remarks>
    /// Placeholders follow a specific format: [TYPE_HASH], where TYPE represents an identifier for the entity type,
    /// and HASH is a 16-character hexadecimal string derived from the original value. This regular expression is
    /// instrumental in locating and processing placeholders within strings, such as during the substitution
    /// of placeholders with their corresponding original values.
    /// </remarks>
    private static readonly Regex PlaceholderRegex = TYPE_HASH_REGEX();

    /// <summary>
    /// Gets the total number of placeholder mappings currently stored in the mapping store.
    /// </summary>
    public int PlaceholderCount => _placeholderToOriginal.Count;

    /// <summary>
    /// Retrieves the existing placeholder for the given original value, or creates and stores a new one
    /// using the specified entity type. Matching is exact, so the value is later restored with its exact
    /// original casing and spacing.
    /// </summary>
    /// <param name="entityType">The type of entity to be associated with the placeholder. Cannot be null, empty, or whitespace.</param>
    /// <param name="original">The original value to be mapped to a placeholder. Cannot be null.</param>
    /// <returns>The placeholder associated with the original value, created on first use.</returns>
    public string GetOrCreatePlaceholder(string entityType, string original)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityType);
        ArgumentNullException.ThrowIfNull(original);

        // Exact key: each distinct value gets its own placeholder, so it is restored 1:1 (the proxy
        // stays transparent). Identical values still share a placeholder, keeping the LLM's coreference.
        if (_originalToPlaceholder.TryGetValue(original, out var existing))
            return existing;

        var placeholder = $"[{entityType}_{ComputeHash(original)}]";

        _originalToPlaceholder[original]   = placeholder;
        _placeholderToOriginal[placeholder] = original;

        Log.Debug("Created placeholder for {Original} ({Placeholder})", original, placeholder);
        return placeholder;
    }

    /// <summary>
    /// Replaces placeholders in the given text with their corresponding original values using the stored mappings.
    /// </summary>
    /// <param name="pseudonymizedText">The text containing placeholders to be replaced with original values.</param>
    /// <returns>The text with placeholders replaced by their original values. If a placeholder does not have a matching original value in the mapping, it is left unchanged.</returns>
    public string Depseudonymize(string pseudonymizedText)
    {
        if (string.IsNullOrEmpty(pseudonymizedText)) return pseudonymizedText;

        return PlaceholderRegex.Replace(pseudonymizedText, m =>
            _placeholderToOriginal.TryGetValue(m.Value, out var original) ? original : m.Value);
    }

    /// <summary>
    /// JSON serializer options that keep non-ASCII characters (e.g. umlauts) readable
    /// while still escaping everything JSON requires ('"', '\', control characters).
    /// </summary>
    private static readonly JsonSerializerOptions RelaxedJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// Like <see cref="Depseudonymize"/>, but for text that is serialized JSON (e.g. tool_calls).
    /// Instead of replacing in the raw text (where a restored '"' or '\' would corrupt the
    /// document), the JSON is parsed, placeholders are replaced inside the string values, and
    /// the document is serialized again - so escaping is correct on every nesting level,
    /// including string values that themselves contain serialized JSON (tool_call arguments).
    /// </summary>
    /// <param name="json">The pseudonymized raw JSON text containing placeholders.</param>
    /// <returns>The JSON text with placeholders replaced by their original values.</returns>
    public string DepseudonymizeJson(string json)
    {
        if (string.IsNullOrEmpty(json)) return json;

        try
        {
            var node = JsonNode.Parse(json);
            node = DepseudonymizeNode(node);
            return node?.ToJsonString(RelaxedJson) ?? json;
        }
        catch (JsonException)
        {
            // Not valid JSON at all (some models emit malformed arguments):
            // fall back to plain text replacement instead of failing the request.
            return Depseudonymize(json);
        }
    }

    /// <summary>
    /// Recursively depseudonymizes all string values of a JSON tree in place.
    /// </summary>
    private JsonNode? DepseudonymizeNode(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(property => property.Key).ToList())
                {
                    var child    = obj[key];
                    var replaced = DepseudonymizeNode(child);
                    if (!ReferenceEquals(child, replaced)) obj[key] = replaced;
                }
                return obj;

            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    var child    = array[i];
                    var replaced = DepseudonymizeNode(child);
                    if (!ReferenceEquals(child, replaced)) array[i] = replaced;
                }
                return array;

            case JsonValue value when value.TryGetValue<string>(out var text):
                return JsonValue.Create(DepseudonymizeStringValue(text));

            default:
                return node;
        }
    }

    /// <summary>
    /// Depseudonymizes a single JSON string value. If the string itself contains serialized JSON
    /// (like tool_call "arguments"), the placeholders live one encoding level deeper: the string
    /// is parsed, depseudonymized recursively and serialized again, so the restored values are
    /// escaped correctly at that level too. Otherwise it is treated as plain text.
    /// </summary>
    private string DepseudonymizeStringValue(string text)
    {
        var trimmed = text.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            try
            {
                var inner = JsonNode.Parse(text);
                if (inner is JsonObject or JsonArray)
                {
                    DepseudonymizeNode(inner);
                    return inner.ToJsonString(RelaxedJson);
                }
            }
            catch (JsonException)
            {
                // not valid JSON -> fall through and treat it as plain text
            }
        }
        return Depseudonymize(text);
    }

    /// <summary>
    /// Computes a hash value for the given input string using the SHA-256 algorithm.
    /// </summary>
    /// <param name="input">The input string to compute the hash for.</param>
    /// <returns>A hexadecimal string representation of the computed hash, truncated to the first 16 characters.</returns>
    private static string ComputeHash(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var hashBytes = SHA256.HashData(bytes);

        // Convert the hash to a hexadecimal string in lower chars and take the first 16 characters
        return Convert.ToHexString(hashBytes).ToLowerInvariant()[..16];
    }
}
