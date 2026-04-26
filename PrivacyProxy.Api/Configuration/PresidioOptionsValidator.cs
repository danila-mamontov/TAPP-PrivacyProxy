using Microsoft.Extensions.Options;

namespace PrivacyProxy.Api.Configuration;

public class PresidioOptionsValidator : IValidateOptions<PresidioOptions>
{
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