
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
        var options = Options.Create(Configuration.GetSection("Presidio").Get<PresidioOptions>()!);

        var httpClient = new HttpClient { BaseAddress = new Uri(options.Value.AnalyzerUrl) };
        var analyzer   = new PresidioAnalyzerClient(httpClient, options);
        var policy     = new DefaultEntityPolicy(options);
        var store      = new MappingStore();

        return (new PresidioService(analyzer, policy, store), store);    
    }
    
    [Fact]
    public async Task AnonymizeAsync_RealPresidio_DetectsEnglishPerson()
    {
        var (sut, store) = CreateSut();

        var result = await sut.AnonymizeAsync("John Smith lives in New York.");

        Assert.DoesNotContain("John Smith", result);
        Assert.True(store.PlaceholderCount > 0);
        Assert.Contains("John Smith", store.Deanonymize(result));
    }

    [Fact]
    public async Task AnonymizeAsync_RealPresidio_DetectsEmail()
    {
        var (sut, store) = CreateSut();

        var result = await sut.AnonymizeAsync("Contact me at john@example.com");

        Assert.DoesNotContain("john@example.com", result);
        Assert.Equal("Contact me at john@example.com", store.Deanonymize(result));
    }

    [Fact]
    public async Task AnonymizeAsync_RealPresidio_DetectsGermanPerson()
    {
        var (sut, store) = CreateSut();

        var result = await sut.AnonymizeAsync("Max Mustermann wohnt in Berlin.");

        Assert.DoesNotContain("Max Mustermann", result);
        Assert.Contains("Max Mustermann", store.Deanonymize(result));
    }

    [Fact]
    public async Task AnonymizeAsync_RealPresidio_NoPii_ReturnsUnchanged()
    {
        var (sut, _) = CreateSut();

        var result = await sut.AnonymizeAsync("The weather is nice today.");

        Assert.Equal("The weather is nice today.", result);
    }

    [Fact]
    public async Task AnonymizeAsync_RealPresidio_DeanonymizedTextMatchesOriginal()
    {
        var (sut, store) = CreateSut();

        const string original     = "Alice schreibt an bob@example.com über das Projekt.";
        var          anonymized   = await sut.AnonymizeAsync(original);
        var          deanonymized = store.Deanonymize(anonymized);

        Assert.NotEqual(original,    anonymized);
        Assert.Equal(original, deanonymized);
    }
}