namespace PrivacyProxy.Core.Configuration;

/// <summary>
/// Resolves the location of the shared, hot-reloadable configuration file that the WebUI writes
/// and the API reads. Both apps call this so they always agree on the same path.
/// </summary>
/// <remarks>
/// Resolution order:
/// <list type="number">
///   <item>the <c>PRIVACYPROXY_CONFIG_FILE</c> environment variable, if set;</item>
///   <item>the mounted <c>/config</c> volume when running in Docker;</item>
///   <item>~/.privacyproxy for local development.</item>
/// </list>
/// </remarks>
public static class ProxyConfigFile
{
    /// <summary>The configuration file name.</summary>
    public const string FileName = "privacyproxy.json";

    /// <summary>The environment variable that overrides the resolved path.</summary>
    public const string PathEnvVar = "PRIVACYPROXY_CONFIG_FILE";

    /// <summary>
    /// Resolves the absolute path of the shared configuration file (see the type remarks for the order).
    /// </summary>
    public static string ResolvePath() => ResolvePath(
        Environment.GetEnvironmentVariable(PathEnvVar),
        "/config",
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <summary>
    /// Core resolution logic with every external input as a parameter, so each branch (env
    /// override, Docker volume present, local fallback) can be exercised in tests without
    /// depending on real environment variables or the filesystem root.
    /// </summary>
    public static string ResolvePath(string? envOverride, string dockerConfigDir, string localHome)
    {
        if (!string.IsNullOrWhiteSpace(envOverride))
            return envOverride;

        // In Docker the shared config lives on the mounted /config volume.
        if (Directory.Exists(dockerConfigDir))
            return Path.Combine(dockerConfigDir, FileName);

        // Local dev: a stable, visible per-user folder (~/.privacyproxy) — not the opaque
        // temp dir, and not the source tree (this is per-machine runtime state).
        return Path.Combine(localHome, ".privacyproxy", FileName);
    }
}
