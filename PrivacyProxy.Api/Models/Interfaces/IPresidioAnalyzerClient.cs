using PrivacyProxy.Api.Models.DTOs;
using PrivacyProxy.Api.Models.Enums;

namespace PrivacyProxy.Api.Models.Interfaces;

public interface IPresidioAnalyzerClient
{
    Task<IReadOnlyList<PresidioAnalyzerResponse>> AnalyzeAsync(
        string            text,
        Language          language,
        CancellationToken ct = default);
}