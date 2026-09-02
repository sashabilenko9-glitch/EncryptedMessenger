namespace EncryptedMessenger.Core.Network
{
    public class MessageReceivedEventArgs(string senderId, string plainText, string messageId, DateTime timestamp) : EventArgs
    {
        public string SenderId   { get; } = senderId;
        public string PlainText  { get; } = plainText;
        public string MessageId  { get; } = messageId;
        public DateTime Timestamp{ get; } = timestamp;
    }

    public class ContactStatusEventArgs(string contactId, bool isOnline, string? publicKeyXml = null) : EventArgs
    {
        public string  ContactId    { get; } = contactId;
        public bool    IsOnline     { get; } = isOnline;

        /// <summary>The peer's RSA public key, set only when <see cref="IsOnline"/> is true (fresh handshake).</summary>
        public string? PublicKeyXml { get; } = publicKeyXml;
    }

    public class PeerDiscoveredEventArgs(string peerId, string displayName, string ip, int port) : EventArgs
    {
        public string PeerId      { get; } = peerId;
        public string DisplayName { get; } = displayName;
        public string IpAddress   { get; } = ip;
        public int    Port        { get; } = port;
    }

    public class DeliveryAckEventArgs(string messageId) : EventArgs
    {
        public string MessageId { get; } = messageId;
    }
}
