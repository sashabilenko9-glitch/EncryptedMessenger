using System.Collections.Concurrent;
using System.Net.Sockets;
using EncryptedMessenger.Core.Encryption;
using EncryptedMessenger.Core.Models;
using EncryptedMessenger.Core.Network;

namespace EncryptedMessenger.Tests
{
    /// <summary>MessengerServer/MessengerClient over real TCP on localhost (no service, no database).</summary>
    public class NetworkTests : IDisposable
    {
        private readonly TempDir _dir = new();
        private readonly List<MessengerServer> _servers = [];
        private readonly List<MessengerClient> _clients = [];

        public void Dispose()
        {
            foreach (var c in _clients) c.Dispose();
            foreach (var s in _servers) s.Stop();
            _dir.Dispose();
        }

        private async Task<(MessengerServer Server, int Port)> StartServer(
            string id, CryptoManager keys, PeerKeyVerifier? verify = null, Func<string, Task<bool>>? acceptsFrom = null)
        {
            var port = Net.FreeTcpPort();
            var server = new MessengerServer(port, id, keys, verifyPeerKey: verify, acceptsMessagesFrom: acceptsFrom);
            _servers.Add(server);
            _ = Task.Run(server.StartAsync);
            await Task.Delay(150);
            return (server, port);
        }

        private MessengerClient Client(string id, CryptoManager keys, PeerKeyVerifier? verify = null)
        {
            var c = new MessengerClient(id, keys, verifyPeerKey: verify);
            _clients.Add(c);
            return c;
        }

        [Fact]
        public async Task TwoParallelConnections_HaveIndependentSessionKeys()
        {
            var kA = _dir.NewKeys("a"); var kB = _dir.NewKeys("b");
            var (serverA, portA) = await StartServer("A", kA);
            var (serverB, portB) = await StartServer("B", kB);
            var atB = new ConcurrentQueue<string>();
            var atA = new ConcurrentQueue<string>();
            serverB.MessageReceived += (_, e) => atB.Enqueue($"{e.SenderId}:{e.PlainText}");
            serverA.MessageReceived += (_, e) => atA.Enqueue($"{e.SenderId}:{e.PlainText}");

            var aToB = Client("A", kA); var bToA = Client("B", kB);
            await aToB.ConnectAsync("127.0.0.1", portB);
            await bToA.ConnectAsync("127.0.0.1", portA);
            await aToB.SendMessageAsync("hi B", "m1");
            await bToA.SendMessageAsync("hi A", "m2");
            Assert.True(await Wait.Until(() => atB.Contains("A:hi B") && atA.Contains("B:hi A")));

            // Closing one connection must not delete the other's key.
            bToA.Dispose();
            await Task.Delay(300);
            await aToB.SendMessageAsync("still there?", "m3");
            Assert.True(await Wait.Until(() => atB.Contains("A:still there?")));
        }

        [Fact]
        public async Task ReadAck_ReachesSender_AsRead()
        {
            var kA = _dir.NewKeys("a"); var kB = _dir.NewKeys("b");
            var (serverB, portB) = await StartServer("B", kB);
            var aToB = Client("A", kA);
            var readAcks = new ConcurrentQueue<string>();
            aToB.DeliveryAcknowledged += (_, e) => { if (e.IsRead) readAcks.Enqueue(e.MessageId); };
            await aToB.ConnectAsync("127.0.0.1", portB);
            await Task.Delay(200);

            Assert.True(await serverB.SendControlAsync("A", PacketType.ReadAck, "m1"));
            Assert.True(await Wait.Until(() => readAcks.Contains("m1")));
        }

        [Fact]
        public async Task Server_ReportsPeerIp_AndNoDisconnectForAnAbortedHandshake()
        {
            var (server, port) = await StartServer("B", _dir.NewKeys("b"));
            string? ip = null; int connects = 0, disconnects = 0;
            server.ContactConnected += (_, e) => { ip = e.IpAddress; connects++; };
            server.ContactDisconnected += (_, _) => disconnects++;

            var client = Client("A", _dir.NewKeys("a"));
            await client.ConnectAsync("127.0.0.1", port);
            Assert.True(await Wait.Until(() => connects == 1));
            Assert.Equal("127.0.0.1", ip);
            client.Dispose();
            Assert.True(await Wait.Until(() => disconnects == 1));

            using (var raw = new TcpClient()) { await raw.ConnectAsync("127.0.0.1", port); }   // hangs up mid-handshake
            await Task.Delay(400);
            Assert.Equal((1, 1), (connects, disconnects));
        }

        [Fact]
        public async Task ClientVerifierRejectingKey_AbortsBeforeSessionKeyIsSent()
        {
            var (server, port) = await StartServer("B", _dir.NewKeys("b"));
            var serverGotSession = false;
            server.ContactConnected += (_, _) => serverGotSession = true;

            var client = Client("A", _dir.NewKeys("a"), verify: (_, _) => Task.FromResult(false));

            await Assert.ThrowsAsync<UntrustedPeerKeyException>(() => client.ConnectAsync("127.0.0.1", port));
            await Task.Delay(300);
            Assert.False(serverGotSession);
        }

        [Fact]
        public async Task ServerVerifierRejectingKey_NeverAttributesMessages()
        {
            var (server, port) = await StartServer("B", _dir.NewKeys("b"), verify: (_, _) => Task.FromResult(false));
            var received = 0;
            server.MessageReceived += (_, _) => received++;

            // The server hangs up right after rejecting the key. Depending on timing the impostor's
            // handshake either still "completes" from its side or fails while sending the session
            // key — both are fine; what matters is that nothing gets through.
            var impostor = Client("ALICE", _dir.NewKeys("m"));
            try
            {
                await impostor.ConnectAsync("127.0.0.1", port);
                await impostor.SendMessageAsync("trust me", "x");
            }
            catch (IOException) { }
            await Task.Delay(400);

            Assert.Equal(0, received);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Consent_StrangersGetNotAContact_ContactsGetDeliveryAck(bool isContact)
        {
            var (server, port) = await StartServer("B", _dir.NewKeys("b"), acceptsFrom: _ => Task.FromResult(isContact));
            var received = 0;
            server.MessageReceived += (_, _) => received++;

            var client = Client("A", _dir.NewKeys("a"));
            var control = new ConcurrentQueue<ContactControlEventArgs>();
            var acks = new ConcurrentQueue<string>();
            client.ContactControlReceived += (_, e) => control.Enqueue(e);
            client.DeliveryAcknowledged += (_, e) => acks.Enqueue(e.MessageId);
            await client.ConnectAsync("127.0.0.1", port);

            await client.SendMessageAsync("hello", "m1");
            await Wait.Until(() => !acks.IsEmpty || !control.IsEmpty);

            if (isContact)
            {
                Assert.Contains("m1", acks);
                Assert.Equal(1, received);
            }
            else
            {
                Assert.Contains(control, e => e.Type == PacketType.NotAContact && e.MessageId == "m1");
                Assert.Empty(acks);
                Assert.Equal(0, received);
            }
        }
    }
}
