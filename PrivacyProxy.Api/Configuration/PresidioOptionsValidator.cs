using Microsoft.Extensions.Options;
using Serilog;

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

        // Check if Presidio is reachable
        PresidioIsReachable(options);

        return ValidateOptionsResult.Success;
    }

    /// <summary>
    /// Checks whether the configured Presidio Analyzer endpoint is reachable by calling its health endpoint
    /// and logging the result. Connectivity issues are logged but do not fail validation.
    /// </summary>
    private static void PresidioIsReachable(PresidioOptions options)
    {
        Log.Debug("Checking Presidio connectivity...");
        using var client = new HttpClient();
        client.Timeout = TimeSpan.FromSeconds(10);

        var healthUrl = $"{options.AnalyzerUrl.TrimEnd('/')}/health";

        try
        {
            var response = client.GetAsync(healthUrl).GetAwaiter().GetResult();
            if (response.IsSuccessStatusCode)
            {
                Log.Information("Presidio reachable at {Url}: {StatusCode}", healthUrl, response.StatusCode);
            }
            else
            {
                Log.Warning("Presidio responded with non-success status at {Url}: {StatusCode}", 
                            healthUrl, response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Presidio not reachable at {Url}", healthUrl);
        }
    }
}