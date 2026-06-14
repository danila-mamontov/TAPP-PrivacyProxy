namespace PrivacyProxy.Api.Configuration;

/// <summary>
/// Represents configuration options for integrating with a Language Model service (LLM).
/// </summary>
/// <remarks>
/// This class is used to configure the necessary details required to connect to a Language Model service,
/// such as the base URL for the service endpoint and the API key used for authentication.
/// </remarks>
public class LlmOptions
{
    /// <summary>
    /// Gets or initializes the base URL of the Language Model (LLM) service.
    /// </summary>
    /// <remarks>
    /// This property specifies the root endpoint used to interact with the LLM service.
    /// It is a required configuration and must be provided for the application to function correctly.
    /// </remarks>
    public required string BaseUrl { get; init; }

    /// <summary>
    /// Represents the API key required to authenticate with the LLM service.
    /// </summary>
    /// <remarks>
    /// This property is a required configuration setting used to authorize requests to the LLM service
    /// specified by the <c>BaseUrl</c>. It must be a non-empty string for the application to operate correctly.
    /// </remarks>
    /// <value>
    /// A string containing the API key used for authentication.
    /// </value>
    public required string ApiKey  { get; init; }
    
    /// <summary>
    /// Gets or initializes the specific model identifier for the Language Model (LLM) service.
    /// </summary>
    /// <remarks>
    /// This property represents the name or version of the language model to be utilized during interactions
    /// with the LLM service. It is a required configuration that determines the specific model
    /// to be engaged for processing requests.
    /// </remarks>
    public required string Model { get; init; }
}