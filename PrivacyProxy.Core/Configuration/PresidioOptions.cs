namespace PrivacyProxy.Core.Configuration;

/// <summary>
/// Configuration options for Presidio, a library for text analytics and data anonymization.
/// </summary>
public class PresidioOptions
{
    public required string AnalyzerUrl { get; init; }

    /// <summary>
    /// Gets or sets the list of terms or entities that will be excluded
    /// from being analyzed or transformed by the Presidio engine.
    /// </summary>
    /// <remarks>
    /// The <c>AllowList</c> property provides a mechanism to define specific
    /// entries that should bypass privacy-preserving processes. These entries
    /// are treated as exceptions and will not be redacted, anonymized, or analyzed,
    /// ensuring that critical or sensitive information can be preserved as-is.
    /// This property is an array of strings where each string represents an
    /// entry to be exempted.
    /// </remarks>
    public string[] AllowList { get; set; } = [];

    /// <summary>
    /// Gets or sets the contextual information that can influence the analysis or transformation
    /// process performed by the Presidio engine.
    /// </summary>
    /// <remarks>
    /// The <c>Context</c> property allows the inclusion of supplementary data or metadata
    /// that provides additional insight into the environment or scenario being processed. This
    /// information is used to refine and enhance the behavior of the text analytics
    /// and data anonymization functions. The context is represented as an array of strings
    /// where each string serves as an identifiable piece of contextual information.
    /// </remarks>
    public string[] Context { get; set; } = [];
    
    public double ScoreThreshold { get; init; } = 0.4;

    /// <summary>
    /// Specifies the list of entity types recognized for German language analysis.
    /// The system uses these entity types for identifying sensitive information
    /// such as names, locations, and organizations within German text data.
    /// </summary>
    public string[] GermanEntityTypes { get; set; } =
        ["PERSON", "LOCATION", "ORGANIZATION"];

    /// <summary>
    /// Defines the types of entities to be detected in English-language content.
    /// </summary>
    /// <remarks>
    /// These types are used in conjunction with detection mechanisms, allowing for
    /// the identification and analysis of specific entities in textual data.
    /// The property supports customization to modify or extend the default entity types as needed.
    /// </remarks>
    public string[] EnglishEntityTypes { get; set; } =
    ["EMAIL_ADDRESS", "PHONE_NUMBER", "IP_ADDRESS", 
        "CREDIT_CARD", "IBAN_CODE", "URL"];

    /// <summary>
    /// Gets the dictionary of entity types and their associated confidence score thresholds
    /// specifically for German language entities.
    /// </summary>
    /// <remarks>
    /// The <c>GermanEntityThresholds</c> property defines the minimum confidence scores
    /// required to consider an entity of a specific type as valid during processing.
    /// Each key in the dictionary represents a German entity type (e.g., "PERSON", "LOCATION",
    /// "ORGANIZATION"), while the corresponding value is a <c>double</c> representing
    /// the confidence score threshold.
    /// These thresholds are used as part of the privacy-preserving mechanisms to determine
    /// whether an entity identified in German-language input data meets the criteria for
    /// further processing or redaction.
    /// The property is initialized with default values, which can be customized as needed
    /// to align with the desired sensitivity level for specific entity types.
    /// </remarks>
    public Dictionary<string, double> GermanEntityThresholds { get; init; } = new()
                                                                              {
                                                                                  { "PERSON",       0.85 },
                                                                                  { "LOCATION",     0.90 },
                                                                                  { "ORGANIZATION", 0.85 }
                                                                              };

    /// <summary>
    /// Gets or initializes the entity-specific confidence thresholds for English-language entities
    /// used by the Presidio engine during text analysis and data anonymization.
    /// </summary>
    /// <remarks>
    /// The <c>EnglishEntityThresholds</c> property defines the minimum confidence scores required
    /// for identifying and recognizing specific entity types in English language text. These thresholds
    /// determine the sensitivity of the recognition process, with higher values requiring greater certainty.
    /// The dictionary keys represent the entity types (e.g., "EMAIL_ADDRESS", "PHONE_NUMBER"), while
    /// the corresponding values indicate the confidence threshold for each entity.
    /// Adjusting these values allows for fine-tuning the analysis process to balance accuracy and recall,
    /// depending on the specific use case.
    /// </remarks>
    public Dictionary<string, double> EnglishEntityThresholds { get; init; } = new()
                                                                               {
                                                                                   { "EMAIL_ADDRESS", 0.4 },
                                                                                   { "PHONE_NUMBER",  0.4 },
                                                                                   { "IP_ADDRESS",    0.4 },
                                                                                   { "CREDIT_CARD",   0.4 },
                                                                                   { "IBAN_CODE",     0.4 },
                                                                                   { "URL",           0.4 }
                                                                               };
}