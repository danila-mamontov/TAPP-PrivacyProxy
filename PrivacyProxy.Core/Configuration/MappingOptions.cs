namespace PrivacyProxy.Core.Configuration;

/// <summary>
/// Configuration options for the mapping store that holds PII-to-placeholder mappings.
/// </summary>
public class MappingOptions
{
    /// <summary>
    /// Gets or sets the sliding-expiration time-to-live, in minutes, for PII-to-placeholder mappings.
    /// </summary>
    /// <remarks>
    /// Every time a mapping is read (anonymization or deanonymization) its TTL is reset, so frequently
    /// used mappings are kept alive while unused ones are evicted after this many minutes of inactivity.
    /// </remarks>
    public double TtlMinutes { get; set; } = 30;
}
