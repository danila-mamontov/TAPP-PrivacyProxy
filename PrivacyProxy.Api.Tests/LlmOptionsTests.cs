using PrivacyProxy.Api.Configuration;

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
}