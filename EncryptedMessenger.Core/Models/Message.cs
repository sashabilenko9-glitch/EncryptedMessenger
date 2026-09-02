using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace EncryptedMessenger.Core.Models
{
    /// <summary>
    /// Represents a single chat message stored in the local SQLite database.
    /// EncryptedContent is persisted; DecryptedContent is resolved at runtime only.
    /// </summary>
    public class Message
    {
        [Key]
        public int Id { get; set; }

        /// <summary>Shared ID for a conversation, formed as "smallerId_largerId".</summary>
        [Required, MaxLength(128)]
        public string ConversationId { get; set; } = string.Empty;

        [Required, MaxLength(64)]
        public string SenderId { get; set; } = string.Empty;

        [Required, MaxLength(64)]
        public string RecipientId { get; set; } = string.Empty;

        /// <summary>AES-256 encrypted message content, stored as Base64.</summary>
        [Required]
        public string EncryptedContent { get; set; } = string.Empty;

        /// <summary>Unique message ID (GUID), used for delivery acknowledgements.</summary>
        [Required, MaxLength(64)]
        public string MessageId { get; set; } = Guid.NewGuid().ToString();

        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public bool IsOutgoing { get; set; }
        public MessageStatus Status { get; set; } = MessageStatus.Pending;

        // ── Not stored in DB ─────────────────────────────────────────────
        [NotMapped]
        public string? DecryptedContent { get; set; }
    }

    public enum MessageStatus
    {
        Pending   = 0,
        Sent      = 1,
        Delivered = 2,
        Read      = 3,
        Failed    = 4
    }
}
