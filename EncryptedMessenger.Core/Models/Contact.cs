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

        // ── Runtime-only ─────────────────────────────────────────────────
        [NotMapped] public bool IsOnline    { get; set; }
        [NotMapped] public int  UnreadCount { get; set; }
        [NotMapped] public string? LastMessagePreview { get; set; }
    }
}
