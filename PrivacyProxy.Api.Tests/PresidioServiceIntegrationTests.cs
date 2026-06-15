
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using PrivacyProxy.Api.Configuration;
using PrivacyProxy.Api.Services;

namespace PrivacyProxy.Api.Tests;

[Trait("Category", "Integration")]
public class PresidioServiceIntegrationTests
{

    private static readonly IConfiguration Configuration = new ConfigurationBuilder()
                                                          .AddJsonFile("appsettings.Development.json")
                                                          .Build();
    
    private static (PresidioService sut, MappingStore store) CreateSut()
    {
        var presidioOptions = Configuration.GetSection("Presidio").Get<PresidioOptions>()!;
        var options         = new StaticOptionsMonitor<PresidioOptions>(presidioOptions);

        var httpClient = new HttpClient { BaseAddress = new Uri(presidioOptions.AnalyzerUrl) };
        var analyzer   = new PresidioAnalyzerClient(httpClient, options);
        var policy     = new DefaultEntityPolicy(options);
        var store      = new MappingStore();

        return (new PresidioService(analyzer, policy, store), store);    
    }
    
    [Fact]
    public async Task AnonymizeAsync_RealPresidio_DetectsEnglishPerson()
    {
        // Arrange
        var (sut, store) = CreateSut();

        // Act
        var result = await sut.AnonymizeAsync("John Smith lives in New York.");

        // Assert
        Assert.DoesNotContain("John Smith", result);
        Assert.True(store.PlaceholderCount > 0);
        Assert.Contains("John Smith", store.Deanonymize(result));
    }

    [Fact]
    public async Task AnonymizeAsync_RealPresidio_DetectsEmail()
    {
        // Arrange
        var (sut, store) = CreateSut();

        // Act
        var result = await sut.AnonymizeAsync("Contact me at john@example.com");

        // Assert
        Assert.DoesNotContain("john@example.com", result);
        Assert.Equal("Contact me at john@example.com", store.Deanonymize(result));
    }

    [Fact]
    public async Task AnonymizeAsync_RealPresidio_DetectsGermanPerson()
    {
        // Arrange
        var (sut, store) = CreateSut();

        // Act
        var result = await sut.AnonymizeAsync("Max Mustermann wohnt in Berlin.");

        // Assert
        Assert.DoesNotContain("Max Mustermann", result);
        Assert.Contains("Max Mustermann", store.Deanonymize(result));
    }

    [Fact]
    public async Task AnonymizeAsync_RealPresidio_NoPii_ReturnsUnchanged()
    {
        // Arrange
        var (sut, _) = CreateSut();

        // Act
        var result = await sut.AnonymizeAsync("The weather is nice today.");

        // Assert
        Assert.Equal("The weather is nice today.", result);
    }

    [Fact]
    public async Task AnonymizeAsync_RealPresidio_DeanonymizedTextMatchesOriginal()
    {
        // Arrange
        var (sut, store) = CreateSut();
        
        const string original     = "Alice schreibt an bob@example.com über das Projekt.";
        
        // Act
        var          anonymized   = await sut.AnonymizeAsync(original);
        var          deanonymized = store.Deanonymize(anonymized);

        // Assert
        Assert.NotEqual(original,    anonymized);
        Assert.Equal(original, deanonymized);
    }
}