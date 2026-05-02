using PrivacyProxy.Api.Models.DTOs;
using PrivacyProxy.Api.Models.Enums;

namespace PrivacyProxy.Api.Models.Interfaces;

/// <summary>
/// Interface defining the contract for applying entity policies to a collection of analyzed entities.
/// Provides functionality for processing entities identified by the Presidio Analyzer
/// and refining them based on custom policy logic.
/// With this interface, developers can define custom rules for filtering, modifying, or enhancing the entities
/// e.g., depending on language, etc.
/// </summary>
public interface IEntityPolicy
{
    /// <summary>
    /// Applies entity policy rules to filter and modify the given list of entities
    /// based on the specified source language and original text.
    /// </summary>
    /// <param name="entities">A collection of <see cref="PresidioAnalyzerResponse"/> instances
    /// representing the entities identified from text analysis.</param>
    /// <param name="sourceLanguage">The <see cref="Language"/> of the text being analyzed.</param>
    /// <param name="originalText">The original text from which the entities were identified.</param>
    /// <returns>An IReadOnlyList of <see cref="PresidioAnalyzerResponse"/> reflecting the filtered or modified
    /// entities after applying policy rules.</returns>
    IReadOnlyList<PresidioAnalyzerResponse> Apply(
        IEnumerable<PresidioAnalyzerResponse> entities,
        Language sourceLanguage,
        string originalText
    );
    
    /// <summary>
    /// Resolves overlapping entities within a collection of analyzed entities by
    /// identifying and handling conflicts between them based on predefined logic.
    /// This ensures that the final set of entities is non-overlapping and appropriately prioritized.
    /// </summary>
    /// <param name="entities">A collection of <see cref="PresidioAnalyzerResponse"/> instances representing
    /// the analyzed entities to process for overlap resolution.</param>
    /// <returns>An IReadOnlyList of <see cref="PresidioAnalyzerResponse"/> where overlapping entities
    /// have been resolved and refined.</returns>
    IReadOnlyList<PresidioAnalyzerResponse> ResolveOverlaps(
        IEnumerable<PresidioAnalyzerResponse> entities);
}