using System.Text.Json;
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
    public void GetOrCreatePlaceholder_CaseSensitive_DifferentCasingGetsDifferentPlaceholders()
    {
        // Arrange
        var sut = CreateSut();

        // Act — matching is exact, so different casing/whitespace are distinct values
        var p1 = sut.GetOrCreatePlaceholder("LOCATION", "Berlin");
        var p2 = sut.GetOrCreatePlaceholder("LOCATION", "berlin");
        var p3 = sut.GetOrCreatePlaceholder("LOCATION", "  BERLIN  ");

        // Assert
        Assert.NotEqual(p1, p2);
        Assert.NotEqual(p1, p3);
        Assert.NotEqual(p2, p3);
        Assert.Equal(3, sut.PlaceholderCount);
    }

    [Fact]
    public void Deanonymize_RestoresExactOriginalCasing()
    {
        // Arrange
        var sut = CreateSut();

        // Act — each casing variant keeps its own placeholder and its own original value
        var upper = sut.GetOrCreatePlaceholder("LOCATION", "Berlin");
        var lower = sut.GetOrCreatePlaceholder("LOCATION", "berlin");

        // Assert — restoration returns each exact original
        Assert.Equal("Berlin", sut.Deanonymize(upper));
        Assert.Equal("berlin", sut.Deanonymize(lower));
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
    public void DeanonymizeJsonValueWithQuoteKeepsJsonValid()
    {
        // Arrange: a value like a body height 6' 4" contains a double quote that would
        // terminate the surrounding JSON string if inserted unescaped.
        var sut         = CreateSut();
        var placeholder = sut.GetOrCreatePlaceholder("HEIGHT", "6' 4\"");
        var json        = $"{{\"pii_text\": \"my height is {placeholder} thanks\"}}";

        // Act
        var deanonymized = sut.DeanonymizeJson(json);

        // Assert: still parseable JSON, and the decoded value is the original again
        var parsed = JsonSerializer.Deserialize<JsonElement>(deanonymized);
        Assert.Equal("my height is 6' 4\" thanks", parsed.GetProperty("pii_text").GetString());
    }

    [Fact]
    public void DeanonymizeJsonValueWithBackslashAndNewlineKeepsJsonValid()
    {
        // Arrange
        var sut         = CreateSut();
        var placeholder = sut.GetOrCreatePlaceholder("PATH", "C:\\Users\\alice\nline2");
        var json        = $"{{\"pii_text\": \"{placeholder}\"}}";

        // Act
        var deanonymized = sut.DeanonymizeJson(json);

        // Assert
        var parsed = JsonSerializer.Deserialize<JsonElement>(deanonymized);
        Assert.Equal("C:\\Users\\alice\nline2", parsed.GetProperty("pii_text").GetString());
    }

    [Fact]
    public void DeanonymizeJsonUnknownPlaceholderIsLeftUnchanged()
    {
        // Arrange
        var sut  = CreateSut();
        var json = "{\"pii_text\": \"[PERSON_0123456789abcdef]\"}";

        // Act & Assert
        Assert.Equal(json, sut.DeanonymizeJson(json));
    }

}