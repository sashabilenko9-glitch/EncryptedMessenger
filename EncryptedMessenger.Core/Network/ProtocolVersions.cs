namespace EncryptedMessenger.Core.Network
{
    /// <summary>
    /// Version of the peer-to-peer protocol (TCP handshake/packets and UDP discovery), exchanged
    /// in the KeyExchange packet and in discovery announcements. Peers with a different version
    /// are refused with a clear error instead of failing in confusing ways later (v1 sent a
    /// 48-byte CBC key+IV that v2's AES-GCM can't use, and ignored contact-request packets).
    ///
    /// Only strict equality is checked — there is no negotiation, so an attacker who rewrites
    /// the number can at worst make a connection fail, never force weaker crypto. If versions
    /// are ever negotiated, never fall back to a less secure variant.
    /// </summary>
    public static class ProtocolVersions
    {
        /// <summary>The protocol of v1.0.0, which had no version field.</summary>
        public const int Legacy = 1;

        /// <summary>This build: AES-GCM, key pinning, contact requests, consent (app v2.x).</summary>
        public const int Current = 2;

        /// <summary>
        /// Interprets a received version field: missing (deserialised as 0) means a peer from
        /// before versions existed, i.e. <see cref="Legacy"/>.
        /// </summary>
        public static int Of(int received) => received <= 0 ? Legacy : received;

        public static bool IsCompatible(int received) => Of(received) == Current;
    }

    /// <summary>The peer speaks a different protocol version; the handshake was aborted.</summary>
    public sealed class IncompatibleProtocolException(string contactId, int peerVersion)
        : IOException($"Peer '{contactId}' uses protocol v{peerVersion}, this app v{ProtocolVersions.Current}; connection refused.")
    {
        public string ContactId { get; } = contactId;
        public int PeerVersion { get; } = peerVersion;
    }
}
