using Microsoft.Extensions.Options;
using PrivacyProxy.Api.Configuration;
using PrivacyProxy.Core.Configuration;
using PrivacyProxy.Api.Models.DTOs.Presidio;
using PrivacyProxy.Api.Models.Enums;
using PrivacyProxy.Api.Services;

namespace PrivacyProxy.Api.Tests;

public class DefaultEntityPolicyTests
{
    private static DefaultEntityPolicy CreateSut(
        Dictionary<string, double>? germanThresholds  = null,
        Dictionary<string, double>? englishThresholds = null)
    {
        var options = new PresidioOptions
                      {
                          AnalyzerUrl              = "http://localhost:5002",
                          GermanEntityThresholds   = germanThresholds ?? new Dictionary<string, double>
                                                                         {
                                                                             { "PERSON",       0.85 },
                                                                             { "LOCATION",     0.90 },
                                                                             { "ORGANIZATION", 0.85 }
                                                                         },
                          EnglishEntityThresholds  = englishThresholds ?? new Dictionary<string, double>
                                                                          {
                                                                              { "EMAIL_ADDRESS", 0.4 },
                                                                              { "PHONE_NUMBER",  0.4 }
                                                                          }
                      };

        return new DefaultEntityPolicy(new StaticOptionsMonitor<PresidioOptions>(options));
    }
    
    [Fact]
    public void ApplyEmptyListReturnsEmpty()
    {
        // Arrange & Act
        var result = CreateSut().Apply([], Language.German, "some text");
        
        // Assert
        Assert.Empty(result);
    }
    
    [Fact]
    public void ApplyGermanEntityAboveThresholdIsRetained()
    {
        // Arrange
        var entity = new PresidioAnalyzerResponse
                     {
                         EntityType = "PERSON",
                         Score      = 0.92,
                         Start      = 0,
                         End        = 5
                     };
        
        // Act
        var result = CreateSut().Apply([entity], Language.German, "Alice war hier");
        
        // Assert
        Assert.Single(result);
    }
    
    [Fact]
    public void ApplyGermanEntityAtExactThresholdIsRetained()
    {
        // Arrange
        var entity = new PresidioAnalyzerResponse
                     {
                         EntityType = "PERSON",
                         Score      = 0.85,
                         Start      = 0,
                         End        = 5
                     };
        
        // Act
        var result = CreateSut().Apply([entity], Language.German, "Alice war hier");
        
        // Assert
        Assert.Single(result);
    }
    
    [Fact]
    public void ApplyGermanEntityBelowThresholdIsFiltered()
    {
        // Arrange
        var entity = new PresidioAnalyzerResponse
                     {
                         EntityType = "PERSON",
                         Score      = 0.80,
                         Start      = 0,
                         End        = 5
                     };
        
        // Act
        var result = CreateSut().Apply([entity], Language.German, "Alice war hier");
        
        // Assert
        Assert.Empty(result);
    }
    
    [Fact]
    public void ApplyGermanLocationBelowStricterThresholdIsFiltered()
    {
        // Arrange
        var entity = new PresidioAnalyzerResponse
                     {
                         EntityType = "LOCATION",
                         Score      = 0.87,
                         Start      = 0,
                         End        = 6
                     };

        // Act
        var result = CreateSut().Apply([entity], Language.German, "Berlin ist schön");
        
        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public void ApplyEnglishEntityAboveThresholdIsRetained()
    {
        // Arrange
        var entity = new PresidioAnalyzerResponse
                     {
                         EntityType = "EMAIL_ADDRESS",
                         Score      = 0.6,
                         Start      = 0,
                         End        = 20
                     };
        
        // Act
        var result = CreateSut().Apply([entity], Language.English, "test@example.com");
        
        // Assert
        Assert.Single(result);
    }

    [Fact]
    public void ApplyEnglishEntityBelowThresholdIsFiltered()
    {
        // Arrange
        var entity = new PresidioAnalyzerResponse
                     {
                         EntityType = "EMAIL_ADDRESS",
                         Score      = 0.3,
                         Start      = 0,
                         End        = 20
                     };
        
        // Act
        var result = CreateSut().Apply([entity], Language.English, "test@example.com");
        
        // Assert
        Assert.Empty(result);
    }
    
    [Fact]
    public void ApplyUnknownEntityTypeIsFiltered()
    {
        // Arrange
        var entity = new PresidioAnalyzerResponse
                     {
                         EntityType = "US_DRIVER_LICENSE",
                         Score      = 0.99,
                         Start      = 0,
                         End        = 10
                     };
        
        // Act
        var result = CreateSut().Apply([entity], Language.German, "AC432223");
        
        // Assert
        Assert.Empty(result);
    }
    
    [Fact]
    public void GermanThresholdsAppliedForGermanNotEnglish()
    {
        // Arrange
        var germanThresholds  = new Dictionary<string, double> { { "PERSON", 0.90 } };
        var englishThresholds = new Dictionary<string, double> { { "PERSON", 0.50 } };
        var sut               = CreateSut(germanThresholds, englishThresholds);

        var entity = new PresidioAnalyzerResponse
                     {
                        EntityType = "PERSON",
                        Score      = 0.7,
                        Start      = 0,
                        End        = 5
                     };
        
        // Act
        var germanResult  = sut.Apply([entity], Language.German,  "Alice");
        var englishResult = sut.Apply([entity], Language.English, "Alice");

        // Assert
        Assert.Empty(germanResult);
        Assert.Single(englishResult);
    }
    
    [Fact]
    public void ApplyNonOverlappingEntitiesAllRetained()
    {
        // Arrange
        var entities = new[]
                       {
                           new PresidioAnalyzerResponse { EntityType = "PERSON", Score = 0.90, Start = 0, End = 5 },
                           new PresidioAnalyzerResponse { EntityType = "LOCATION", Score = 0.92, Start = 10, End = 16 }
                       };

        // Act
        var result = CreateSut().Apply(entities, Language.German, "Alice wohnt Berlin");
        
        // Assert
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void ApplyOverlappingEntitiesHigherScoreWins()
    {
        // Arrange
        var entities = new[]
                       {
                           new PresidioAnalyzerResponse { EntityType = "PERSON", Score   = 0.88, Start = 0, End  = 10 },
                           new PresidioAnalyzerResponse { EntityType = "PERSON", Score = 0.92, Start = 0, End = 10 }
                       };

        // Act
        var result = CreateSut().Apply(entities, Language.German, "John Smith");
        
        // Assert
        Assert.Single(result);
        Assert.Equal(0.92, result[0].Score);
    }

    [Fact]
    public void ApplyPartialOverlapHigherScoreWins()
    {
        // Arrange
        var entities = new[]
                       {
                           new PresidioAnalyzerResponse { EntityType = "PERSON", Score = 0.92, Start = 0, End = 10 },
                           new PresidioAnalyzerResponse { EntityType = "PERSON", Score = 0.85, Start = 0, End = 4 }
                       };

        // Act
        var result = CreateSut().Apply(entities, Language.German, "John Smith");
        
        // Assert
        Assert.Single(result);
        Assert.Equal(0.92, result[0].Score);
    }

    [Fact]
    public void ApplyOverlappingEntitiesLowerScoreIsDiscarded()
    {
        // Arrange
        var entities = new[]
                       {
                           new PresidioAnalyzerResponse { EntityType = "PERSON", Score = 0.92, Start = 0, End = 10 },
                           new PresidioAnalyzerResponse { EntityType = "LOCATION", Score = 0.88, Start = 5, End = 15 }
                       };

        // Act
        var result = CreateSut().Apply(entities, Language.German, "John Smith Berlin");
        
        // Assert
        Assert.Single(result);
        Assert.Equal("PERSON", result[0].EntityType);
    }
}