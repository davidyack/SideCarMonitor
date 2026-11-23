using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace SideCarMonitor.Services;

/// <summary>
/// Minimal HTTPS CONNECT proxy on 127.0.0.1 that only tunnels to an allowlist of host:443 names.
/// The sandboxed agent's only route out: MXC denies direct egress and points the sandbox at this proxy,
/// so this is where hostname-level network policy is enforced. Everything else gets 403.
/// </summary>
public sealed class AllowlistProxy : IDisposable
{
    private readonly HashSet<string> _allowedHosts;
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();

    public int Port { get; private set; }

    public AllowlistProxy(IEnumerable<string> allowedHosts)
    {
        _allowedHosts = new HashSet<string>(allowedHosts, StringComparer.OrdinalIgnoreCase);
    }

    public string Url => $"http://127.0.0.1:{Port}";

    public void Start()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch (Exception) { return; }
            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using var owned = client;
        try
        {
            var downstream = client.GetStream();
            var requestLine = await ReadHeaderAsync(downstream);
            if (requestLine is null) return;

            var parts = requestLine.Split(' ');
            var target = parts.Length >= 2 ? parts[1] : "";
            var colon = target.LastIndexOf(':');
            var host = colon > 0 ? target[..colon] : "";
            var allowed = parts[0] == "CONNECT" && target.EndsWith(":443") && _allowedHosts.Contains(host);
            Debug.WriteLine($"[proxy] {(allowed ? "ALLOW" : "DENY ")} {requestLine}");

            if (!allowed)
            {
                SystemEvents.Report("Blocked the agent's network request", $"to {(host.Length > 0 ? host : target)} (not on the sandbox allowlist)",
                    dedupe: TimeSpan.FromMinutes(30));
                await downstream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\n\r\n"));
                return;
            }

            using var upstream = new TcpClient();
            await upstream.ConnectAsync(host, 443, _cts.Token);
            await downstream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n"));
            var up = upstream.GetStream();
            await Task.WhenAny(downstream.CopyToAsync(up, _cts.Token), up.CopyToAsync(downstream, _cts.Token));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[proxy] connection failed: {ex.Message}");
        }
    }

    /// <summary>Reads the request header (up to the blank line) and returns its first line.</summary>
    private static async Task<string?> ReadHeaderAsync(NetworkStream stream)
    {
        var header = new StringBuilder();
        var one = new byte[1];
        while (header.Length < 8192)
        {
            if (await stream.ReadAsync(one) == 0) return null;
            header.Append((char)one[0]);
            if (header.Length >= 4 && header.ToString(header.Length - 4, 4) == "\r\n\r\n")
                return header.ToString().Split("\r\n")[0];
        }
        return null;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        _cts.Dispose();
    }
}
