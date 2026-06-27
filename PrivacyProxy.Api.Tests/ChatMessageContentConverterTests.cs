using System.Text.Json;
using PrivacyProxy.Api.Models.DTOs.LLM;

namespace PrivacyProxy.Api.Tests;

public class ChatMessageContentConverterTests
{
    [Fact]
    public void Read_PlainStringContent_ReturnsStringAsIs()
    {
        // Act
        var message = JsonSerializer.Deserialize<ChatMessage>(
            """{"role":"user","content":"Hello"}""");

        // Assert
        Assert.Equal("Hello", message!.Content);
    }

    [Fact]
    public void Read_ArrayContent_ConcatenatesTextPartsAndDropsOtherTypes()
    {
        // Arrange — OpenAI's multimodal content-parts array format
        const string json = """
                             {"role":"user","content":[
                                 {"type":"text","text":"Hello"},
                                 {"type":"image_url","image_url":{"url":"https://example.com/x.png"}},
                                 {"type":"text","text":"World"}
                             ]}
                             """;

        // Act
        var message = JsonSerializer.Deserialize<ChatMessage>(json);

        // Assert — text parts are joined with a newline, the image part is dropped
        Assert.Equal("Hello\nWorld", message!.Content);
    }

    [Fact]
    public void Read_ArrayContentWithNoTextParts_ReturnsEmptyString()
    {
        // Act
        var message = JsonSerializer.Deserialize<ChatMessage>(
            """{"role":"user","content":[{"type":"image_url","image_url":{"url":"https://example.com/x.png"}}]}""");

        // Assert
        Assert.Equal("", message!.Content);
    }

    [Fact]
    public void Read_UnsupportedContentType_ReturnsEmptyString()
    {
        // Act — neither a string nor an array (e.g. a stray number)
        var message = JsonSerializer.Deserialize<ChatMessage>(
            """{"role":"user","content":42}""");

        // Assert
        Assert.Equal("", message!.Content);
    }

    [Fact]
    public void Write_SerializesContentAsPlainString()
    {
        // Arrange
        var message = new ChatMessage { Role = "user", Content = "Hello" };

        // Act
        var json = JsonSerializer.Serialize(message);

        // Assert
        Assert.Contains("\"content\":\"Hello\"", json);
    }
}
