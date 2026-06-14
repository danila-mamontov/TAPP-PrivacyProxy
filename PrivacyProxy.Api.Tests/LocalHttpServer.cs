using System.Net;
using System.Net.Sockets;

namespace PrivacyProxy.Api.Tests;

/// <summary>
/// Minimal local HTTP server used to exercise the connectivity-check code paths
/// (reachable with success/non-success status codes) in <see cref="PrivacyProxy.Api.Configuration.LlmOptionsValidator"/>
/// and <see cref="PrivacyProxy.Api.Configuration.PresidioOptionsValidator"/> without depending on external services.
/// </summary>
internal sealed class LocalHttpServer : IDisposable
{
    private readonly HttpListener _listener;

    /// <summary>
    /// The base URL (including trailing slash) the server is listening on, e.g. <c>http://localhost:12345/</c>.
    /// </summary>
    public string BaseUrl { get; }

    private LocalHttpServer(HttpListener listener, string baseUrl)
    {
        _listener = listener;
        BaseUrl   = baseUrl;
    }

    /// <summary>
    /// Starts a local HTTP server on a free loopback port that responds to a single
    /// incoming request with the given status code and an empty body.
    /// </summary>
    public static LocalHttpServer StartSingleResponse(int statusCode)
    {
        var port     = GetFreeTcpPort();
        var baseUrl  = $"http://localhost:{port}/";
        var listener = new HttpListener();
        listener.Prefixes.Add(baseUrl);
        listener.Start();

        _ = Task.Run(() =>
        {
            try
            {
                var context = listener.GetContext();
                context.Response.StatusCode = statusCode;
                context.Response.OutputStream.Close();
            }
            catch (HttpListenerException)
            {
                // Listener was stopped/disposed before a request arrived - ignore.
            }
            catch (ObjectDisposedException)
            {
                // Listener was disposed before a request arrived - ignore.
            }
        });

        return new LocalHttpServer(listener, baseUrl);
    }

    private static int GetFreeTcpPort()
    {
        var tcpListener = new TcpListener(IPAddress.Loopback, 0);
        tcpListener.Start();
        var port = ((IPEndPoint)tcpListener.LocalEndpoint).Port;
        tcpListener.Stop();
        return port;
    }

    public void Dispose()
    {
        _listener.Close();
    }
}
