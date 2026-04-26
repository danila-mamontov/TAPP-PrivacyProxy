using Microsoft.Extensions.Options;
using PrivacyProxy.Api.Configuration;
using PrivacyProxy.Api.Models.DTOs;
using PrivacyProxy.Api.Models.Enums;
using PrivacyProxy.Api.Models.Interfaces;

namespace PrivacyProxy.Api.Services;

public class DefaultEntityPolicy(IOptions<PresidioOptions> options) : IEntityPolicy
{
    private readonly PresidioOptions _options = options.Value;
    
    public IReadOnlyList<PresidioAnalyzerResponse> Apply(
        IEnumerable<PresidioAnalyzerResponse> entities, 
        Language sourceLanguage, 
        string originalText)
    {
        // Get the entity thresholds for the source language.
        var thresholds = sourceLanguage == Language.German
                            ? _options.GermanEntityThresholds
                            : _options.EnglishEntityThresholds;

        // Filter the entities based on their confidence scores and language depending on scores.
        var filtered = entities
                      .Where(e =>
                                 thresholds.TryGetValue(e.EntityType, out var threshold) &&
                                 e.Score >= threshold)
                      .ToList();
        
        return ResolveOverlaps(filtered);
    }

    private static List<PresidioAnalyzerResponse> ResolveOverlaps(
        List<PresidioAnalyzerResponse> overlappingEntities)
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