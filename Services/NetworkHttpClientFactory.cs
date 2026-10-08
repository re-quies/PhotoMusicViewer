using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace PhotoMusicViewer.Services
{
    internal static class NetworkHttpClientFactory
    {
        internal static HttpClient Create(bool loopbackOnly = false) =>
            new(CreateHandler(loopbackOnly)) { Timeout = TimeSpan.FromMinutes(15) };
        internal static SocketsHttpHandler CreateHandler(bool loopbackOnly) => new()
        {
            AllowAutoRedirect = false,
            UseProxy = !loopbackOnly,
            ConnectCallback = loopbackOnly ? ConnectLoopbackAsync : null
        };
        private static async ValueTask<Stream> ConnectLoopbackAsync(SocketsHttpConnectionContext context, CancellationToken token)
        {
            string host = context.DnsEndPoint.Host.Trim('[', ']');
            IPAddress address;
            if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) address = IPAddress.Loopback;
            else if (!IPAddress.TryParse(host, out address!)) throw new TranslationException("HTTP endpoint must be a literal loopback address.");
            if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
            if (!IPAddress.IsLoopback(address)) throw new TranslationException("Non-loopback HTTP connection is blocked.");
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), token).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch { socket.Dispose(); throw; }
        }
    }
}
