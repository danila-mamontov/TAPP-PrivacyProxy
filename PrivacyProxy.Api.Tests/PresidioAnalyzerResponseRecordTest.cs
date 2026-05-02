using System.Text.Json;
using PrivacyProxy.Api.Models.DTOs;

namespace PrivacyProxy.Api.Tests;

public class PresidioAnalyzerResponseRecordTest
{
    public static TheoryData<int, string, int, double> PresidioAnalyzerResponseRecordTestData() =>
        new()
        {
            { 5, "PERSON", 0, 0.6 },
            { 20, "LOCATION", 10, 0.95 },
            { 42, "CREDIT_CARD", 25, 1.0 },
            { 12, "IP_ADDRESS", 1, 0.123}
        };
    
    [Fact]
    public void DeserializationShouldRespectJsonPropertyNames()
    {
        // Arrange
        const string json = """
                            {
                                "analysis_explanation": {
                                    "recognizer": "EmailRecognizer",
                                    "pattern_name": "email",
                                    "original_score": 0.85
                                },
                                "end": 24,
                                "entity_type": "EMAIL_ADDRESS",
                                "start": 10,
                                "score": 0.85
                            }
                            """;
        
        // Act
        var response = JsonSerializer.Deserialize<PresidioAnalyzerResponse>(json);
        
        // Assert
        Assert.NotNull(response);
        Assert.NotNull(response.AnalysisExplanation);
        Assert.Equal(24, response.End);
        Assert.Equal("EMAIL_ADDRESS", response.EntityType);
        Assert.Equal(10, response.Start);
        Assert.Equal(0.85, response.Score);
        
        var explanation = response.AnalysisExplanation.Value;
        Assert.Equal(JsonValueKind.Object, explanation.ValueKind);
        Assert.Equal("EmailRecognizer", explanation.GetProperty("recognizer").GetString());
        Assert.Equal("email", explanation.GetProperty("pattern_name").GetString());
        Assert.Equal(0.85, explanation.GetProperty("original_score").GetDouble());
    }

    [Fact]
    public void DeserializationShouldAllowNullAnalysisExplanation()
    {
        // Arrange
        const string json = """
                            {
                                "analysis_explanation": null,
                                "end": 8,
                                "entity_type": "PERSON",
                                "start": 0,
                                "score": 0.99
                            }
                            """;
        
        // Act
        var response = JsonSerializer.Deserialize<PresidioAnalyzerResponse>(json);

        // Assert
        Assert.NotNull(response);
        Assert.Equal(8, response.End);
        Assert.Equal("PERSON", response.EntityType);
        Assert.Equal(0, response.Start);
        Assert.Equal(0.99, response.Score);
        Assert.Null(response.AnalysisExplanation);
    }
    
    [Fact]
    public void DeserializationShouldHandleMissingAnalysisExplanation()
    {
        // Arrange
        const string json = """
                            {
                                "end": 15,
                                "entity_type": "PHONE_NUMBER",
                                "start": 3,
                                "score": 0.75
                            }
                            """;

        // Act
        var response = JsonSerializer.Deserialize<PresidioAnalyzerResponse>(json);

        // Assert
        Assert.NotNull(response);
        Assert.Equal(15, response.End);
        Assert.Equal("PHONE_NUMBER", response.EntityType);
        Assert.Equal(3, response.Start);
        Assert.Equal(0.75, response.Score);
        Assert.Null(response.AnalysisExplanation);
    }
    
    [Theory]
    [MemberData(nameof(PresidioAnalyzerResponseRecordTestData))]
    public void DeserializationShouldSetRequiredPropertiesCorrectly(
        int    end,
        string entityType,
        int    start,
        double score)
    {
        // Arrange
        var json = $$"""
                      {
                          "analysis_explanation": null,
                          "end": {{end}},
                          "entity_type": "{{entityType}}",
                          "start": {{start}},
                          "score": {{score.ToString(System.Globalization.CultureInfo.InvariantCulture)}}
                      }
                      """;

        // Act
        var response = JsonSerializer.Deserialize<PresidioAnalyzerResponse>(json);

        // Assert
        Assert.NotNull(response);
        Assert.Equal(end, response.End);
        Assert.Equal(entityType, response.EntityType);
        Assert.Equal(start, response.Start);
        Assert.Equal(score, response.Score);
        Assert.Null(response.AnalysisExplanation);
    }
}