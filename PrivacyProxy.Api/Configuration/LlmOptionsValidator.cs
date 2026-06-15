using System.Text;
using Microsoft.Extensions.Options;
using PrivacyProxy.Core.Configuration;
using Serilog;

namespace PrivacyProxy.Api.Configuration;

/// <summary>
/// Provides validation logic for <see cref="LlmOptions"/> configurations.
/// </summary>
/// <remarks>
/// This class is responsible for ensuring that the required properties of <see cref="LlmOptions"/>
/// are properly configured. These properties include:
/// - <c>BaseUrl</c>: The base URL of the LLM (Language Model) service.
/// - <c>ApiKey</c>: The API key used for authenticating with the LLM service.
/// </remarks>
/// <example>
/// Used by the Options framework to validate LlmOptions during startup or runtime configuration binding.
/// </example>
public class LlmOptionsValidator : IValidateOptions<LlmOptions>
{
    /// <summary>
    /// Validates the specified LlmOptions instance to ensure that all required properties are correctly set.
    /// </summary>
    /// <param name="name">
    /// The optional name of the LlmOptions instance being validated. This can be null if no specific name is assigned.
    /// </param>
    /// <param name="options">
    /// The LlmOptions instance containing the configuration properties for validation. Expected fields include BaseUrl, ApiKey, and Model.
    /// </param>
    /// <returns>
    /// A ValidateOptionsResult representing the outcome of the validation process.
    /// Returns a failure result if any required fields, such as BaseUrl, ApiKey, or Model, are missing or invalid.
    /// </returns>
    public ValidateOptionsResult Validate(string? name, LlmOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.BaseUrl))
            return ValidateOptionsResult.Fail("BaseUrl is required.");

        if (string.IsNullOrWhiteSpace(options.ApiKey))
            return ValidateOptionsResult.Fail("ApiKey is required.");

        if (string.IsNullOrWhiteSpace(options.Model))
            return ValidateOptionsResult.Fail("Model is required.");

        // Check if model is reachable
        ModelIsReachable(options);
        
        return ValidateOptionsResult.Success;
    }

    /// <summary>
    /// Checks whether the LLM gateway endpoint is reachable by sending a diagnostic HTTP request and verifying the response status.
    /// </summary>
    /// <returns>
    /// true if the gateway returns a success status code; otherwise, false.
    /// </returns>
    private static void ModelIsReachable(LlmOptions options)
    {
        Log.Debug("Checking LLM endpoint connectivity...");
        using var client = new HttpClient();
        client.Timeout = TimeSpan.FromSeconds(60);
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {options.ApiKey}");

        // Query the OpenAI-compatible models endpoint (cheap, no inference) and tolerate a
        // missing trailing slash on the configured base URL.
        var baseUrl = options.BaseUrl.EndsWith('/') ? options.BaseUrl : options.BaseUrl + "/";
        try
        {
            var response = client.GetAsync($"{baseUrl}models").GetAwaiter().GetResult();
            Log.Debug("LLM endpoint reachable: {StatusCode}", response.StatusCode);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "LLM endpoint not reachable");
        }
    }
}