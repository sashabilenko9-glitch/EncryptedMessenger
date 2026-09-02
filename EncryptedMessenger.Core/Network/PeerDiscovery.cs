using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using EncryptedMessenger.Core.Models;

namespace EncryptedMessenger.Core.Network
{
    /// <summary>
    /// Discovers other messenger instances on the same LAN using UDP broadcast.
    ///
    /// Works for two processes on the SAME machine (port-sharing) as well as
    /// across machines on the same Wi-Fi:
    ///   • ExclusiveAddressUse=false + ReuseAddress let multiple processes bind
    ///     the same UDP port on one host.
    ///   • Packets are sent to 255.255.255.255 AND to 127.0.0.1 so same-host
    ///     copies reliably receive each other even if the NIC drops local broadcast.
    ///   • Diagnostic output goes to the VS Output window (Debug.WriteLine).
    /// </summary>
    public sealed class PeerDiscovery : IDisposable
    {
        public event EventHandler<PeerDiscoveredEventArgs>? PeerDiscovered;

        // Lowered to 5s for easier testing; raise back to 30 for production.
        public const int BroadcastIntervalSeconds = 5;

        private readonly int _udpPort;
        private readonly string _ownId;
        private readonly string _ownDisplayName;
        private readonly int _ownTcpPort;

        private UdpClient? _udpClient;
        private CancellationTokenSource _cts = new();

        public PeerDiscovery(int udpPort, string ownId, string ownDisplayName, int ownTcpPort)
        {
            _udpPort = udpPort;
            _ownId = ownId;
            _ownDisplayName = ownDisplayName;
            _ownTcpPort = ownTcpPort;
        }

        // ── Lifecycle ─────────────────────────────────────────────────────

        public async Task StartAsync()
        {
            _cts = new CancellationTokenSource();

            // Build a socket that several processes on one host can share.
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.ExclusiveAddressUse = false;
            socket.EnableBroadcast = true;
            socket.Bind(new IPEndPoint(IPAddress.Any, _udpPort));

            _udpClient = new UdpClient { ExclusiveAddressUse = false };
            _udpClient.Client = socket;
            _udpClient.EnableBroadcast = true;

            Debug.WriteLine($"[Discovery] START id={Short(_ownId)} name={_ownDisplayName} " +
                            $"udp={_udpPort} tcp={_ownTcpPort}");

            _ = Task.Run(() => ListenLoopAsync(_cts.Token));
            _ = Task.Run(() => BroadcastLoopAsync(_cts.Token));

            await BroadcastAsync(isRequest: true);
        }

        public void Stop()
        {
            _cts.Cancel();
            _udpClient?.Close();
        }

        // ── Broadcast loop ────────────────────────────────────────────────

        private async Task BroadcastLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(BroadcastIntervalSeconds), ct);
                    await BroadcastAsync(isRequest: false);
                }
                catch (OperationCanceledException) { break; }
            }
        }

        private async Task BroadcastAsync(bool isRequest)
        {
            var packet = new DiscoveryPacket
            {
                PeerId = _ownId,
                DisplayName = _ownDisplayName,
                TcpPort = _ownTcpPort,
                IsRequest = isRequest
            };
            var data = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(packet));

            try
            {
                // 1) Subnet broadcast – reaches other machines on the Wi-Fi.
                await _udpClient!.SendAsync(data, data.Length,
                    new IPEndPoint(IPAddress.Broadcast, _udpPort));

                // 2) Loopback – guarantees same-host copies receive it.
                await _udpClient!.SendAsync(data, data.Length,
                    new IPEndPoint(IPAddress.Loopback, _udpPort));

                Debug.WriteLine($"[Discovery] SENT  request={isRequest} from={_ownDisplayName}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Discovery] SEND FAILED: {ex.Message}");
            }
        }

        // ── Listen loop ───────────────────────────────────────────────────

        private async Task ListenLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var result = await _udpClient!.ReceiveAsync(ct);
                    _ = Task.Run(() => HandlePacketAsync(result), ct);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Discovery] RECV ERROR: {ex.Message}");
                }
            }
        }

        private async Task HandlePacketAsync(UdpReceiveResult result)
        {
            try
            {
                var json = Encoding.UTF8.GetString(result.Buffer);
                var packet = JsonSerializer.Deserialize<DiscoveryPacket>(json);

                if (packet == null)
                {
                    Debug.WriteLine("[Discovery] RECV but packet null");
                    return;
                }

                if (packet.PeerId == _ownId)
                {
                    Debug.WriteLine("[Discovery] RECV own packet → ignored");
                    return;
                }

                var ip = result.RemoteEndPoint.Address.ToString();
                Debug.WriteLine($"[Discovery] RECV PEER name={packet.DisplayName} " +
                                $"ip={ip} tcp={packet.TcpPort} → raising PeerDiscovered");

                PeerDiscovered?.Invoke(this, new PeerDiscoveredEventArgs(
                    packet.PeerId, packet.DisplayName, ip, packet.TcpPort));

                if (packet.IsRequest) await BroadcastAsync(isRequest: false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Discovery] PARSE ERROR: {ex.Message}");
            }
        }

        private static string Short(string id) => id.Length > 8 ? id[..8] : id;

        public void Dispose() => Stop();
    }
}