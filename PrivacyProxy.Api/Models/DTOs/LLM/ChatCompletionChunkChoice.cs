using System.Text.Json;
using System.Text.Json.Serialization;

namespace PrivacyProxy.Api.Models.DTOs.LLM;

public record ChatCompletionChunkChoice
{
    [JsonPropertyName("delta")]
    public required ChatMessageDelta Delta { get; init; }
    
    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; init; }
    
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; init; }
}