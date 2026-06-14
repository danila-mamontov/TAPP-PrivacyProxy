using PrivacyProxy.Api.Configuration;

namespace PrivacyProxy.Api.Tests;

public class MappingOptionsTests
{
    private readonly MappingOptionsValidator _mappingOptionsValidator = new();

    [Fact]
    public void Default_options_succeed()
    {
        // Arrange
        var options = new MappingOptions();

        // Act
        var result = _mappingOptionsValidator.Validate(null, options);

        // Assert
        Assert.True(result.Succeeded);
        Assert.Equal(30, options.TtlMinutes);
    }

    [Fact]
    public void Positive_ttl_succeeds()
    {
        // Arrange
        var options = new MappingOptions { TtlMinutes = 5 };

        // Act
        var result = _mappingOptionsValidator.Validate(null, options);

        // Assert
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Zero_ttl_fails()
    {
        // Arrange
        var options = new MappingOptions { TtlMinutes = 0 };

        // Act
        var result = _mappingOptionsValidator.Validate(null, options);

        // Assert
        Assert.True(result.Failed);
        Assert.Contains("TtlMinutes", result.FailureMessage);
    }

    [Fact]
    public void Negative_ttl_fails()
    {
        // Arrange
        var options = new MappingOptions { TtlMinutes = -1 };

        // Act
        var result = _mappingOptionsValidator.Validate(null, options);

        // Assert
        Assert.True(result.Failed);
        Assert.Contains("TtlMinutes", result.FailureMessage);
    }
}
