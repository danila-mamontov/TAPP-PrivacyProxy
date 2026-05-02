using PrivacyProxy.Api.Models.DTOs.LLM;
using PrivacyProxy.Api.Services;

namespace PrivacyProxy.Api.Endpoints;

/// <summary>
/// Represents an endpoint for managing and processing chat completion requests,
/// allowing integration with large language models (LLM) while incorporating
/// mechanisms for ensuring privacy and data protection.
/// </summary>
public static class ChatCompletionEndpoint
{
    /// <summary>
    /// Maps the Chat Completion endpoint to the API pipeline.
    /// This endpoint processes requests for generating chat-based completions
    /// using a large language model (LLM) with integrated privacy protection mechanisms.
    /// </summary>
    /// <param name="app">The <see cref="IEndpointRouteBuilder"/> instance used to define API routes.</param>
    public static void MapChatCompletion(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/chat/completions", HandleAsync);
    }

    /// <summary>
    /// Handles the processing of a chat completion request by validating the request,
    /// delegating it to the chat completion service, and returning the processed response.
    /// </summary>
    /// <param name="request">The chat completion request containing the model, messages, and optional attributes.</param>
    /// <param name="service">The service responsible for handling chat completion logic, including tasks such as validation and interaction with the LLM.</param>
    /// <param name="ct">A cancellation token allowing the operation to be canceled if necessary.</param>
    /// <returns>A task representing the asynchronous operation, containing the result of the processed chat completion request or an error response.</returns>
    private static async Task<IResult> HandleAsync(
        ChatCompletionRequest request,
        ChatCompletionService service,
        CancellationToken     ct)
    {
        if (request.Stream == true)
            return Results.BadRequest("Streaming is not yet supported.");

        var response = await service.ProcessAsync(request, ct);
        return Results.Ok(response);
    }
}