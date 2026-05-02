using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Moq;
using PrivacyProxy.Api.Models.DTOs.LLM;
using PrivacyProxy.Api.Models.DTOs.Presidio;
using PrivacyProxy.Api.Models.Enums;
using PrivacyProxy.Api.Models.Interfaces;
using PrivacyProxy.Api.Services;

namespace PrivacyProxy.Api.Tests;

public class ChatCompletionServiceTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
                                                                {
                                                                    PropertyNamingPolicy        = JsonNamingPolicy.SnakeCaseLower,
                                                                    DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
                                                                    PropertyNameCaseInsensitive = true
                                                                };

    private static (
        ChatCompletionService sut,
        Mock<IPresidioService> presidio,
        Mock<ILlmClient> llmClient,
        Mock<IMappingStore> store) CreateSut()
    {
        var analyzer = new Mock<IPresidioAnalyzerClient>();
        analyzer.Setup(a => a.AnalyzeAsync(It.IsAny<string>(), It.IsAny<Language>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([]);

        var policy = new Mock<IEntityPolicy>();
        policy.Setup(p => p.Apply(It.IsAny<IEnumerable<PresidioAnalyzerResponse>>(), It.IsAny<Language>(), It.IsAny<string>()))
              .Returns<IEnumerable<PresidioAnalyzerResponse>, Language, string>((e, _, _) => e.ToList().AsReadOnly());
        policy.Setup(p => p.ResolveOverlaps(It.IsAny<IEnumerable<PresidioAnalyzerResponse>>()))
              .Returns<IEnumerable<PresidioAnalyzerResponse>>(e => e.ToList().AsReadOnly());

        var store    = new Mock<IMappingStore>();
        var presidio = new Mock<IPresidioService>();
        var llm      = new Mock<ILlmClient>();

        return (new ChatCompletionService(presidio.Object, llm.Object, store.Object), presidio, llm, store);
    }
    
    private static HttpResponseMessage LlmResponse(string content) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                                        JsonSerializer.Serialize(new
                                                                 {
                                                                     id      = "chatcmpl-1",
                                                                     choices = new[] { new { message = new { role = "assistant", content } } }
                                                                 }, JsonOptions),
                                        Encoding.UTF8,
                                        "application/json")
        };
    
    [Fact]
    public async Task ProcessAsync_AnonymizesEachMessage()
    {
        // Arrange
        var (sut, presidio, llm, store) = CreateSut();

        presidio.Setup(p => p.AnonymizeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string t, CancellationToken _) => t);  
        
        llm.Setup(l => l.SendAsync(It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(LlmResponse("ok"));
        
        store.Setup(s => s.Deanonymize(It.IsAny<string>())).Returns<string>(t => t);

        var request = new ChatCompletionRequest
                      {
                          Model    = "llama3",
                          Messages = [
                              new ChatMessage { Role = "system", Content = "You are helpful." },
                              new ChatMessage { Role = "user",   Content = "Hello Alice." }
                          ]
                      };

        // Act
        await sut.ProcessAsync(request);

        // Assert
        presidio.Verify(p => p.AnonymizeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }
    
        [Fact]
    public async Task ProcessAsync_ForwardsAnonymizedContentToLlm()
    {
        // Arrange
        var (sut, presidio, llm, store) = CreateSut();

        presidio.Setup(p => p.AnonymizeAsync("Alice", It.IsAny<CancellationToken>()))
                .ReturnsAsync("[PERSON_abc123]");
        
        llm.Setup(l => l.SendAsync(It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(LlmResponse("ok"));
        
        store.Setup(s => s.Deanonymize(It.IsAny<string>())).Returns<string>(t => t);

        JsonElement? captured = null;
        llm.Setup(l => l.SendAsync(It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
           .Callback<JsonElement, CancellationToken>((req, _) => captured = req)
           .ReturnsAsync(LlmResponse("ok"));

        // Act
        await sut.ProcessAsync(new ChatCompletionRequest
        {
            Model    = "llama3",
            Messages = [new ChatMessage { Role = "user", Content = "Alice" }]
        });

        var messages = captured!.Value.GetProperty("messages");
        
        // Assert
        Assert.Equal("[PERSON_abc123]", messages[0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task ProcessAsync_DeanonymizesResponseContent()
    {
        // Arrange
        var (sut, presidio, llm, store) = CreateSut();

        presidio.Setup(p => p.AnonymizeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync<string, CancellationToken, IPresidioService, string>((t, _) => t);
        
        llm.Setup(l => l.SendAsync(It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(LlmResponse("[PERSON_abc123] wohnt hier"));
        
        store.Setup(s => s.Deanonymize("[PERSON_abc123] wohnt hier"))
             .Returns("Alice wohnt hier");

        // Act
        var result = await sut.ProcessAsync(new ChatCompletionRequest
        {
            Model    = "llama3",
            Messages = [new ChatMessage { Role = "user", Content = "Wo wohnt Alice?" }]
        });

        // Assert
        Assert.Equal("Alice wohnt hier", result.Choices[0].Message.Content);
    }

    [Fact]
    public async Task ProcessAsync_ExtensionDataIsPreservedInForwardedRequest()
    {
        // Arrange
        var (sut, presidio, llm, store) = CreateSut();

        presidio.Setup(p => p.AnonymizeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync<string, CancellationToken, IPresidioService, string>((t, _) => t);
        
        llm.Setup(l => l.SendAsync(It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(LlmResponse("ok"));
        
        store.Setup(s => s.Deanonymize(It.IsAny<string>())).Returns<string>(t => t);

        JsonElement? captured = null;
        llm.Setup(l => l.SendAsync(It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
           .Callback<JsonElement, CancellationToken>((req, _) => captured = req)
           .ReturnsAsync(LlmResponse("ok"));

        var request = new ChatCompletionRequest
        {
            Model      = "llama3",
            Messages   = [new ChatMessage { Role = "user", Content = "Hello" }],
            Extensions = new Dictionary<string, JsonElement>
            {
                ["temperature"] = JsonSerializer.SerializeToElement(0.7)
            }
        };

        // Act
        await sut.ProcessAsync(request);

        // Assert
        Assert.Equal(0.7, captured!.Value.GetProperty("temperature").GetDouble());
    }

    [Fact]
    public async Task ProcessAsync_PassesCancellationToken()
    {
        // Arrange
        var (sut, presidio, llm, store) = CreateSut();
        var cts = new CancellationTokenSource();

        presidio.Setup(p => p.AnonymizeAsync(It.IsAny<string>(), cts.Token))
                .ReturnsAsync<string, CancellationToken, IPresidioService, string>((t, _) => t);
        
        llm.Setup(l => l.SendAsync(It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(LlmResponse("ok"));
        
        store.Setup(s => s.Deanonymize(It.IsAny<string>())).Returns<string>(t => t);

        // Act
        await sut.ProcessAsync(new ChatCompletionRequest
        {
            Model    = "llama3",
            Messages = [new ChatMessage { Role = "user", Content = "Hello" }]
        }, cts.Token);

        // Assert
        presidio.Verify(p => p.AnonymizeAsync(It.IsAny<string>(), cts.Token), Times.Once);
        llm.Verify(l => l.SendAsync(It.IsAny<JsonElement>(), cts.Token), Times.Once);
    }
}