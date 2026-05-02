namespace PrivacyProxy.Api.Models.Interfaces;

/// <summary>
/// Defines the contract for interacting with the Presidio service,
/// enabling operations such as anonymizing sensitive data within a given text.
/// </summary>
public interface IPresidioService
{
    /// <summary>
    /// Asynchronously anonymizes sensitive entities within the provided text based on predefined policies.
    /// </summary>
    /// <param name="text">The input text containing potential sensitive entities to be anonymized.</param>
    /// <param name="ct">A <see cref="CancellationToken"/> to observe cancellation requests.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains the anonymized text.</returns>
    Task<string> AnonymizeAsync(string text, CancellationToken ct = default);
}