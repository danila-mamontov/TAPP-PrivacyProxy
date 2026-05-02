using System.Text.Json;

namespace PrivacyProxy.Api.Models.Interfaces;

/// <summary>
/// Defines a contract for a client that interacts with a Language Model (LLM) service.
/// </summary>
/// <remarks>
/// Implementations of this interface are responsible for sending requests to a
/// Language Model (LLM) service and processing the corresponding responses.
/// The request payload typically contains structured data in JSON format, and
/// communication occurs via HTTP.
/// </remarks>
public interface ILlmClient
{
    /// Sends an HTTP POST request to a specified endpoint with a JSON payload.
    /// <param name="request">
    /// The JSON payload to be sent in the request body, represented as a JsonElement.
    /// </param>
    /// <param name="ct">
    /// A CancellationToken to monitor for cancellation requests. This parameter is optional.
    /// </param>
    /// <returns>
    /// A Task representing the asynchronous operation. The task result contains the HttpResponseMessage
    /// returned by the HTTP endpoint.
    /// </returns>
    /// <exception cref="HttpRequestException">
    /// Thrown when the server returns a non-success HTTP status code, providing details about the
    /// failure, including status code and response body.
    /// </exception>
    Task<HttpResponseMessage> SendAsync(JsonElement request, CancellationToken ct = default);
}