using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using PrivacyProxy.Api.Configuration;
using PrivacyProxy.Api.Models.Interfaces;

namespace PrivacyProxy.Api.Services;

/// <summary>
/// Provides a client for interacting with a Language Model (LLM) service.
/// </summary>
/// <remarks>
/// The LlmClient is responsible for sending requests to a specified LLM service endpoint
/// and handling the responses. The client uses an HTTP POST method to send data
/// and requires an API key for authorization. Configuration for the client is provided
/// through <see cref="LlmOptions"/>.
/// </remarks>
public class LlmClient(
    HttpClient httpClient, 
    IOptions<LlmOptions> llmOptions) : ILlmClient
{
    /// <summary>
    /// Represents the configuration options for the Language Model service (LLM) used by this client.
    /// </summary>
    /// <remarks>
    /// This variable provides access to essential properties required to interact with the LLM service,
    /// such as the base URL and API key for authentication. It is initialized from the application's
    /// configuration settings and stored as a strongly typed object for convenient access during requests.
    /// </remarks>
    private readonly LlmOptions _llmOptions = llmOptions.Value;

    /// <summary>
    /// Defines shared options for JSON serialization within the application.
    /// </summary>
    /// <remarks>
    /// This static instance of <see cref="JsonSerializerOptions"/> is configured to use
    /// a snake_case naming policy for property serialization and ignores properties with null values
    /// during serialization. It is used to ensure consistent and efficient JSON payload formatting
    /// for communication with external services.
    /// </remarks>
    private static readonly JsonSerializerOptions JsonOptions = new()
                                                                {
                                                                    PropertyNamingPolicy   = JsonNamingPolicy.SnakeCaseLower,
                                                                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                                                                };

    /// Sends an HTTP POST request to the LLM provider's endpoint with the specified JSON payload.
    /// <param name="request">
    /// The JSON payload to be sent in the request body, represented as a JsonElement.
    /// </param>
    /// <param name="ct">
    /// A CancellationToken to observe while waiting for the task to complete. This parameter is optional.
    /// </param>
    /// <returns>
    /// A Task that represents the asynchronous operation. The task result contains the HttpResponseMessage
    /// received from the LLM provider if the request was successful.
    /// </returns>
    /// <exception cref="HttpRequestException">
    /// Thrown when the LLM provider returns a non-successful response code (e.g., 4xx, 5xx) with details
    /// about the response status and reason.
    /// </exception>
    public async Task<HttpResponseMessage> SendAsync(JsonElement request, CancellationToken ct = default)
    {
        var json    = JsonSerializer.Serialize(request, JsonOptions);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/chat/completions");
        httpRequest.Content = content;
        
        // Add the API key to the request headers
        httpRequest.Headers.Add("Authorization", $"Bearer {_llmOptions.ApiKey}");

        // Send the request and await the response with cancellation support and till headers are read
        // to avoid blocking the thread when using streaming responses
        var response = await httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);

        if (response.IsSuccessStatusCode) 
            return response;
        
        var body = await response.Content.ReadAsStringAsync(ct);
        throw new HttpRequestException(
                                       $"LLM provider returned {(int)response.StatusCode} " +
                                       $"{response.ReasonPhrase}: {body}");
    }
}