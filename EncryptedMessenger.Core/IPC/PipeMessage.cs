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

    /// <summary>
    /// History reply. Carries the conversation it belongs to, because it is broadcast to
    /// every UI and may arrive after the user already switched to another chat.
    /// </summary>
    public record MessageHistoryPayload(string ConversationId, List<Models.Message> Messages);

    /// <summary>
    /// Sent to the UI right after a handshake completes. <paramref name="Changed"/> is true
    /// when this contact previously had a different key on file — a signal to the user that
    /// this may not be the same peer as last time (reinstall, or a possible MITM).
    /// </summary>
    public record KeyFingerprintPayload(string ContactId, string Fingerprint, bool Changed);




}
