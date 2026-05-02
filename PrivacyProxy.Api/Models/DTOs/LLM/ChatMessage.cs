using System.Text.Json.Serialization;

namespace PrivacyProxy.Api.Models.DTOs.LLM;

public record ChatMessage
{
    [JsonPropertyName("role")]
    public required string Role { get; init; }
    
    [JsonPropertyName("content")]
    public required string Content { get; init; }
}