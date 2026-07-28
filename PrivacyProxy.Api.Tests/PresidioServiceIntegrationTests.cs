using Microsoft.Extensions.Configuration;
using PrivacyProxy.Core.Configuration;
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
    public async Task PseudonymizeAsync_RealPresidio_DetectsEnglishPerson()
    {
        // Arrange
        var (sut, store) = CreateSut();

        // Act
        var result = await sut.PseudonymizeAsync("John Smith lives in New York.");

        // Assert
        Assert.DoesNotContain("John Smith", result);
        Assert.True(store.PlaceholderCount > 0);
        Assert.Contains("John Smith", store.Depseudonymize(result));
    }

    [Fact]
    public async Task PseudonymizeAsync_RealPresidio_DetectsEmail()
    {
        // Arrange
        var (sut, store) = CreateSut();

        // Act
        var result = await sut.PseudonymizeAsync("Contact me at john@example.com");

        // Assert
        Assert.DoesNotContain("john@example.com", result);
        Assert.Equal("Contact me at john@example.com", store.Depseudonymize(result));
    }

    [Fact]
    public async Task PseudonymizeAsync_RealPresidio_DetectsGermanPerson()
    {
        // Arrange
        var (sut, store) = CreateSut();

        // Act
        var result = await sut.PseudonymizeAsync("Max Mustermann wohnt in Berlin.");

        // Assert
        Assert.DoesNotContain("Max Mustermann", result);
        Assert.Contains("Max Mustermann", store.Depseudonymize(result));
    }

    [Fact]
    public async Task PseudonymizeAsync_RealPresidio_NoPii_ReturnsUnchanged()
    {
        // Arrange
        var (sut, _) = CreateSut();

        // Act
        var result = await sut.PseudonymizeAsync("The weather is nice today.");

        // Assert
        Assert.Equal("The weather is nice today.", result);
    }

    [Fact]
    public async Task PseudonymizeAsync_RealPresidio_DepseudonymizedTextMatchesOriginal()
    {
        // Arrange
        var (sut, store) = CreateSut();
        
        const string original     = "Alice schreibt an bob@example.com über das Projekt.";
        
        // Act
        var          pseudonymized   = await sut.PseudonymizeAsync(original);
        var          depseudonymized = store.Depseudonymize(pseudonymized);

        // Assert
        Assert.NotEqual(original,    pseudonymized);
        Assert.Equal(original, depseudonymized);
    }
}