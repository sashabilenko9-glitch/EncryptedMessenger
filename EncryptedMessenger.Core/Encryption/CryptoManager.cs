using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace EncryptedMessenger.Core.Encryption
{
    /// <summary>
    /// Central entry point for all cryptographic operations.
    ///
    /// Lifecycle
    /// ─────────
    ///  1. On first run: generates RSA-2048 key pair, writes private key to disk
    ///     (DPAPI-protected, CurrentUser scope — unreadable outside this Windows account).
    ///  2. On reconnect:  loads existing private key from disk.
    ///  3. Handshake (initiator):
    ///       a. Gets own public key → transmit as KeyExchange packet.
    ///       b. Receives peer's public key. Caller should compare its fingerprint
    ///          (<see cref="RsaCryptoService.ComputeFingerprint"/>) against any
    ///          previously known key for this contact.
    ///       c. CreateAndEncryptSessionKey() → transmit as SessionKey packet.
    ///  4. Handshake (acceptor):
    ///       a. Gets own public key → transmit as KeyExchange packet.
    ///       b. Receives peer's public key (stored in Contact.PublicKeyXml).
    ///       c. DecryptAndStoreSessionKey() to decode the AES key.
    ///  5. Messaging: EncryptMessage() / DecryptMessage() per sessionId (one per connection).
    ///     Every call uses a fresh random nonce (never a fixed per-session IV) —
    ///     required for AES-GCM's security guarantees to hold.
    /// </summary>
    public sealed class CryptoManager : IDisposable
    {
        private readonly RsaCryptoService _rsa;
        private readonly byte[] _storageKey;

        /// <summary>
        /// Maps sessionId → AES session key. A sessionId identifies ONE TCP connection
        /// (see <see cref="NewSessionId"/>), not a contact: the same contact may have an
        /// inbound and an outbound connection at once, each with its own key, and closing
        /// one must not remove the key the other is still using.
        /// </summary>
        private readonly ConcurrentDictionary<string, SessionKey> _sessions = new();

        public string PublicKeyXml => _rsa.GetPublicKeyXml();

        /// <summary>SHA-256 fingerprint of this instance's own public key.</summary>
        public string OwnFingerprint => RsaCryptoService.ComputeFingerprint(PublicKeyXml);

        // ── Constructor ───────────────────────────────────────────────────

        /// <param name="privateKeyPath">
        /// Relative or absolute path to the stored RSA private key (DPAPI-protected XML).
        /// If the file does not exist, a new key pair is generated and saved.
        /// </param>
        /// <param name="storageKeyPath">
        /// Relative or absolute path to the persistent local AES key used to
        /// encrypt message history at rest (independent of per-session keys,
        /// which are discarded on disconnect). Generated on first run, DPAPI-protected.
        /// </param>
        public CryptoManager(string privateKeyPath, string? storageKeyPath = null)
        {
            var dir = Path.GetDirectoryName(privateKeyPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            if (File.Exists(privateKeyPath))
            {
                var privateKeyXml = DpapiProtectedFile.ReadText(privateKeyPath,
                    text => text.TrimStart().StartsWith("<RSAKeyValue>", StringComparison.Ordinal));
                _rsa = new RsaCryptoService(privateKeyXml);
            }
            else
            {
                _rsa = new RsaCryptoService();
                DpapiProtectedFile.WriteText(privateKeyPath, _rsa.GetPrivateKeyXml());
            }

            storageKeyPath ??= privateKeyPath + ".storage";
            if (File.Exists(storageKeyPath))
            {
                _storageKey = Convert.FromBase64String(DpapiProtectedFile.ReadText(storageKeyPath, IsBase64AesKey));
            }
            else
            {
                _storageKey = AesCryptoService.GenerateKey();
                DpapiProtectedFile.WriteText(storageKeyPath, Convert.ToBase64String(_storageKey));
            }
        }

        // ── Session key management ────────────────────────────────────────

        /// <summary>
        /// Initiator side: generates a fresh AES-256 session key and encrypts it
        /// with the peer's RSA public key. Returns the Base64-encoded blob to
        /// be sent as the SessionKey packet payload.
        /// </summary>
        public string CreateAndEncryptSessionKey(string sessionId, string peerPublicKeyXml)
        {
            var key = AesCryptoService.GenerateKey();
            _sessions[sessionId] = new SessionKey(key);

            return Convert.ToBase64String(_rsa.EncryptWithPublicKey(key, peerPublicKeyXml));
        }

        /// <summary>
        /// Acceptor side: RSA-decrypts the session key received from the
        /// initiating peer and stores it locally.
        /// </summary>
        public void DecryptAndStoreSessionKey(string sessionId, string encryptedBase64)
        {
            var key = _rsa.DecryptWithPrivateKey(Convert.FromBase64String(encryptedBase64));
            _sessions[sessionId] = new SessionKey(key);
        }

        // ── Message crypto ────────────────────────────────────────────────

        /// <summary>
        /// AES-GCM-encrypts <paramref name="plainText"/> with a fresh random nonce;
        /// returns Base64 of nonce + ciphertext + auth tag.
        /// </summary>
        public string EncryptMessage(string plainText, string sessionId)
        {
            var s = GetSession(sessionId);
            return EncryptWithKey(plainText, s.Key);
        }

        /// <summary>
        /// AES-GCM-decrypts a Base64 blob produced by <see cref="EncryptMessage"/>.
        /// Throws if the peer's key doesn't match or the data was tampered with.
        /// </summary>
        public string DecryptMessage(string encryptedBase64, string sessionId)
        {
            var s = GetSession(sessionId);
            return DecryptWithKey(encryptedBase64, s.Key);
        }

        // ── Local storage crypto (at-rest, independent of session keys) ────

        /// <summary>Encrypts <paramref name="plainText"/> with the persistent local storage key for saving to the database.</summary>
        public string EncryptForStorage(string plainText) => EncryptWithKey(plainText, _storageKey);

        /// <summary>Decrypts a value previously produced by <see cref="EncryptForStorage"/>.</summary>
        public string DecryptForStorage(string encryptedBase64) => DecryptWithKey(encryptedBase64, _storageKey);

        // ── Helpers ───────────────────────────────────────────────────────

        /// <summary>Creates a unique id for the session key of a single connection with <paramref name="contactId"/>.</summary>
        public static string NewSessionId(string contactId) => $"{contactId}#{Guid.NewGuid():N}";

        public bool HasSession(string sessionId) => _sessions.ContainsKey(sessionId);
        public void RemoveSession(string sessionId) => _sessions.TryRemove(sessionId, out _);

        private SessionKey GetSession(string sessionId)
        {
            if (_sessions.TryGetValue(sessionId, out var s)) return s;
            throw new InvalidOperationException(
                $"No AES session '{sessionId}'. " +
                "Complete the RSA key-exchange handshake first.");
        }

        /// <summary>Encrypts with a fresh nonce, packed as nonce + ciphertext+tag, Base64-encoded.</summary>
        private static string EncryptWithKey(string plainText, byte[] key)
        {
            var nonce = AesCryptoService.GenerateNonce();
            var cipherAndTag = AesCryptoService.Encrypt(plainText, key, nonce);

            var blob = new byte[nonce.Length + cipherAndTag.Length];
            Buffer.BlockCopy(nonce, 0, blob, 0, nonce.Length);
            Buffer.BlockCopy(cipherAndTag, 0, blob, nonce.Length, cipherAndTag.Length);
            return Convert.ToBase64String(blob);
        }

        private static string DecryptWithKey(string encryptedBase64, byte[] key)
        {
            var blob = Convert.FromBase64String(encryptedBase64);
            var nonce = blob[..AesCryptoService.NonceSizeBytes];
            var cipherAndTag = blob[AesCryptoService.NonceSizeBytes..];
            return AesCryptoService.Decrypt(cipherAndTag, key, nonce);
        }

        /// <summary>Legacy (pre-DPAPI) storage key file format: Base64 of a 32-byte AES key.</summary>
        private static bool IsBase64AesKey(string text)
        {
            var buffer = new byte[64];
            return Convert.TryFromBase64String(text.Trim(), buffer, out var written) && written == 32;
        }

        public void Dispose() => _rsa.Dispose();

        // ── Inner type ────────────────────────────────────────────────────

        private sealed record SessionKey(byte[] Key);
    }
}
