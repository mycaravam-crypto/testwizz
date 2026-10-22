using System.Net;
using System.Net.Sockets;

namespace TestWizz.AI;

/// <summary>
/// Keeps model traffic on the local network. Every connection resolves the host itself, requires <b>all</b> resolved
/// addresses to be private, and then connects to one of those exact addresses, so a DNS answer cannot change between
/// the check and the connect. System proxies and redirects are disabled; TLS uses default certificate validation.
/// </summary>
public sealed class EndpointGuard(Func<string, CancellationToken, Task<IPAddress[]>>? resolver = null)
{
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolve = resolver ?? Dns.GetHostAddressesAsync;

    /// <summary>
    /// Loopback, RFC 1918, carrier-grade NAT (100.64.0.0/10, used by Tailscale and similar VPNs) and IPv6 unique-local
    /// addresses. Link-local is excluded because it includes cloud metadata services (169.254.169.254).
    /// </summary>
    public static bool IsPrivate(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] == 10
                || (b[0] == 172 && b[1] is >= 16 and <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 100 && b[1] is >= 64 and <= 127);
        }

        return address.AddressFamily == AddressFamily.InterNetworkV6 && address.IsIPv6UniqueLocal;
    }

    public async Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        IPAddress[] addresses;
        if (IPAddress.TryParse(host.Trim('[', ']'), out var literal))
        {
            addresses = [literal];
        }
        else
        {
            try
            {
                addresses = await _resolve(host, cancellationToken).ConfigureAwait(false);
            }
            catch (SocketException ex)
            {
                throw new ModelClientException(ModelFailure.DnsFailed, $"Could not resolve '{host}'.", ex);
            }

            if (addresses.Length == 0)
            {
                throw new ModelClientException(ModelFailure.DnsFailed, $"Could not resolve '{host}'.");
            }
        }

        var rejected = addresses.Where(a => !IsPrivate(a)).ToList();
        if (rejected.Count > 0)
        {
            throw new ModelClientException(
                ModelFailure.EndpointNotPrivate,
                $"'{host}' resolves to non-private address {rejected[0]}. Only loopback and private-network model servers are allowed.");
        }

        return addresses;
    }

    public SocketsHttpHandler CreateHandler() => new()
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        UseCookies = false,
        ConnectCallback = ConnectAsync,
    };

    private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var addresses = await ResolveAsync(context.DnsEndPoint.Host, cancellationToken).ConfigureAwait(false);
        SocketException? lastError = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException ex)
            {
                socket.Dispose();
                lastError = ex;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        throw new ModelClientException(
            ModelFailure.ConnectionFailed,
            $"Could not connect to {context.DnsEndPoint.Host}:{context.DnsEndPoint.Port}.",
            lastError);
    }
}
