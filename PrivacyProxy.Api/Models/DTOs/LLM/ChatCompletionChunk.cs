using System.Text.Json;
using System.Text.Json.Serialization;

namespace PrivacyProxy.Api.Models.DTOs.LLM;

/// <summary>
/// Represents a chunk of JSON data received during a chat completion streaming process.
/// This class is used to deserialize and handle incoming data chunks from a large language model (LLM) response stream.
/// </summary>
public record ChatCompletionChunk
{
    /// <summary>
    /// Represents the unique identifier associated with this instance of a chat completion chunk.
    /// This property is required and provides a string value that serves as the primary identifier.
    /// </summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>
    /// Represents the collection of choices associated with a chat completion chunk.
    /// Each choice contains detailed information such as message content deltas and
    /// additional metadata related to the chat response generation process.
    /// </summary>
    [JsonPropertyName("choices")]
    public required List<ChatCompletionChunkChoice> Choices { get; init; }

    /// <summary>
    /// Gets a collection of additional JSON properties that were not explicitly mapped to any other
    /// properties in the object. This property provides a mechanism to handle unknown or
    /// dynamically added data without causing deserialization errors.
    /// </summary>
    /// <remarks>
    /// This property is decorated with the <see cref="System.Text.Json.Serialization.JsonExtensionDataAttribute"/>,
    /// which allows extra JSON elements to be captured during deserialization. The captured properties
    /// are stored as key-value pairs, where the key is the JSON property name, and the value is a
    /// <see cref="System.Text.Json.JsonElement"/> representing the corresponding JSON data.
    /// </remarks>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}