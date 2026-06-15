namespace PrivacyProxy.Core.Configuration;

/// <summary>
/// Represents configuration options for integrating with a Language Model service (LLM).
/// </summary>
/// <remarks>
/// Configures the details required to connect to a Language Model service, such as the base URL
/// and the API key. Properties are mutable so the WebUI can edit them at runtime; required values
/// are enforced by <c>LlmOptionsValidator</c> on startup and on every reload.
/// </remarks>
public class LlmOptions
{
    /// <summary>
    /// Gets or sets the base URL of the LLM service (OpenAI-compatible "v1" root, with trailing slash).
    /// </summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the API key used to authenticate with the LLM service.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the model identifier used for chat completions.
    /// </summary>
    public string Model { get; set; } = string.Empty;
}
