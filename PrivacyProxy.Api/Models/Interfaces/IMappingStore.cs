namespace PrivacyProxy.Api.Models.Interfaces;

/// <summary>
/// Represents a store that manages mappings between sensitive data and their placeholders.
/// </summary>
public interface IMappingStore
{
    /// <summary>
    /// Retrieves an existing placeholder for the specified entity type and original value,
    /// or creates a new placeholder if none exists. Matching is exact, so the value is restored
    /// with its exact original casing and spacing.
    /// </summary>
    /// <param name="entityType">The type of the entity for which the placeholder is being requested.</param>
    /// <param name="original">The original value to be mapped or represented by the placeholder.</param>
    /// <returns>A string representing the placeholder corresponding to the provided entity type and original value.</returns>
    string GetOrCreatePlaceholder(string entityType, string original);

    /// <summary>
    /// Converts an pseudonymized representation of a string back to its original form, if a mapping exists.
    /// </summary>
    /// <param name="text">The pseudonymized text to be converted back to its original representation.</param>
    /// <returns>The original text corresponding to the pseudonymized input, or the input itself if no mapping is found.</returns>
    string Depseudonymize(string text);

    /// <summary>
    /// Like <see cref="Depseudonymize"/>, but for text that is serialized JSON (e.g. tool_call arguments):
    /// restored values are JSON-escaped so characters like '"' or '\' in an original value
    /// cannot break the surrounding JSON document.
    /// </summary>
    /// <param name="json">The pseudonymized raw JSON text containing placeholders.</param>
    /// <returns>The JSON text with placeholders replaced by their JSON-escaped original values.</returns>
    string DepseudonymizeJson(string json);

    /// <summary>
    /// Gets the total count of placeholders currently stored in the mapping store.
    /// </summary>
    /// <remarks>
    /// Placeholders are used as substitutes for original data to ensure privacy or anonymity.
    /// This property provides the number of such placeholders maintained in the store.
    /// </remarks>
    int PlaceholderCount { get; }
}