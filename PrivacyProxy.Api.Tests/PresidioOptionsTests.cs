using PrivacyProxy.Api.Configuration;

namespace PrivacyProxy.Api.Tests;

public class PresidioOptionsTests
{
    private readonly PresidioOptionsValidator _presidioOptionsValidator = new();

    [Fact]
    public void Valid_options_succeed()
    {
        // Arrange
        var options = new PresidioOptions { AnalyzerUrl = "http://localhost:5002" };
        
        // Act
        var result  = _presidioOptionsValidator.Validate(null, options);
        
        // Assert
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Missing_AnalyzerUrl_fails()
    {
        // Arrange
        var options = new PresidioOptions { AnalyzerUrl = "" };
        
        // Act
        var result  = _presidioOptionsValidator.Validate(null, options);
        
        // Assert
        Assert.True(result.Failed);
        Assert.Contains("AnalyzerUrl", result.FailureMessage);
    }

    [Fact]
    public void ScoreThreshold_above_min_threshold_fails()
    {
        // Arrange
        var options = new PresidioOptions
                      {
                          AnalyzerUrl            = "http://localhost:5002",
                          ScoreThreshold         = 0.95,
                          GermanEntityThresholds = new Dictionary<string, double> { { "PERSON", 0.85 } }
                      };
        
        // Act
        var result = _presidioOptionsValidator.Validate(null, options);
        
        // Assert
        Assert.True(result.Failed);
        Assert.Contains("ScoreThreshold", result.FailureMessage);
    }
}