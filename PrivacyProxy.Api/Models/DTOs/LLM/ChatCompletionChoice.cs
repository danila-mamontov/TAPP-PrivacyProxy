using System.Text.Json;
using System.Text.Json.Serialization;

namespace PrivacyProxy.Api.Models.DTOs.LLM;

/// <summary>
/// Represents a single choice in a chat completion response. Each choice contains a message,
/// an optional reason indicating why the response was completed, and additional extension data.
/// </summary>
public record ChatCompletionChoice
{
    /// <summary>
    /// Represents the message associated with a chat completion choice.
    /// </summary>
    /// <remarks>
    /// This property encapsulates details about the chat message,
    /// including its role (e.g., system, user, assistant) and content.
    /// It is a required field within a chat completion choice and
    /// plays a significant role in defining the context and behavior
    /// of the conversation.
    /// </remarks>
    [JsonPropertyName("message")]
    public required ChatMessage Message { get; init; }

    /// <summary>
    /// Indicates the reason why the chat completion process was concluded.
    /// </summary>
    /// <remarks>
    /// Possible values for this property can include reasons such as
    /// reaching a predefined completion criteria, encountering an interruption,
    /// or the process timing out. The value is optional and may be null
    /// if the reason is not provided.
    /// </remarks>
    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; init; }

    /// <summary>
    /// Gets additional data not explicitly defined in the model but included in the serialized JSON.
    /// This property allows support for extensibility by capturing any extra elements in the JSON payload
    /// that are not mapped to predefined properties in the object.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extensions { get; init; }
}