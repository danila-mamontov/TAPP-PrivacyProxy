using Microsoft.AspNetCore.Http;
using PrivacyProxy.Api.Configuration;
using PrivacyProxy.Core.Configuration;

namespace PrivacyProxy.Api.Tests;

public class LlmOptionsTests
{
    private readonly LlmOptionsValidator _llmOptionsValidator = new();
    
    [Fact]
    public void Valid_options_succeed()
    {
        // Arrange
        var options = new LlmOptions { BaseUrl = "http://localhost:5002", ApiKey = "dummy", Model = "dummy"};
        
        // Act
        var result  = _llmOptionsValidator.Validate(null, options);
        
        // Assert
        Assert.True(result.Succeeded);
    }
    
    [Fact]
    public void Missing_BaseUrl_fails()
    {
        // Arrange
        var options = new LlmOptions { BaseUrl = "", ApiKey = "dummy", Model = "dummy"};
        
        // Act
        var result  = _llmOptionsValidator.Validate(null, options);
        
        // Assert
        Assert.True(result.Failed);
        Assert.Contains("BaseUrl", result.FailureMessage);
    }
    
    [Fact]
    public void Missing_ApiKey_fails()
    {
        // Arrange
        var options = new LlmOptions { BaseUrl = "URL", ApiKey = "", Model = "dummy"};
        
        // Act
        var result  = _llmOptionsValidator.Validate(null, options);
        
        // Assert
        Assert.True(result.Failed);
        Assert.Contains("ApiKey", result.FailureMessage);
    }

    [Fact]
    public void MissingModelFails()
    {
        // Arrange
        var options = new LlmOptions { BaseUrl = "URL", ApiKey = "dummy", Model = "" };

        // Act
        var result  = _llmOptionsValidator.Validate(null, options);

        //Assert
        Assert.True(result.Failed);
        Assert.Contains("Model", result.FailureMessage);
    }

    [Fact]
    public void Reachable_endpoint_still_succeeds()
    {
        // Arrange - a local server that responds with 200 OK exercises the
        // "model reachable" logging branch of ModelIsReachable.
        using var server  = LocalHttpServer.StartSingleResponse(StatusCodes.Status200OK);
        var       options = new LlmOptions { BaseUrl = server.BaseUrl, ApiKey = "dummy", Model = "dummy" };

        // Act
        var result = _llmOptionsValidator.Validate(null, options);

        // Assert
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Unreachable_endpoint_still_succeeds()
    {
        // Arrange - nothing is listening on this port, so ModelIsReachable hits
        // its catch block and logs the connectivity failure without failing validation.
        var options = new LlmOptions { BaseUrl = "http://localhost:1/", ApiKey = "dummy", Model = "dummy" };

        // Act
        var result = _llmOptionsValidator.Validate(null, options);

        // Assert
        Assert.True(result.Succeeded);
    }
}