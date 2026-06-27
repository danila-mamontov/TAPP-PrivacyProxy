using PrivacyProxy.Core.Configuration;

namespace PrivacyProxy.Api.Tests;

public class ProxyConfigFileTests
{
    [Fact]
    public void ResolvePath_EnvOverrideSet_ReturnsEnvValueVerbatim()
    {
        // Arrange & Act
        var result = ProxyConfigFile.ResolvePath(
            "/custom/path.json", "/this-docker-dir-does-not-exist-12345", "/this-home-does-not-exist");

        // Assert
        Assert.Equal("/custom/path.json", result);
    }

    [Fact]
    public void ResolvePath_EnvOverrideIsWhitespace_FallsThroughToNextSource()
    {
        // Arrange & Act — whitespace-only override must not be treated as set
        var result = ProxyConfigFile.ResolvePath(
            "   ", "/this-docker-dir-does-not-exist-12345", "/home/testuser");

        // Assert
        Assert.Equal(Path.Combine("/home/testuser", ".privacyproxy", ProxyConfigFile.FileName), result);
    }

    [Fact]
    public void ResolvePath_NoEnvOverride_DockerConfigDirExists_ReturnsDockerConfigPath()
    {
        // Arrange — any directory guaranteed to exist stands in for the /config volume
        var dockerDir = Path.GetTempPath();

        // Act
        var result = ProxyConfigFile.ResolvePath(null, dockerDir, "/this-home-does-not-exist");

        // Assert
        Assert.Equal(Path.Combine(dockerDir, ProxyConfigFile.FileName), result);
    }

    [Fact]
    public void ResolvePath_NoEnvOverride_NoDockerConfigDir_ReturnsLocalHomeFallback()
    {
        // Arrange & Act
        var result = ProxyConfigFile.ResolvePath(
            null, "/this-docker-dir-does-not-exist-12345", "/home/testuser");

        // Assert
        Assert.Equal(Path.Combine("/home/testuser", ".privacyproxy", ProxyConfigFile.FileName), result);
    }

    [Fact]
    public void ResolvePath_Parameterless_ReturnsNonEmptyPathEndingInFileName()
    {
        // Smoke test for the real, parameterless entry point used by the API/WebUI.
        // Act
        var result = ProxyConfigFile.ResolvePath();

        // Assert
        Assert.False(string.IsNullOrWhiteSpace(result));
        Assert.EndsWith(ProxyConfigFile.FileName, result);
    }
}
