using System.Text.Json;
using System.Text.Json.Serialization;

namespace PrivacyProxy.Api.Models.DTOs.LLM;

public record ChatCompletionChunk
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }
    
    [JsonPropertyName("choices")]
    public required List<ChatCompletionChunkChoice> Choices { get; init; }
    
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}