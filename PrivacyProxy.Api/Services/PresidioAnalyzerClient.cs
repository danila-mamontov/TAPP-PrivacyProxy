using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using PrivacyProxy.Api.Configuration;
using PrivacyProxy.Api.Models.DTOs.Presidio;
using PrivacyProxy.Api.Models.Enums;
using PrivacyProxy.Api.Models.Interfaces;

namespace PrivacyProxy.Api.Services;

/// <summary>
/// A client for interacting with the Presidio Analyzer service to perform text analysis and entity recognition.
/// </summary>
public class PresidioAnalyzerClient(
    HttpClient                httpClient,
    IOptions<PresidioOptions> presidioOptions) : IPresidioAnalyzerClient
{
    /// <summary>
    /// Represents the configuration options for the Presidio Analyzer client.
    /// This variable is initialized with values from <see cref="PresidioOptions"/> and
    /// provides access to analyzer-related configurations such as allowed list, entity types,
    /// context, and score thresholds, essential for performing text analysis requests.
    /// </summary>
    private readonly PresidioOptions _options = presidioOptions.Value;

    /// <summary>
    /// Provides configuration options for JSON serialization and deserialization operations
    /// within the context of the PresidioAnalyzerClient.
    /// These options include custom naming policies, case sensitivity rules,
    /// null value handling, and specific JSON converters.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
                                                                {
                                                                    PropertyNamingPolicy        = JsonNamingPolicy.SnakeCaseLower,
                                                                    DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
                                                                    PropertyNameCaseInsensitive = true,
                                                                    Converters                  = { new JsonStringEnumConverter() }
                                                                };

    /// <summary>
    /// Analyzes the specified text for personally identifiable information (PII) based on the configured
    /// Presidio Analyzer options and returns a list of detected entities and their metadata.
    /// </summary>
    /// <param name="text">The input text to analyze for sensitive entities.</param>
    /// <param name="language">The language of the input text (e.g., English, German).</param>
    /// <param name="ct">An optional cancellation token that can be used to cancel the operation.</param>
    /// <returns>
    /// A read-only list of <see cref="PresidioAnalyzerResponse"/> representing the detected entities
    /// and their associated details.
    /// </returns>
    /// <exception cref="HttpRequestException">
    /// Thrown when the Presidio Analyzer service returns a non-success status code.
    /// </exception>
    /// <exception cref="JsonException">
    /// Thrown when the response from the Presidio Analyzer service cannot be deserialized.
    /// </exception>
    public async Task<IReadOnlyList<PresidioAnalyzerResponse>> AnalyzeAsync(
        string text,
        Language language,
        CancellationToken ct = default)
    {
        var request = new PresidioAnalyzerRequest
        {
            Text                  = text,
            Language              = language,
            AllowList             = _options.AllowList,
            Context               = _options.Context,
            ScoreThreshold        = _options.ScoreThreshold,
            Entities              = language == Language.German
                                        ? _options.GermanEntityTypes
                                        : _options.EnglishEntityTypes,
            ReturnDecisionProcess = false
        };

        var json     = JsonSerializer.Serialize(request, JsonOptions);
        var content  = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await httpClient.PostAsync("/analyze", content, ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"Presidio Analyzer returned {(int)response.StatusCode} " +
                $"{response.ReasonPhrase}: {body}");
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct);

        try
        {
            return JsonSerializer.Deserialize<List<PresidioAnalyzerResponse>>(
                       responseBody, JsonOptions) ?? [];
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Failed to deserialize Presidio response: {ex.Message}", ex);
        }
    }
}