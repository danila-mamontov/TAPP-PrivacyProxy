using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Moq;
using PrivacyProxy.Api.Models.DTOs.LLM;
using PrivacyProxy.Api.Models.Interfaces;
using PrivacyProxy.Api.Services;
using PrivacyProxy.Core.Configuration;

namespace PrivacyProxy.Api.Tests;

public class ChatCompletionServiceTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
                                                                {
                                                                    PropertyNamingPolicy        = JsonNamingPolicy.SnakeCaseLower,
                                                                    DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
                                                                    PropertyNameCaseInsensitive = true
                                                                };

    private static IOptionsMonitor<LlmOptions> LlmOpts(string model = "configured-model") =>
        new StaticOptionsMonitor<LlmOptions>(
            new LlmOptions { BaseUrl = "http://localhost:11434/v1/", ApiKey = "k", Model = model });

    private static (
        ChatCompletionService  sut,
        Mock<IPresidioService> presidio,
        Mock<ILlmClient>       llmClient,
        Mock<IMappingStore>    store) CreateSut()
    {
        var presidio = new Mock<IPresidioService>();
        var llm      = new Mock<ILlmClient>();
        var store    = new Mock<IMappingStore>();
        var deanon   = new StreamingDepseudonymizer(store.Object);

        return (new ChatCompletionService(
                                          presidio.Object, llm.Object, store.Object, deanon, LlmOpts()),
                presidio, llm, store);
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
    
    private static HttpResponseMessage SseResponse(string sseBody) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(sseBody, Encoding.UTF8, "text/event-stream")
        };

    private static DefaultHttpContext CreateHttpContext()
    {
        var context  = new DefaultHttpContext
        {
            Response =
            {
                Body = new MemoryStream()
            }
        };
        return context;
    }
    
    [Fact]
    public async Task ProcessAsync_OverridesOutgoingModelWithConfiguredModel()
    {
        // Arrange
        var presidio = new Mock<IPresidioService>();
        var llm      = new Mock<ILlmClient>();
        var store    = new Mock<IMappingStore>();
        var deanon   = new StreamingDepseudonymizer(store.Object);
        var sut      = new ChatCompletionService(
            presidio.Object, llm.Object, store.Object, deanon, LlmOpts("kimi-k2.6:cloud"));

        presidio.Setup(p => p.PseudonymizeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string t, CancellationToken _) => t);
        store.Setup(s => s.Depseudonymize(It.IsAny<string>())).Returns<string>(t => t);

        JsonElement? captured = null;
        llm.Setup(l => l.SendAsync(It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
           .Callback<JsonElement, CancellationToken>((req, _) => captured = req)
           .ReturnsAsync(LlmResponse("ok"));

        // Act — the client asks for a different model than configured
        await sut.ProcessAsync(new ChatCompletionRequest
        {
            Model    = "whatever-the-client-sent",
            Messages = [new ChatMessage { Role = "user", Content = "Hi" }]
        });

        // Assert — the proxy forwards the configured model instead
        Assert.Equal("kimi-k2.6:cloud", captured!.Value.GetProperty("model").GetString());
    }

    [Fact]
    public async Task ProcessAsync_PseudonymizesEveryCallerMessage()
    {
        // Arrange
        var (sut, presidio, llm, store) = CreateSut();

        presidio.Setup(p => p.PseudonymizeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string t, CancellationToken _) => t);  
        
        llm.Setup(l => l.SendAsync(It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(LlmResponse("ok"));
        
        store.Setup(s => s.Depseudonymize(It.IsAny<string>())).Returns<string>(t => t);

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

        // Assert — both caller messages pseudonymized (our own instruction is prepended verbatim, not counted)
        presidio.Verify(p => p.PseudonymizeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task ProcessAsync_PseudonymizesAllRolesAndSendsInstructionVerbatim()
    {
        // Arrange — every caller role (incl. system/developer) is pseudonymized; only our own
        // placeholder instruction is forwarded 1:1.
        var (sut, presidio, llm, store) = CreateSut();

        presidio.Setup(p => p.PseudonymizeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string t, CancellationToken _) => "ANON(" + t + ")");

        store.Setup(s => s.Depseudonymize(It.IsAny<string>())).Returns<string>(t => t);

        JsonElement? captured = null;
        llm.Setup(l => l.SendAsync(It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
           .Callback<JsonElement, CancellationToken>((req, _) => captured = req)
           .ReturnsAsync(LlmResponse("ok"));

        // Act
        await sut.ProcessAsync(new ChatCompletionRequest
        {
            Model    = "llama3",
            Messages =
            [
                new ChatMessage { Role = "system",    Content = "sys" },
                new ChatMessage { Role = "developer", Content = "dev" },
                new ChatMessage { Role = "assistant", Content = "asst" },
                new ChatMessage { Role = "user",      Content = "usr" }
            ]
        });

        // Assert — all four caller messages were pseudonymized
        presidio.Verify(p => p.PseudonymizeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(4));

        var messages = captured!.Value.GetProperty("messages");

        // Our own instruction is first and sent verbatim (never pseudonymized)
        Assert.Equal(5, messages.GetArrayLength());
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        var instruction = messages[0].GetProperty("content").GetString()!;
        Assert.DoesNotContain("ANON(", instruction);
        Assert.Contains("placeholder", instruction, StringComparison.OrdinalIgnoreCase);

        // The caller messages follow, each pseudonymized, original order preserved
        Assert.Equal("ANON(sys)",  messages[1].GetProperty("content").GetString());
        Assert.Equal("ANON(dev)",  messages[2].GetProperty("content").GetString());
        Assert.Equal("ANON(asst)", messages[3].GetProperty("content").GetString());
        Assert.Equal("ANON(usr)",  messages[4].GetProperty("content").GetString());
    }

        [Fact]
    public async Task ProcessAsync_ForwardsPseudonymizedContentToLlm()
    {
        // Arrange
        var (sut, presidio, llm, store) = CreateSut();

        presidio.Setup(p => p.PseudonymizeAsync("Alice", It.IsAny<CancellationToken>()))
                .ReturnsAsync("[PERSON_abc123]");
        
        llm.Setup(l => l.SendAsync(It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(LlmResponse("ok"));
        
        store.Setup(s => s.Depseudonymize(It.IsAny<string>())).Returns<string>(t => t);

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
        Assert.Equal("[PERSON_abc123]", messages[1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task ProcessAsync_DepseudonymizesResponseContent()
    {
        // Arrange
        var (sut, presidio, llm, store) = CreateSut();

        presidio.Setup(p => p.PseudonymizeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync<string, CancellationToken, IPresidioService, string>((t, _) => t);
        
        llm.Setup(l => l.SendAsync(It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(LlmResponse("[PERSON_abc123] wohnt hier"));
        
        store.Setup(s => s.Depseudonymize("[PERSON_abc123] wohnt hier"))
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
    public async Task ProcessAsync_DepseudonymizesAndLogsToolCallsExtension()
    {
        // Arrange — a response whose message carries a "tool_calls" extension (not content)
        var store    = new MappingStore();
        var presidio = new Mock<IPresidioService>();
        var llm      = new Mock<ILlmClient>();
        var deanon   = new StreamingDepseudonymizer(store);
        var sut      = new ChatCompletionService(presidio.Object, llm.Object, store, deanon, LlmOpts());

        presidio.Setup(p => p.PseudonymizeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string t, CancellationToken _) => t);

        var placeholder = store.GetOrCreatePlaceholder("PERSON", "Alice");

        var responseJson =
            "{\"id\":\"chatcmpl-1\",\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"\"," +
            "\"tool_calls\":[{\"function\":{\"name\":\"lookup\",\"arguments\":\"" + placeholder + "\"}}]}}]}";

        llm.Setup(l => l.SendAsync(It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
           {
               Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
           });

        // Act
        var result = await sut.ProcessAsync(new ChatCompletionRequest
        {
            Model    = "llama3",
            Messages = [new ChatMessage { Role = "user", Content = "Hi" }]
        });

        // Assert — the tool_calls extension was depseudonymized (placeholder -> original PII)
        var toolCallsRaw = result.Choices[0].Message.Extensions!["tool_calls"].GetRawText();
        Assert.Contains("Alice", toolCallsRaw);
        Assert.DoesNotContain(placeholder, toolCallsRaw);
    }

    [Fact]
    public async Task ProcessAsync_PseudonymizesToolCallArgumentsInRequestHistory()
    {
        // Arrange — a real multi-round agent echoes a prior assistant tool_call back in the history.
        // Its arguments (in the extension data, not in content) still hold the PII that was
        // depseudonymized on the way out so the tool could run. Those must be re-pseudonymized before
        // reaching the LLM, otherwise the placeholder guarantee breaks from round 2 onwards.
        var (sut, presidio, llm, store) = CreateSut();

        presidio.Setup(p => p.PseudonymizeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string t, CancellationToken _) => t.Replace("Alice", "[PERSON_abc123]"));

        store.Setup(s => s.Depseudonymize(It.IsAny<string>())).Returns<string>(t => t);

        JsonElement? captured = null;
        llm.Setup(l => l.SendAsync(It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
           .Callback<JsonElement, CancellationToken>((req, _) => captured = req)
           .ReturnsAsync(LlmResponse("ok"));

        var toolCalls = JsonSerializer.SerializeToElement(new object[]
        {
            new { function = new { name = "send_email", arguments = "{\"to\":\"Alice\"}" }, id = "call-1", type = "function" }
        });

        // Act
        await sut.ProcessAsync(new ChatCompletionRequest
        {
            Model    = "llama3",
            Messages =
            [
                new ChatMessage
                {
                    Role       = "assistant",
                    Content    = "",
                    Extensions = new Dictionary<string, JsonElement> { ["tool_calls"] = toolCalls }
                }
            ]
        });

        // Assert — the forwarded assistant message carries the placeholder, not the real value
        var messages     = captured!.Value.GetProperty("messages");
        var toolCallsRaw = messages[1].GetProperty("tool_calls").GetRawText();
        Assert.Contains("[PERSON_abc123]", toolCallsRaw);
        Assert.DoesNotContain("Alice", toolCallsRaw);
    }

    [Fact]
    public async Task ProcessAsync_ExtensionDataIsPreservedInForwardedRequest()
    {
        // Arrange
        var (sut, presidio, llm, store) = CreateSut();

        presidio.Setup(p => p.PseudonymizeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync<string, CancellationToken, IPresidioService, string>((t, _) => t);
        
        llm.Setup(l => l.SendAsync(It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(LlmResponse("ok"));
        
        store.Setup(s => s.Depseudonymize(It.IsAny<string>())).Returns<string>(t => t);

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

        presidio.Setup(p => p.PseudonymizeAsync(It.IsAny<string>(), cts.Token))
                .ReturnsAsync<string, CancellationToken, IPresidioService, string>((t, _) => t);
        
        llm.Setup(l => l.SendAsync(It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(LlmResponse("ok"));
        
        store.Setup(s => s.Depseudonymize(It.IsAny<string>())).Returns<string>(t => t);

        // Act
        await sut.ProcessAsync(new ChatCompletionRequest
        {
            Model    = "llama3",
            Messages = [new ChatMessage { Role = "user", Content = "Hello" }]
        }, cts.Token);

        // Assert
        presidio.Verify(p => p.PseudonymizeAsync(It.IsAny<string>(), cts.Token), Times.Once);
        llm.Verify(l => l.SendAsync(It.IsAny<JsonElement>(), cts.Token), Times.Once);
    }
    
    [Fact]
    public async Task ProcessStreamAsync_PseudonymizesMessages()
    {
        // Arrange
        var (sut, presidio, llm, _) = CreateSut();

        presidio.Setup(p => p.PseudonymizeAsync("Hello Alice", It.IsAny<CancellationToken>()))
                .ReturnsAsync("Hello [PERSON_abc]");

        llm.Setup(l => l.SendAsync(It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(SseResponse("data: [DONE]\n\n"));

        var context = CreateHttpContext();

        // Act
        await sut.ProcessStreamAsync(
                                     new ChatCompletionRequest
                                     {
                                         Model    = "llama3",
                                         Messages = [new ChatMessage { Role = "user", Content = "Hello Alice" }]
                                     },
                                     context.Response);

        // Assert
        presidio.Verify(p => p.PseudonymizeAsync("Hello Alice", It.IsAny<CancellationToken>()), Times.Once);
    }
    
    [Fact]
    public async Task ProcessStreamAsync_ForwardsPseudonymizedContentToLlm()
    {
        // Arrange
        var (sut, presidio, llm, _) = CreateSut();

        presidio.Setup(p => p.PseudonymizeAsync("Alice", It.IsAny<CancellationToken>()))
                .ReturnsAsync("[PERSON_abc]");

        JsonElement? captured = null;
        llm.Setup(l => l.SendAsync(It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
           .Callback<JsonElement, CancellationToken>((req, _) => captured = req)
           .ReturnsAsync(SseResponse("data: [DONE]\n\n"));

        var context = CreateHttpContext();

        // Act
        await sut.ProcessStreamAsync(
                                     new ChatCompletionRequest
                                     {
                                         Model    = "llama3",
                                         Messages = [new ChatMessage { Role = "user", Content = "Alice" }]
                                     },
                                     context.Response);

        // Assert
        var messages = captured!.Value.GetProperty("messages");
        Assert.Equal("[PERSON_abc]", messages[1].GetProperty("content").GetString());
    }
    
    [Fact]
    public async Task ProcessStreamAsync_DepseudonymizesDeltaContent()
    {
        var store    = new MappingStore();
        var presidio = new Mock<IPresidioService>();
        var llm      = new Mock<ILlmClient>();
        var deanon   = new StreamingDepseudonymizer(store);
        var sut      = new ChatCompletionService(presidio.Object, llm.Object, store, deanon, LlmOpts());

        var placeholder = store.GetOrCreatePlaceholder("PERSON", "Alice");

        presidio.Setup(p => p.PseudonymizeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string t, CancellationToken _) => t);

        // JSON-String sauber bauen statt Raw-String-Interpolation
        var chunkJson = JsonSerializer.Serialize(new
        {
            id      = "1",
            choices = new[] { new { delta = new { content = placeholder } } }
        }, JsonOptions);

        var sseBody = $"data: {chunkJson}\n\ndata: [DONE]\n\n";

        llm.Setup(l => l.SendAsync(It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(SseResponse(sseBody));

        var context = CreateHttpContext();

        await sut.ProcessStreamAsync(
                                     new ChatCompletionRequest
                                     {
                                         Model    = "llama3",
                                         Messages = [new ChatMessage { Role = "user", Content = "Hello" }]
                                     },
                                     context.Response);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        var output = await new StreamReader(context.Response.Body).ReadToEndAsync();
        Assert.Contains("Alice", output);
    }
    
    [Fact]
    public async Task ProcessStreamAsync_WritesDoneSignal()
    {
        // Arrange
        var (sut, presidio, llm, _) = CreateSut();

        presidio.Setup(p => p.PseudonymizeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string t, CancellationToken _) => t);

        llm.Setup(l => l.SendAsync(It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(SseResponse("data: [DONE]\n\n"));

        var context = CreateHttpContext();

        // Act
        await sut.ProcessStreamAsync(
                                     new ChatCompletionRequest
                                     {
                                         Model    = "llama3",
                                         Messages = [new ChatMessage { Role = "user", Content = "Hello" }]
                                     },
                                     context.Response);

        // Assert
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        var output = await new StreamReader(context.Response.Body).ReadToEndAsync();
        Assert.Contains("[DONE]", output);
    }
    
    [Fact]
    public async Task ProcessStreamAsync_SetsEventStreamContentType()
    {
        // Arrange
        var (sut, presidio, llm, _) = CreateSut();

        presidio.Setup(p => p.PseudonymizeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string t, CancellationToken _) => t);

        llm.Setup(l => l.SendAsync(It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(SseResponse("data: [DONE]\n\n"));

        var context = CreateHttpContext();

        // Act
        await sut.ProcessStreamAsync(
                                     new ChatCompletionRequest
                                     {
                                         Model    = "llama3",
                                         Messages = [new ChatMessage { Role = "user", Content = "Hello" }]
                                     },
                                     context.Response);

        // Assert
        Assert.Equal("text/event-stream", context.Response.Headers.ContentType.ToString());
    }
    
    [Fact]
    public async Task ProcessStreamAsync_PlaceholderSplitAcrossChunks_DepseudonymizedCorrectly()
    {
        // Arrange
        var store    = new MappingStore();
        var presidio = new Mock<IPresidioService>();
        var llm      = new Mock<ILlmClient>();
        var deanon   = new StreamingDepseudonymizer(store);
        var sut      = new ChatCompletionService(presidio.Object, llm.Object, store, deanon, LlmOpts());

        var placeholder = store.GetOrCreatePlaceholder("PERSON", "Alice");
        var mid         = placeholder.Length / 2;
        var firstHalf   = placeholder[..mid];
        var secondHalf  = placeholder[mid..];

        presidio.Setup(p => p.PseudonymizeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string t, CancellationToken _) => t);

        var chunk1 = JsonSerializer.Serialize(new
        {
            id      = "1",
            choices = new[] { new { delta = new { content = firstHalf } } }
        }, JsonOptions);

        var chunk2 = JsonSerializer.Serialize(new
        {
            id      = "1",
            choices = new[] { new { delta = new { content = secondHalf } } }
        }, JsonOptions);

        var sseBody = $"data: {chunk1}\n\ndata: {chunk2}\n\ndata: [DONE]\n\n";

        llm.Setup(l => l.SendAsync(It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(SseResponse(sseBody));

        var context = CreateHttpContext();

        // Act
        await sut.ProcessStreamAsync(
                                     new ChatCompletionRequest
                                     {
                                         Model    = "llama3",
                                         Messages = [new ChatMessage { Role = "user", Content = "Hello" }]
                                     },
                                     context.Response);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        var output = await new StreamReader(context.Response.Body).ReadToEndAsync();
        
        // Assert
        Assert.Contains("Alice", output);
        Assert.DoesNotContain(placeholder, output);
    }
    
    [Fact]
    public async Task ProcessStreamAsync_FlushesRemainingBufferOnDone()
    {
        // Arrange
        var store    = new MappingStore();
        var presidio = new Mock<IPresidioService>();
        var llm      = new Mock<ILlmClient>();
        var deanon   = new StreamingDepseudonymizer(store);
        var sut      = new ChatCompletionService(presidio.Object, llm.Object, store, deanon, LlmOpts());

        var placeholder = store.GetOrCreatePlaceholder("PERSON", "Alice");
        var mid         = placeholder.Length / 2;
        var firstHalf   = placeholder[..mid];

        presidio.Setup(p => p.PseudonymizeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string t, CancellationToken _) => t);


        // Only the first half of the placeholder is displayed — the second half never appears
        // StreamingDepseudonymizer buffers firstHalf until [DONE] calls FlushAll
        var chunk1 = JsonSerializer.Serialize(new
        {
            id      = "1",
            choices = new[] { new { delta = new { content = firstHalf } } }
        }, JsonOptions);

        var sseBody = $"data: {chunk1}\n\ndata: [DONE]\n\n";

        llm.Setup(l => l.SendAsync(It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(SseResponse(sseBody));

        var context = CreateHttpContext();

        // Act
        await sut.ProcessStreamAsync(
                                     new ChatCompletionRequest
                                     {
                                         Model    = "llama3",
                                         Messages = [new ChatMessage { Role = "user", Content = "Hello" }]
                                     },
                                     context.Response);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        var output = await new StreamReader(context.Response.Body).ReadToEndAsync();

        // Assert
        Assert.Contains(firstHalf, output);
        Assert.Contains("[DONE]", output);
    }
    
    [Fact]
    public async Task ProcessStreamAsync_FlushAll_SkipsEmptyContent()
    {
        // Arrange
        var store    = new MappingStore();
        var presidio = new Mock<IPresidioService>();
        var llm      = new Mock<ILlmClient>();
        var deanon   = new StreamingDepseudonymizer(store);
        var sut      = new ChatCompletionService(presidio.Object, llm.Object, store, deanon, LlmOpts());

        presidio.Setup(p => p.PseudonymizeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string t, CancellationToken _) => t);

        // Chunk with empty content → StreamingDepseudonymizer buffers nothing
        // but another contextKey has an empty carry
        var chunk1 = JsonSerializer.Serialize(new
        {
            id      = "1",
            choices = new[] { new { delta = new { content = "Hello" } } }
        }, JsonOptions);

        var sseBody = $"data: {chunk1}\n\ndata: [DONE]\n\n";

        llm.Setup(l => l.SendAsync(It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(SseResponse(sseBody));

        var context = CreateHttpContext();

        // Act
        await sut.ProcessStreamAsync(
                                     new ChatCompletionRequest
                                     {
                                         Model    = "llama3",
                                         Messages = [new ChatMessage { Role = "user", Content = "Hello" }]
                                     },
                                     context.Response);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        var output = await new StreamReader(context.Response.Body).ReadToEndAsync();
        
        // Assert
        Assert.Contains("[DONE]", output);
    }
    
    [Fact]
    public async Task ProcessStreamAsync_SkipsChunkWhenDeserializationReturnsNull()
    {
        // Arrange
        var (sut, presidio, llm, _) = CreateSut();

        presidio.Setup(p => p.PseudonymizeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string t, CancellationToken _) => t);

        // Invalid JSON deserialized to zero
        const string sseBody = "data: null\n\ndata: [DONE]\n\n";

        llm.Setup(l => l.SendAsync(It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(SseResponse(sseBody));

        var context = CreateHttpContext();

        // Act
        await sut.ProcessStreamAsync(
                                     new ChatCompletionRequest
                                     {
                                         Model    = "llama3",
                                         Messages = [new ChatMessage { Role = "user", Content = "Hello" }]
                                     },
                                     context.Response);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        var output = await new StreamReader(context.Response.Body).ReadToEndAsync();
        
        // Assert
        Assert.Contains("[DONE]", output);
    }
    
    [Fact]
    public async Task ProcessStreamAsync_ChunkWithEmptyChoicesArray_PassesThroughUnprocessed()
    {
        // Arrange
        var (sut, presidio, llm, _) = CreateSut();

        presidio.Setup(p => p.PseudonymizeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string t, CancellationToken _) => t);

        // "choices": [] -> nothing to process, the chunk is just written through
        const string sseBody = "data: {\"id\":\"1\",\"choices\":[]}\n\ndata: [DONE]\n\n";

        llm.Setup(l => l.SendAsync(It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(SseResponse(sseBody));

        var context = CreateHttpContext();

        // Act
        await sut.ProcessStreamAsync(
                                     new ChatCompletionRequest
                                     {
                                         Model    = "llama3",
                                         Messages = [new ChatMessage { Role = "user", Content = "Hello" }]
                                     },
                                     context.Response);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        var output = await new StreamReader(context.Response.Body).ReadToEndAsync();

        // Assert
        Assert.Contains("\"choices\":[]", output);
        Assert.Contains("[DONE]", output);
    }

    [Fact]
    public async Task ProcessStreamAsync_ChunkWithNullFirstChoice_PassesThroughUnprocessed()
    {
        // Arrange
        var (sut, presidio, llm, _) = CreateSut();

        presidio.Setup(p => p.PseudonymizeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string t, CancellationToken _) => t);

        // "choices": [null] -> the first (only) choice is not an object
        const string sseBody = "data: {\"id\":\"1\",\"choices\":[null]}\n\ndata: [DONE]\n\n";

        llm.Setup(l => l.SendAsync(It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(SseResponse(sseBody));

        var context = CreateHttpContext();

        // Act
        await sut.ProcessStreamAsync(
                                     new ChatCompletionRequest
                                     {
                                         Model    = "llama3",
                                         Messages = [new ChatMessage { Role = "user", Content = "Hello" }]
                                     },
                                     context.Response);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        var output = await new StreamReader(context.Response.Body).ReadToEndAsync();

        // Assert
        Assert.Contains("\"choices\":[null]", output);
        Assert.Contains("[DONE]", output);
    }

    [Fact]
    public async Task ProcessStreamAsync_DepseudonymizesReasoningDelta()
    {
        // Arrange — reasoning-capable models (Qwen3, DeepSeek-R1, ...) stream a "reasoning" field
        var store    = new MappingStore();
        var presidio = new Mock<IPresidioService>();
        var llm      = new Mock<ILlmClient>();
        var deanon   = new StreamingDepseudonymizer(store);
        var sut      = new ChatCompletionService(presidio.Object, llm.Object, store, deanon, LlmOpts());

        var placeholder = store.GetOrCreatePlaceholder("PERSON", "Alice");

        presidio.Setup(p => p.PseudonymizeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string t, CancellationToken _) => t);

        var chunkJson = JsonSerializer.Serialize(new
        {
            id      = "1",
            choices = new[] { new { delta = new { reasoning = placeholder } } }
        }, JsonOptions);

        var sseBody = $"data: {chunkJson}\n\ndata: [DONE]\n\n";

        llm.Setup(l => l.SendAsync(It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(SseResponse(sseBody));

        var context = CreateHttpContext();

        // Act
        await sut.ProcessStreamAsync(
                                     new ChatCompletionRequest
                                     {
                                         Model    = "llama3",
                                         Messages = [new ChatMessage { Role = "user", Content = "Hello" }]
                                     },
                                     context.Response);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        var output = await new StreamReader(context.Response.Body).ReadToEndAsync();

        // Assert
        Assert.Contains("Alice", output);
        Assert.DoesNotContain(placeholder, output);
    }

    [Fact]
    public async Task ProcessStreamAsync_DepseudonymizesToolCallArguments_AndSkipsNonStringArguments()
    {
        // Arrange — one tool call with non-string arguments (skipped) and one with a placeholder
        // (depseudonymized), exercising both branches of the tool_calls loop.
        var store    = new MappingStore();
        var presidio = new Mock<IPresidioService>();
        var llm      = new Mock<ILlmClient>();
        var deanon   = new StreamingDepseudonymizer(store);
        var sut      = new ChatCompletionService(presidio.Object, llm.Object, store, deanon, LlmOpts());

        var placeholder = store.GetOrCreatePlaceholder("PERSON", "Alice");

        presidio.Setup(p => p.PseudonymizeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string t, CancellationToken _) => t);

        var chunkJson =
            "{\"id\":\"1\",\"choices\":[{\"delta\":{\"tool_calls\":[" +
            "{\"function\":{\"name\":\"skip\",\"arguments\":12345}}," +
            "{\"function\":{\"name\":\"lookup\",\"arguments\":\"" + placeholder + "\"}}" +
            "]}}]}";

        var sseBody = $"data: {chunkJson}\n\ndata: [DONE]\n\n";

        llm.Setup(l => l.SendAsync(It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(SseResponse(sseBody));

        var context = CreateHttpContext();

        // Act
        await sut.ProcessStreamAsync(
                                     new ChatCompletionRequest
                                     {
                                         Model    = "llama3",
                                         Messages = [new ChatMessage { Role = "user", Content = "Hello" }]
                                     },
                                     context.Response);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        var output = await new StreamReader(context.Response.Body).ReadToEndAsync();

        // Assert — valid tool call depseudonymized, invalid one passed through untouched
        Assert.Contains("Alice", output);
        Assert.DoesNotContain(placeholder, output);
        Assert.Contains("12345", output);
    }

    [Fact]
    public async Task ProcessStreamAsync_SkipsChunkWhenDeltaContentIsNull()
    {
        // Arrange
        var (sut, presidio, llm, _) = CreateSut();

        presidio.Setup(p => p.PseudonymizeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string t, CancellationToken _) => t);

        // Chunk without content — only role, no content (first chunk from LLM)
        var chunkWithoutContent = JsonSerializer.Serialize(new
        {
            id      = "1",
            choices = new[] { new { delta = new { role = "assistant" } } }
        }, JsonOptions);

        var sseBody = $"data: {chunkWithoutContent}\n\ndata: [DONE]\n\n";

        llm.Setup(l => l.SendAsync(It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(SseResponse(sseBody));

        var context = CreateHttpContext();

        // Act
        await sut.ProcessStreamAsync(
                                     new ChatCompletionRequest
                                     {
                                         Model    = "llama3",
                                         Messages = [new ChatMessage { Role = "user", Content = "Hello" }]
                                     },
                                     context.Response);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        var output = await new StreamReader(context.Response.Body).ReadToEndAsync();
        
        // Assert
        Assert.Contains("[DONE]", output);
    }

    [Fact]
    public async Task ProcessStreamAsync_UnclosedBracketAtStreamEndIsFlushedAsValidChunk()
    {
        // Arrange - the streamed text ends with "[URL_": an unclosed '[' that looks
        // like a placeholder start, so the depseudonymizer buffers it until the end.
        // The flushed leftover must be a PARSEABLE chunk, not a raw text line.
        var store    = new MappingStore();
        var presidio = new Mock<IPresidioService>();
        var llm      = new Mock<ILlmClient>();
        var deanon   = new StreamingDepseudonymizer(store);
        var sut      = new ChatCompletionService(presidio.Object, llm.Object, store, deanon, LlmOpts());

        presidio.Setup(p => p.PseudonymizeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string t, CancellationToken _) => t);

        var chunkJson = "{\"id\":\"1\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"link at [URL_\"}}]}";
        llm.Setup(l => l.SendAsync(It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(SseResponse($"data: {chunkJson}\n\ndata: [DONE]\n\n"));

        var context = CreateHttpContext();

        // Act
        await sut.ProcessStreamAsync(
            new ChatCompletionRequest
            {
                Model    = "llama3",
                Messages = [new ChatMessage { Role = "user", Content = "Hello" }]
            },
            context.Response);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        var output = await new StreamReader(context.Response.Body).ReadToEndAsync();

        // Assert - every data line (except [DONE]) must be valid JSON, and the
        // buffered "[URL_" must arrive inside a proper delta.content.
        var reassembled = "";
        foreach (var line in output.Split("\n"))
        {
            if (!line.StartsWith("data: ")) continue;
            var payload = line["data: ".Length..];
            if (payload == "[DONE]") continue;

            var chunk = JsonSerializer.Deserialize<JsonElement>(payload);   // throws on raw text
            foreach (var choice in chunk.GetProperty("choices").EnumerateArray())
            {
                if (choice.GetProperty("delta").TryGetProperty("content", out var content))
                    reassembled += content.GetString();
            }
        }
        Assert.Contains("link at [URL_", reassembled);
    }
}