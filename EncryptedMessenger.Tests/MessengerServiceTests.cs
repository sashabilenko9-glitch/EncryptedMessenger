using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using EncryptedMessenger.Core.Database;
using EncryptedMessenger.Core.Encryption;
using EncryptedMessenger.Core.IPC;
using EncryptedMessenger.Core.Models;
using EncryptedMessenger.Core.Network;

namespace EncryptedMessenger.Tests
{
    public class ListenerFailureTests
    {
        [Fact]
        public async Task TcpPortInUse_IsReportedToTheUi()
        {
            var port = Net.FreeTcpPort();
            var squatter = new System.Net.Sockets.TcpListener(IPAddress.Any, port) { ExclusiveAddressUse = true };
            squatter.Start();
            try
            {
                await using var bob = await ServiceHarness.StartAsync(tcpPort: port);

                // Reported again whenever the UI asks for its lists (it usually connects after the failure).
                await bob.SendUi(PipeMessageType.GetContacts, new { });

                Assert.True(await Wait.Until(() => bob.Events.Any(m => m.Type == PipeMessageType.ListenerFailed
                                                                      && m.Deserialize<int>() == port)));
            }
            finally { squatter.Stop(); }
        }
    }

    /// <summary>
    /// End-to-end: a real MessengerService ("Bob") driven through its pipe like the WPF UI,
    /// talking over TCP to scripted peers built from MessengerClient/MessengerServer.
    /// </summary>
    public class MessengerServiceTests : IAsyncLifetime
    {
        private ServiceHarness _bob = null!;
        private readonly TempDir _peerKeys = new();
        private readonly List<IDisposable> _cleanup = [];

        public async Task InitializeAsync() => _bob = await ServiceHarness.StartAsync(discovery: true);

        public async Task DisposeAsync()
        {
            foreach (var d in _cleanup) d.Dispose();
            await _bob.DisposeAsync();
            _peerKeys.Dispose();
        }

        // ── helpers ───────────────────────────────────────────────────────

        private sealed class Peer(MessengerClient client) : IDisposable
        {
            public MessengerClient Client { get; } = client;
            public ConcurrentQueue<ContactControlEventArgs> Control { get; } = new();
            public ConcurrentQueue<DeliveryAckEventArgs> Acks { get; } = new();
            public void Dispose() => Client.Dispose();
        }

        /// <summary>A scripted peer that connects TO Bob.</summary>
        private async Task<Peer> ConnectPeer(string id, CryptoManager keys)
        {
            var peer = new Peer(new MessengerClient(id, keys));
            peer.Client.ContactControlReceived += (_, e) => peer.Control.Enqueue(e);
            peer.Client.DeliveryAcknowledged += (_, e) => peer.Acks.Enqueue(e);
            _cleanup.Add(peer);
            await peer.Client.ConnectAsync("127.0.0.1", _bob.TcpPort);
            return peer;
        }

        /// <summary>A scripted peer that Bob connects to.</summary>
        private async Task<(MessengerServer Server, int Port, ConcurrentQueue<ContactControlEventArgs> Control)> ListeningPeer(string id, CryptoManager keys, int? port = null)
        {
            var p = port ?? Net.FreeTcpPort();
            var server = new MessengerServer(p, id, keys);
            var control = new ConcurrentQueue<ContactControlEventArgs>();
            server.ContactControlReceived += (_, e) => control.Enqueue(e);
            _ = Task.Run(server.StartAsync);
            _cleanup.Add(server);
            await Task.Delay(150);
            return (server, p, control);
        }

        /// <summary>Alice asks Bob, Bob's UI accepts: afterwards they are contacts.</summary>
        private async Task<Peer> AcceptedAlice(CryptoManager keys)
        {
            var alice = await ConnectPeer("ALICE", keys);
            await alice.Client.SendControlAsync(PacketType.ContactRequest, payload: "Alice");
            Assert.True(await Wait.Until(() => _bob.Requests().Any(r => r.ContactId == "ALICE")));
            await _bob.SendUi(PipeMessageType.AcceptContactRequest, new ContactIdPayload("ALICE"));
            Assert.True(await Wait.Until(() => _bob.Contacts().Any(c => c.Id == "ALICE")));
            return alice;
        }

        private static async Task<Contact?> StoredContact(string id)
        {
            await using var db = new AppDbContext(AppSettings.DatabaseFileName);
            return await new ContactRepository(db).GetByIdAsync(id);
        }

        private static async Task<Message?> StoredMessage(string id)
        {
            await using var db = new AppDbContext(AppSettings.DatabaseFileName);
            return await new MessageRepository(db).GetByMessageIdAsync(id);
        }

        private async Task Announce(string id, string name, int tcpPort)
        {
            using var udp = new UdpClient();
            var data = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new DiscoveryPacket { PeerId = id, DisplayName = name, TcpPort = tcpPort }));
            await udp.SendAsync(data, data.Length, new IPEndPoint(IPAddress.Loopback, _bob.UdpPort));
        }

        // ── consent & contact requests ───────────────────────────────────

        [Fact]
        public async Task StrangersMessage_IsRefused_UntilTheirRequestIsAccepted()
        {
            var alice = await ConnectPeer("ALICE", _peerKeys.NewKeys("alice"));

            await alice.Client.SendMessageAsync("we don't know each other", "a-1");
            Assert.True(await Wait.Until(() => alice.Control.Any(e => e.Type == PacketType.NotAContact && e.MessageId == "a-1")));
            Assert.DoesNotContain(alice.Acks, a => a.MessageId == "a-1");
            Assert.Null(await StoredMessage("a-1"));

            await alice.Client.SendControlAsync(PacketType.ContactRequest, payload: "Alice");
            Assert.True(await Wait.Until(() => _bob.Requests().Any(r => r.ContactId == "ALICE" && r.Incoming && r.DisplayName == "Alice")));
            Assert.DoesNotContain(_bob.Contacts(), c => c.Id == "ALICE");

            await _bob.SendUi(PipeMessageType.AcceptContactRequest, new ContactIdPayload("ALICE"));
            Assert.True(await Wait.Until(() => alice.Control.Any(e => e.Type == PacketType.ContactAccept)));
            Assert.True(await Wait.Until(() => _bob.Contacts().Any(c => c.Id == "ALICE")));

            await alice.Client.SendMessageAsync("now we're contacts", "a-2");
            Assert.True(await Wait.Until(() => alice.Acks.Any(a => a.MessageId == "a-2")));
            Assert.Equal(MessageStatus.Delivered, (await StoredMessage("a-2"))?.Status);
        }

        [Fact]
        public async Task RequestByIp_IsSent_AndPeersAcceptMakesThemAContact()
        {
            var (carol, port, carolControl) = await ListeningPeer("CAROL", _peerKeys.NewKeys("carol"));

            await _bob.SendUi(PipeMessageType.AddContact, new AddContactPayload("Carol", "127.0.0.1", port));

            Assert.True(await Wait.Until(() => carolControl.Any(e => e.Type == PacketType.ContactRequest && e.ContactId == _bob.BobId && e.Payload == "Bob")));
            Assert.True(await Wait.Until(() => _bob.Requests().Any(r => r.ContactId == "CAROL" && !r.Incoming && r.DisplayName == "Carol")),
                        "pending request should be listed under the real id with the typed name");

            await carol.SendControlAsync(_bob.BobId, PacketType.ContactAccept);
            Assert.True(await Wait.Until(() => _bob.Contacts().Any(c => c.Id == "CAROL") && _bob.Requests().All(r => r.ContactId != "CAROL")));
        }

        [Fact]
        public async Task AcceptNobodyAskedFor_IsIgnored()
        {
            var dave = await ConnectPeer("DAVE", _peerKeys.NewKeys("dave"));

            await dave.Client.SendControlAsync(PacketType.ContactAccept);
            await Task.Delay(500);

            Assert.NotEqual(ContactState.Accepted, (await StoredContact("DAVE"))?.State);
        }

        [Fact]
        public async Task Decline_RemovesThePendingRequest()
        {
            var (dave, port, daveControl) = await ListeningPeer("DAVE", _peerKeys.NewKeys("dave"));
            await _bob.SendUi(PipeMessageType.AddContact, new AddContactPayload("Dave", "127.0.0.1", port));
            Assert.True(await Wait.Until(() => _bob.Requests().Any(r => r.ContactId == "DAVE")));

            await dave.SendControlAsync(_bob.BobId, PacketType.ContactDecline);

            Assert.True(await Wait.Until(() => _bob.Requests().All(r => r.ContactId != "DAVE")));
            Assert.DoesNotContain(_bob.Contacts(), c => c.Id == "DAVE");
        }

        [Fact]
        public async Task DiscoveredPeer_AppearsInNearby_NotInContacts()
        {
            await Announce("CAROL", "Carol", 9876);

            Assert.True(await Wait.Until(() => (_bob.Last<List<NearbyPeerPayload>>(PipeMessageType.NearbyList) ?? []).Any(p => p.PeerId == "CAROL")));
            await _bob.SendUi(PipeMessageType.GetContacts, new { });
            await Task.Delay(300);
            Assert.DoesNotContain(_bob.Contacts(), c => c.Id == "CAROL");
        }

        [Fact]
        public async Task RequestToOfflinePeer_IsDeliveredWhenItComesOnline()
        {
            var port = Net.FreeTcpPort();
            await Announce("EVE", "Eve", port);   // announces, but nothing listens on the TCP port yet
            Assert.True(await Wait.Until(() => (_bob.Last<List<NearbyPeerPayload>>(PipeMessageType.NearbyList) ?? []).Any(p => p.PeerId == "EVE")));
            await _bob.SendUi(PipeMessageType.AddNearbyPeer, new AddNearbyPeerPayload("EVE"));
            Assert.True(await Wait.Until(() => _bob.Requests().Any(r => r.ContactId == "EVE" && !r.Incoming)));

            var (_, _, eveControl) = await ListeningPeer("EVE", _peerKeys.NewKeys("eve"), port);
            await Announce("EVE", "Eve", port);   // online again → presence transition → Bob connects

            Assert.True(await Wait.Until(() => eveControl.Any(e => e.Type == PacketType.ContactRequest && e.ContactId == _bob.BobId), 8000));
        }

        // ── key pinning ──────────────────────────────────────────────────

        [Fact]
        public async Task Impostor_IsRefused_PinnedKeyKept_AndOnlyTheShownKeyCanBeAccepted()
        {
            var aliceKeys = _peerKeys.NewKeys("alice");
            var impostorKeys = _peerKeys.NewKeys("impostor");
            (await AcceptedAlice(aliceKeys)).Dispose();
            await Task.Delay(200);

            // Bob hangs up right after rejecting the key; depending on timing the impostor's own
            // handshake may fail too. Either way nothing may get through.
            try
            {
                var impostor = await ConnectPeer("ALICE", impostorKeys);
                await impostor.Client.SendMessageAsync("I am Alice", "evil-1");
            }
            catch (IOException) { }

            Assert.True(await Wait.Until(() => _bob.Events.Any(m => m.Type == PipeMessageType.KeyFingerprint
                && m.Deserialize<KeyFingerprintPayload>() is { Changed: true } p && p.Fingerprint == impostorKeys.OwnFingerprint)));
            Assert.Null(await StoredMessage("evil-1"));
            Assert.Equal(aliceKeys.PublicKeyXml, (await StoredContact("ALICE"))?.PublicKeyXml);

            await _bob.SendUi(PipeMessageType.AcceptPeerKey, new AcceptPeerKeyPayload("ALICE", aliceKeys.OwnFingerprint));
            await Task.Delay(400);
            Assert.Equal(aliceKeys.PublicKeyXml, (await StoredContact("ALICE"))?.PublicKeyXml);   // not the pending key → ignored

            await _bob.SendUi(PipeMessageType.AcceptPeerKey, new AcceptPeerKeyPayload("ALICE", impostorKeys.OwnFingerprint));
            Assert.True(await Wait.Until(() => StoredContact("ALICE").Result?.PublicKeyXml == impostorKeys.PublicKeyXml));
        }

        [Fact]
        public async Task RequestByIp_AnsweredWithADifferentKey_IsFlaggedOnTheRequest()
        {
            (await AcceptedAlice(_peerKeys.NewKeys("alice"))).Dispose();   // pins Alice's real key
            var (_, port, _) = await ListeningPeer("ALICE", _peerKeys.NewKeys("impostor"));

            await _bob.SendUi(PipeMessageType.AddContact, new AddContactPayload("Alice?", "127.0.0.1", port));

            var manualId = $"manual_127.0.0.1_{port}";
            Assert.True(await Wait.Until(() => _bob.Requests().Any(r => r.ContactId == manualId && r.KeyRejected)),
                        "the pending request should say the key was refused");
            Assert.Contains(_bob.Events, m => m.Type == PipeMessageType.KeyFingerprint
                && m.Deserialize<KeyFingerprintPayload>() is { Changed: true } p && p.ViaContactId == manualId);
        }

        [Fact]
        public async Task ReopeningAChat_OnAnOpenConnection_StillSendsKeyAndCode()
        {
            var (carol, port, _) = await ListeningPeer("CAROL", _peerKeys.NewKeys("carol"));
            await _bob.SendUi(PipeMessageType.AddContact, new AddContactPayload("Carol", "127.0.0.1", port));
            Assert.True(await Wait.Until(() => _bob.Requests().Any(r => r.ContactId == "CAROL")));
            await carol.SendControlAsync(_bob.BobId, PacketType.ContactAccept);
            Assert.True(await Wait.Until(() => _bob.Contacts().Any(c => c.Id == "CAROL")));

            // The outbound connection to Carol is already open; opening the chat again must not
            // leave it without fingerprint / verification code.
            _bob.Events.Clear();
            await _bob.SendUi(PipeMessageType.Connect, new ConnectPayload("CAROL"));

            Assert.True(await Wait.Until(() => _bob.Events.Any(m => m.Type == PipeMessageType.KeyFingerprint
                && m.Deserialize<KeyFingerprintPayload>() is { Changed: false, ContactId: "CAROL" } p
                && !string.IsNullOrEmpty(p.VerificationCode))));
        }

        // ── read receipts ────────────────────────────────────────────────

        [Fact]
        public async Task ReadAck_WaitsWhileSenderIsOffline_AndIsDeliveredOnReconnect()
        {
            var aliceKeys = _peerKeys.NewKeys("alice");
            var alice = await AcceptedAlice(aliceKeys);
            await alice.Client.SendMessageAsync("hello Bob", "m-1");
            Assert.True(await Wait.Until(() => alice.Acks.Any(a => a.MessageId == "m-1")));
            alice.Dispose();
            await Task.Delay(400);

            var conversation = MessageRepository.ConversationId(_bob.BobId, "ALICE");
            await _bob.SendUi(PipeMessageType.MarkRead, new MarkReadPayload(conversation, "m-1"));
            Assert.True(await Wait.Until(() => StoredMessage("m-1").Result?.Status == MessageStatus.ReadAckPending));

            var back = await ConnectPeer("ALICE", aliceKeys);
            Assert.True(await Wait.Until(() => back.Acks.Any(a => a.IsRead && a.MessageId == "m-1")));
            Assert.True(await Wait.Until(() => StoredMessage("m-1").Result?.Status == MessageStatus.Read));
        }

        // ── verification code ────────────────────────────────────────────

        [Fact]
        public async Task VerificationCode_MatchesOnBothSides_AndMarkVerifiedNeedsThatCode()
        {
            var aliceKeys = _peerKeys.NewKeys("alice");
            var alice = await ConnectPeer("ALICE", aliceKeys);
            await alice.Client.SendControlAsync(PacketType.ContactRequest, payload: "Alice");
            Assert.True(await Wait.Until(() => _bob.Requests().Any(r => r.ContactId == "ALICE")));

            var shownToBob = _bob.Requests().Single(r => r.ContactId == "ALICE").VerificationCode;
            Assert.Equal(VerificationCode.Compute(aliceKeys.PublicKeyXml, _bob.BobPublicKey), shownToBob);

            await _bob.SendUi(PipeMessageType.AcceptContactRequest, new ContactIdPayload("ALICE"));
            Assert.True(await Wait.Until(() => _bob.Contacts().Any(c => c.Id == "ALICE" && !c.Verified)));

            await _bob.SendUi(PipeMessageType.MarkVerified, new MarkVerifiedPayload("ALICE", "00000 00000 00000 00000 00000 00000 00000 00000"));
            await Task.Delay(400);
            Assert.False((await StoredContact("ALICE"))?.Verified);

            await _bob.SendUi(PipeMessageType.MarkVerified, new MarkVerifiedPayload("ALICE", shownToBob!));
            Assert.True(await Wait.Until(() => _bob.Contacts().Any(c => c.Id == "ALICE" && c.Verified)));
        }
    }
}
