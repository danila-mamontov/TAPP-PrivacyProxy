using System.Text.Json;
using System.Text.Json.Serialization;

namespace PrivacyProxy.Api.Models.DTOs.LLM;

public record ChatCompletionResponse
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }
    
    [JsonPropertyName("choices")]
    public required List<ChatCompletionChoice> Choices { get; init; }
    
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extensions { get; init; }
}