using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using MaddoxTasks.Application;
using MaddoxTasks.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace MaddoxTasks.Web;

/// <summary>
/// Hosts the browser UI and HTTP API. The server intentionally has no
/// authentication in this first LAN-oriented version; callers should only
/// bind it to a trusted network.
/// </summary>
public static class WebServer
{
    public const string DefaultHost = "0.0.0.0";
    public const int DefaultPort = 5000;

    public static void Run(string dbPath, string host = DefaultHost, int port = DefaultPort)
    {
        if (!TryValidateBinding(host, port, out var error))
        {
            throw new ArgumentException(error, nameof(host));
        }

        Console.WriteLine("MaddoxTasks web server");
        Console.WriteLine($"Database: {AppPaths.ResolveDatabasePath(dbPath)}");
        Console.WriteLine($"Listening on {GetListenUrl(host, port)}");
        Console.WriteLine();
        Console.WriteLine("Open one of these URLs on this computer or another device on the LAN:");
        foreach (var url in GetAdvertisedUrls(host, port))
        {
            Console.WriteLine($"  {url}");
        }

        Console.WriteLine();
        Console.WriteLine("Warning: this server has no authentication. Use it only on a trusted LAN and stop it when finished.");
        Console.WriteLine("Press Ctrl+C to stop.");

        using var app = CreateApplication(dbPath, host, port);
        app.Run();
    }

    /// <summary>
    /// Creates an application for the CLI and integration tests. Port zero is
    /// accepted here so tests can ask Kestrel for an available ephemeral port;
    /// the user-facing CLI still requires a concrete port.
    /// </summary>
    public static WebApplication CreateApplication(
        string dbPath,
        string host = DefaultHost,
        int port = DefaultPort,
        IAiTaskDraftGenerator? draftGenerator = null)
    {
        if (!TryValidateBinding(host, port, allowEphemeralPort: true, out var error))
        {
            throw new ArgumentException(error, nameof(host));
        }

        var builder = WebApplication.CreateSlimBuilder([]);
        builder.WebHost.UseKestrel();
        builder.WebHost.UseUrls(GetListenUrl(host, port));
        var engine = new IssueEngine(
            new SqliteEventStore(AppPaths.ResolveDatabasePath(dbPath)),
            new SystemClock());

        var app = builder.Build();
        WebEndpoints.Map(app, engine, draftGenerator ?? new CodexTaskDraftGenerator());
        return app;
    }

    public static bool TryValidateBinding(string? host, int port, out string error)
        => TryValidateBinding(host, port, allowEphemeralPort: false, out error);

    private static bool TryValidateBinding(
        string? host,
        int port,
        bool allowEphemeralPort,
        out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(host))
        {
            error = "--host cannot be empty.";
            return false;
        }

        var trimmedHost = host.Trim();
        if (trimmedHost.Contains("://", StringComparison.Ordinal) ||
            trimmedHost.Any(char.IsWhiteSpace) ||
            trimmedHost.Contains('/', StringComparison.Ordinal) ||
            trimmedHost.Contains('?', StringComparison.Ordinal) ||
            trimmedHost.Contains('#', StringComparison.Ordinal))
        {
            error = "--host must be a host name or IP address, without a scheme or path.";
            return false;
        }

        var hostForParsing = trimmedHost.Trim('[', ']');
        if (!IsWildcardHost(trimmedHost) &&
            IPAddress.TryParse(hostForParsing, out var address) &&
            address.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
        {
            error = $"Unsupported --host address '{host}'.";
            return false;
        }

        var minimumPort = allowEphemeralPort ? 0 : 1;
        if (port < minimumPort || port > 65535)
        {
            error = allowEphemeralPort
                ? "--port must be between 0 and 65535."
                : "--port must be between 1 and 65535.";
            return false;
        }

        return true;
    }

    internal static string GetListenUrl(string host, int port)
    {
        var urlHost = host.Trim();
        if (urlHost.Contains(':') &&
            !urlHost.StartsWith("[", StringComparison.Ordinal))
        {
            urlHost = $"[{urlHost}]";
        }

        return $"http://{urlHost}:{port}/";
    }

    public static IReadOnlyList<string> GetAdvertisedUrls(string host, int port)
    {
        var urls = new List<string>();
        var normalizedHost = host.Trim();

        AddUrl(urls, "localhost", port);
        if (!IsWildcardHost(normalizedHost))
        {
            AddUrl(urls, normalizedHost.Trim('[', ']'), port);
            return urls;
        }

        try
        {
            foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces()
                         .Where(item => item.OperationalStatus == OperationalStatus.Up)
                         .Where(item => item.NetworkInterfaceType != NetworkInterfaceType.Loopback))
            {
                foreach (var address in networkInterface.GetIPProperties().UnicastAddresses
                             .Select(item => item.Address)
                             .Where(item => item.AddressFamily == AddressFamily.InterNetwork)
                             .Where(item => !IPAddress.IsLoopback(item)))
                {
                    AddUrl(urls, address.ToString(), port);
                }
            }
        }
        catch (Exception exception) when (exception is NetworkInformationException or SocketException or PlatformNotSupportedException)
        {
            // A machine may not expose network interfaces (for example in a
            // restricted container). localhost is still a useful URL.
        }

        return urls;
    }

    private static bool IsWildcardHost(string host)
        => string.Equals(host, "0.0.0.0", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(host, "*", StringComparison.Ordinal) ||
           string.Equals(host, "+", StringComparison.Ordinal) ||
           string.Equals(host, "::", StringComparison.Ordinal) ||
           string.Equals(host, "[::]", StringComparison.Ordinal);

    private static void AddUrl(ICollection<string> urls, string host, int port)
    {
        var url = GetListenUrl(host, port);
        if (!urls.Contains(url, StringComparer.OrdinalIgnoreCase))
        {
            urls.Add(url);
        }
    }
}
