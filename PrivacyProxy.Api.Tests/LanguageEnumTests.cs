using System.Text.Json;
using System.Text.Json.Serialization;
using PrivacyProxy.Api.Models.Enums;

namespace PrivacyProxy.Api.Tests;

public class LanguageEnumTests
{
    // Needed for serialization options to work so that automatically
    // serialized enums are converted to strings and not numbers e.g., 0 or 1.
    private readonly JsonSerializerOptions _options;

    private record TestWrapperRecord
    {
        public Language Language { get; set; }
    }
    
    private record TestWrapperClass
    {
        public Language Language { get; set; }
    }
    
    public LanguageEnumTests()
    {
        _options = new JsonSerializerOptions();
        _options.Converters.Add(new JsonStringEnumConverter());
    }

    [Theory]
    [InlineData(Language.English, "\"en\"")]
    [InlineData(Language.German, "\"de\"")]
    public void LanguageShouldSerializeToCorrectString(Language language, string expectedString)
    {
        // Act
        var result = JsonSerializer.Serialize(language, _options);
        
        // Assert
        Assert.Equal(expectedString, result);
    }
    
    [Theory]
    [InlineData(Language.English, "\"en\"")]
    [InlineData(Language.German, "\"de\"")]
    public void LanguageWithinRecordShouldSerializeCorrectly(Language language, string expectedString)
    {
        // Arrange
        var model = new TestWrapperRecord { Language = language };

        // Act
        var json = JsonSerializer.Serialize(model, _options);

        // Assert
        Assert.NotNull(json);
        Assert.NotEmpty(json);
        Assert.Contains($"\"Language\":{expectedString}", json);
    }
    
    [Theory]
    [InlineData(Language.English, "\"en\"")]
    [InlineData(Language.German, "\"de\"")]
    public void LanguageWithinClassShouldSerializeCorrectly(Language language, string expectedString)
    {
        // Arrange
        var model = new TestWrapperClass { Language = language };

        // Act
        var json = JsonSerializer.Serialize(model, _options);

        // Assert
        Assert.NotNull(json);
        Assert.NotEmpty(json);
        Assert.Contains($"\"Language\":{expectedString}", json);
    }
}