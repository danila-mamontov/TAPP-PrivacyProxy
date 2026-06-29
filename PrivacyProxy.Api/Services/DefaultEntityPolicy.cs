using Microsoft.Extensions.Options;
using PrivacyProxy.Core.Configuration;
using PrivacyProxy.Api.Models.DTOs.Presidio;
using PrivacyProxy.Api.Models.Enums;
using PrivacyProxy.Api.Models.Interfaces;

namespace PrivacyProxy.Api.Services;

/// <summary>
/// Represents the default entity policy used to apply filtering logic
/// on recognized entities based on preset confidence thresholds,
/// considering the source language.
/// Implements the <c>IEntityPolicy</c> interface.
/// </summary>
public class DefaultEntityPolicy(IOptionsMonitor<PresidioOptions> options) : IEntityPolicy
{
    /// <summary>
    /// Applies the entity policy by filtering entities based on predefined thresholds
    /// for the specified language and resolves any overlapping entities.
    /// </summary>
    /// <param name="entities">A collection of entities to process, each containing details such as entity type, score, and positional indices.</param>
    /// <param name="sourceLanguage">The language of the source text, which determines the applicable entity thresholds.</param>
    /// <param name="originalText">The original text being analyzed, used for contextual operations if necessary.</param>
    /// <returns>A filtered and non-overlapping list of entities that meet the confidence thresholds for the specified language.</returns>
    public IReadOnlyList<PresidioAnalyzerResponse> Apply(
        IEnumerable<PresidioAnalyzerResponse> entities, 
        Language sourceLanguage, 
        string originalText)
    {
        // Get the entity thresholds for the source language (read live so config changes apply).
        var currentOptions = options.CurrentValue;
        var thresholds = sourceLanguage == Language.German
                            ? currentOptions.GermanEntityThresholds
                            : currentOptions.EnglishEntityThresholds;
        
        // Filter the entities based on their confidence scores and language depending on scores.
        var filtered = entities
                      .Where(e =>
                                 thresholds.TryGetValue(e.EntityType, out var threshold) &&
                                 e.Score >= threshold)
                      .ToList();
        
        return ResolveOverlaps(filtered);
    }

    /// <summary>
    /// Resolves overlapping entities by selecting the highest-scoring entities and discarding any that overlap
    /// with a previously selected entity.
    /// </summary>
    /// <param name="overlappingEntities">A collection of entities that may contain positional overlaps, where each entity includes details such as type, score, and positional indices.</param>
    /// <returns>A list of non-overlapping entities selected based on their confidence scores in descending order.</returns>
    public IReadOnlyList<PresidioAnalyzerResponse> ResolveOverlaps(
        IEnumerable<PresidioAnalyzerResponse> overlappingEntities)
    {
        // Sort the entities by their score in descending order.
        var byScore = overlappingEntities
           .OrderByDescending(e => e.Score);
        
        var resolved = new List<PresidioAnalyzerResponse>();

        foreach (var entity in byScore)
        {
            // If the entity doesn't overlap with any other entity, add it to the resolved list.
            // Otherwise, skip it, as it's already been resolved.
            if (!resolved.Any(e => e.Start < entity.End && e.End > entity.Start))
            {
                resolved.Add(entity);
            }
        }

        return resolved;
    }
}