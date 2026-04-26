using Microsoft.Extensions.Options;

namespace PrivacyProxy.Api.Configuration;

/// <summary>
/// Provides validation logic for instances of the <see cref="PresidioOptions"/> class.
/// </summary>
/// <remarks>
/// Validates configuration properties for Presidio integration and ensures the following:
/// - The <see cref="PresidioOptions.AnalyzerUrl"/> is specified and not empty.
/// - The global <see cref="PresidioOptions.ScoreThreshold"/> does not exceed the minimum threshold
/// value among the German and English entity-specific thresholds.
/// </remarks>
/// <example>
/// This validator is invoked automatically when configuring options using
/// the dependency injection framework and <see cref="IOptions{TOptions}"/> in the application.
/// </example>
public class PresidioOptionsValidator : IValidateOptions<PresidioOptions>
{
    /// <summary>
    /// Validates the provided <see cref="PresidioOptions"/> instance to ensure all required configurations
    /// and constraints are satisfied.
    /// </summary>
    /// <param name="name">
    /// The name of the option instance being validated. Can be null for unnamed options.
    /// </param>
    /// <param name="options">
    /// The <see cref="PresidioOptions"/> instance containing the configuration values to validate.
    /// </param>
    /// <returns>
    /// Returns a <see cref="ValidateOptionsResult"/> indicating whether the validation succeeded or failed.
    /// If validation fails, the result includes an error message describing the failure reason.
    /// </returns>
    public ValidateOptionsResult Validate(string? name, PresidioOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.AnalyzerUrl))
            return ValidateOptionsResult.Fail("AnalyzerUrl is required.");

        var minThreshold = options.GermanEntityThresholds.Values
                                  .Concat(options.EnglishEntityThresholds.Values)
                                  .DefaultIfEmpty(double.MaxValue)
                                  .Min();

        if (options.ScoreThreshold > minThreshold)
            return ValidateOptionsResult.Fail(
                                              $"ScoreThreshold ({options.ScoreThreshold}) must be " +
                                              $"≤ min(GermanEntityThresholds, EnglishEntityThresholds) ({minThreshold}).");

        return ValidateOptionsResult.Success;
    }
}