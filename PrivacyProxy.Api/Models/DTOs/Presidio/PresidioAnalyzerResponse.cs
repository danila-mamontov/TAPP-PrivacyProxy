using System.Text.Json;
using System.Text.Json.Serialization;

namespace PrivacyProxy.Api.Models.DTOs.Presidio;

/// <summary>
/// Represents the response returned from the Presidio Analyzer after text analysis.
/// This response contains details about the identified entity,
/// its location within the analyzed text, the score indicating confidence,
/// and an optional analysis explanation providing further context.
/// </summary>
public record PresidioAnalyzerResponse
{
    /// <summary>
    /// Provides additional details about the reasoning behind the identified entity within the analysis.
    /// </summary>
    /// <remarks>
    /// This property contains a JSON object that includes information such as the recognizer used,
    /// the matching pattern, and scoring details for the analysis. Its presence is optional and
    /// may be null or missing if no explanation is available for the given analysis result.
    /// </remarks>
    [JsonPropertyName("analysis_explanation")]
    public JsonElement? AnalysisExplanation { get; init; }

    /// <summary>
    /// Represents the zero-based index of the last character in the detected entity within the analyzed text.
    /// </summary>
    /// <remarks>
    /// This property is used to specify the end position of an entity identified during the text analysis process.
    /// The value corresponds to the character index directly following the last character of the identified entity.
    /// </remarks>
    [JsonPropertyName("end")]
    public required int End { get; init; }

    /// <summary>
    /// Represents the type of entity identified in the result of an analysis operation.
    /// </summary>
    /// <remarks>
    /// The value of this property is determined based on the input text analyzed by the Presidio service.
    /// Common entity types include "EMAIL_ADDRESS", "PERSON", "PHONE_NUMBER", etc.
    /// </remarks>
    [JsonPropertyName("entity_type")]
    public required string EntityType { get; init; }

    /// <summary>
    /// Gets the starting position of the identified entity within the analyzed text.
    /// This value represents the zero-based index in the text where the entity begins.
    /// It is extracted during the analysis process and corresponds to the value of the
    /// "start" field in the serialized JSON response.
    /// </summary>
    [JsonPropertyName("start")]
    public required int Start { get; init; }

    /// <summary>
    /// Represents the confidence level associated with the detected entity during the analysis.
    /// </summary>
    /// <remarks>
    /// The value typically ranges from 0.0 (no confidence) to 1.0 (maximum confidence),
    /// indicating the likelihood that the detected entity matches the intended category.
    /// </remarks>
    [JsonPropertyName("score")]
    public required double Score { get; init; }
}