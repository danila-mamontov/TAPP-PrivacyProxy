using System.Text.Json.Serialization;

namespace PrivacyProxy.Api.Models.Enums;

/// <summary>
/// Represents a language that can be used to define the language of text analysis with Presidio Analyzer.
/// </summary>
/// <remarks>
/// This enumeration is used to standardize language representations and their serialization.
/// Each member is mapped to its corresponding string value when serialized as JSON.
/// </remarks>
public enum Language
{
    /// <summary>
    /// Represents the English language within the <see cref="Language"/> enumeration.
    /// </summary>
    /// <remarks>
    /// This member corresponds to the value "en" when serialized to JSON using
    /// <see cref="System.Text.Json"/> with the <see cref="JsonStringEnumConverter"/>.
    /// </remarks>
    [JsonStringEnumMemberName("en")]
    English,

    /// <summary>
    /// Represents the German language in the <see cref="Language"/> enumeration.
    /// </summary>
    /// <remarks>
    /// The German language is associated with the serialized string value "de".
    /// This enum member is used for scenarios where a German language designation is required.
    /// </remarks>
    [JsonStringEnumMemberName("de")]
    German
}