using Microsoft.Extensions.Options;
using PrivacyProxy.Api.Configuration;
using PrivacyProxy.Core.Configuration;
using PrivacyProxy.Api.Services;

namespace PrivacyProxy.Api.Tests;

public class MappingStoreTests
{
    private static MappingStore CreateSut() => new();
    
    [Fact]
    public void GetOrCreatePlaceholderSameInputReturnsSamePlaceholder()
    {
        // Arrange
        var sut = CreateSut();
        
        // Act
        var p1  = sut.GetOrCreatePlaceholder("PERSON", "Alice");
        var p2  = sut.GetOrCreatePlaceholder("PERSON", "Alice");
        
        // Assert
        Assert.Equal(p1, p2);
    }
    
    [Fact]
    public void GetOrCreatePlaceholderDifferentInputsReturnsDifferentPlaceholders()
    {
        // Arrange
        var sut = CreateSut();
        
        // Act
        var p1  = sut.GetOrCreatePlaceholder("PERSON", "Alice");
        var p2  = sut.GetOrCreatePlaceholder("PERSON", "Bob");
        
        // Assert
        Assert.NotEqual(p1, p2);
    }
    
    [Fact]
    public void GetOrCreatePlaceholderPlaceholderFormatIsCorrect()
    {
        // Arrange
        var sut = CreateSut();
        
        // Act
        var placeholder = sut.GetOrCreatePlaceholder("PERSON", "Alice");
        
        // Assert
        Assert.Matches(@"^\[PERSON_[0-9a-f]{16}\]$", placeholder);
    }
    
    [Fact]
    public void GetOrCreatePlaceholderSameValueDifferentTypeReturnsSamePlaceholder()
    {
        // Arrange
        var sut = CreateSut();
        
        // Act
        var p1  = sut.GetOrCreatePlaceholder("PERSON",   "Alice");
        var p2  = sut.GetOrCreatePlaceholder("LOCATION", "Alice");
        
        // Assert
        Assert.Equal(p1, p2);
    }
    
    [Fact]
    public void PlaceholderCountIncrementsOnNewEntry()
    {
        // Arrange
        var sut = CreateSut();
        
        // Act & Assert
        Assert.Equal(0, sut.PlaceholderCount);
        
        sut.GetOrCreatePlaceholder("PERSON", "Alice");
        Assert.Equal(1, sut.PlaceholderCount);
        
        sut.GetOrCreatePlaceholder("PERSON", "Bob");
        Assert.Equal(2, sut.PlaceholderCount);
    }

    [Fact]
    public void PlaceholderCountDoesNotIncrementOnDuplicate()
    {
        // Arrange
        var sut = CreateSut();
        
        // Act
        sut.GetOrCreatePlaceholder("PERSON", "Alice");
        sut.GetOrCreatePlaceholder("PERSON", "Alice");
        
        // Assert
        Assert.Equal(1, sut.PlaceholderCount);
    }

    [Fact]
    public void GetOrCreatePlaceholder_CaseInsensitive_SameNormalizedValueSharesPlaceholder()
    {
        // Arrange
        var sut = CreateSut();

        // Act — different casing + surrounding whitespace must collapse to ONE placeholder
        var p1 = sut.GetOrCreatePlaceholder("LOCATION", "Berlin");
        var p2 = sut.GetOrCreatePlaceholder("LOCATION", "berlin");
        var p3 = sut.GetOrCreatePlaceholder("LOCATION", "  BERLIN  ");

        // Assert
        Assert.Equal(p1, p2);
        Assert.Equal(p1, p3);
        Assert.Equal(1, sut.PlaceholderCount);
    }

    [Fact]
    public void Deanonymize_RestoresFirstSeenOriginalCasing()
    {
        // Arrange
        var sut = CreateSut();

        // Act — "Berlin" is seen first, then a lowercase variant reuses the same placeholder
        var placeholder = sut.GetOrCreatePlaceholder("LOCATION", "Berlin");
        sut.GetOrCreatePlaceholder("LOCATION", "berlin");

        // Assert — restoration uses the first-seen casing
        Assert.Equal("Berlin", sut.Deanonymize(placeholder));
    }

    [Fact]
    public void DeanonymizeKnownPlaceholderIsReplaced()
    {
        // Arrange
        var sut         = CreateSut();
        
        // Act
        var placeholder = sut.GetOrCreatePlaceholder("PERSON", "Alice");
        var result      = sut.Deanonymize($"Hallo {placeholder}!");
        
        // Assert
        Assert.Equal("Hallo Alice!", result);
    }

    [Fact]
    public void DeanonymizeUnknownPlaceholderIsLeftUnchanged()
    {
        // Arrange & Act
        var result = CreateSut().Deanonymize("Hallo [PERSON_0000000000000000]!");
        
        // Assert
        Assert.Equal("Hallo [PERSON_0000000000000000]!", result);
    }

    [Fact]
    public void DeanonymizeMultiplePlaceholdersAllReplaced()
    {
        // Arrange
        var sut  = CreateSut();
        
        // Act
        var pp1  = sut.GetOrCreatePlaceholder("PERSON",   "Alice");
        var pp2  = sut.GetOrCreatePlaceholder("LOCATION", "Berlin");
        var result = sut.Deanonymize($"{pp1} wohnt in {pp2}.");
        
        //Assert
        Assert.Equal("Alice wohnt in Berlin.", result);
    }

    [Fact]
    public void DeanonymizeEmptyStringReturnsEmpty()
    {
        // Arrange & Act & Assert
        Assert.Equal("", CreateSut().Deanonymize(""));
    }

    [Fact]
    public void GetOrCreatePlaceholderEmptyEntityTypeThrows()
    {
        // Arrange & Act & Assert
        Assert.Throws<ArgumentException>(
            () => CreateSut().GetOrCreatePlaceholder("", "Alice"));
    }

    [Fact]
    public void GetOrCreatePlaceholderNullOriginalThrows()
    {
        // Arrange & Act & Assert
        Assert.Throws<ArgumentNullException>(
            () => CreateSut().GetOrCreatePlaceholder("PERSON", null!));
    }

    [Fact]
    public void ParameterlessConstructorUsesDefaultTtl()
    {
        // Arrange
        var sut = new MappingStore();

        // Act
        var placeholder = sut.GetOrCreatePlaceholder("PERSON", "Alice");
        var result      = sut.Deanonymize(placeholder);

        // Assert - default TTL (30 minutes) keeps the mapping alive
        Assert.Equal("Alice", result);
    }

    [Fact]
    public void Dispose_DoesNotThrow()
    {
        // Arrange
        var sut = new MappingStore();
        sut.GetOrCreatePlaceholder("PERSON", "Alice");

        // Act & Assert
        sut.Dispose();
    }

    [Fact]
    public async Task EntriesExpireAfterConfiguredTtl()
    {
        // Arrange - a very short TTL so the mapping expires almost immediately
        var options = Options.Create(new MappingOptions { TtlMinutes = 0.0005 }); // ~30ms
        using var sut = new MappingStore(options);

        // Act
        var placeholder = sut.GetOrCreatePlaceholder("PERSON", "Alice");
        await Task.Delay(TimeSpan.FromMilliseconds(200));
        var result = sut.Deanonymize(placeholder);

        // Assert - the mapping has expired, so the placeholder is left unresolved
        Assert.Equal(placeholder, result);
        Assert.Equal(0, sut.PlaceholderCount);
    }

    [Fact]
    public async Task AccessWithinTtlKeepsMappingAlive()
    {
        // Arrange - a short TTL that gets refreshed by repeated access (sliding expiration)
        var options = Options.Create(new MappingOptions { TtlMinutes = 0.01 }); // ~600ms
        using var sut = new MappingStore(options);

        // Act
        var placeholder = sut.GetOrCreatePlaceholder("PERSON", "Alice");

        // Repeatedly access the mapping, each time well within the TTL window,
        // for longer than the configured TTL in total.
        for (var i = 0; i < 5; i++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100));
            Assert.Equal("Alice", sut.Deanonymize(placeholder));
        }
    }
}