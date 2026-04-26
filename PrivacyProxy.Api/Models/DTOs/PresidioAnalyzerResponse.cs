using System.Text.Json;
using System.Text.Json.Serialization;

namespace PrivacyProxy.Api.Models.DTOs;

public record PresidioAnalyzerResponse
{
    [JsonPropertyName("analysis_explanation")]
    public JsonElement? AnalysisExplanation { get; set; }
    
    [JsonPropertyName("end")]
    public required int End { get; set; }
    
    [JsonPropertyName("entity_type")]
    public required string EntityType { get; set; }
    
    [JsonPropertyName("start")]
    public required int Start { get; set; }
    
    [JsonPropertyName("score")]
    public required double Score { get; set; }
}