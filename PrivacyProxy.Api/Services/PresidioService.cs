using System.Text;
using PrivacyProxy.Api.Models.Enums;
using PrivacyProxy.Api.Models.Interfaces;
using Serilog;

namespace PrivacyProxy.Api.Services;

/// <summary>
/// The PresidioService class provides functionality for analyzing and anonymizing sensitive
/// information from text inputs using multi-language support. It interacts with an analyzer
/// client to detect sensitive entities, applies policy filters, and replaces the detected
/// entities with placeholders.
/// </summary>
public class PresidioService(
    IPresidioAnalyzerClient presidioAnalyzerClient, 
    IEntityPolicy entityPolicy,
    IMappingStore mappingStore) : IPresidioService
{
    /// <summary>
    /// Anonymizes sensitive information in the provided text by analyzing it in multiple languages
    /// and replacing detected entities with placeholders.
    /// </summary>
    /// <param name="text">The input text to be anonymized. If null or whitespace, the original text is returned.</param>
    /// <param name="ct">An optional cancellation token used to cancel the anonymization process.</param>
    /// <returns>A string where sensitive entities have been replaced with generated placeholders.
    /// If no entities are detected, the original text is returned.</returns>
    public async Task<string> AnonymizeAsync(
        string text,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;
        
        // Send both language requests in parallel
        var deTask = presidioAnalyzerClient.AnalyzeAsync(text, Language.German, ct);
        var enTask = presidioAnalyzerClient.AnalyzeAsync(text, Language.English, ct);
        await Task.WhenAll(deTask, enTask);
        
        // Filter each result through the policy with the correct language
        var deEntities = entityPolicy.Apply(deTask.Result, Language.German,  text);
        var enEntities = entityPolicy.Apply(enTask.Result, Language.English, text);
        
        // Combine and resolve cross-language overlaps
        var combined = entityPolicy.ResolveOverlaps(deEntities.Concat(enEntities));
        
        if (combined.Count == 0)
            return text;
        
        // Sort descending by start so replacements don't shift indices
        var sorted = combined.OrderByDescending(e => e.Start).ToList();
        
        // Replace each entity with its placeholder
        var sb = new StringBuilder(text);
        foreach (var entity in sorted)
        {
            var original    = text[entity.Start..entity.End];
            var placeholder = mappingStore.GetOrCreatePlaceholder(entity.EntityType, original);
            sb.Remove(entity.Start, entity.End - entity.Start)
              .Insert(entity.Start, placeholder);
        }
        
        Log.Information("Anonymized {Count} entities in message", combined.Count);
        Log.Debug("Original message: {Message}", text);
        Log.Debug("Anonymized message: {Message}", sb.ToString());
        Log.Debug("Anonymized entities: {@Entities}", combined);
        
        return sb.ToString();
    }
}