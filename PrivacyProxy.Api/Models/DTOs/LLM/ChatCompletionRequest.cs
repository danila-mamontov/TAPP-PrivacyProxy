using System.Text.Json;
using System.Text.Json.Serialization;

namespace PrivacyProxy.Api.Models.DTOs.LLM;

public record ChatCompletionRequest
{
    [JsonPropertyName("model")]
    public required string Model { get; init; }
    
    [JsonPropertyName("messages")]
    public required List<ChatMessage> Messages { get; init; }
    
    [JsonPropertyName("stream")]
    public bool? Stream { get; init; }
    
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extensions { get; init; }
}