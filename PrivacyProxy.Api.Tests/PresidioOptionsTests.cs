using Microsoft.AspNetCore.Http;
using PrivacyProxy.Api.Configuration;
using PrivacyProxy.Core.Configuration;

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

    [Fact]
    public void Reachable_analyzer_with_success_status_succeeds()
    {
        // Arrange - a local server that responds with 200 OK on /health exercises the
        // "Presidio reachable" (Information log) branch of PresidioIsReachable.
        using var server  = LocalHttpServer.StartSingleResponse(StatusCodes.Status200OK);
        var       options = new PresidioOptions { AnalyzerUrl = server.BaseUrl };

        // Act
        var result = _presidioOptionsValidator.Validate(null, options);

        // Assert
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Reachable_analyzer_with_non_success_status_still_succeeds()
    {
        // Arrange - a local server that responds with 503 on /health exercises the
        // "non-success status" (Warning log) branch of PresidioIsReachable.
        using var server  = LocalHttpServer.StartSingleResponse(StatusCodes.Status503ServiceUnavailable);
        var       options = new PresidioOptions { AnalyzerUrl = server.BaseUrl };

        // Act
        var result = _presidioOptionsValidator.Validate(null, options);

        // Assert
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Unreachable_analyzer_still_succeeds()
    {
        // Arrange - nothing is listening on this port, so PresidioIsReachable hits
        // its catch block and logs the connectivity failure without failing validation.
        var options = new PresidioOptions { AnalyzerUrl = "http://localhost:1/" };

        // Act
        var result = _presidioOptionsValidator.Validate(null, options);

        // Assert
        Assert.True(result.Succeeded);
    }
}