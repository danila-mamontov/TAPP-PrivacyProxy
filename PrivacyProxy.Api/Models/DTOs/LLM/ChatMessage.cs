using System.Text;
using System.Text.Json;
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
    [JsonConverter(typeof(ChatMessageContentConverter))]
    public required string Content { get; init; }

    /// <summary>
    /// Represents a collection of additional data associated with the message.
    /// This property allows for the inclusion of arbitrary key-value pairs,
    /// where keys are strings and values are JSON elements, for extensibility purposes.
    /// </summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? Extensions { get; init; }
}

/// <summary>
/// A custom JSON converter that processes the content of a chat message in various formats,
/// ensuring compatibility with OpenAI-compatible clients.
/// </summary>
/// <remarks>
/// This converter supports two formats for message content:
/// 1. A plain string, e.g., "content": "Hello".
/// 2. An array of content parts, e.g., "content": [{"type": "text", "text": "Hello"}, ...].
/// When reading:
/// - If the content is a plain string, it is returned as-is.
/// - If the content is an array, all text parts are concatenated into a single string,
/// while non-text parts (such as images) are ignored.
/// - For null or unsupported formats, an empty string is returned.
/// When writing:
/// - The content is always serialized as a plain string.
/// This converter simplifies the handling of complex content structures
/// by normalizing all contents into a single string representation.
/// </remarks>
public class ChatMessageContentConverter : JsonConverter<string>
{
    public override string Read(
        ref Utf8JsonReader    reader,
        Type                  typeToConvert,
        JsonSerializerOptions options)
    {
        // Plain string case
        if (reader.TokenType == JsonTokenType.String)
            return reader.GetString() ?? "";

        // Array case: concatenate all text parts
        if (reader.TokenType == JsonTokenType.StartArray)
        {
            var sb = new StringBuilder();

            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType != JsonTokenType.StartObject) continue;

                using var doc = JsonDocument.ParseValue(ref reader);
                var element = doc.RootElement;

                if (element.TryGetProperty("type", out var type) &&
                    type.GetString() == "text" &&
                    element.TryGetProperty("text", out var text))
                {
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(text.GetString());
                }
                // Other content types (image_url, etc.) are silently dropped.
            }

            return sb.ToString();
        }

        // Null or anything else
        return "";
    }

    /// <summary>
    /// Writes a string value to the specified JSON writer.
    /// </summary>
    /// <param name="writer">
    /// The <see cref="Utf8JsonWriter"/> to write the JSON data to. This parameter cannot be null.
    /// </param>
    /// <param name="value">
    /// The string value to write. This parameter represents the content that will be serialized
    /// as a JSON string.
    /// </param>
    /// <param name="options">
    /// The <see cref="JsonSerializerOptions"/> to use when writing the JSON data.
    /// This parameter may contain serialization options or settings; it can be null.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown if the <paramref name="writer"/> parameter is null.
    /// </exception>
    public override void Write(
        Utf8JsonWriter writer,
        string value,
        JsonSerializerOptions options)
    {
        writer.WriteStringValue(value);
    }
}