using System.Text.Json.Serialization;
using PrivacyProxy.Api.Models.Enums;

namespace PrivacyProxy.Api.Models.DTOs;

public record PresidioAnalyzerRequest
{
    [JsonPropertyName( "text")]
    public required string Text { get; init; }
    
    [JsonPropertyName( "language")]
    public required Language Language { get; init; }
    
    [JsonPropertyName( "return_decision_process")]
    public bool? ReturnDecisionProcess { get; set; }
    
    [JsonPropertyName("allow_list")]
    public string[]? AllowList { get; set; }
    
    [JsonPropertyName("context")]
    public string[]? Context { get; set; }
    
    [JsonPropertyName("entities")]
    public string[]? Entities { get; init; }
    
    [JsonPropertyName("score_threshold")]
    public double? ScoreThreshold { get; init; }
}