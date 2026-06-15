using Microsoft.Extensions.Options;

namespace PrivacyProxy.Api.Tests;

/// <summary>
/// Minimal <see cref="IOptionsMonitor{TOptions}"/> test double that always returns a fixed value.
/// Used to construct services that read configuration through <see cref="IOptionsMonitor{TOptions}"/>
/// (for live, hot-reloadable config) without spinning up the full options infrastructure.
/// </summary>
internal sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue => value;

    public T Get(string? name) => value;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
