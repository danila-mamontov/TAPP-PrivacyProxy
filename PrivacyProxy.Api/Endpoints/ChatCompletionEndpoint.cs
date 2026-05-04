using PrivacyProxy.Api.Models.DTOs.LLM;
using PrivacyProxy.Api.Models.Interfaces;
using Serilog;

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
    /// Handles the processing of a chat completion request by validating the incoming data,
    /// invoking the chat completion service for processing, and generating an appropriate response.
    /// Supports both streaming and non-streaming response modes.
    /// </summary>
    /// <param name="request">The chat completion request containing the model, messages, and streaming configuration.</param>
    /// <param name="service">The service responsible for processing the chat completion logic, including interactions with the underlying LLM.</param>
    /// <param name="httpResponse">The HTTP response object used to manage streaming responses, if applicable.</param>
    /// <param name="ct">A cancellation token to manage task cancellation during the operation.</param>
    /// <returns>An asynchronous task containing the chat completion result or an empty HTTP result for streamed responses.</returns>
    private static async Task<IResult> HandleAsync(
        ChatCompletionRequest  request,
        IChatCompletionService service,
        HttpResponse           httpResponse, // ASP .NET initializes it automatically.
                                             // HttpResponse is the open TCP-Connection between
                                             // PrivacyProxy and Endpoint-Caller.
        CancellationToken ct)
    {
        Log.Information("Received chat completion request: {@Request}", request);
        
        if (request.Stream == true)
        {
            Log.Information("Streaming response requested.");
            await service.ProcessStreamAsync(request, httpResponse, ct);

            // ProcessStreamAsync sends response directly per httpResponse.body
            return Results.Empty;
        }
        
        Log.Information("Non-streaming response requested.");
        var response = await service.ProcessAsync(request, ct);
        
        return Results.Ok(response);
    }
}