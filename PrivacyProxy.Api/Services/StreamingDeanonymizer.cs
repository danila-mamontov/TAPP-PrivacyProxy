using System.Text.RegularExpressions;
using PrivacyProxy.Api.Models.Interfaces;

namespace PrivacyProxy.Api.Services;

/// <summary>
/// Handles deanonymization of streamed text fragments from an LLM response.
///
/// Problem: A placeholder like [PERSON_3fa91b8a2c1d4e5f] can be split across chunk boundaries:
///   Chunk 1: "[PER"
///   Chunk 2: "SON_3fa91b8a2c1d4e5"
///   Chunk 3: "f] is nice"
///
/// Solution: Each streaming context (e.g. "content", "toolcall_0") maintains its own carry
/// buffer. Incomplete placeholders are held back until the next chunk completes them.
///
/// Per-chunk logic in ProcessFragment:
///   1. Prepend carry buffer to the incoming fragment.
///   2. Run the placeholder regex on the combined string.
///   3a. If complete placeholders are found:,
///        Check the tail (text after the last match) for an opening '['.
///       - If no '[' in tail → flush everything: deanonymize and return.
///       - If '[' in tail but no ']' yet → buffer the tail, return everything before it.
///   3b. If no complete placeholders are found:
///       - Find the last '[' in the combined string.
///       - If no '[' → flush everything through.
///       - If '[' found but already has a ']' after it → invalid format, flush through.
///       - If '[' found with no ']' yet → buffer from '[' onward, return everything before.
///
/// FlushAll: Called when the stream ends. Returns and clears all carry buffers,
/// deanonymizing any remaining buffered content.
/// </summary>
public partial class StreamingDeanonymizer(IMappingStore mappingStore)
{
    private readonly Dictionary<string, string> _carries = new();
    
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
    /// Processes a fragment of text, attempting to resolve placeholders while handling incomplete fragments
    /// across multiple calls. Includes context handling to support multiple independent streams of data.
    /// </summary>
    /// <param name="fragment">The current fragment of text to process. This may contain partial, full, or no placeholders.</param>
    /// <param name="isFinal">
    /// A boolean indicating whether this is the final fragment in the sequence. If true, any buffered content
    /// will be processed and released.
    /// </param>
    /// <param name="contextKey">
    /// An optional identifier for tracking separate streams of text independently. When omitted, defaults
    /// to "content".
    /// </param>
    /// <returns>
    /// The processed text with resolved placeholders. Any incomplete placeholders are buffered for future
    /// completion if this is not the final fragment.
    /// </returns>
    public string ProcessFragment(string fragment, bool isFinal, string contextKey = "content")
    {
        fragment ??= "";

        // Retrieve the carry buffer for this context or start with an empty string.
        _carries.TryGetValue(contextKey, out var carry);
        carry ??= "";

        // If this is the last fragment, flush the carry buffer and deanonymize everything.
        if (isFinal)
        {
            _carries.Remove(contextKey);
            return mappingStore.Deanonymize(carry + fragment);
        }

        // Prepend any previously buffered content to the incoming fragment.
        var combined = carry + fragment;
        var matches  = PlaceholderRegex.Matches(combined);

        if (matches.Count > 0)
        {
            var lastMatch = matches[^1];
            var lastEnd   = lastMatch.Index + lastMatch.Length;
            var tail      = combined[lastEnd..];

            if (tail.IndexOf('[') < 0)
            {
                _carries.Remove(contextKey);
                return mappingStore.Deanonymize(combined);
            }

            // Split the tail: text before '[' is safe to release, from '[' onward buffer
            var openInTail = tail.LastIndexOf('[');
            _carries[contextKey] = tail[openInTail..];
            return mappingStore.Deanonymize(combined[..lastEnd] + tail[..openInTail]);
        }

        // No complete placeholder found — look for an opening '['.
        var lastOpen = combined.LastIndexOf('[');

        // No '[' at all → no placeholder possible, release everything.
        if (lastOpen < 0)
        {
            _carries.Remove(contextKey);
            return mappingStore.Deanonymize(combined);
        }

        // '[' found — check if there is already a closing ']' after it.
        var closingAfterOpen = combined.IndexOf(']', lastOpen);
        if (closingAfterOpen >= 0)
        {
            // Both '[' and ']' are present, but the regex did not match →
            // not a valid placeholder format, release everything.
            _carries.Remove(contextKey);
            return mappingStore.Deanonymize(combined);
        }

        // '[' found but no ']' yet → placeholder may still be arriving in the next chunk.
        // Buffer everything from '[' onward and release the text before it.
        _carries[contextKey] = combined[lastOpen..];
        return mappingStore.Deanonymize(combined[..lastOpen]);
    }

    /// <summary>
    /// Completes the processing of all buffered fragments across all contexts
    /// by applying deanonymization and returning the results.
    /// Any remaining buffered data will be cleared after this operation.
    /// </summary>
    /// <returns>A dictionary where each key represents a context, and each value is the deanonymized result of the buffered fragments for that context.</returns>
    public Dictionary<string, string> FlushAll()
    {
        var result = new Dictionary<string, string>();

        foreach (var (key, carry) in _carries)
        {
            if (!string.IsNullOrEmpty(carry))
                result[key] = mappingStore.Deanonymize(carry);
        }

        _carries.Clear();
        return result;
    }
}