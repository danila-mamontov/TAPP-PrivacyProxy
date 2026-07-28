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
/// while maintaining user privacy through pseudonymization and secure data handling.
/// </summary>
/// <remarks>
/// This service integrates pseudonymization, interaction with a Large Language Model (LLM),
/// and secure depseudonymization mechanisms. Every incoming message (regardless of role) is pseudonymized
/// before it is forwarded to the LLM, and the responses are depseudonymized again, ensuring sensitive
/// data remains protected throughout the process.
/// </remarks>
/// <param name="presidioService">
/// A service responsible for detecting and pseudonymizing sensitive or personally identifiable information
/// in incoming messages.
/// </param>
/// <param name="llmClient">
/// A client interface for communicating with the configured Large Language Model for processing
/// pseudonymized chat inputs and generating responses.
/// </param>
/// <param name="mappingStore">
/// A storage mechanism for managing pseudonymization mappings to ensure accurate depseudonymization
/// of LLM-generated output.
/// </param>
/// <param name="streamingDepseudonymizer">
/// A component used for streaming depseudonymization of LLM responses to provide real-time privacy-aware outputs.
/// </param>
public class ChatCompletionService(
    IPresidioService      presidioService,
    ILlmClient            llmClient,
    IMappingStore         mappingStore,
    StreamingDepseudonymizer streamingDepseudonymizer,
    IOptionsMonitor<LlmOptions> llmOptions) : IChatCompletionService
{
    /// <summary>
    /// Builds the messages forwarded to the LLM: our own placeholder-handling instruction verbatim
    /// (never pseudonymized, never altered) followed by EVERY caller message — regardless of role —
    /// with its content pseudonymized via Presidio. Maximum privacy: no caller content reaches the LLM
    /// without passing through pseudonymization.
    /// </summary>
    private async Task<List<ChatMessage>> BuildPseudonymizedMessagesAsync(
        IReadOnlyList<ChatMessage> incoming, CancellationToken ct)
    {
        var messages = new List<ChatMessage>(incoming.Count + 1) { SystemInstruction };

        foreach (var message in incoming)
        {
            var pseudonymizedContent = await presidioService.PseudonymizeAsync(message.Content, ct);
            var pseudonymizedExtensions = await PseudonymizeToolCallArgumentsAsync(message.Extensions, ct);
            messages.Add(message with { Content = pseudonymizedContent, Extensions = pseudonymizedExtensions });
        }

        return messages;
    }

    /// <summary>
    /// Pseudonymizes the <c>arguments</c> of any assistant <c>tool_calls</c> carried in a message's
    /// extension data. A real multi-round agent echoes its previous tool call (whose arguments were
    /// depseudonymized on the way out so the tool could run) back in the conversation history. Those
    /// arguments live in <see cref="ChatMessage.Extensions"/>, not in <c>content</c>, so without this
    /// step the real PII inside them would reach the LLM on the next round — defeating pseudonymization.
    /// Returns a new extensions dictionary with pseudonymized tool-call arguments, or the original
    /// reference when there is nothing to pseudonymize.
    /// </summary>
    private async Task<IDictionary<string, JsonElement>?> PseudonymizeToolCallArgumentsAsync(
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

            // The whole arguments JSON string is pseudonymized as text: Presidio replaces any PII
            // value with its placeholder, and placeholders contain no JSON-breaking characters,
            // so the argument object's structure stays intact.
            var pseudonymizedArguments = await presidioService.PseudonymizeAsync(argumentsJson, ct);
            if (!string.Equals(pseudonymizedArguments, argumentsJson, StringComparison.Ordinal))
            {
                toolCall["function"]!["arguments"] = pseudonymizedArguments;
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
    /// Processes a chat completion request by pseudonymizing the content of input messages,
    /// forwarding the pseudonymized data to a Large Language Model (LLM), and depseudonymizing
    /// the responses received from the LLM.
    /// </summary>
    /// <param name="request">The chat completion request containing the input messages to be processed.</param>
    /// <param name="ct">An optional cancellation token for cancelling the operation if necessary.</param>
    /// <returns>A <see cref="ChatCompletionResponse"/> object containing the processed results
    /// with contents depseudonymized.</returns>
    public async Task<ChatCompletionResponse> ProcessAsync(
        ChatCompletionRequest request,
        CancellationToken     ct = default)
    {

        Log.Information("Processing {MessageCount} messages...", request.Messages.Count);

        // Every caller message (any role) is pseudonymized; our own instruction is prepended verbatim.
        var pseudonymizedMessages = await BuildPseudonymizedMessagesAsync(request.Messages, ct);

        // Forward pseudonymized request to LLM
        // Force the configured model regardless of what the client sent, so the proxy
        // owns the model selection (the WebUI/Llm.Model setting is the single source).
        var pseudonymizedRequest = request with { Model = llmOptions.CurrentValue.Model, Messages = pseudonymizedMessages };
        var requestElement    = JsonSerializer.SerializeToElement(pseudonymizedRequest, JsonOptions);
        
        Log.Debug("Sending request to LLM provider: {Request}", requestElement.GetRawText());
        
        var httpResponse = await llmClient.SendAsync(requestElement, ct);
        var responseBody = await httpResponse.Content.ReadAsStringAsync(ct);
        
        Log.Debug("Received response from LLM provider: {ResponseBody}", responseBody);

        var llmResponse = JsonSerializer.Deserialize<ChatCompletionResponse>(responseBody, JsonOptions)
                          ?? throw new InvalidOperationException("Failed to deserialize LLM response.");
        
        var depseudonymizedChoices = llmResponse.Choices.Select(DepseudonymizeChoice).ToList();
        
        Log.Information("Depseudonymized {ChoiceCount} choices.", depseudonymizedChoices.Count);
        Log.Debug("Depseudonymized choices: {@Choices}", depseudonymizedChoices);

        foreach (var choice in depseudonymizedChoices)
        {
            if(choice.Message.Extensions?.TryGetValue("tool_calls", out var toolCalls) == true)
                Log.Debug("Depseudonymized tool calls: {@ToolCalls}", toolCalls.GetRawText());
            else
                Log.Debug("Depseudonymized content: {Content}", choice.Message.Content);
        }

        return llmResponse with { Choices = depseudonymizedChoices };
    }

    /// <summary>
    /// Processes a streaming chat completion request by pseudonymizing input messages, sending the pseudonymized request
    /// to a Large Language Model (LLM), and streaming the depseudonymized responses back to the client as
    /// Server-Sent Events (SSE).
    /// </summary>
    /// <param name="request">The chat completion request containing the input messages to be pseudonymized and processed.</param>
    /// <param name="httpResponse">The HTTP response through which the streaming results are sent back to the client.</param>
    /// <param name="ct">Optional cancellation token to cancel the operation if needed.</param>
    /// <returns>A task that represents the asynchronous operation of processing the streaming request.</returns>
    public async Task ProcessStreamAsync(
        ChatCompletionRequest request,
        HttpResponse          httpResponse,
        CancellationToken     ct = default)
    {
        Log.Information("Processing {MessageCount} messages...", request.Messages.Count);

        // Every caller message (any role) is pseudonymized
        var pseudonymizedMessages = await BuildPseudonymizedMessagesAsync(request.Messages, ct);

        // Force the configured model regardless of what the client sent, so the proxy
        // owns the model selection
        var pseudonymizedRequest = request with { Model = llmOptions.CurrentValue.Model, Messages = pseudonymizedMessages };
        var requestElement    = JsonSerializer.SerializeToElement(pseudonymizedRequest, JsonOptions);

        Log.Debug("Sending request to LLM provider: {Request}", requestElement.GetRawText());

        var llmHttpResponse = await llmClient.SendAsync(requestElement, ct);

        httpResponse.Headers.ContentType  = "text/event-stream";
        httpResponse.Headers.CacheControl = "no-cache";

        await using var stream       = await llmHttpResponse.Content.ReadAsStreamAsync(ct);
        using       var streamReader = new StreamReader(stream);

        // tool_call arguments are NOT streamed out fragment by fragment (see part 2).
        // They are collected puffered here (key = position in the tool_calls array) and sent as ONE
        // complete, JSON-aware depseudonymized chunk right before [DONE].
        // DSR Approach found in exp for RQ1, see exp FINDINGS.md
        var toolCallArguments   = new Dictionary<int, string>();
        var toolCallChoiceIndex = 0;

        while (await streamReader.ReadLineAsync(ct) is { } line && !ct.IsCancellationRequested)
        {
            Log.Debug("Received SSE line: {Line}", line);

            if (!line.StartsWith("data:")) continue;

            var data = line["data:".Length..].Trim();

            // [DONE] → flush and terminate stream
            if (data == "[DONE]")
            {
                // Send the held-back tool_call arguments (see path 2): complete and
                // depseudonymized JSON-aware, so restored values are escaped correctly.
                foreach (var (index, arguments) in toolCallArguments)
                {
                    var finalChunk = new JsonObject
                    {
                        ["object"]  = "chat.completion.chunk",
                        ["choices"] = new JsonArray(new JsonObject
                        {
                            ["index"] = toolCallChoiceIndex,
                            ["delta"] = new JsonObject
                            {
                                ["tool_calls"] = new JsonArray(new JsonObject
                                {
                                    ["index"]    = index,
                                    ["function"] = new JsonObject
                                    {
                                        ["arguments"] = mappingStore.DepseudonymizeJson(arguments)
                                    }
                                })
                            }
                        })
                    };
                    await WriteChunk(httpResponse, finalChunk, ct);
                }

                // Leftover carry (e.g. the text ends with an unclosed '[' that looked
                // like a placeholder start) must go out as a PROPER chunk - a raw text
                // line would be invalid SSE JSON and break every OpenAI client.
                var remaining = streamingDepseudonymizer.FlushAll();
                foreach (var (contextKey, leftover) in remaining)
                {
                    if (string.IsNullOrEmpty(leftover)) continue;
                    var leftoverChunk = new JsonObject
                    {
                        ["object"]  = "chat.completion.chunk",
                        ["choices"] = new JsonArray(new JsonObject
                        {
                            ["index"] = 0,
                            ["delta"] = new JsonObject
                            {
                                [contextKey == "reasoning" ? "reasoning" : "content"] = leftover
                            }
                        })
                    };
                    await WriteChunk(httpResponse, leftoverChunk, ct);
                }
                await httpResponse.WriteAsync("data: [DONE]\n\n", ct);
                break;
            }

            // parse Chunk as JsonNode
            if (JsonNode.Parse(data) is not JsonObject chunkNode) continue;

            var choices = chunkNode["choices"]?.AsArray();
            if (choices == null || choices.Count == 0)
            {
                // chunk without choice -> continue, no processing  
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

            // path 1a: Text-Content available → through StreamingDepseudonymizer
            var contentNode = delta?["content"];
            if (contentNode != null && contentNode.GetValueKind() == JsonValueKind.String)
            {
                var contentText = contentNode.GetValue<string>();
                if (!string.IsNullOrEmpty(contentText))
                {
                    var depseudonymized = streamingDepseudonymizer.ProcessFragment(
                        contentText, isFinal: false, contextKey: "content");
                    delta!["content"] = depseudonymized;
                }
            }
            
            // path 1b: Reasoning-Tokens (Qwen3, DeepSeek-R1, o.ä.) → through StreamingDepseudonymizer
            // some models have reasoning abilities.
            var reasoningNode = delta?["reasoning"];
            if (reasoningNode != null && reasoningNode.GetValueKind() == JsonValueKind.String)
            {
                var reasoningText = reasoningNode.GetValue<string>();
                if (!string.IsNullOrEmpty(reasoningText))
                {
                    var depseudonymized = streamingDepseudonymizer.ProcessFragment(
                                                                             reasoningText, isFinal: false, contextKey: "reasoning");
                    delta!["reasoning"] = depseudonymized;
                }
            }

            // path 2: tool_calls available -> collect the arguments instead of streaming them.
            // Depseudonymizing fragment by fragment cannot escape restored values correctly
            // (a '"' inside an original value would corrupt the arguments JSON). So the raw
            // fragments are buffered and sent as ONE complete chunk at [DONE] - no one
            // consumes a half tool_call anyway, so nothing is lost by waiting.
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
                    toolCallArguments[i] = toolCallArguments.GetValueOrDefault(i, "") + argsFragment;
                    toolCallChoiceIndex  = firstChoice["index"]?.GetValue<int>() ?? 0;

                    toolCall!["function"]!["arguments"] = "";   // hold the text back for now
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
    /// Depseudonymizes the content and extensions of the provided <see cref="ChatCompletionChoice"/>
    /// by replacing pseudonymized data with their original values using the mapping store.
    /// </summary>
    /// <param name="choice">The chat completion choice containing pseudonymized content and optional extensions to be depseudonymized.</param>
    /// <returns>A <see cref="ChatCompletionChoice"/> object with content and extensions depseudonymized.</returns>
    /// <example>
    /// Given a choice with pseudonymized content:
    /// <code>
    /// var pseudonymizedChoice = new ChatCompletionChoice
    /// {
    ///     Message = new ChatMessage
    ///     {
    ///         Role = "assistant",
    ///         Content = "Hello [PERSON_a6ab9045d1042ef4], your email is [EMAIL_ADDRESS_445793faef225679]"
    ///     },
    ///     FinishReason = "stop"
    /// };
    /// 
    /// var depseudonymized = DepseudonymizeChoice(pseudonymizedChoice);
    /// // Result: "Hello John Doe, your email is john.doe@example.com"
    /// </code>
    /// </example>
    private ChatCompletionChoice DepseudonymizeChoice(ChatCompletionChoice choice)
    {
        // depseudonymize choice content
        var depseudonymizedContent = mappingStore.Depseudonymize(choice.Message.Content);
        
        // depseudonymize choice extensions (e.g., tool_calls) by running their raw JSON through MappingStore.
        // JSON-aware variant: restored values are escaped, so a '"' or '\' inside an original
        // value cannot break the surrounding JSON (which would make the Deserialize below throw).
        Dictionary<string, JsonElement>? depseudonymizedChoiceExtensions = null;

        // Start if there are extensions
        if (choice.Message.Extensions != null)
        {
            // Create a new dictionary with the same keys and values, but with the JSON elements depseudonymized.
            depseudonymizedChoiceExtensions = choice.Message.Extensions.ToDictionary(
                 kv => kv.Key,
                 kv => JsonSerializer.Deserialize<JsonElement>(mappingStore.DepseudonymizeJson(kv.Value.GetRawText())));
        }

        // Return the choice with the depseudonymized content and extensions
        return choice with
               {
                   Message = choice.Message with
                             {
                                 Content = depseudonymizedContent,
                                 Extensions = depseudonymizedChoiceExtensions
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