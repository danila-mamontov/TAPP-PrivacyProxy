using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc.Testing;
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
            builder.ConfigureServices(configureServices))
            .CreateClient();
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
    public async Task Post_StreamTrue_Returns400()
    {
        // Arrange
        var client = CreateClient(_ => { });

        // Act
        var response = await client.PostAsync("/v1/chat/completions",
            JsonContent(new
            {
                model    = "llama3",
                messages = new[] { new { role = "user", content = "Hello" } },
                stream   = true
            }));

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
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