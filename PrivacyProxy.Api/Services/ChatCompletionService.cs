using System.Text.Json;
using System.Text.Json.Serialization;
using PrivacyProxy.Api.Models.DTOs.LLM;
using PrivacyProxy.Api.Models.Interfaces;
using Serilog;

namespace PrivacyProxy.Api.Services;

/// <summary>
/// The <c>ChatCompletionService</c> class provides functionality to process chat completion requests
/// while maintaining user privacy through anonymization and secure data handling.
/// </summary>
/// <remarks>
/// This service integrates anonymization, interaction with a Large Language Model (LLM),
/// and secure deanonymization mechanisms. It uses dependency services to anonymize user input,
/// communicate with the LLM, and reconstruct the original context of the responses, ensuring
/// sensitive data remains protected throughout the process.
/// </remarks>
/// <param name="presidioService">
/// A service responsible for detecting and anonymizing sensitive or personally identifiable information
/// in user messages.
/// </param>
/// <param name="llmClient">
/// A client interface for communicating with the configured Large Language Model for processing
/// anonymized chat inputs and generating responses.
/// </param>
/// <param name="mappingStore">
/// A storage mechanism for managing anonymization mappings to ensure accurate deanonymization
/// of LLM-generated output.
/// </param>
/// <param name="streamingDeanonymizer">
/// A component used for streaming deanonymization of LLM responses to provide real-time privacy-aware outputs.
/// </param>
public class ChatCompletionService(
    IPresidioService      presidioService,
    ILlmClient            llmClient,
    IMappingStore         mappingStore,
    StreamingDeanonymizer streamingDeanonymizer) : IChatCompletionService
{
    /// <summary>
    /// A static instance of <see cref="JsonSerializerOptions"/> used to configure
    /// JSON serialization and deserialization settings tailored to the service.
    /// </summary>
    /// <remarks>
    /// Configured with the following settings:
    /// - Utilizes SnakeCaseLower naming policy for property names to align with API specifications.
    /// - Omits null values during serialization to reduce payload size.
    /// - Supports case-insensitive property name matching for deserialization.
    /// </remarks>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy =
            JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition =
            JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Processes a chat completion request by anonymizing the content of input messages,
    /// forwarding the anonymized data to a Large Language Model (LLM), and deanonymizing
    /// the responses received from the LLM.
    /// </summary>
    /// <param name="request">The chat completion request containing the input messages to be processed.</param>
    /// <param name="ct">An optional cancellation token for cancelling the operation if necessary.</param>
    /// <returns>A <see cref="ChatCompletionResponse"/> object containing the processed results
    /// with contents deanonymized.</returns>
    public async Task<ChatCompletionResponse> ProcessAsync(
        ChatCompletionRequest request,
        CancellationToken     ct = default)
    {
        
        Log.Information("Processing {@MessageCount} messages...", request.Messages.Count);
        
        // Anonymize each message content
        var anonymizedMessages = new List<ChatMessage>();
        foreach (var message in request.Messages)
        {
            var anonymizedContent = await presidioService.AnonymizeAsync(message.Content, ct);
            anonymizedMessages.Add(message with { Content = anonymizedContent });
        }
        
        Log.Information("Anonymized {MessageCount} messages.", anonymizedMessages.Count);

        // Forward anonymized request to LLM
        var anonymizedRequest = request with { Messages = anonymizedMessages };
        var requestElement    = JsonSerializer.SerializeToElement(anonymizedRequest, JsonOptions);
        
        Log.Debug("Sending request to LLM provider: {Request}", requestElement.GetRawText());
        
        var httpResponse = await llmClient.SendAsync(requestElement, ct);
        var responseBody = await httpResponse.Content.ReadAsStringAsync(ct);
        
        Log.Debug("Received response from LLM provider: {ResponseBody}", responseBody);

        var llmResponse = JsonSerializer.Deserialize<ChatCompletionResponse>(responseBody, JsonOptions)
                          ?? throw new InvalidOperationException("Failed to deserialize LLM response.");

        // Deanonymize each choice
        var deanonymizedChoices = llmResponse.Choices
                                             .Select(choice => choice with
                                                               {
                                                                   Message = choice.Message with
                                                                             {
                                                                                 Content = mappingStore.Deanonymize(choice.Message.Content)
                                                                             }
                                                               })
                                             .ToList();
        
        Log.Information("Deanonymized {ChoiceCount} choices.", deanonymizedChoices.Count);
        Log.Debug("Deanonymized choices: {@Choices}", deanonymizedChoices);

        return llmResponse with { Choices = deanonymizedChoices };
    }

    /// <summary>
    /// Processes a streaming chat completion request by anonymizing input messages, sending the anonymized request
    /// to a Large Language Model (LLM), and streaming the deanonymized responses back to the client as
    /// Server-Sent Events (SSE).
    /// </summary>
    /// <param name="request">The chat completion request containing the input messages to be anonymized and processed.</param>
    /// <param name="httpResponse">The HTTP response through which the streaming results are sent back to the client.</param>
    /// <param name="ct">Optional cancellation token to cancel the operation if needed.</param>
    /// <returns>A task that represents the asynchronous operation of processing the streaming request.</returns>
    public async Task ProcessStreamAsync(
        ChatCompletionRequest request,
        HttpResponse          httpResponse,
        CancellationToken     ct = default)
    {
        
        Log.Information("Processing {MessageCount} messages...", request.Messages.Count);
        
        var anonymizedMessages = new List<ChatMessage>();
        foreach (var message in request.Messages)
        {
            var anonymizedContent = await presidioService.AnonymizeAsync(message.Content, ct);
            anonymizedMessages.Add(message with { Content = anonymizedContent });
        }
        
        Log.Information("Anonymized {MessageCount} messages.", anonymizedMessages.Count);
        Log.Debug("Anonymized messages: {@Messages}", anonymizedMessages);

        var anonymizedRequest = request with { Messages = anonymizedMessages };
        var requestElement    = JsonSerializer.SerializeToElement(anonymizedRequest, JsonOptions);
        
        Log.Debug("Sending request to LLM provider: {Request}", requestElement.GetRawText());
        
        var llmHttpResponse   = await llmClient.SendAsync(requestElement, ct);

        // Preparing SSE response
        httpResponse.Headers.ContentType  = "text/event-stream";
        httpResponse.Headers.CacheControl = "no-cache";
        
        await using var stream = await llmHttpResponse.Content.ReadAsStreamAsync(ct);
        
        Log.Debug("Received response from LLM provider");

        using var streamReader = new StreamReader(stream);

        while (await streamReader.ReadLineAsync(ct) is { } line && !ct.IsCancellationRequested)
        {
            if (!line.StartsWith("data:")) continue;
            
            var data = line["data:".Length..].Trim();

            // date = [DONE] → Terminate stream
            if (data == "[DONE]")
            {
                // flush buffers when stream finished
                var remaining = streamingDeanonymizer.FlushAll();
                foreach (var (_, content) in remaining)
                {
                    if (!string.IsNullOrEmpty(content))
                        await httpResponse.WriteAsync($"data: {content}\n\n", ct);
                }
                await httpResponse.WriteAsync("data: [DONE]\n\n", ct);
                break;
            }
            
            // deserialize chunk
            var chunk = JsonSerializer.Deserialize<ChatCompletionChunk>(data, JsonOptions);
            if (chunk == null) continue;

            var deltaContent = chunk.Choices.FirstOrDefault()?.Delta.Content;
            if (deltaContent == null) continue;
            
            // deanonymize and send out
            var deanonymized = streamingDeanonymizer.ProcessFragment(deltaContent, false);
            if (string.IsNullOrEmpty(deanonymized)) continue;
            
            var outChunk = chunk with
            {
                Choices = [chunk.Choices[0] with
                {
                    Delta = chunk.Choices[0].Delta with { Content = deanonymized }
                }]
            };
            
            Log.Debug("Sending chunk: {@Chunk}", outChunk);
            
            await httpResponse.WriteAsync(
                                          $"data: {JsonSerializer.Serialize(outChunk, JsonOptions)}\n\n", ct);
            await httpResponse.Body.FlushAsync(ct);
        }
    }
}