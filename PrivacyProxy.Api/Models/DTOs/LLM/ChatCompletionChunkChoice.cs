using System.Text.Json;
using System.Text.Json.Serialization;

namespace PrivacyProxy.Api.Models.DTOs.LLM;

/// <summary>
/// Represents an individual choice within a streamed response during a chat completion process.
/// This class is a part of the deserialization model for handling incremental responses from a large language model (LLM).
/// </summary>
public record ChatCompletionChunkChoice
{
    /// <summary>
    /// Represents the delta information for a chat message fragment
    /// within a chat completion chunk.
    /// This property encapsulates the incremental content changes and
    /// accompanying metadata of a chat message as it is being streamed.
    /// </summary>
    [JsonPropertyName("delta")]
    public required ChatMessageDelta Delta { get; init; }

    /// <summary>
    /// Represents the reason why the generation of content was completed.
    /// </summary>
    /// <remarks>
    /// This property can indicate various reasons, such as the model completing its response
    /// or encountering a specific stop condition during content generation.
    /// </remarks>
    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; init; }

    /// <summary>
    /// Gets or initializes a collection of key-value pairs containing additional JSON
    /// properties that are not mapped to the defined members of the record. This property
    /// is populated during deserialization when extra elements are present in the JSON object.
    /// </summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; init; }
}