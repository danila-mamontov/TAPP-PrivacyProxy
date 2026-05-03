using System.Text.Json;
using System.Text.Json.Serialization;

namespace PrivacyProxy.Api.Models.DTOs.LLM;

public record ChatMessageDelta
{
    [JsonPropertyName("role")]
    public string? Role { get; init; }
    
    [JsonPropertyName("content")]
    public string? Content { get; init; }
    
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; init; }
}