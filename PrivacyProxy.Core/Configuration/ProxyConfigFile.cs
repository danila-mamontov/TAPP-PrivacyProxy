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
///   <item>a per-user temp folder for local development.</item>
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
    public static string ResolvePath()
    {
        var fromEnv = Environment.GetEnvironmentVariable(PathEnvVar);
        if (!string.IsNullOrWhiteSpace(fromEnv))
            return fromEnv;

        if (Directory.Exists("/config"))
            return Path.Combine("/config", FileName);

        return Path.Combine(Path.GetTempPath(), "privacyproxy", FileName);
    }
}
