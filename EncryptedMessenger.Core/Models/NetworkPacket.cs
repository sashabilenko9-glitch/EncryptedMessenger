namespace EncryptedMessenger.Core.Models
{
    /// <summary>
    /// Wire-format packet exchanged over TCP between peers.
    /// Serialized as JSON and length-prefixed (4-byte big-endian int32).
    /// </summary>
    public class NetworkPacket
    {
        public PacketType Type      { get; set; }
        public string SenderId      { get; set; } = string.Empty;
        public string RecipientId   { get; set; } = string.Empty;

        /// <summary>Type-specific payload. See PacketPayloads below.</summary>
        public string Payload       { get; set; } = string.Empty;

        /// <summary>Echoed in Ack packets to match delivery acknowledgements.</summary>
        public string? MessageId    { get; set; }

        public DateTime Timestamp   { get; set; } = DateTime.UtcNow;
    }

    public enum PacketType
    {
        /// <summary>Payload = sender's RSA public key XML.</summary>
        KeyExchange  = 0,

        /// <summary>Payload = AES key+IV (48 bytes) encrypted with recipient's RSA public key, Base64.</summary>
        SessionKey   = 1,

        /// <summary>Payload = AES-encrypted message text, Base64.</summary>
        Message      = 2,

        /// <summary>Payload empty – confirms message reached the peer process.</summary>
        DeliveryAck  = 3,

        /// <summary>Payload empty – confirms message was read by the user.</summary>
        ReadAck      = 4,

        /// <summary>Graceful disconnect notification.</summary>
        Disconnect   = 5,

        /// <summary>Payload = display name – sent on connection to identify the peer.</summary>
        Identity     = 6,

        // ── Contact requests (sent over a handshaked, key-pinned connection) ──

        /// <summary>Payload = requester's display name. "Please add me as a contact."</summary>
        ContactRequest = 7,

        /// <summary>Payload empty. Answer to ContactRequest: we are contacts now.</summary>
        ContactAccept  = 8,

        /// <summary>Payload empty. Answer to ContactRequest: no.</summary>
        ContactDecline = 9,

        /// <summary>
        /// MessageId = the rejected message. Reply to a Message from a peer we haven't accepted:
        /// the message was dropped (not stored, no DeliveryAck).
        /// </summary>
        NotAContact    = 10
    }

    // ── UDP peer-discovery packets (separate from TCP) ────────────────────

    /// <summary>
    /// Broadcast over UDP to announce/discover peers on the local subnet.
    /// </summary>
    public class DiscoveryPacket
    {
        public string PeerId      { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public int    TcpPort     { get; set; }

        /// <summary>true = "Who is there?", false = "Here I am!".</summary>
        public bool   IsRequest   { get; set; }
    }
}
