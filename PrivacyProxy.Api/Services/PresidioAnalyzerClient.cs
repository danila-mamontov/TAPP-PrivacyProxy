using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using PrivacyProxy.Api.Configuration;
using PrivacyProxy.Api.Models.DTOs;
using PrivacyProxy.Api.Models.Enums;
using PrivacyProxy.Api.Models.Interfaces;

namespace PrivacyProxy.Api.Services;

public class PresidioAnalyzerClient(
    HttpClient                httpClient,
    IOptions<PresidioOptions> presidioOptions) : IPresidioAnalyzerClient
{
    private readonly PresidioOptions _options = presidioOptions.Value;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        Converters                  = { new JsonStringEnumConverter() }
    };

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