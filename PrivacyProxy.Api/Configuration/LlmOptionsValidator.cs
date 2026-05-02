using Microsoft.Extensions.Options;

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
    /// Validates the specified LlmOptions instance to ensure required properties are correctly set.
    /// </summary>
    /// <param name="name">
    /// The name of the option instance being validated. This can be null if no name is specified.
    /// </param>
    /// <param name="options">
    /// The LlmOptions instance to validate. This object contains the configuration values for the LLM service.
    /// </param>
    /// <returns>
    /// A ValidateOptionsResult that indicates whether the validation was successful.
    /// Returns a failure result if required fields such as BaseUrl or ApiKey are missing or empty.
    /// </returns>
    public ValidateOptionsResult Validate(string? name, LlmOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.BaseUrl))
            return ValidateOptionsResult.Fail("BaseUrl is required.");
        
        if (string.IsNullOrWhiteSpace(options.ApiKey))
            return ValidateOptionsResult.Fail("ApiKey is required.");
        
        return ValidateOptionsResult.Success;
    }
}