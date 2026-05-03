using System.Text.Json;
using System.Text.Json.Serialization;

namespace PrivacyProxy.Api.Models.DTOs.LLM;

/// <summary>
/// Represents a partial or incremental message update within a chat conversation.
/// This model is used to capture structured data for a message component during interactions with a large language model (LLM).
/// </summary>
public record ChatMessageDelta
{
    /// <summary>
    /// Represents the role associated with a chat message delta.
    /// This property indicates the context or purpose of the message within the conversation (e.g., system, user, or assistant).
    /// </summary>
    [JsonPropertyName("role")]
    public string? Role { get; init; }

    /// <summary>
    /// Represents the textual content of a chat message delta.
    /// This property may contain a part of the incremental message being processed
    /// during a chat interaction stream. The content is typically received or
    /// modified during the stream handling process, such as in data transformations
    /// or deanonymization steps.
    /// </summary>
    [JsonPropertyName("content")]
    public string? Content { get; init; }

    /// <summary>
    /// Contains additional data not explicitly defined by the model.
    /// This property is used to capture extra fields in the JSON payload
    /// that do not have corresponding properties in the class.
    /// </summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; init; }
}