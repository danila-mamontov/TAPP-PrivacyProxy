using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using PrivacyProxy.Api.Configuration;
using PrivacyProxy.Api.Models.DTOs.Presidio;
using PrivacyProxy.Api.Models.Enums;
using PrivacyProxy.Api.Models.Interfaces;
using Serilog;

namespace PrivacyProxy.Api.Services;

/// <summary>
/// A client for interacting with the Presidio Analyzer service to perform text analysis and entity recognition.
/// </summary>
public class PresidioAnalyzerClient(
    HttpClient                httpClient,
    IOptionsMonitor<PresidioOptions> presidioOptions) : IPresidioAnalyzerClient
{
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
        // Read current (hot-reloadable) options so config changes apply without a restart.
        var options = presidioOptions.CurrentValue;

        var request = new PresidioAnalyzerRequest
        {
            Text                  = text,
            Language              = language,
            AllowList             = options.AllowList,
            Context               = options.Context,
            ScoreThreshold        = options.ScoreThreshold,
            Entities              = language == Language.German
                                        ? options.GermanEntityTypes
                                        : options.EnglishEntityTypes,
            ReturnDecisionProcess = false
        };

        var json     = JsonSerializer.Serialize(request, JsonOptions);
        var content  = new StringContent(json, Encoding.UTF8, "application/json");
        
        Log.Information("Sending request to Presidio Analyzer");
        Log.Debug("Sending request to Presidio Analyzer: {Request}", json);
        
        var response = await httpClient.PostAsync("/analyze", content, ct);

        if (!response.IsSuccessStatusCode)
        {
            Log.Error("Presidio Analyzer returned non-success status code: {StatusCode}", response.StatusCode);
            
            var body = await response.Content.ReadAsStringAsync(ct);
            
            Log.Debug("Presidio Analyzer response body: {ResponseBody}", body);
            
            throw new HttpRequestException(
                $"Presidio Analyzer returned {(int)response.StatusCode} " +
                $"{response.ReasonPhrase}: {body}");
        }
        
        var responseBody = await response.Content.ReadAsStringAsync(ct);
        
        Log.Information("Received response from Presidio Analyzer");
        Log.Debug("Received response from Presidio Analyzer: {ResponseBody}", responseBody);

        try
        {
            return JsonSerializer.Deserialize<List<PresidioAnalyzerResponse>>(
                       responseBody, JsonOptions) ?? [];
        }
        catch (JsonException ex)
        {
            Log.Error(ex, "Failed to deserialize Presidio response");
            
            throw new InvalidOperationException(
                $"Failed to deserialize Presidio response: {ex.Message}", ex);
        }
    }
}