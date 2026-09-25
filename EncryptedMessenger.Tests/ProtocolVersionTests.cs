using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using EncryptedMessenger.Core.IPC;
using EncryptedMessenger.Core.Models;
using EncryptedMessenger.Core.Network;

namespace EncryptedMessenger.Tests
{
    public class ProtocolVersionTests : IDisposable
    {
        private readonly TempDir _dir = new();
        public void Dispose() => _dir.Dispose();

        [Theory]
        [InlineData(0, 1)]   // field missing → v1
        [InlineData(-3, 1)]
        [InlineData(2, 2)]
        [InlineData(7, 7)]
        public void Of_TreatsMissingAsLegacy(int received, int expected)
            => Assert.Equal(expected, ProtocolVersions.Of(received));

        /// <summary>
        /// The trap this guards against: an initializer "= Current" on the property would make
        /// JSON WITHOUT the field (every v1 peer) deserialise as the current version.
        /// </summary>
        [Fact]
        public void JsonWithoutTheField_DeserialisesAsZero_ThatIsV1()
        {
            var camel = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };

            var packet = JsonSerializer.Deserialize<NetworkPacket>("""{"type":0,"senderId":"old","payload":"<k/>"}""", camel)!;
            var announce = JsonSerializer.Deserialize<DiscoveryPacket>("""{"PeerId":"old","DisplayName":"Old","TcpPort":9876}""")!;

            Assert.Equal(ProtocolVersions.Legacy, ProtocolVersions.Of(packet.ProtocolVersion));
            Assert.Equal(ProtocolVersions.Legacy, ProtocolVersions.Of(announce.ProtocolVersion));
        }

        /// <summary>A v2 client connecting to a v1 server: refused, and the v1 side still learns our version.</summary>
        [Fact]
        public async Task Client_RefusesAV1Server_AfterTellingItsOwnVersion()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            NetworkPacket? clientHello = null;
            var fakeV1Server = Task.Run(async () =>
            {
                using var tcp = await listener.AcceptTcpClientAsync();
                var stream = tcp.GetStream();
                // What v1.0.0 sends: a KeyExchange without any ProtocolVersion field.
                await PacketHelper.SendAsync(stream, new NetworkPacket { Type = PacketType.KeyExchange, SenderId = "OLD", Payload = _dir.NewKeys("old").PublicKeyXml });
                clientHello = await PacketHelper.ReceiveAsync(stream);
            });

            var client = new MessengerClient("NEW", _dir.NewKeys("new"));
            var ex = await Assert.ThrowsAsync<IncompatibleProtocolException>(() => client.ConnectAsync("127.0.0.1", port));
            await fakeV1Server.WaitAsync(TimeSpan.FromSeconds(5));
            listener.Stop();
            client.Dispose();

            Assert.Equal(("OLD", ProtocolVersions.Legacy), (ex.ContactId, ex.PeerVersion));
            Assert.Equal(ProtocolVersions.Current, clientHello?.ProtocolVersion);
        }

        /// <summary>A v1 client connecting to a v2 server: reported, never treated as a connection.</summary>
        [Fact]
        public async Task Server_ReportsAV1Client_AndDoesNotAnnounceIt()
        {
            var port = Net.FreeTcpPort();
            var server = new MessengerServer(port, "NEW", _dir.NewKeys("new"));
            var incompatible = new ConcurrentQueue<IncompatiblePeerEventArgs>();
            var connected = 0;
            server.IncompatiblePeer += (_, e) => incompatible.Enqueue(e);
            server.ContactConnected += (_, _) => connected++;
            _ = Task.Run(server.StartAsync);
            await Task.Delay(150);

            using (var tcp = new TcpClient())
            {
                await tcp.ConnectAsync("127.0.0.1", port);
                var stream = tcp.GetStream();
                var serverHello = await PacketHelper.ReceiveAsync(stream);
                Assert.Equal(ProtocolVersions.Current, serverHello?.ProtocolVersion);
                await PacketHelper.SendAsync(stream, new NetworkPacket { Type = PacketType.KeyExchange, SenderId = "OLD", Payload = _dir.NewKeys("old").PublicKeyXml });
                Assert.True(await Wait.Until(() => !incompatible.IsEmpty));
            }
            server.Stop();

            var e = Assert.Single(incompatible);
            Assert.Equal(("OLD", ProtocolVersions.Legacy), (e.ContactId, e.PeerVersion));
            Assert.Equal(0, connected);
        }
    }

    /// <summary>What the UI gets told about peers with another protocol version.</summary>
    public class ProtocolVersionServiceTests : IAsyncLifetime
    {
        private ServiceHarness _bob = null!;
        private readonly TempDir _peerKeys = new();

        public async Task InitializeAsync() => _bob = await ServiceHarness.StartAsync(discovery: true);

        public async Task DisposeAsync()
        {
            await _bob.DisposeAsync();
            _peerKeys.Dispose();
        }

        [Fact]
        public async Task V1Announcement_ShowsUpInNearby_AsVersion1()
        {
            using var udp = new UdpClient();
            // v1.0.0 announcement: no ProtocolVersion field at all.
            var data = Encoding.UTF8.GetBytes("""{"PeerId":"OLD","DisplayName":"Old","TcpPort":9876,"IsRequest":false}""");
            await udp.SendAsync(data, data.Length, new IPEndPoint(IPAddress.Loopback, _bob.UdpPort));

            Assert.True(await Wait.Until(() => (_bob.Last<List<NearbyPeerPayload>>(PipeMessageType.NearbyList) ?? [])
                .Any(p => p.PeerId == "OLD" && p.ProtocolVersion == ProtocolVersions.Legacy)));
        }

        [Fact]
        public async Task V1PeerConnecting_IsReportedToTheUi()
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync("127.0.0.1", _bob.TcpPort);
            var stream = tcp.GetStream();
            await PacketHelper.ReceiveAsync(stream);
            await PacketHelper.SendAsync(stream, new NetworkPacket { Type = PacketType.KeyExchange, SenderId = "OLD", Payload = _peerKeys.NewKeys("old").PublicKeyXml });

            Assert.True(await Wait.Until(() => _bob.Events.Any(m => m.Type == PipeMessageType.IncompatiblePeer
                && m.Deserialize<IncompatiblePeerPayload>() is { ContactId: "OLD", PeerVersion: ProtocolVersions.Legacy, OurVersion: ProtocolVersions.Current })));
        }
    }
}
