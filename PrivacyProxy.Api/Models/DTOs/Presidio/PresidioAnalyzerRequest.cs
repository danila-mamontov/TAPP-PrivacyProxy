using System.Text.Json.Serialization;
using PrivacyProxy.Api.Models.Enums;

namespace PrivacyProxy.Api.Models.DTOs.Presidio;

/// <summary>
/// Represents a request object for sending data to the Presidio Analyzer service for
/// identifying and processing sensitive information in text.
/// </summary>
public record PresidioAnalyzerRequest
{
    /// <summary>
    /// Gets or sets the input text to be analyzed by the Presidio Analyzer service.
    /// This property represents the content that will be processed for detecting sensitive
    /// information, such as Personally Identifiable Information (PII).
    /// The text provided should be in plain string format and is required for the analysis.
    /// </summary>
    [JsonPropertyName( "text")]
    public required string Text { get; init; }

    /// <summary>
    /// Gets or sets the language used for text analysis.
    /// Specifies the language of the input text, enabling the analysis engine
    /// to apply appropriate language-specific models and configurations.
    /// Supported values are predefined in the <see cref="Language"/> enumeration,
    /// such as English and German.
    /// This property is required and must be populated to ensure accurate analysis.
    /// </summary>
    [JsonPropertyName( "language")]
    public required Language Language { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether the decision-making process
    /// of the Presidio Analyzer should be included in the response.
    /// When set to true, additional information regarding how decisions
    /// about entity recognition were made will be returned. This can be useful
    /// for understanding, auditing, or debugging the analysis process.
    /// If null or false, the response will only include the final results
    /// without detailed decision-making information.
    /// </summary>
    [JsonPropertyName( "return_decision_process")]
    public bool? ReturnDecisionProcess { get; set; }

    /// <summary>
    /// Gets or sets a list of strings that specify elements to be excluded from detection
    /// by the analysis process. These strings are treated as exceptions and will not be flagged
    /// as sensitive information during the entity recognition process.
    /// This property can be used to customize the analysis by explicitly allowing specific
    /// terms, names, or patterns that may otherwise be misidentified.
    /// </summary>
    [JsonPropertyName("allow_list")]
    public string[]? AllowList { get; set; }

    /// <summary>
    /// Gets or sets the context for the analysis process.
    /// Provides additional hints or keywords that can aid the entity recognition engine
    /// in identifying sensitive information within the input text.
    /// This property can be optionally populated with an array of contextual strings, which
    /// helps customize the analysis to better align with specific use cases or domain-specific
    /// language patterns.
    /// Example values might include specific prefixes, headers, or phrases often associated
    /// with sensitive information.
    /// </summary>
    [JsonPropertyName("context")]
    public string[]? Context { get; set; }

    /// <summary>
    /// Gets or initializes the list of entities to be detected or processed
    /// in the analysis request. Entities are used to specify the types of
    /// information (e.g., PERSON, LOCATION, etc.) that the analyzer should
    /// focus on when processing the text.
    /// </summary>
    [JsonPropertyName("entities")]
    public string[]? Entities { get; init; }

    /// <summary>
    /// Defines the minimum confidence score required for detection to be considered valid.
    /// </summary>
    /// <remarks>
    /// The value must be double between 0 and 1, where 1 represents full confidence.
    /// Any detection with a confidence score below this threshold will be excluded from the results.
    /// </remarks>
    [JsonPropertyName("score_threshold")]
    public double? ScoreThreshold { get; init; }
}