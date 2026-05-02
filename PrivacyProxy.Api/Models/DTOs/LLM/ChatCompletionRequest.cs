using System.Text.Json;
using System.Text.Json.Serialization;

namespace PrivacyProxy.Api.Models.DTOs.LLM;

/// <summary>
/// Represents a request for generating chat completions in an LLM (Large Language Model) system.
/// </summary>
/// <remarks>
/// This class defines the structure of the request body sent to the LLM API for generating
/// chat-based responses. It includes details about the language model to use, the list of
/// messages in the conversation, streaming options, and additional extensible data.
/// </remarks>
public record ChatCompletionRequest
{
    /// <summary>
    /// Represents the name or identifier of the AI model to be used for processing the request.
    /// </summary>
    [JsonPropertyName("model")]
    public required string Model { get; init; }
    
    [JsonPropertyName("messages")]
    public required List<ChatMessage> Messages { get; init; }

    /// <summary>
    /// Indicates whether the response should be returned as a stream.
    /// Set to true to enable streaming behavior, where the response is provided incrementally
    /// as it becomes available. Set to false or null if streaming is not required.
    /// </summary>
    [JsonPropertyName("stream")]
    public bool? Stream { get; init; }

    /// <summary>
    /// A collection of additional data associated with the request that is not explicitly mapped to a predefined property.
    /// This property is primarily used for extending the schema to include custom or unstructured data
    /// provided in the JSON payload.
    /// </summary>
    /// <remarks>
    /// The data is stored in a dictionary format where the key represents the property name as a string,
    /// and the value is stored as a <see cref="System.Text.Json.JsonElement"/>.
    /// Use this property to capture and access any extra fields submitted with the payload that are not
    /// explicitly defined in the model.
    /// </remarks>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extensions { get; init; }
}