using System.Text.Json;
using System.Text.Json.Serialization;

namespace PrivacyProxy.Api.Models.DTOs;

public record PresidioAnalyzerResponse
{
    [JsonPropertyName("analysis_explanation")]
    public JsonElement? AnalysisExplanation { get; init; }
    
    [JsonPropertyName("end")]
    public required int End { get; init; }
    
    [JsonPropertyName("entity_type")]
    public required string EntityType { get; init; }
    
    [JsonPropertyName("start")]
    public required int Start { get; init; }
    
    [JsonPropertyName("score")]
    public required double Score { get; init; }
}