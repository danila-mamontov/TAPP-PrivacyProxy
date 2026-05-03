using PrivacyProxy.Api.Models.DTOs.LLM;

namespace PrivacyProxy.Api.Models.Interfaces;

/// <summary>
/// Interface defining the contract for a service responsible for processing chat completions
/// using a large language model (LLM).
/// </summary>
public interface IChatCompletionService
{
    /// <summary>
    /// Processes the specified chat completion request by interacting with the underlying LLM client
    /// and additional services to generate a response.
    /// </summary>
    /// <param name="request">
    /// An instance of <see cref="ChatCompletionRequest"/> containing the model, messages, and related parameters
    /// for the chat completion operation.
    /// </param>
    /// <param name="ct">
    /// An optional <see cref="CancellationToken"/> to notify the method about cancellation requests.
    /// </param>
    /// <returns>
    /// A task that represents the asynchronous operation. The task's result contains an instance of
    /// <see cref="ChatCompletionResponse"/> with the results of the chat completion operation.
    /// </returns>
    public Task<ChatCompletionResponse> ProcessAsync(ChatCompletionRequest request, CancellationToken ct = default);
    
    public Task ProcessStreamAsync(ChatCompletionRequest request, HttpResponse httpResponse, CancellationToken ct = default);
}