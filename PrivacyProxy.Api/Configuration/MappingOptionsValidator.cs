using Microsoft.Extensions.Options;

namespace PrivacyProxy.Api.Configuration;

/// <summary>
/// Provides validation logic for <see cref="MappingOptions"/> configurations.
/// </summary>
public class MappingOptionsValidator : IValidateOptions<MappingOptions>
{
    /// <summary>
    /// Validates the specified <see cref="MappingOptions"/> instance to ensure that
    /// <see cref="MappingOptions.TtlMinutes"/> is a positive value.
    /// </summary>
    /// <param name="name">The optional name of the options instance being validated.</param>
    /// <param name="options">The <see cref="MappingOptions"/> instance to validate.</param>
    /// <returns>A <see cref="ValidateOptionsResult"/> indicating whether validation succeeded.</returns>
    public ValidateOptionsResult Validate(string? name, MappingOptions options)
    {
        if (options.TtlMinutes <= 0)
            return ValidateOptionsResult.Fail("TtlMinutes must be greater than 0.");

        return ValidateOptionsResult.Success;
    }
}
