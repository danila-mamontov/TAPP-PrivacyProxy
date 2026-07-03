namespace PrivacyProxy.Api.Tests;

/// <summary>
/// Minimal in-memory configuration for the WebApplicationFactory-based tests so the
/// host passes its ValidateOnStart checks. Presidio and the LLM are mocked in those
/// tests, so these values only need to exist - they are never actually contacted.
/// </summary>
internal static class TestHostConfig
{
    public static readonly Dictionary<string, string?> RequiredOptions = new()
    {
        ["Presidio:AnalyzerUrl"] = "http://localhost:5002",
        ["Llm:BaseUrl"]          = "http://localhost:8000",
        ["Llm:ApiKey"]           = "test",
        ["Llm:Model"]            = "test",
    };
}
