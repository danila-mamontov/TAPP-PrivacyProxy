using PrivacyProxy.Api.Services;

namespace PrivacyProxy.Api.Tests;

public class StreamingDeanonymizerTests
{
    private static (StreamingDeanonymizer sut, MappingStore mappingStore) CreateSut()
    {
        var store = new MappingStore();
        return (new StreamingDeanonymizer(store), store);
    }

    [Fact]
    public void ProcessFragmentNoPlaceholderPassesThroughImmediately()
    {
        // Arrange
        var (sut, _) = CreateSut();
        
        // Act
        var result   = sut.ProcessFragment("Hallo Welt", false);
        
        // Assert
        Assert.Equal("Hallo Welt", result);
    }

    [Fact]
    public void ProcessFragmentCompletePlaceholderIsResolvedImmediately()
    {
        // Arrange
        var (sut, store) = CreateSut();
        var placeholder  = store.GetOrCreatePlaceholder("PERSON", "Alice");

        // Act
        var result = sut.ProcessFragment(placeholder, true);
        
        // Assert
        Assert.Equal("Alice", result);
    }

    [Fact]
    public void ProcessFragmentPlaceholderSplitAcrossChunksIsResolvedCorrectly()
    {
        // Arrange
        var (sut, store) = CreateSut();
        var placeholder  = store.GetOrCreatePlaceholder("PERSON", "Alice");

        var mid    = placeholder.Length / 2;
        var first  = placeholder[..mid];
        var second = placeholder[mid..];

        // Act
        var r1 = sut.ProcessFragment(first,  false);
        var r2 = sut.ProcessFragment(second, false);

        // Assert
        Assert.Equal("",      r1);
        Assert.Equal("Alice", r2);
    }

    [Fact]
    public void ProcessFragmentIsFinalFlushesIncompleteBuffer()
    {
        // Arrange
        var (sut, _) = CreateSut();

        var firstOutput = sut.ProcessFragment("some text [INCOMPLETE", false);
        Assert.Equal("some text ", firstOutput);

        // Act
        var result = sut.ProcessFragment("", true);

        // Assert
        Assert.Equal("[INCOMPLETE", result);
    }

    [Fact]
    public void ProcessFragmentMultipleContextKeysWorkIndependently()
    {
        // Arrange
        var (sut, store) = CreateSut();
        var p1 = store.GetOrCreatePlaceholder("PERSON",   "Alice");
        var p2 = store.GetOrCreatePlaceholder("LOCATION", "Berlin");

        var mid1 = p1.Length / 2;
        var mid2 = p2.Length / 2;

        // Act
        sut.ProcessFragment(p1[..mid1], false);
        sut.ProcessFragment(p2[..mid2], false, "toolcall_0");

        var r1 = sut.ProcessFragment(p1[mid1..], false);
        var r2 = sut.ProcessFragment(p2[mid2..], false, "toolcall_0");

        // Assert
        Assert.Equal("Alice",  r1);
        Assert.Equal("Berlin", r2);
    }

    [Fact]
    public void ProcessFragmentInvalidPlaceholderFormatIsPassedThrough()
    {
        // Arrange
        var (sut, _) = CreateSut();
        
        // Act
        var result   = sut.ProcessFragment("[INVALID_FORMAT]", false);
        
        // Assert
        Assert.Equal("[INVALID_FORMAT]", result);
    }

    [Fact]
    public void FlushAllReturnsPendingBuffersAndClears()
    {
        // Arrange
        var (sut, store) = CreateSut();
        var placeholder  = store.GetOrCreatePlaceholder("PERSON", "Alice");

        var mid       = placeholder.Length / 2;
        var firstHalf = placeholder[..mid];
        sut.ProcessFragment(firstHalf, false);

        // Act
        var flushed = sut.FlushAll();

        // Assert
        Assert.True(flushed.ContainsKey("content"));
        Assert.Equal(firstHalf, flushed["content"]);

        var flushedAgain = sut.FlushAll();
        Assert.Empty(flushedAgain);
    }

    [Fact]
    public void FlushAllEmptyBuffersReturnsEmptyDictionary()
    {
        // Arrange
        var (sut, _) = CreateSut();
        
        // Act
        var result   = sut.FlushAll();
        
        // Assert
        Assert.Empty(result);
    }
    
    [Fact]
    public void ProcessFragment_CompletePlaceholderFollowedByOpenOne_BuffersTail()
    {
        // Arrange
        var (sut, store) = CreateSut();
        var p1 = store.GetOrCreatePlaceholder("PERSON",   "Alice");
        var p2 = store.GetOrCreatePlaceholder("LOCATION", "Berlin");

        var fragment = p1 + " und " + p2[..6]; // e.g. "[LOCA"

        // Act & Assert
        var result = sut.ProcessFragment(fragment, false);
        Assert.Equal("Alice und ", result);

        var result2 = sut.ProcessFragment(p2[6..] + " wohnt", false);
        Assert.Equal("Berlin wohnt", result2);
    }
}