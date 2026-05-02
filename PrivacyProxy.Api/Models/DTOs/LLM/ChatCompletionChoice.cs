using System.Text.Json;
using System.Text.Json.Serialization;

namespace PrivacyProxy.Api.Models.DTOs.LLM;

public record ChatCompletionChoice
{
    [JsonPropertyName("message")]
    public required ChatMessage Message { get; init; }
    
    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; init; }
    
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extensions { get; init; }
}