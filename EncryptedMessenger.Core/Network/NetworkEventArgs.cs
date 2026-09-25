namespace EncryptedMessenger.Core.Network
{
    public class MessageReceivedEventArgs(string senderId, string plainText, string messageId, DateTime timestamp) : EventArgs
    {
        public string SenderId   { get; } = senderId;
        public string PlainText  { get; } = plainText;
        public string MessageId  { get; } = messageId;
        public DateTime Timestamp{ get; } = timestamp;
    }

    public class ContactStatusEventArgs(string contactId, bool isOnline, string? publicKeyXml = null, string? ipAddress = null) : EventArgs
    {
        public string  ContactId    { get; } = contactId;
        public bool    IsOnline     { get; } = isOnline;

        /// <summary>The peer's RSA public key, set only when <see cref="IsOnline"/> is true (fresh handshake).</summary>
        public string? PublicKeyXml { get; } = publicKeyXml;

        /// <summary>The peer's IPv4 address as seen on the inbound connection, set only when <see cref="IsOnline"/> is true.</summary>
        public string? IpAddress    { get; } = ipAddress;
    }

    public class PeerDiscoveredEventArgs(string peerId, string displayName, string ip, int port) : EventArgs
    {
        public string PeerId      { get; } = peerId;
        public string DisplayName { get; } = displayName;
        public string IpAddress   { get; } = ip;
        public int    Port        { get; } = port;
    }

    public class DeliveryAckEventArgs(string messageId, bool isRead = false) : EventArgs
    {
        public string MessageId { get; } = messageId;

        /// <summary>true = the peer's user has read the message (ReadAck); false = it reached the peer (DeliveryAck).</summary>
        public bool IsRead { get; } = isRead;
    }
}
