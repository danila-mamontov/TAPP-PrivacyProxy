namespace PrivacyProxy.Api.Models.Interfaces;

/// <summary>
/// Defines a contract for processing text to identify and pseudonymize sensitive entities through the Presidio service.
/// </summary>
public interface IPresidioService
{
    /// <summary>
    /// Asynchronously pseudonymizes sensitive entities within the provided text based on predefined policies.
    /// </summary>
    /// <param name="text">The input text containing potential sensitive entities to be pseudonymized.</param>
    /// <param name="ct">A <see cref="CancellationToken"/> to observe cancellation requests.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains the pseudonymized text.</returns>
    Task<string> PseudonymizeAsync(string text, CancellationToken ct = default);
}