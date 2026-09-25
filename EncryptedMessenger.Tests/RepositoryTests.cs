using EncryptedMessenger.Core.Database;
using EncryptedMessenger.Core.Models;
using Microsoft.Data.Sqlite;

namespace EncryptedMessenger.Tests
{
    /// <summary>Repositories against a real SQLite file per test.</summary>
    public class RepositoryTests : IAsyncLifetime
    {
        private readonly TempDir _dir = new();
        private AppDbContext _db = null!;
        private ContactRepository _contacts = null!;
        private MessageRepository _messages = null!;

        public Task InitializeAsync()
        {
            _db = new AppDbContext(_dir.File("test.db"));
            _db.EnsureCreated();
            _contacts = new ContactRepository(_db);
            _messages = new MessageRepository(_db);
            return Task.CompletedTask;
        }

        public async Task DisposeAsync()
        {
            await _db.DisposeAsync();
            _dir.Dispose();
        }

        private static Contact Peer(string id, string ip = "10.0.0.5", string? name = null) => new()
        {
            Id = id, DisplayName = name ?? id, IpAddress = ip, Port = 9876, LastSeen = DateTime.UtcNow
        };

        // ── Messages ──────────────────────────────────────────────────────

        [Fact]
        public void ConversationId_IsTheSameForBothParticipants()
            => Assert.Equal(MessageRepository.ConversationId("a", "b"), MessageRepository.ConversationId("b", "a"));

        [Fact]
        public async Task History_ReturnsNewest50_OldestFirst_AndPagesBack()
        {
            var t0 = DateTime.UtcNow.AddHours(-1);
            for (var i = 1; i <= 60; i++)
                await _messages.SaveAsync(new Message
                {
                    ConversationId = "c", SenderId = "a", RecipientId = "b",
                    EncryptedContent = i.ToString(), Timestamp = t0.AddSeconds(i)
                });

            var page = await _messages.GetByConversationAsync("c", 0, 50);
            var older = await _messages.GetByConversationAsync("c", 50, 50);

            Assert.Equal(Enumerable.Range(11, 50).Select(i => i.ToString()), page.Select(m => m.EncryptedContent));
            Assert.Equal(Enumerable.Range(1, 10).Select(i => i.ToString()), older.Select(m => m.EncryptedContent));
        }

        [Fact]
        public async Task PendingReadAcks_AreOnlyIncomingReadAckPendingFromThatSender()
        {
            await _messages.SaveAsync(new Message { ConversationId = "c", SenderId = "x", RecipientId = "me", MessageId = "1", EncryptedContent = "-", Status = MessageStatus.ReadAckPending });
            await _messages.SaveAsync(new Message { ConversationId = "c", SenderId = "x", RecipientId = "me", MessageId = "2", EncryptedContent = "-", Status = MessageStatus.Delivered });
            await _messages.SaveAsync(new Message { ConversationId = "c", SenderId = "y", RecipientId = "me", MessageId = "3", EncryptedContent = "-", Status = MessageStatus.ReadAckPending });

            var pending = await _messages.GetPendingReadAcksAsync("x");

            Assert.Equal(["1"], pending.Select(m => m.MessageId));
        }

        // ── Contacts: discovery ───────────────────────────────────────────

        [Fact]
        public async Task Discovery_NeverCreatesContacts()
        {
            var (state, changed) = await _contacts.ApplyDiscoveryAsync(Peer("new"), TimeSpan.FromMinutes(1));

            Assert.Null(state);
            Assert.False(changed);
            Assert.Null(await _contacts.GetByIdAsync("new"));
        }

        [Fact]
        public async Task Discovery_WritesOnlyWhenSomethingChanged()
        {
            await _contacts.PinOrVerifyKeyAsync("p", "<k/>");   // known peer (name "p"), no address yet
            var (_, firstChanged) = await _contacts.ApplyDiscoveryAsync(Peer("p", "10.0.0.5"), TimeSpan.FromMinutes(1));
            var seen = (await _contacts.GetByIdAsync("p"))!.LastSeen;

            // Same announcement again, seconds later: nothing to write, LastSeen untouched.
            var (_, repeatChanged) = await _contacts.ApplyDiscoveryAsync(Peer("p", "10.0.0.5"), TimeSpan.FromMinutes(1));
            Assert.Equal(seen, (await _contacts.GetByIdAsync("p"))!.LastSeen);

            var (_, movedChanged) = await _contacts.ApplyDiscoveryAsync(Peer("p", "10.0.0.9"), TimeSpan.FromMinutes(1));

            Assert.True(firstChanged);
            Assert.False(repeatChanged);
            Assert.True(movedChanged);
            Assert.Equal("10.0.0.9", (await _contacts.GetByIdAsync("p"))!.IpAddress);
        }

        [Fact]
        public async Task Discovery_FillsAPlaceholderName_ButNeverRenamesAKnownContact()
        {
            await _contacts.PinOrVerifyKeyAsync("peer-with-placeholder", "<k1/>");   // name = placeholder
            await _contacts.MarkRequestSentAsync(Peer("carol-id", name: "Carol (typed)"));

            await _contacts.ApplyDiscoveryAsync(Peer("peer-with-placeholder", name: "Dave"), TimeSpan.FromMinutes(1));
            await _contacts.ApplyDiscoveryAsync(Peer("carol-id", name: "Evil rename"), TimeSpan.FromMinutes(1));

            Assert.Equal("Dave", (await _contacts.GetByIdAsync("peer-with-placeholder"))!.DisplayName);
            Assert.Equal("Carol (typed)", (await _contacts.GetByIdAsync("carol-id"))!.DisplayName);
        }

        [Fact]
        public async Task EnsureWithAddress_FillsMissingIp_ButNeverOverwritesOne()
        {
            await _contacts.PinOrVerifyKeyAsync("x", "<k/>");   // row without an address

            Assert.True(await _contacts.EnsureWithAddressAsync("x", "10.0.0.7", 9876));
            Assert.False(await _contacts.EnsureWithAddressAsync("x", "6.6.6.6", 9876));
            Assert.Equal("10.0.0.7", (await _contacts.GetByIdAsync("x"))!.IpAddress);
        }

        // ── Contacts: key pinning ─────────────────────────────────────────

        [Fact]
        public async Task PinOrVerify_TrustsFirstKey_ThenOnlyThatKey()
        {
            Assert.True(await _contacts.PinOrVerifyKeyAsync("alice", "<key-A/>"));   // TOFU
            Assert.True(await _contacts.PinOrVerifyKeyAsync("alice", "<key-A/>"));
            Assert.False(await _contacts.PinOrVerifyKeyAsync("alice", "<key-M/>"));
            Assert.Equal("<key-A/>", (await _contacts.GetByIdAsync("alice"))!.PublicKeyXml);
        }

        [Fact]
        public async Task PinnedStranger_IsNotInTheContactList()
        {
            await _contacts.PinOrVerifyKeyAsync("stranger", "<k/>");

            Assert.Equal(ContactState.Stranger, (await _contacts.GetByIdAsync("stranger"))!.State);
            Assert.Empty(await _contacts.GetAcceptedAsync());
        }

        // ── Contacts: request state machine ───────────────────────────────

        [Fact]
        public async Task Request_Sent_ThenAcceptedByPeer()
        {
            Assert.Equal(ContactState.OutgoingRequest, await _contacts.MarkRequestSentAsync(Peer("carol")));
            Assert.True(await _contacts.TransitionAsync("carol", ContactState.OutgoingRequest, ContactState.Accepted));
            Assert.Single(await _contacts.GetAcceptedAsync());
        }

        [Fact]
        public async Task Request_Received_ThenAcceptedByUs()
        {
            var (old, now) = await _contacts.MarkRequestReceivedAsync("alice", "Alice");

            Assert.Equal((ContactState.Stranger, ContactState.IncomingRequest), (old, now));
            Assert.Equal("Alice", (await _contacts.GetByIdAsync("alice"))!.DisplayName);
            Assert.True(await _contacts.TransitionAsync("alice", ContactState.IncomingRequest, ContactState.Accepted));
        }

        [Fact]
        public async Task MutualRequests_BecomeAccepted()
        {
            await _contacts.MarkRequestReceivedAsync("dave", "Dave");
            Assert.Equal(ContactState.Accepted, await _contacts.MarkRequestSentAsync(Peer("dave")));

            await _contacts.MarkRequestSentAsync(Peer("erin"));
            Assert.Equal(ContactState.Accepted, (await _contacts.MarkRequestReceivedAsync("erin", "Erin")).New);
        }

        [Fact]
        public async Task Transition_IsCompareAndSet()
        {
            await _contacts.PinOrVerifyKeyAsync("x", "<k/>");   // Stranger

            // An Accept nobody asked for: Stranger isn't OutgoingRequest → no transition.
            Assert.False(await _contacts.TransitionAsync("x", ContactState.OutgoingRequest, ContactState.Accepted));
            Assert.Equal(ContactState.Stranger, (await _contacts.GetByIdAsync("x"))!.State);
        }

        // ── Contacts: verification ────────────────────────────────────────

        [Fact]
        public async Task MarkVerified_RequiresTheCodeOfThePinnedKey()
        {
            await _contacts.PinOrVerifyKeyAsync("a", "<key-A/>");
            string CodeFor(string key) => "code-of-" + key;

            Assert.False(await _contacts.MarkVerifiedAsync("a", "code-of-<other/>", CodeFor));
            Assert.True(await _contacts.MarkVerifiedAsync("a", "code-of-<key-A/>", CodeFor));
            Assert.True((await _contacts.GetByIdAsync("a"))!.Verified);
        }

        [Fact]
        public async Task RepinningADifferentKey_ResetsVerified()
        {
            await _contacts.PinOrVerifyKeyAsync("a", "<key-A/>");
            await _contacts.MarkVerifiedAsync("a", "c", _ => "c");

            await _contacts.SetPublicKeyXmlAsync("a", "<key-B/>");

            Assert.False((await _contacts.GetByIdAsync("a"))!.Verified);
        }
    }

    public class SchemaUpgradeTests : IDisposable
    {
        private readonly TempDir _dir = new();
        public void Dispose() => _dir.Dispose();

        /// <summary>A database created by a version before Contact.State/Verified existed.</summary>
        private string CreateOldSchemaDatabase()
        {
            var path = _dir.File("old.db");
            using var conn = new SqliteConnection($"Data Source={path}");
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE "Contacts" ("Id" TEXT NOT NULL PRIMARY KEY, "DisplayName" TEXT NOT NULL, "IpAddress" TEXT NOT NULL,
                                         "Port" INTEGER NOT NULL, "PublicKeyXml" TEXT NULL, "LastSeen" TEXT NOT NULL);
                CREATE TABLE "Messages" ("Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, "ConversationId" TEXT NOT NULL, "SenderId" TEXT NOT NULL,
                                         "RecipientId" TEXT NOT NULL, "EncryptedContent" TEXT NOT NULL, "MessageId" TEXT NOT NULL,
                                         "Timestamp" TEXT NOT NULL, "IsOutgoing" INTEGER NOT NULL, "Status" INTEGER NOT NULL);
                INSERT INTO "Contacts" VALUES ('old-friend', 'Old Friend', '10.0.0.2', 9876, NULL, '2026-01-01 00:00:00');
                """;
            cmd.ExecuteNonQuery();
            SqliteConnection.ClearAllPools();
            return path;
        }

        [Fact]
        public async Task OldDatabase_GetsNewColumns_AndKeepsContactsAccepted()
        {
            var path = CreateOldSchemaDatabase();

            await using (var db = new AppDbContext(path))
            {
                db.EnsureCreated();
                var accepted = await new ContactRepository(db).GetAcceptedAsync();
                var friend = Assert.Single(accepted);
                Assert.Equal("old-friend", friend.Id);
                Assert.False(friend.Verified);
            }

            // Second start: the upgrade must be a no-op, not fail on existing columns.
            await using (var db = new AppDbContext(path))
            {
                db.EnsureCreated();
                Assert.Single(await new ContactRepository(db).GetAcceptedAsync());
            }
        }
    }
}
