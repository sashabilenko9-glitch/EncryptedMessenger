namespace EncryptedMessenger.Core.Network
{
    /// <summary>
    /// Decides during the handshake whether the public key a peer presented may be used
    /// for <paramref name="contactId"/>. Called by <see cref="MessengerClient"/> and
    /// <see cref="MessengerServer"/> right after the peer's KeyExchange packet — before
    /// any session key is sent or accepted — so a rejected key never protects anything.
    ///
    /// The network classes don't know about contacts or the database; the policy
    /// (key pinning / trust on first use) lives in MessengerService.
    /// </summary>
    /// <returns>true to continue the handshake, false to abort it.</returns>
    public delegate Task<bool> PeerKeyVerifier(string contactId, string publicKeyXml);

    /// <summary>
    /// The peer presented a public key that doesn't match the one pinned for this contact.
    /// Derives from IOException so existing "connection failed" handling still applies.
    /// </summary>
    public sealed class UntrustedPeerKeyException(string contactId)
        : IOException($"Peer '{contactId}' presented a public key that differs from the pinned one; connection refused.")
    {
        public string ContactId { get; } = contactId;
    }
}
