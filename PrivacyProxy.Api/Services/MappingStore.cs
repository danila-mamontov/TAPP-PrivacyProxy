using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using PrivacyProxy.Core.Configuration;
using PrivacyProxy.Api.Models.Interfaces;
using Serilog;

namespace PrivacyProxy.Api.Services;

/// <summary>
/// Represents a store for managing mappings between original values and their anonymized placeholders.
/// Provides functionality for creating and retrieving placeholders as well as deanonymizing text by replacing placeholders
/// with their corresponding original values.
/// </summary>
public partial class MappingStore : IMappingStore, IDisposable
{
    /// <summary>
    /// Maps a normalized original value to its placeholder.
    /// </summary>
    /// <remarks>
    /// Keys are normalized (case- and surrounding-whitespace-insensitive), so all variants of a value
    /// (e.g. "Berlin", "berlin", " Berlin ") map to the SAME placeholder, keeping the LLM's coreference
    /// intact. The placeholder combines an entity type with a hash of the normalized value.
    /// </remarks>
    private readonly IMemoryCache _originalToPlaceholder;

    /// <summary>
    /// Stores the mappings between placeholders and their corresponding original values.
    /// </summary>
    /// <remarks>
    /// This dictionary serves as a reverse lookup for the original values based on their placeholders.
    /// It is primarily used during the deanonymization process to replace placeholders within text
    /// with their associated original values.
    /// </remarks>
    private readonly IMemoryCache _placeholderToOriginal;

    /// <summary>
    /// Sliding-expiration time-to-live applied to every mapping entry, taken from
    /// <see cref="MappingOptions.TtlMinutes"/>.
    /// </summary>
    private readonly TimeSpan _ttl;

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
    /// <remarks>
    /// Each placeholder represents a mapping between an anonymized value and its corresponding original value.
    /// This property provides a count of the existing mappings stored in memory at any given time.
    /// </remarks>
    public int PlaceholderCount => 
        _placeholderToOriginal is MemoryCache mc ? mc.Count : 0;
    
    /// <summary>
    /// Creates a new <see cref="MappingStore"/> using the default TTL of <see cref="MappingOptions"/> (30 minutes).
    /// </summary>
    public MappingStore() : this(Options.Create(new MappingOptions()))
    {
    }

    /// <summary>
    /// Creates a new <see cref="MappingStore"/> whose mapping entries expire after
    /// <see cref="MappingOptions.TtlMinutes"/> minutes of inactivity.
    /// </summary>
    /// <param name="options">The mapping configuration, in particular the entry TTL.</param>
    public MappingStore(IOptions<MappingOptions> options)
    {
        _ttl                   = TimeSpan.FromMinutes(options.Value.TtlMinutes);
        _originalToPlaceholder = new MemoryCache(new MemoryCacheOptions());
        _placeholderToOriginal = new MemoryCache(new MemoryCacheOptions());
    }

    /// <summary>
    /// Disposes the internal caches backing this mapping store.
    /// </summary>
    public void Dispose()
    {
        _originalToPlaceholder.Dispose();
        _placeholderToOriginal.Dispose();
    }

    /// <summary>
    /// Retrieves the existing placeholder for the given original value, or creates and stores a new one
    /// using the specified entity type. Matching is case- and surrounding-whitespace-insensitive, so value
    /// variants share one placeholder; the first-seen casing is kept for later restoration.
    /// </summary>
    /// <param name="entityType">The type of entity to be associated with the placeholder. Cannot be null, empty, or whitespace.</param>
    /// <param name="original">The original value to be mapped to a placeholder. Cannot be null.</param>
    /// <returns>The placeholder associated with the (normalized) original value, created on first use.</returns>
    public string GetOrCreatePlaceholder(string entityType, string original)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityType);
        ArgumentNullException.ThrowIfNull(original);

        // Case-/whitespace-insensitive key so "Berlin", "berlin" and " Berlin " share ONE placeholder
        // (otherwise the LLM sees different hashes and treats them as different entities).
        var key = NormalizeKey(original);

        if (_originalToPlaceholder.TryGetValue(key, out string? existing))
        {
            // Refresh sliding TTL on both directions, keeping the first-seen original casing.
            _originalToPlaceholder.Set(key, existing!, SlidingOptions());
            if (_placeholderToOriginal.TryGetValue(existing!, out string? firstSeen) && firstSeen is not null)
                _placeholderToOriginal.Set(existing!, firstSeen, SlidingOptions());
            return existing!;
        }

        var placeholder = $"[{entityType}_{ComputeHash(key)}]";

        _originalToPlaceholder.Set(key, placeholder, SlidingOptions());
        // Reverse map keeps the first-seen original (with its casing) for restoration.
        _placeholderToOriginal.Set(placeholder, original, SlidingOptions());

        Log.Debug("Created placeholder for {Original} ({Placeholder})", original, placeholder);
        return placeholder;
    }

    /// <summary>
    /// Normalizes an original value into a lookup key so that case- and surrounding-whitespace
    /// variants map to the SAME placeholder, preserving the LLM's coreference across the conversation.
    /// </summary>
    private static string NormalizeKey(string original) => original.Trim().ToLowerInvariant();

    private MemoryCacheEntryOptions SlidingOptions() => new() { SlidingExpiration = _ttl };

    /// <summary>
    /// Replaces placeholders in the given text with their corresponding original values using the stored mappings.
    /// </summary>
    /// <param name="anonymizedText">The text containing placeholders to be replaced with original values.</param>
    /// <returns>The text with placeholders replaced by their original values. If a placeholder does not have a matching original value in the mapping, it is left unchanged.</returns>
    public string Deanonymize(string anonymizedText)
    {
        if (string.IsNullOrEmpty(anonymizedText)) return anonymizedText;

        return PlaceholderRegex.Replace(anonymizedText, m =>
        {
            if (_placeholderToOriginal.TryGetValue(m.Value, out string? original))
            {
                // Sliding Expiration auch beim Deanonymisieren
                _placeholderToOriginal.Set(m.Value, original, SlidingOptions());
                return original!;
            }
            return m.Value;
        });
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