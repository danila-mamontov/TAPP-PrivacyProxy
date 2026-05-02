using System.Text.Json;
using System.Text.Json.Serialization;
using PrivacyProxy.Api.Models.DTOs.Presidio;
using PrivacyProxy.Api.Models.Enums;

namespace PrivacyProxy.Api.Tests;

public class PresidioAnalyzerRequestRecordTests
{
    private readonly JsonSerializerOptions _options;

    public PresidioAnalyzerRequestRecordTests()
    {
        _options = new JsonSerializerOptions();
        _options.Converters.Add(new JsonStringEnumConverter());
        _options.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    }
    
    public static TheoryData<string, Language, string[], double> PresidioAnalyzerRequestRecordTestData() =>
        new()
        {
            { "Hello world",                          Language.English, ["PERSON", "LOCATION"],                   0.5 },
            { "Hallo Welt",                           Language.German,  ["PERSON"],                               0.8 },
            { "Some text",                            Language.English, [],                                       0.0 },
            { "My name is John and I live in Paris",  Language.English, ["PERSON", "LOCATION"],                   0.7 },
            { "Mein Name ist Hans",                   Language.German,  ["PERSON"],                               0.6 },
            { "Call me at 555-1234",                  Language.English, ["PHONE_NUMBER"],                         0.9 },
            { "My email is foo@bar.com",              Language.English, ["EMAIL_ADDRESS"],                        0.85 },
            { "IBAN: DE89 3704 0044 0532 0130 00",    Language.German,  ["IBAN_CODE"],                            0.95 },
            { "Born on 01.01.1990",                   Language.German,  ["DATE_TIME"],                            0.5 },
            { "IP address is 192.168.0.1",            Language.English, ["IP_ADDRESS"],                           1.0 },
            { "Visit https://example.com for info",   Language.English, ["URL"],                                  0.75 },
            { "",                                     Language.English, [],                                       0.0 },
            { "No entities expected here",            Language.English, ["PERSON", "LOCATION", "PHONE_NUMBER"],   0.3 },
        };
    
    [Theory]
    [MemberData(nameof(PresidioAnalyzerRequestRecordTestData))]
    public void ConstructorShouldSetPropertiesCorrectly(
        string   text,
        Language language,
        string[] entities,
        double   scoreThreshold)
    {
        // Act
        var request = new PresidioAnalyzerRequest
                      {
                          Text           = text,
                          Language       = language,
                          Entities       = entities,
                          ScoreThreshold = scoreThreshold
                      };

        // Assert
        Assert.NotNull(request);
        Assert.IsType<PresidioAnalyzerRequest>(request);
        Assert.Equal(text, request.Text);
        Assert.Equal(language, request.Language);
        Assert.Equal(entities, request.Entities);
        Assert.Equal(scoreThreshold, request.ScoreThreshold);
    }
    
    [Fact]
    public void SerializationShouldRespectJsonPropertyNames()
    {
        // Arrange
        var request = new PresidioAnalyzerRequest
                      {
                          Text                  = "Sensitive info",
                          Language              = Language.German,
                          ReturnDecisionProcess = true,
                          AllowList             = ["dennis.itzel@uni-ulm.de", "Dennis Itzel"],
                          Context               = ["Von:", "An:", "Mein Name ist"],
                          Entities              = ["PERSON", "LOCATION"],
                          ScoreThreshold        = 0.85
                      };

        // Act
        var json = JsonSerializer.Serialize(request, _options);

        // Assert
        Assert.NotNull(json);
        Assert.NotEmpty(json);
        Assert.Contains("\"text\":\"Sensitive info\"", json);
        Assert.Contains("\"language\":\"de\"", json);
        Assert.Contains("\"return_decision_process\":true", json);
        Assert.Contains("\"allow_list\":[\"dennis.itzel@uni-ulm.de\",\"Dennis Itzel\"]", json);
        Assert.Contains("\"context\":[\"Von:\",\"An:\",\"Mein Name ist\"]", json);
        Assert.Contains("\"entities\":[\"PERSON\",\"LOCATION\"]", json);
        Assert.Contains("\"score_threshold\":0.85", json);
    }
    
    [Fact]
    public void EqualityTwoRecordsWithSameDataShouldBeEqual()
    {
        // Arrange & Act
        var req1 = new PresidioAnalyzerRequest { Text = "Test", Language = Language.German };
        var req2 = new PresidioAnalyzerRequest { Text = "Test", Language = Language.German };

        // Assert
        Assert.Equal(req1, req2);
    }
}