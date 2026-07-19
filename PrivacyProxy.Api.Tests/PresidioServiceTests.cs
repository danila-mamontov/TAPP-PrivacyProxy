using Moq;
using PrivacyProxy.Core.Configuration;
using PrivacyProxy.Api.Models.DTOs.Presidio;
using PrivacyProxy.Api.Models.Enums;
using PrivacyProxy.Api.Models.Interfaces;
using PrivacyProxy.Api.Services;

namespace PrivacyProxy.Api.Tests;

public class PresidioServiceTests
{
    private static (PresidioService sut, 
                    Mock<IPresidioAnalyzerClient> analyzer,
                    Mock<IEntityPolicy> policy,
                    Mock<IMappingStore> store) CreateSut()
    {
        var analyzer = new Mock<IPresidioAnalyzerClient>();
        var policy   = new Mock<IEntityPolicy>();
        var store    = new Mock<IMappingStore>();

        analyzer.Setup(a => 
                           a.AnalyzeAsync(It.IsAny<string>(), 
                                          It.IsAny<Language>(), 
                                          It.IsAny<CancellationToken>())
                           )
                .ReturnsAsync([]);

        policy.Setup(p => p.Apply(It.IsAny<IEnumerable<PresidioAnalyzerResponse>>(), It.IsAny<Language>(),
                                  It.IsAny<string>()))
              .Returns<IEnumerable<PresidioAnalyzerResponse>, Language, string>((e, _, _) => e.ToList().AsReadOnly());

        policy.Setup(p => p.ResolveOverlaps(It.IsAny<IEnumerable<PresidioAnalyzerResponse>>()))
              .Returns<IEnumerable<PresidioAnalyzerResponse>>(e => e.ToList().AsReadOnly());

        return (new PresidioService(analyzer.Object, policy.Object, store.Object), analyzer, policy, store);
    }

    private static PresidioAnalyzerResponse Entity(string type, double score, int start, int end) =>
        new() { EntityType = type, Score = score, Start = start, End = end };
    
    private static (PresidioService sut, MappingStore store) CreateSut(
        IReadOnlyList<PresidioAnalyzerResponse> deEntities,
        IReadOnlyList<PresidioAnalyzerResponse> enEntities)
    {
        var analyzer = new Mock<IPresidioAnalyzerClient>();
        analyzer.Setup(a => a.AnalyzeAsync(It.IsAny<string>(), Language.German,  It.IsAny<CancellationToken>()))
                .ReturnsAsync(deEntities);
        analyzer.Setup(a => a.AnalyzeAsync(It.IsAny<string>(), Language.English, It.IsAny<CancellationToken>()))
                .ReturnsAsync(enEntities);

        var options = new StaticOptionsMonitor<PresidioOptions>(new PresidioOptions
                                     {
                                         AnalyzerUrl = "http://localhost:5002",
                                         GermanEntityThresholds = new Dictionary<string, double>
                                                                  { { "PERSON", 0.85 }, { "LOCATION", 0.90 } },
                                         EnglishEntityThresholds = new Dictionary<string, double>
                                                                   { { "EMAIL_ADDRESS", 0.4 } }
                                     });

        var policy = new DefaultEntityPolicy(options);
        var store  = new MappingStore();

        return (new PresidioService(analyzer.Object, policy, store), store);
    }

    [Fact]
    public async Task AnonymizeAsync_EmptyText_ReturnsUnchanged()
    {
        // Arrange
        var (sut, _, _, _) = CreateSut();
        
        // Act + Assert
        Assert.Equal("",    await sut.AnonymizeAsync(""));
        Assert.Equal("   ", await sut.AnonymizeAsync("   "));
    }

    [Fact]
    public async Task AnonymizeAsync_NoEntitiesFound_ReturnsUnchanged()
    {
        // Arrange
        var (sut, _, _, _) = CreateSut();
        
        // Act
        var result = await sut.AnonymizeAsync("no pii here");
        
        // Assert
        Assert.Equal("no pii here", result);
    }

    [Fact]
    public async Task AnonymizeAsync_SendsTwoParallelRequests()
    {
        // Arrange
        var (sut, analyzer, _, _) = CreateSut();
        
        // Act
        await sut.AnonymizeAsync("some text");

        // Assert
        analyzer.Verify(a => a.AnalyzeAsync("some text", Language.German,  It.IsAny<CancellationToken>()), Times.Once);
        analyzer.Verify(a => a.AnalyzeAsync("some text", Language.English, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AnonymizeAsync_AppliesPolicyWithCorrectLanguage()
    {
        // Arrange
        var (sut, analyzer, policy, _) = CreateSut();
        
        analyzer.Setup(a => a.AnalyzeAsync(It.IsAny<string>(), Language.German, It.IsAny<CancellationToken>()))
                .ReturnsAsync([Entity("PERSON", 0.92, 0, 5)]);

        // Act
        await sut.AnonymizeAsync("Alice wohnt hier");

        // Assert
        policy.Verify(p => p.Apply(It.IsAny<IEnumerable<PresidioAnalyzerResponse>>(), Language.German,  It.IsAny<string>()), Times.AtLeastOnce);
        policy.Verify(p => p.Apply(It.IsAny<IEnumerable<PresidioAnalyzerResponse>>(), Language.English,
                                   It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task AnonymizeAsync_ReplacesEntityWithPlaceholder()
    {
        // Arrange
        var (sut, analyzer, policy, store) = CreateSut();

        analyzer.Setup(a => a.AnalyzeAsync(It.IsAny<string>(), Language.German, It.IsAny<CancellationToken>()))
                .ReturnsAsync([Entity("PERSON", 0.92, 0, 5)]);

        policy.Setup(p => p.Apply(It.IsAny<IEnumerable<PresidioAnalyzerResponse>>(), It.IsAny<Language>(),
                                  It.IsAny<string>()))
              .Returns<IEnumerable<PresidioAnalyzerResponse>, Language, string>((e, _, _) => e.ToList().AsReadOnly());

        store.Setup(s => s.GetOrCreatePlaceholder("PERSON", "Alice"))
             .Returns("[PERSON_abc123]");

        // Act
        var result = await sut.AnonymizeAsync("Alice wohnt hier");
        
        // Assert
        Assert.Equal("[PERSON_abc123] wohnt hier", result);
    }

    [Fact]
    public async Task AnonymizeAsync_PassesCancellationToken()
    {
        // Arrange
        var (sut, analyzer, _, _) = CreateSut();
        var cts = new CancellationTokenSource();

        // Act
        await sut.AnonymizeAsync("text", cts.Token);

        // Assert
        analyzer.Verify(a => a.AnalyzeAsync(It.IsAny<string>(), It.IsAny<Language>(), cts.Token), Times.Exactly(2));
    }

    [Fact]
    public async Task AnonymizeAsync_MultipleEntities_ReplacedCorrectly()
    {
        // Arrange
        var (sut, analyzer, policy, store) = CreateSut();

        analyzer.Setup(a => a.AnalyzeAsync(It.IsAny<string>(), Language.German, It.IsAny<CancellationToken>()))
                .ReturnsAsync([
                     Entity("PERSON",   0.92, 0,  5),
                     Entity("LOCATION", 0.91, 15, 21)
                 ]);

        policy.Setup(p => p.Apply(It.IsAny<IEnumerable<PresidioAnalyzerResponse>>(), It.IsAny<Language>(),
                                  It.IsAny<string>()))
              .Returns<IEnumerable<PresidioAnalyzerResponse>, Language, string>((e, _, _) => e.ToList().AsReadOnly());

        store.Setup(s => s.GetOrCreatePlaceholder("PERSON",   "Alice")) .Returns("[PERSON_aaa]");
        store.Setup(s => s.GetOrCreatePlaceholder("LOCATION", "Berlin")).Returns("[LOCATION_bbb]");

        // Act
        var result = await sut.AnonymizeAsync("Alice wohnt in Berlin");
        
        // Assert
        Assert.Equal("[PERSON_aaa] wohnt in [LOCATION_bbb]", result);
    }
    
        [Fact]
    public async Task AnonymizeAsync_GermanPerson_IsAnonymized()
    {
        // Arrange
        var (sut, store) = CreateSut(
            deEntities: [Entity("PERSON", 0.92, 0, 5)],
            enEntities: []);

        // Act
        var result = await sut.AnonymizeAsync("Alice wohnt hier");

        // Assert
        Assert.DoesNotContain("Alice", result);
        Assert.Equal(1, store.PlaceholderCount);
        Assert.Equal("Alice wohnt hier", store.Deanonymize(result));
    }

    [Fact]
    public async Task AnonymizeAsync_EntityBelowThreshold_IsNotAnonymized()
    {
        // Arrange
        var (sut, _) = CreateSut(
            deEntities: [Entity("PERSON", 0.70, 0, 5)],
            enEntities: []);

        // Act
        var result = await sut.AnonymizeAsync("Alice wohnt hier");
        
        // Assert
        Assert.Equal("Alice wohnt hier", result);
    }

    [Fact]
    public async Task AnonymizeAsync_OverlappingEntities_OnlyHigherScoreAnonymized()
    {
        // Arrange
        var (sut, store) = CreateSut(
            deEntities: [
                Entity("PERSON", 0.92, 0, 10),
                Entity("PERSON", 0.86, 0,  5)
            ],
            enEntities: []);

        // Act
        var result = await sut.AnonymizeAsync("John Smith wohnt hier");

        // Assert
        Assert.Equal(1, store.PlaceholderCount);
        Assert.DoesNotContain("John Smith", result);
    }

    [Fact]
    public async Task AnonymizeAsync_GermanAndEnglishEntities_BothAnonymized()
    {
        // Arrange
        var (sut, store) = CreateSut(
            deEntities: [Entity("PERSON",        0.92, 0,  5)],
            enEntities: [Entity("EMAIL_ADDRESS", 0.80, 15, 30)]);

        // Act
        var result = await sut.AnonymizeAsync("Alice schreibt test@example.com");

        // Assert
        Assert.Equal(2, store.PlaceholderCount);
        Assert.DoesNotContain("Alice",            result);
        Assert.DoesNotContain("test@example.com", result);
        Assert.Equal("Alice schreibt test@example.com", store.Deanonymize(result));
    }

    [Fact]
    public async Task AnonymizeAsync_SameEntityTwice_SamePlaceholder()
    {
        // Arrange
        var (sut, store) = CreateSut(
                                     deEntities: [
                                         Entity("PERSON", 0.92, 0,  5),
                                         Entity("PERSON", 0.91, 16, 21)
                                     ],
                                     enEntities: []);

        // Act
        var result = await sut.AnonymizeAsync("Alice wohnt bei Alice");

        // Assert
        Assert.Equal(1, store.PlaceholderCount);
        var unused = store.Deanonymize(result[..result.IndexOf(' ')]);
        Assert.Equal(result[..result.IndexOf(' ')], result[(result.LastIndexOf(' ') + 1)..]);
    }

    [Fact]
    public async Task AnonymizeAsyncEmojiBeforeEntityUsesCodePointOffsets()
    {
        // Arrange: "😅 Anna!" - Presidio (Python) counts code points, so "Anna" is
        // at 2..6. In UTF-16 the emoji occupies TWO units, so "Anna" is at 3..7
        // there. Without the offset conversion the replacement would be shifted by
        // one character and leak part of the value.
        var (sut, analyzer, _, store) = CreateSut();
        var text = "\U0001F605 Anna!";

        analyzer.Setup(a => a.AnalyzeAsync(text, Language.English, It.IsAny<CancellationToken>()))
                .ReturnsAsync([Entity("PERSON", 1.0, 2, 6)]);
        store.Setup(s => s.GetOrCreatePlaceholder("PERSON", "Anna"))
             .Returns("[PERSON_0123456789abcdef]");

        // Act
        var anonymized = await sut.AnonymizeAsync(text);

        // Assert: the placeholder sits exactly where "Anna" was - nothing leaked,
        // nothing swallowed.
        Assert.Equal("\U0001F605 [PERSON_0123456789abcdef]!", anonymized);
    }
}