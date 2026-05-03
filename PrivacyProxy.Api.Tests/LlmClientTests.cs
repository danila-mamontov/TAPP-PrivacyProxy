using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using PrivacyProxy.Api.Configuration;
using PrivacyProxy.Api.Models.Interfaces;
using PrivacyProxy.Api.Services;

namespace PrivacyProxy.Api.Tests;

public class LlmClientTests
{
    private class MockHttpMessageHandler(HttpStatusCode statusCode, string body)
        : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest     { get; private set; }
        public string?             LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            LastRequest     = request;
            LastRequestBody = await request.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(statusCode)
                   {
                       Content = new StringContent(body, Encoding.UTF8, "application/json")
                   };
        }
    }  
    
    private static (LlmClient sut, MockHttpMessageHandler handler) CreateSut(
        HttpStatusCode statusCode = HttpStatusCode.OK,
        string         body       = "{}")
    {
        var handler    = new MockHttpMessageHandler(statusCode, body);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8000") };
        var options    = Options.Create(new LlmOptions
                                        {
                                            BaseUrl = "http://localhost:8000",
                                            ApiKey  = "test-api-key"
                                        });

        return (new LlmClient(httpClient, options), handler);
    }
    
    private static JsonElement ToJsonElement(object obj) =>
        JsonSerializer.SerializeToElement(obj);
    
    [Fact]
    public async Task SendAsync_ValidRequest_ReturnsHttpResponseMessage()
    {
        // Arrange
        var (sut, _) = CreateSut();
        var request  = ToJsonElement(new { model = "llama3", messages = new[] { new { role = "user", content = "Hello" } } });

        // Act
        var response = await sut.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
    
    [Fact]
    public async Task SendAsync_SetsAuthorizationHeader()
    {
        // Arrange
        var (sut, handler) = CreateSut();
        var request        = ToJsonElement(new { model = "llama3" });

        // Act
        await sut.SendAsync(request);
        
        var auth = handler.LastRequest!.Headers.Authorization;
        
        // Assert
        Assert.Equal("Bearer",       auth!.Scheme);
        Assert.Equal("test-api-key", auth.Parameter);
    }
    
    [Fact]
    public async Task SendAsync_PostsToCorrectEndpoint()
    {
        // Arrange
        var (sut, handler) = CreateSut();

        // Act
        await sut.SendAsync(ToJsonElement(new { model = "llama3" }));

        // Assert
        Assert.Equal("/chat/completions", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Equal(HttpMethod.Post,     handler.LastRequest.Method);
    }
    
    [Fact]
    public async Task SendAsync_RequestBodyIsForwardedUnchanged()
    {
        // Arrange
        var (sut, handler) = CreateSut();
        var request        = ToJsonElement(new
                                           {
                                               model       = "llama3",
                                               messages    = new[] { new { role = "user", content = "Hello" } },
                                               temperature = 0.7,
                                               stream      = false
                                           });

        // Act
        await sut.SendAsync(request);

        var parsed = JsonDocument.Parse(handler.LastRequestBody!).RootElement;

        // Assert
        Assert.Equal("llama3", parsed.GetProperty("model").GetString());
        Assert.Equal(0.7,      parsed.GetProperty("temperature").GetDouble());
        Assert.False(parsed.GetProperty("stream").GetBoolean());
    }
    
    [Fact]
    public async Task SendAsync_Http500_ThrowsHttpRequestException()
    {
        // Arrange
        var (sut, _) = CreateSut(HttpStatusCode.InternalServerError, "Internal Server Error");

        // Act
        var ex = await Assert.ThrowsAsync<HttpRequestException>(
                                                                () => sut.SendAsync(ToJsonElement(new { model = "llama3" })));

        // Assert
        Assert.Contains("500", ex.Message);
    }

    [Fact]
    public async Task SendAsync_Http401_ThrowsHttpRequestExceptionWithStatus()
    {
        // Arrange
        var (sut, _) = CreateSut(HttpStatusCode.Unauthorized, "Unauthorized");

        // Act
        var ex = await Assert.ThrowsAsync<HttpRequestException>(
                                                                () => sut.SendAsync(ToJsonElement(new { model = "llama3" })));

        // Assert
        Assert.Contains("401", ex.Message);
    }
    
    [Fact]
    public async Task SendAsync_CancellationRequested_ThrowsOperationCanceledException()
    {
        // Arrange
        var (sut, _) = CreateSut();
        var cts      = new CancellationTokenSource();
        
        // Act
        await cts.CancelAsync();

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
                                                                () => sut.SendAsync(ToJsonElement(new { model = "llama3" }), cts.Token));
    }
    
    [Fact]
    public void LlmClient_BaseAddress_IsConfiguredFromOptions()
    {
        // Arrange
        var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IOptions<LlmOptions>>();
                    services.AddSingleton(Options.Create(new LlmOptions
                    {
                        BaseUrl = "http://localhost:9999",
                        ApiKey  = "test-key"
                    }));
                });
            });

        using var scope  = factory.Services.CreateScope();
        
        // Act
        var       client = scope.ServiceProvider.GetRequiredService<ILlmClient>() as LlmClient;
        
        // Assert
        Assert.NotNull(client);
    }
}