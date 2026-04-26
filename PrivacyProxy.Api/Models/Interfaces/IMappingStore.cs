namespace PrivacyProxy.Api.Models.Interfaces;

/// <summary>
/// Represents a store that manages mappings between sensitive data and their placeholders.
/// </summary>
public interface IMappingStore
{
    /// <summary>
    /// Retrieves an existing placeholder for the specified entity type and original value,
    /// or creates a new placeholder if none exists.
    /// </summary>
    /// <param name="entityType">The type of the entity for which the placeholder is being requested.</param>
    /// <param name="original">The original value to be mapped or represented by the placeholder.</param>
    /// <returns>A string representing the placeholder corresponding to the provided entity type and original value.</returns>
    string GetOrCreatePlaceholder(string entityType, string original);

    /// <summary>
    /// Converts an anonymized representation of a string back to its original form, if a mapping exists.
    /// </summary>
    /// <param name="text">The anonymized text to be converted back to its original representation.</param>
    /// <returns>The original text corresponding to the anonymized input, or the input itself if no mapping is found.</returns>
    string Deanonymize(string text);

    /// <summary>
    /// Gets the total count of placeholders currently stored in the mapping store.
    /// </summary>
    /// <remarks>
    /// Placeholders are used as substitutes for original data to ensure privacy or anonymity.
    /// This property provides the number of such placeholders maintained in the store.
    /// </remarks>
    int PlaceholderCount { get; }
}