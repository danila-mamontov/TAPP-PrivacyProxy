using System.Text.Json.Serialization;

namespace PrivacyProxy.Api.Models.DTOs.LLM;

/// <summary>
/// Represents a single message in a chat-based system. A message includes the role
/// of the sender (e.g., 'user', 'system', 'assistant') and the content of the message.
/// </summary>
/// <remarks>
/// This class is used as a foundational component for constructing conversations in a
/// Large Language Model (LLM) system. Each message in the conversation must specify
/// the role and content details.
/// </remarks>
public record ChatMessage
{
    /// <summary>
    /// Represents the role associated with a chat message.
    /// This property defines the context or identity of the message sender,
    /// such as 'user', 'assistant', or other specific roles.
    /// </summary>
    [JsonPropertyName("role")]
    public required string Role { get; init; }

    /// <summary>
    /// Represents the body of the message associated with a specific role in a chat.
    /// </summary>
    [JsonPropertyName("content")]
    public required string Content { get; init; }
}