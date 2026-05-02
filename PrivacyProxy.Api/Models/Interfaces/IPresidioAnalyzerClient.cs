using PrivacyProxy.Api.Models.DTOs;
using PrivacyProxy.Api.Models.Enums;

namespace PrivacyProxy.Api.Models.Interfaces;

/// <summary>
/// Provides an interface for analyzing text to identify sensitive information using Presidio Analyzer.
/// </summary>
public interface IPresidioAnalyzerClient
{
    /// <summary>
    /// Analyzes the specified text with the Presidio Analyzer and identifies entities
    /// along with their positions, types, confidence scores, and optional analysis explanations.
    /// </summary>
    /// <param name="text">
    /// The text to analyze for identifying sensitive entities.
    /// </param>
    /// <param name="language">
    /// The language in which the text is written. Supported languages are represented by the <see cref="Language"/> enum.
    /// </param>
    /// <param name="ct">
    /// A <see cref="CancellationToken"/> that can be used to cancel the operation.
    /// </param>
    /// <returns>
    /// A task that represents the asynchronous operation.
    /// The task result contains a read-only list of <see cref="PresidioAnalyzerResponse"/> objects,
    /// each representing an identified entity and its analysis details.
    /// </returns>
    Task<IReadOnlyList<PresidioAnalyzerResponse>> AnalyzeAsync(
        string            text,
        Language          language,
        CancellationToken ct = default);
}