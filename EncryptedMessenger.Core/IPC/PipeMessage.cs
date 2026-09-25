using System.Text.Json;

namespace EncryptedMessenger.Core.IPC
{
    /// <summary>
    /// Message type exchanged over the Named Pipe between
    /// the Windows Service (server side) and the WPF UI (client side).
    /// </summary>
    public enum PipeMessageType
    {
        // UI → Service
        SendMessage      = 1,
        AddContact       = 2,
        GetContacts      = 3,
        GetHistory       = 4,
        MarkRead         = 5,
        Connect          = 6,
        ContactIdChanged = 7,
        AcceptPeerKey    = 8,
        GetNearby        = 9,
        AddNearbyPeer    = 10,

        // Service → UI
        NewIncomingMessage = 100,
        ContactOnline      = 101,
        ContactOffline     = 102,
        DeliveryAck        = 103,
        ContactList        = 104,
        MessageHistory     = 105,
        KeyFingerprint     = 106,
        SendFailed         = 107, // Payload = messageId (string)
        MessageRead        = 108, // Payload = messageId (string)
        NearbyList         = 109, // Payload = List<NearbyPeerPayload>

        // Bidirectional
        Heartbeat        = 200,
        Error            = 201
    }

    public class PipeMessage
    {
        public PipeMessageType Type    { get; set; }
        public string          Payload { get; set; } = string.Empty;

        // ── Convenience factory / parse ───────────────────────────────────

        public static PipeMessage Create<T>(PipeMessageType type, T payload)
            => new() { Type = type, Payload = JsonSerializer.Serialize(payload) };

        public T Deserialize<T>() => JsonSerializer.Deserialize<T>(Payload)!;

        public string ToJson()           => JsonSerializer.Serialize(this);
        public static PipeMessage FromJson(string json) => JsonSerializer.Deserialize<PipeMessage>(json)!;
    }

    // ── Payload DTOs ──────────────────────────────────────────────────────

    public record SendMessagePayload(string RecipientId, string PlainText, string MessageId);
    public record NewMessagePayload(string SenderId, string PlainText, string MessageId, DateTime Timestamp);
    public record ContactStatusPayload(string ContactId, bool IsOnline);
    public record GetHistoryPayload(string ConversationId, int Skip, int Take);
    public record MarkReadPayload(string ConversationId, string MessageId);
    public record AddContactPayload(string DisplayName, string IpAddress, int Port);
    public record ConnectPayload(string ContactId);
    public record ContactIdChangedPayload(string OldId, string NewId);

    /// <summary>A peer announcing itself on the LAN that is not (yet) one of our contacts.</summary>
    public record NearbyPeerPayload(string PeerId, string DisplayName, string IpAddress, int Port);

    public record AddNearbyPeerPayload(string PeerId);

    /// <summary>
    /// History reply. Carries the conversation it belongs to, because it is broadcast to
    /// every UI and may arrive after the user already switched to another chat.
    /// </summary>
    public record MessageHistoryPayload(string ConversationId, List<Models.Message> Messages);

    /// <summary>
    /// Sent to the UI after a handshake. <paramref name="Changed"/> = false: the key is the
    /// pinned one (or was just pinned on first contact) and the connection is up.
    /// <paramref name="Changed"/> = true: the peer presented a DIFFERENT key than the pinned
    /// one — the connection was refused, and <paramref name="Fingerprint"/> is the fingerprint
    /// of that new key, for the user to compare out-of-band before sending
    /// <see cref="AcceptPeerKeyPayload"/> (legit reinstall) or ignoring it (possible MITM).
    /// <paramref name="ViaContactId"/>: set when the refused connection was opened for a
    /// different local contact id (a manual "manual_ip_port" contact), so the UI can show
    /// the warning in that chat even though the peer identified itself as ContactId.
    /// </summary>
    public record KeyFingerprintPayload(string ContactId, string Fingerprint, bool Changed, string? ViaContactId = null);

    /// <summary>
    /// UI → service: the user compared <paramref name="Fingerprint"/> with the contact and
    /// trusts it. The service re-pins only if the key it is holding for this contact still
    /// has exactly this fingerprint — so a different key that shows up between display and
    /// click can't be accepted by accident. Afterwards it reconnects to
    /// <paramref name="ReconnectContactId"/> (the chat's contact id), or ContactId if null.
    /// </summary>
    public record AcceptPeerKeyPayload(string ContactId, string Fingerprint, string? ReconnectContactId = null);




}
