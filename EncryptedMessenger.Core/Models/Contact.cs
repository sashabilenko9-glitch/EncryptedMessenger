using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace EncryptedMessenger.Core.Models
{
    /// <summary>
    /// A known peer on the local network. Persisted to SQLite.
    /// </summary>
    public class Contact
    {
        [Key, MaxLength(64)]
        public string Id { get; set; } = Guid.NewGuid().ToString();

        [Required, MaxLength(128)]
        public string DisplayName { get; set; } = string.Empty;

        [MaxLength(64)]
        public string IpAddress { get; set; } = string.Empty;

        public int Port { get; set; } = AppSettings.DefaultTcpPort;

        /// <summary>
        /// The contact's RSA-2048 public key in XML format.
        /// Obtained during the initial key-exchange handshake.
        /// </summary>
        public string? PublicKeyXml { get; set; }

        public DateTime LastSeen { get; set; } = DateTime.MinValue;

        /// <summary>
        /// Relationship with this peer. Only <see cref="ContactState.Accepted"/> contacts appear
        /// in the contact list. New rows start as <see cref="ContactState.Stranger"/>; rows that
        /// existed before this column was added are upgraded to Accepted (see AppDbContext).
        /// </summary>
        public ContactState State { get; set; } = ContactState.Stranger;

        /// <summary>True once both users confirmed that their verification codes match.</summary>
        public bool Verified { get; set; }

        /// <summary>Name used for a contact we only know by id (first 8 chars), until discovery or the user provides a real one.</summary>
        public static string PlaceholderName(string id) => id[..Math.Min(8, id.Length)];

        // ── Runtime-only ─────────────────────────────────────────────────
        [NotMapped] public bool IsOnline    { get; set; }
        [NotMapped] public int  UnreadCount { get; set; }
        [NotMapped] public string? LastMessagePreview { get; set; }
    }

    /// <summary>Stored as an int. Values are part of the DB format — don't renumber.</summary>
    public enum ContactState
    {
        /// <summary>Known to the app (e.g. key pinned during a handshake) but not a contact.</summary>
        Stranger        = 0,
        Accepted        = 1,
        /// <summary>We asked them; waiting for their answer.</summary>
        OutgoingRequest = 2,
        /// <summary>They asked us; waiting for our answer.</summary>
        IncomingRequest = 3
    }
}
