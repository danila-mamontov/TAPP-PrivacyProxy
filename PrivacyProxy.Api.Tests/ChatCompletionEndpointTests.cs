using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using PrivacyProxy.Api.Models.DTOs.LLM;
using PrivacyProxy.Api.Models.Interfaces;
using PrivacyProxy.Api.Services;

namespace PrivacyProxy.Api.Tests;

public class ChatCompletionEndpointTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
                                                                {
                                                                    PropertyNamingPolicy        = JsonNamingPolicy.SnakeCaseLower,
                                                                    DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
                                                                    PropertyNameCaseInsensitive = true
                                                                };

    private HttpClient CreateClient(Action<IServiceCollection> configureServices)
    {
        return factory.WithWebHostBuilder(builder =>
        {
            // Provide the required options in-memory so the host passes ValidateOnStart.
            // Presidio and the LLM are mocked in these tests, so the values only need
            // to exist - they are never actually contacted.
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(TestHostConfig.RequiredOptions);
            });

            builder.ConfigureServices(configureServices);

            // JSON Options explizit auch im Test-Host setzen
            builder.ConfigureServices(services =>
            {
                services.ConfigureHttpJsonOptions(opts =>
                {
                    opts.SerializerOptions.PropertyNamingPolicy        = JsonNamingPolicy.SnakeCaseLower;
                    opts.SerializerOptions.PropertyNameCaseInsensitive = true;
                    opts.SerializerOptions.DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull;
                    opts.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
                });
            });
        }).CreateClient();
    }

    private static StringContent JsonContent(object obj) =>
        new(JsonSerializer.Serialize(obj, JsonOptions), Encoding.UTF8, "application/json");

    [Fact]
    public async Task Post_NonStreaming_Returns200()
    {
        // Arrange
        var presidio = new Mock<IPresidioService>();
        presidio.Setup(p => p.AnonymizeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string t, CancellationToken _) => t);

        var llm = new Mock<ILlmClient>();
        llm.Setup(l => l.SendAsync(It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
           {
               Content = new StringContent(
                   JsonSerializer.Serialize(new
                   {
                       id      = "1",
                       choices = new[] { new { message = new { role = "assistant", content = "Hello!" } } }
                   }, JsonOptions), Encoding.UTF8, "application/json")
           });

        var client = CreateClient(services =>
        {
            services.RemoveAll<IPresidioService>();
            services.AddScoped(_ => presidio.Object);
            services.RemoveAll<ILlmClient>();
            services.AddScoped(_ => llm.Object);
        });

        // Act
        var response = await client.PostAsync("/v1/chat/completions",
            JsonContent(new
            {
                model    = "llama3",
                messages = new[] { new { role = "user", content = "Hello" } }
            }));

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
    
    [Fact]
    public async Task Post_StreamTrue_ReturnsEventStream()
    {
        // Arrange
        var store    = new MappingStore();
        var presidio = new Mock<IPresidioService>();
        presidio.Setup(p => p.AnonymizeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string t, CancellationToken _) => t);

        // simulating SSE-Stream
        const string sseBody = """
                               data: {"id":"1","choices":[{"delta":{"content":"Hello"}}]}

                               data: {"id":"1","choices":[{"delta":{"content":" world"}}]}

                               data: [DONE]

                               """;

        var llm = new Mock<ILlmClient>();
        llm.Setup(l => l.SendAsync(It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
           {
               Content = new StringContent(sseBody, Encoding.UTF8, "text/event-stream")
           });

        var client = CreateClient(services =>
        {
            services.RemoveAll<IPresidioService>();
            services.AddSingleton(_ => presidio.Object);
            services.RemoveAll<ILlmClient>();
            services.AddSingleton(_ => llm.Object);
            services.RemoveAll<IMappingStore>();
            services.AddSingleton<IMappingStore>(store);
        });

        // Act
        var response = await client.PostAsync("/v1/chat/completions",
                                              new StringContent(
                                                                """{"model":"llama3","messages":[{"role":"user","content":"Hello"}],"stream":true}""",
                                                                Encoding.UTF8, "application/json"));

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Hello", body);
        Assert.Contains("[DONE]", body);
    }

    [Fact]
    public async Task Post_AnonymizesAndDeanonymizesContent()
    {
        // Arrange
        var store       = new MappingStore();
        var placeholder = store.GetOrCreatePlaceholder("PERSON", "Alice");

        var presidio = new Mock<IPresidioService>();
        presidio.Setup(p => p.AnonymizeAsync("Hello Alice", It.IsAny<CancellationToken>()))
                .ReturnsAsync($"Hello {placeholder}");

        var llm = new Mock<ILlmClient>();
        llm.Setup(l => l.SendAsync(It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
                         {
                             Content = new StringContent(
                                                         JsonSerializer.Serialize(new
                                                             {
                                                                 id      = "1",
                                                                 choices = new[] { new { message = new { role = "assistant", content = $"Hi {placeholder}!" } } }
                                                             }, JsonOptions), Encoding.UTF8, "application/json")
                         });

        var client = CreateClient(services =>
                                  {
                                      services.RemoveAll<IPresidioService>();
                                      services.AddSingleton(_ => presidio.Object);
                                      services.RemoveAll<ILlmClient>();
                                      services.AddSingleton(_ => llm.Object);
                                      services.RemoveAll<IMappingStore>();
                                      services.AddSingleton<IMappingStore>(store);
                                  });

        // Act
        var response = await client.PostAsync("/v1/chat/completions",
                                              JsonContent(new
                                                          {
                                                              model    = "llama3",
                                                              messages = new[] { new { role = "user", content = "Hello Alice" } }
                                                          }));

        var body    = await response.Content.ReadAsStringAsync();
        var parsed  = JsonSerializer.Deserialize<ChatCompletionResponse>(body, JsonOptions);
        var content = parsed!.Choices[0].Message.Content;

        // Assert
        Assert.Equal("Hi Alice!", content);
    }
}