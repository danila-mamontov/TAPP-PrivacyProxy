using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using PrivacyProxy.Api.Models.DTOs.LLM;
using PrivacyProxy.Api.Models.Interfaces;
using PrivacyProxy.Core.Configuration;
using Serilog;

namespace PrivacyProxy.Api.Services;

/// <summary>
/// The <c>ChatCompletionService</c> class provides functionality to process chat completion requests
/// while maintaining user privacy through anonymization and secure data handling.
/// </summary>
/// <remarks>
/// This service integrates anonymization, interaction with a Large Language Model (LLM),
/// and secure deanonymization mechanisms. Every incoming message (regardless of role) is anonymized
/// before it is forwarded to the LLM, and the responses are deanonymized again, ensuring sensitive
/// data remains protected throughout the process.
/// </remarks>
/// <param name="presidioService">
/// A service responsible for detecting and anonymizing sensitive or personally identifiable information
/// in incoming messages.
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
    StreamingDeanonymizer streamingDeanonymizer,
    IOptionsMonitor<LlmOptions> llmOptions) : IChatCompletionService
{
    /// <summary>
    /// Builds the messages forwarded to the LLM: our own placeholder-handling instruction verbatim
    /// (never anonymized, never altered) followed by EVERY caller message — regardless of role —
    /// with its content anonymized via Presidio. Maximum privacy: no caller content reaches the LLM
    /// without passing through anonymization.
    /// </summary>
    private async Task<List<ChatMessage>> BuildAnonymizedMessagesAsync(
        IReadOnlyList<ChatMessage> incoming, CancellationToken ct)
    {
        var messages = new List<ChatMessage>(incoming.Count + 1) { SystemInstruction };

        foreach (var message in incoming)
        {
            var anonymizedContent = await presidioService.AnonymizeAsync(message.Content, ct);
            var anonymizedExtensions = await AnonymizeToolCallArgumentsAsync(message.Extensions, ct);
            messages.Add(message with { Content = anonymizedContent, Extensions = anonymizedExtensions });
        }

        return messages;
    }

    /// <summary>
    /// Anonymizes the <c>arguments</c> of any assistant <c>tool_calls</c> carried in a message's
    /// extension data. A real multi-round agent echoes its previous tool call (whose arguments were
    /// deanonymized on the way out so the tool could run) back in the conversation history. Those
    /// arguments live in <see cref="ChatMessage.Extensions"/>, not in <c>content</c>, so without this
    /// step the real PII inside them would reach the LLM on the next round — defeating anonymization.
    /// Returns a new extensions dictionary with anonymized tool-call arguments, or the original
    /// reference when there is nothing to anonymize.
    /// </summary>
    private async Task<IDictionary<string, JsonElement>?> AnonymizeToolCallArgumentsAsync(
        IDictionary<string, JsonElement>? extensions, CancellationToken ct)
    {
        if (extensions is null
            || !extensions.TryGetValue("tool_calls", out var toolCalls)
            || toolCalls.ValueKind != JsonValueKind.Array)
        {
            return extensions;
        }

        var toolCallsArray = JsonNode.Parse(toolCalls.GetRawText())!.AsArray();
        var changed = false;

        foreach (var toolCall in toolCallsArray)
        {
            if (toolCall?["function"]?["arguments"] is not JsonValue argumentsValue
                || !argumentsValue.TryGetValue<string>(out var argumentsJson)
                || string.IsNullOrEmpty(argumentsJson))
            {
                continue;
            }

            // The whole arguments JSON string is anonymized as text: Presidio replaces any PII
            // value with its placeholder, and placeholders contain no JSON-breaking characters,
            // so the argument object's structure stays intact.
            var anonymizedArguments = await presidioService.AnonymizeAsync(argumentsJson, ct);
            if (!string.Equals(anonymizedArguments, argumentsJson, StringComparison.Ordinal))
            {
                toolCall["function"]!["arguments"] = anonymizedArguments;
                changed = true;
            }
        }

        if (!changed)
        {
            return extensions;
        }

        var updated = new Dictionary<string, JsonElement>(extensions)
        {
            ["tool_calls"] = JsonSerializer.Deserialize<JsonElement>(toolCallsArray.ToJsonString())
        };
        return updated;
    }

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

        Log.Information("Processing {MessageCount} messages...", request.Messages.Count);

        // Every caller message (any role) is anonymized; our own instruction is prepended verbatim.
        var anonymizedMessages = await BuildAnonymizedMessagesAsync(request.Messages, ct);

        // Forward anonymized request to LLM
        // Force the configured model regardless of what the client sent, so the proxy
        // owns the model selection (the WebUI/Llm.Model setting is the single source).
        var anonymizedRequest = request with { Model = llmOptions.CurrentValue.Model, Messages = anonymizedMessages };
        var requestElement    = JsonSerializer.SerializeToElement(anonymizedRequest, JsonOptions);
        
        Log.Debug("Sending request to LLM provider: {Request}", requestElement.GetRawText());
        
        var httpResponse = await llmClient.SendAsync(requestElement, ct);
        var responseBody = await httpResponse.Content.ReadAsStringAsync(ct);
        
        Log.Debug("Received response from LLM provider: {ResponseBody}", responseBody);

        var llmResponse = JsonSerializer.Deserialize<ChatCompletionResponse>(responseBody, JsonOptions)
                          ?? throw new InvalidOperationException("Failed to deserialize LLM response.");
        
        var deanonymizedChoices = llmResponse.Choices.Select(DeanonymizeChoice).ToList();
        
        Log.Information("Deanonymized {ChoiceCount} choices.", deanonymizedChoices.Count);
        Log.Debug("Deanonymized choices: {@Choices}", deanonymizedChoices);

        foreach (var choice in deanonymizedChoices)
        {
            if(choice.Message.Extensions?.TryGetValue("tool_calls", out var toolCalls) == true)
                Log.Debug("Deanonymized tool calls: {@ToolCalls}", toolCalls.GetRawText());
            else
                Log.Debug("Deanonymized content: {Content}", choice.Message.Content);
        }

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

        // Every caller message (any role) is anonymized; our own instruction is prepended verbatim.
        var anonymizedMessages = await BuildAnonymizedMessagesAsync(request.Messages, ct);

        // Force the configured model regardless of what the client sent, so the proxy
        // owns the model selection (the WebUI/Llm.Model setting is the single source).
        var anonymizedRequest = request with { Model = llmOptions.CurrentValue.Model, Messages = anonymizedMessages };
        var requestElement    = JsonSerializer.SerializeToElement(anonymizedRequest, JsonOptions);

        Log.Debug("Sending request to LLM provider: {Request}", requestElement.GetRawText());

        var llmHttpResponse = await llmClient.SendAsync(requestElement, ct);

        httpResponse.Headers.ContentType  = "text/event-stream";
        httpResponse.Headers.CacheControl = "no-cache";

        await using var stream       = await llmHttpResponse.Content.ReadAsStreamAsync(ct);
        using       var streamReader = new StreamReader(stream);

        while (await streamReader.ReadLineAsync(ct) is { } line && !ct.IsCancellationRequested)
        {
            Log.Debug("Received SSE line: {Line}", line);

            if (!line.StartsWith("data:")) continue;

            var data = line["data:".Length..].Trim();

            // [DONE] → flush and terminate stream
            if (data == "[DONE]")
            {
                var remaining = streamingDeanonymizer.FlushAll();
                foreach (var (_, content) in remaining)
                {
                    if (!string.IsNullOrEmpty(content))
                        await httpResponse.WriteAsync($"data: {content}\n\n", ct);
                }
                await httpResponse.WriteAsync("data: [DONE]\n\n", ct);
                break;
            }

            // parse Chunk as JsonNode
            var chunkNode = JsonNode.Parse(data) as JsonObject;
            if (chunkNode == null) continue;

            var choices = chunkNode["choices"]?.AsArray();
            if (choices == null || choices.Count == 0)
            {
                // chunk without choice → continue, no processing  
                await WriteChunk(httpResponse, chunkNode, ct);
                continue;
            }

            var firstChoice = choices[0]?.AsObject();
            if (firstChoice == null)
            {
                await WriteChunk(httpResponse, chunkNode, ct);
                continue;
            }

            var delta = firstChoice["delta"]?.AsObject();

            // path 1a: Text-Content available → through StreamingDeanonymizer
            var contentNode = delta?["content"];
            if (contentNode != null && contentNode.GetValueKind() == JsonValueKind.String)
            {
                var contentText = contentNode.GetValue<string>();
                if (!string.IsNullOrEmpty(contentText))
                {
                    var deanonymized = streamingDeanonymizer.ProcessFragment(
                        contentText, isFinal: false, contextKey: "content");
                    delta!["content"] = deanonymized;
                }
            }
            
            // path 1b: Reasoning-Tokens (Qwen3, DeepSeek-R1, o.ä.) → through StreamingDeanonymizer
            // some models have reasoning abilities.
            var reasoningNode = delta?["reasoning"];
            if (reasoningNode != null && reasoningNode.GetValueKind() == JsonValueKind.String)
            {
                var reasoningText = reasoningNode.GetValue<string>();
                if (!string.IsNullOrEmpty(reasoningText))
                {
                    var deanonymized = streamingDeanonymizer.ProcessFragment(
                                                                             reasoningText, isFinal: false, contextKey: "reasoning");
                    delta!["reasoning"] = deanonymized;
                }
            }

            // path 2: tool_calls available → foreach tool_call.arguments deanonymize
            var toolCallsArray = delta?["tool_calls"]?.AsArray();
            if (toolCallsArray != null)
            {
                for (int i = 0; i < toolCallsArray.Count; i++)
                {
                    var toolCall = toolCallsArray[i]?.AsObject();
                    var argsNode = toolCall?["function"]?["arguments"];
                    if (argsNode == null || argsNode.GetValueKind() != JsonValueKind.String)
                        continue;

                    var argsFragment = argsNode.GetValue<string>();
                    var deanonymized = streamingDeanonymizer.ProcessFragment(
                        argsFragment, isFinal: false, contextKey: $"tool_call_{i}");

                    toolCall!["function"]!["arguments"] = deanonymized;
                }
            }

            // path 3: no text. no tool_calls and no reasoning (e.g., finish_reason-Chunk) → 1:1 pass through
            // Don't do anything. :)
            
            await WriteChunk(httpResponse, chunkNode, ct);
        }
    }

    private static async Task WriteChunk(HttpResponse httpResponse, JsonNode chunkNode, CancellationToken ct)
    {
        await httpResponse.WriteAsync($"data: {chunkNode.ToJsonString()}\n\n", ct);
        await httpResponse.Body.FlushAsync(ct);
    }

    /// <summary>
    /// Deanonymizes the content and extensions of the provided <see cref="ChatCompletionChoice"/>
    /// by replacing anonymized data with their original values using the mapping store.
    /// </summary>
    /// <param name="choice">The chat completion choice containing anonymized content and optional extensions to be deanonymized.</param>
    /// <returns>A <see cref="ChatCompletionChoice"/> object with content and extensions deanonymized.</returns>
    /// <example>
    /// Given a choice with anonymized content:
    /// <code>
    /// var anonymizedChoice = new ChatCompletionChoice
    /// {
    ///     Message = new ChatMessage
    ///     {
    ///         Role = "assistant",
    ///         Content = "Hello [PERSON_a6ab9045d1042ef4], your email is [EMAIL_ADDRESS_445793faef225679]"
    ///     },
    ///     FinishReason = "stop"
    /// };
    /// 
    /// var deanonymized = DeanonymizeChoice(anonymizedChoice);
    /// // Result: "Hello John Doe, your email is john.doe@example.com"
    /// </code>
    /// </example>
    private ChatCompletionChoice DeanonymizeChoice(ChatCompletionChoice choice)
    {
        // deanonymize choice content
        var deanonymizedContent = mappingStore.Deanonymize(choice.Message.Content);
        
        // deanonymize choice extensions (e.g., tool_calls) by running their raw JSON through MappingStore.
        // JSON-aware variant: restored values are escaped, so a '"' or '\' inside an original
        // value cannot break the surrounding JSON (which would make the Deserialize below throw).
        Dictionary<string, JsonElement>? deanonymizedChoiceExtensions = null;

        // Start if there are extensions
        if (choice.Message.Extensions != null)
        {
            // Create a new dictionary with the same keys and values, but with the JSON elements deanonymized.
            deanonymizedChoiceExtensions = choice.Message.Extensions.ToDictionary(
                 kv => kv.Key,
                 kv => JsonSerializer.Deserialize<JsonElement>(mappingStore.DeanonymizeJson(kv.Value.GetRawText())));
        }

        // Return the choice with the deanonymized content and extensions
        return choice with
               {
                   Message = choice.Message with
                             {
                                 Content = deanonymizedContent,
                                 Extensions = deanonymizedChoiceExtensions
                             }
               };
    }

    /// <summary>
    /// A static instance of <see cref="ChatMessage"/> that represents the system's
    /// initialization instruction for the language model. It defines the assistant's
    /// behavior and guidelines for handling user requests containing placeholder tokens.
    /// </summary>
    /// <remarks>
    /// Configured with specific rules for interpreting placeholders, including
    /// - Treating placeholders as real values without modification.
    /// - Using the complete placeholder, including brackets, in tool call arguments and other contexts.
    /// - Adopting consistent use of double quotes in JSON formatting.
    /// These instructions guide the assistant's interaction and ensure compliance with strict processing rules.
    /// </remarks>
    private static readonly ChatMessage SystemInstruction = new()
                                                            {
                                                                Role = "system",
                                                                Content = """
                                                                          You are a helpful assistant. The users requests may contain placeholders tokens
                                                                          in the format of [TYPE_HASH16] where TYPE is uppercase and HASH16 is exactly
                                                                          16 lowercase hey characters. Example: [PERSON_a6ab9045d1042ef4] or [EMAIL_ADDRESS_445793faef225679]
                                                                          Always answer in the same language as the users request.
                                                                          
                                                                          IMPORTANT!!!!!:
                                                                          - Treat placeholders as real values.
                                                                          - In tool call arguments, etc., usw the COMPLETE Placeholder including brackets: [TYPE_HASH]
                                                                          - Never modify, shorten or reformat placeholders!
                                                                          - Never use single quotes, always use double quotes in JSON!
                                                                          """
                                                            };
}