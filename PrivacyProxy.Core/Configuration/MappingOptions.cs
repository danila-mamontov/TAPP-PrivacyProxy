namespace PrivacyProxy.Core.Configuration;

/// <summary>
/// Configuration options for <see cref="PrivacyProxy.Api.Services.MappingStore"/>.
/// </summary>
public class MappingOptions
{
    /// <summary>
    /// Gets the sliding-expiration time-to-live, in minutes, for PII-to-placeholder mappings
    /// held by the <see cref="PrivacyProxy.Api.Services.MappingStore"/>.
    /// </summary>
    /// <remarks>
    /// Every time a mapping is read (anonymization or deanonymization), its TTL is reset,
    /// so frequently used mappings are kept alive while unused ones are evicted after
    /// this many minutes of inactivity.
    /// </remarks>
    public double TtlMinutes { get; init; } = 30;
}
