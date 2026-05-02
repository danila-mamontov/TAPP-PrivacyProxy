using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using PrivacyProxy.Api.Models.Interfaces;

namespace PrivacyProxy.Api.Services;

/// <summary>
/// Represents a store for managing mappings between original values and their anonymized placeholders.
/// Provides functionality for creating and retrieving placeholders as well as deanonymizing text by replacing placeholders
/// with their corresponding original values.
/// </summary>
public partial class MappingStore : IMappingStore
{
    /// <summary>
    /// Maintains a mapping where the keys are original values, and the values are their corresponding placeholders.
    /// </summary>
    /// <remarks>
    /// This dictionary is used to quickly retrieve or associate a unique placeholder with a given original value.
    /// It facilitates the process of anonymizing and mapping data by ensuring a one-to-one relationship between
    /// original values and placeholders. The placeholder generation incorporates a combination of an entity type
    /// and a hash computed from the original value to ensure uniqueness.
    /// </remarks>
    private readonly Dictionary<string, string> _originalToPlaceholder = new();

    /// <summary>
    /// Stores the mappings between placeholders and their corresponding original values.
    /// </summary>
    /// <remarks>
    /// This dictionary serves as a reverse lookup for the original values based on their placeholders.
    /// It is primarily used during the deanonymization process to replace placeholders within text
    /// with their associated original values.
    /// </remarks>
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
    /// <remarks>
    /// Each placeholder represents a mapping between an anonymized value and its corresponding original value.
    /// This property provides a count of the existing mappings stored in memory at any given time.
    /// </remarks>
    public int PlaceholderCount => _placeholderToOriginal.Count;

    /// <summary>
    /// Retrieves an existing placeholder for the given original value if it exists,
    /// or creates and stores a new placeholder for it using the specified entity type.
    /// </summary>
    /// <param name="entityType">The type of entity to be associated with the placeholder. Cannot be null, empty, or whitespace.</param>
    /// <param name="original">The original value to be mapped to a placeholder. Cannot be null.</param>
    /// <returns>The placeholder associated with the original value. If the original value is new, a new placeholder is created and returned.</returns>
    public string GetOrCreatePlaceholder(string entityType, string original)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityType);
        ArgumentNullException.ThrowIfNull(original);

        if (_originalToPlaceholder.TryGetValue(original, out var existing))
            return existing;

        var hash = ComputeHash(original);
        var placeholder = $"[{entityType}_{hash}]";

        _originalToPlaceholder[original] = placeholder;
        _placeholderToOriginal[placeholder] = original;

        return placeholder;
    }

    /// <summary>
    /// Replaces placeholders in the given text with their corresponding original values using the stored mappings.
    /// </summary>
    /// <param name="anonymizedText">The text containing placeholders to be replaced with original values.</param>
    /// <returns>The text with placeholders replaced by their original values. If a placeholder does not have a matching original value in the mapping, it is left unchanged.</returns>
    public string Deanonymize(string anonymizedText)
    {
        if (string.IsNullOrEmpty(anonymizedText)) return anonymizedText;

        // Replace placeholders with their original values if they exist in the mapping.
        // Otherwise, leave the placeholder as-is.
        return PlaceholderRegex.Replace(anonymizedText, m =>
                                                            _placeholderToOriginal.TryGetValue(m.Value, out var original) ? 
                                                                original : 
                                                                m.Value);
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