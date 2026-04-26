using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PrivacyProxy.Api.Configuration;
using PrivacyProxy.Api.Models.Enums;
using PrivacyProxy.Api.Services;

namespace PrivacyProxy.Api.Tests;

public class PresidioAnalyzerClientTests
{
    private class MockHttpMessageHandler(HttpStatusCode statusCode, string body)
        : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
        
            LastRequest = request;
        
            return await Task.FromResult(new HttpResponseMessage(statusCode)
                                         {
                                             Content = new StringContent(body, Encoding.UTF8, "application/json")
                                         });
        }
    }
    
    private static (PresidioAnalyzerClient sut, MockHttpMessageHandler handler) CreateSut(
        HttpStatusCode statusCode, string responseBody)
    {
        var handler    = new MockHttpMessageHandler(statusCode, responseBody);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5002") };
        var options = Options.Create(new PresidioOptions
                                     {
                                         AnalyzerUrl        = "http://localhost:5002",
                                         GermanEntityTypes  = ["PERSON", "LOCATION"],
                                         EnglishEntityTypes = ["EMAIL_ADDRESS"],
                                         ScoreThreshold     = 0.4,
                                         AllowList          = ["Dennis Itzel", "Uni Ulm"],
                                         Context            = ["email"]
                                     });

        return (new PresidioAnalyzerClient(httpClient, options), handler);
    }
    
    [Fact]
    public async Task AnalyzeAsyncValidResponseReturnsEntities()
    {
        // Arrange
        const string json = """
                            [
                                { "entity_type": "PERSON", "score": 0.92, "start": 0, "end": 10 },
                                { "entity_type": "LOCATION", "score": 0.91, "start": 15, "end": 21 }
                            ]
                            """;

        var (sut, _) = CreateSut(HttpStatusCode.OK, json);
        
        // Act
        var result   = await sut.AnalyzeAsync("John Smith wohnt Berlin", Language.German);

        // Assert
        Assert.Equal(2,          result.Count);
        Assert.Equal("PERSON",   result[0].EntityType);
        Assert.Equal("LOCATION", result[1].EntityType);
    }
    
    [Fact]
    public async Task AnalyzeAsyncEmptyResponseReturnsEmptyList()
    {
        // Arrange
        var (sut, _) = CreateSut(HttpStatusCode.OK, "[]");
        
        // Act
        var result   = await sut.AnalyzeAsync("no pii here", Language.English);

        // Assert
        Assert.Empty(result);
    }
    
    [Fact]
    public async Task AnalyzeAsyncGermanSendsGermanEntityTypes()
    {
        // Arrange
        var (sut, handler) = CreateSut(HttpStatusCode.OK, "[]");
        
        // Act
        await sut.AnalyzeAsync("text", Language.German);

        var body    = await handler.LastRequest!.Content!.ReadAsStringAsync();
        var request = JsonDocument.Parse(body).RootElement;

        // Assert
        Assert.Equal("de", request.GetProperty("language").GetString());

        var entities = request.GetProperty("entities")
                              .EnumerateArray()
                              .Select(e => e.GetString())
                              .ToList();

        Assert.Contains("PERSON",   entities);
        Assert.Contains("LOCATION", entities);
        Assert.DoesNotContain("EMAIL_ADDRESS", entities);
    }
    
    [Fact]
    public async Task AnalyzeAsyncEnglishSendsEnglishEntityTypes()
    {
        // Arrange
        var (sut, handler) = CreateSut(HttpStatusCode.OK, "[]");
        await sut.AnalyzeAsync("text", Language.English);

        // Act
        var body    = await handler.LastRequest!.Content!.ReadAsStringAsync();
        var request = JsonDocument.Parse(body).RootElement;

        // Assert
        Assert.Equal("en", request.GetProperty("language").GetString());

        var entities = request.GetProperty("entities")
                              .EnumerateArray()
                              .Select(e => e.GetString())
                              .ToList();

        Assert.Contains("EMAIL_ADDRESS", entities);
        Assert.DoesNotContain("PERSON",  entities);
    }
    
    [Fact]
    public async Task AnalyzeAsyncSendsAllowListAndContext()
    {
        // Arrange
        var (sut, handler) = CreateSut(HttpStatusCode.OK, "[]");
        await sut.AnalyzeAsync("text", Language.German);

        // Act
        var body    = await handler.LastRequest!.Content!.ReadAsStringAsync();
        var request = JsonDocument.Parse(body).RootElement;

        var allowList = request.GetProperty("allow_list")
                               .EnumerateArray()
                               .Select(e => e.GetString())
                               .ToList();

        // Assert
        Assert.Contains("Dennis Itzel", allowList);
        Assert.Contains("Uni Ulm", allowList);
        Assert.Contains("email", request.GetProperty("context")
                                        .EnumerateArray()
                                        .Select(e => e.GetString()));
    }
    
    [Fact]
    public async Task AnalyzeAsyncHttp500ThrowsHttpRequestException()
    {
        // Arrange
        var (sut, _) = CreateSut(HttpStatusCode.InternalServerError, "Internal Server Error");

        // Act
        var ex = await Assert.ThrowsAsync<HttpRequestException>(
                                                                () => sut.AnalyzeAsync("text", Language.German));

        // Assert
        Assert.Contains("500", ex.Message);
    }

    [Fact]
    public async Task AnalyzeAsyncInvalidJsonThrowsInvalidOperationException()
    {
        // Arrange
        var (sut, _) = CreateSut(HttpStatusCode.OK, "not valid json {{");

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
                                                            () => sut.AnalyzeAsync("text", Language.German));
    }

    [Fact]
    public async Task AnalyzeAsyncCancellationRequestedThrowsOperationCanceledException()
    {
        // Arrange
        var cts      = new CancellationTokenSource();
        var (sut, _) = CreateSut(HttpStatusCode.OK, "[]");

        await cts.CancelAsync();

        // Act & Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
                                                                () => sut.AnalyzeAsync("text", Language.German, cts.Token));
    }
}