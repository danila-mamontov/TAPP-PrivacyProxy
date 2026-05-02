using System.Text.Json;
using System.Text.Json.Serialization;
using PrivacyProxy.Api.Models.DTOs.LLM;
using PrivacyProxy.Api.Models.Interfaces;

namespace PrivacyProxy.Api.Services;

/// <summary>
/// The <c>ChatCompletionService</c> class is designed to handle chat completion requests,
/// ensuring that user data is anonymized, processed via an LLM (Large Language Model),
/// and deanonymized securely and efficiently.
/// </summary>
/// <remarks>
/// By leveraging dependencies for anonymization, LLM communication, and mapping management,
/// this service facilitates privacy-aware interactions. It anonymizes incoming user messages,
/// sends them to the LLM for processing, and uses a mapping system to deanonymize the responses,
/// ensuring the protection of sensitive information throughout the workflow.
/// </remarks>
/// <param name="presidioService">
/// A service responsible for identifying and anonymizing PII (Personally Identifiable Information)
/// within the user's input.
/// </param>
/// <param name="llmClient">
/// An LLM client used to send anonymized requests and receive processed responses from a
/// large language model.
/// </param>
/// <param name="mappingStore">
/// A component managing the storage and application of anonymization mappings
/// to ensure accurate deanonymization of responses.
/// </param>
public class ChatCompletionService(
    IPresidioService presidioService,
    ILlmClient       llmClient,
    IMappingStore    mappingStore) : IChatCompletionService
{
    /// <summary>
    /// A static instance of <see cref="JsonSerializerOptions"/> utilized to define
    /// JSON serialization and deserialization settings specific to the service.
    /// </summary>
    /// <remarks>
    /// Configured to support the following behavior:
    /// - Applies SnakeCaseLower naming convention for property names to meet API schema requirements.
    /// - Excludes null values during serialization to minimize data payload.
    /// - Allows case-insensitive matching of property names for deserialization flexibility.
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
    /// Processes a chat completion request by anonymizing input messages, sending the anonymized request
    /// to a Large Language Model (LLM), and deanonymizing the responses received from the LLM.
    /// </summary>
    /// <param name="request">The chat completion request containing the input messages to be processed.</param>
    /// <param name="ct">Optional cancellation token to cancel the operation if needed.</param>
    /// <returns>A <see cref="ChatCompletionResponse"/> object containing the processed and deanonymized results
    /// from the LLM.</returns>
    public async Task<ChatCompletionResponse> ProcessAsync(
        ChatCompletionRequest request,
        CancellationToken     ct = default)
    {
        // Anonymize each message content
        var anonymizedMessages = new List<ChatMessage>();
        foreach (var message in request.Messages)
        {
            var anonymizedContent = await presidioService.AnonymizeAsync(message.Content, ct);
            anonymizedMessages.Add(message with { Content = anonymizedContent });
        }

        // Forward anonymized request to LLM
        var anonymizedRequest = request with { Messages = anonymizedMessages };
        var requestElement    = JsonSerializer.SerializeToElement(anonymizedRequest, JsonOptions);
        var httpResponse      = await llmClient.SendAsync(requestElement, ct);
        var responseBody      = await httpResponse.Content.ReadAsStringAsync(ct);
        var llmResponse       = JsonSerializer.Deserialize<ChatCompletionResponse>(responseBody, JsonOptions)
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

        return llmResponse with { Choices = deanonymizedChoices };
    }
}