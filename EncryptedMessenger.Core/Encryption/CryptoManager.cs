using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace EncryptedMessenger.Core.Encryption
{
    /// <summary>
    /// Central entry point for all cryptographic operations.
    ///
    /// Lifecycle
    /// ─────────
    ///  1. On first run: generates RSA-2048 key pair, writes private key to disk.
    ///  2. On reconnect:  loads existing private key from disk.
    ///  3. Handshake (initiator):
    ///       a. Gets own public key → transmit as KeyExchange packet.
    ///       b. Receives peer's public key.
    ///       c. CreateAndEncryptSessionKey() → transmit as SessionKey packet.
    ///  4. Handshake (acceptor):
    ///       a. Gets own public key → transmit as KeyExchange packet.
    ///       b. Receives peer's public key (stored in Contact.PublicKeyXml).
    ///       c. DecryptAndStoreSessionKey() to decode the AES key.
    ///  5. Messaging: EncryptMessage() / DecryptMessage() per contactId.
    /// </summary>
    public sealed class CryptoManager : IDisposable
    {
        private readonly RsaCryptoService _rsa;
        private readonly byte[] _storageKey;

        /// <summary>Maps contactId → AES session key material.</summary>
        private readonly ConcurrentDictionary<string, SessionKey> _sessions = new();

        public string PublicKeyXml => _rsa.GetPublicKeyXml();

        // ── Constructor ───────────────────────────────────────────────────

        /// <param name="privateKeyPath">
        /// Relative or absolute path to the stored RSA private key (XML).
        /// If the file does not exist, a new key pair is generated and saved.
        /// </param>
        /// <param name="storageKeyPath">
        /// Relative or absolute path to the persistent local AES key used to
        /// encrypt message history at rest (independent of per-session keys,
        /// which are discarded on disconnect). Generated on first run.
        /// </param>
        public CryptoManager(string privateKeyPath, string? storageKeyPath = null)
        {
            var dir = Path.GetDirectoryName(privateKeyPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            if (File.Exists(privateKeyPath))
            {
                _rsa = new RsaCryptoService(File.ReadAllText(privateKeyPath));
            }
            else
            {
                _rsa = new RsaCryptoService();
                File.WriteAllText(privateKeyPath, _rsa.GetPrivateKeyXml());
            }

            storageKeyPath ??= privateKeyPath + ".storage";
            if (File.Exists(storageKeyPath))
            {
                _storageKey = Convert.FromBase64String(File.ReadAllText(storageKeyPath));
            }
            else
            {
                _storageKey = RandomNumberGenerator.GetBytes(32);
                File.WriteAllText(storageKeyPath, Convert.ToBase64String(_storageKey));
            }
        }

        // ── Session key management ────────────────────────────────────────

        /// <summary>
        /// Initiator side: generates a fresh AES session key and encrypts it
        /// with the peer's RSA public key. Returns the Base64-encoded blob to
        /// be sent as the SessionKey packet payload.
        /// </summary>
        public string CreateAndEncryptSessionKey(string contactId, string peerPublicKeyXml)
        {
            var (key, iv) = AesCryptoService.GenerateKeyAndIV();
            _sessions[contactId] = new SessionKey(key, iv);

            // Pack key (32 bytes) + IV (16 bytes) = 48 bytes, then RSA-encrypt
            var blob = new byte[48];
            Buffer.BlockCopy(key, 0, blob,  0, 32);
            Buffer.BlockCopy(iv,  0, blob, 32, 16);

            return Convert.ToBase64String(_rsa.EncryptWithPublicKey(blob, peerPublicKeyXml));
        }

        /// <summary>
        /// Acceptor side: RSA-decrypts the session key blob received from the
        /// initiating peer and stores it locally.
        /// </summary>
        public void DecryptAndStoreSessionKey(string contactId, string encryptedBase64)
        {
            var blob      = _rsa.DecryptWithPrivateKey(Convert.FromBase64String(encryptedBase64));
            var key       = blob[..32];
            var iv        = blob[32..48];
            _sessions[contactId] = new SessionKey(key, iv);
        }

        // ── Message crypto ────────────────────────────────────────────────

        /// <summary>AES-encrypts <paramref name="plainText"/>; returns Base64 cipher.</summary>
        public string EncryptMessage(string plainText, string contactId)
        {
            var s = GetSession(contactId);
            var cipher = AesCryptoService.Encrypt(plainText, s.Key, s.IV);
            return Convert.ToBase64String(cipher);
        }

        /// <summary>AES-decrypts a Base64 cipher string; returns plain text.</summary>
        public string DecryptMessage(string encryptedBase64, string contactId)
        {
            var s = GetSession(contactId);
            var cipher = Convert.FromBase64String(encryptedBase64);
            return AesCryptoService.Decrypt(cipher, s.Key, s.IV);
        }

        // ── Local storage crypto (at-rest, independent of session keys) ────

        /// <summary>Encrypts <paramref name="plainText"/> with the persistent local storage key for saving to the database. A fresh random IV is generated per call and prepended to the ciphertext.</summary>
        public string EncryptForStorage(string plainText)
        {
            var iv = RandomNumberGenerator.GetBytes(16);
            var cipher = AesCryptoService.Encrypt(plainText, _storageKey, iv);

            var blob = new byte[iv.Length + cipher.Length];
            Buffer.BlockCopy(iv, 0, blob, 0, iv.Length);
            Buffer.BlockCopy(cipher, 0, blob, iv.Length, cipher.Length);
            return Convert.ToBase64String(blob);
        }

        /// <summary>Decrypts a value previously produced by <see cref="EncryptForStorage"/>.</summary>
        public string DecryptForStorage(string encryptedBase64)
        {
            var blob = Convert.FromBase64String(encryptedBase64);
            var iv = blob[..16];
            var cipher = blob[16..];
            return AesCryptoService.Decrypt(cipher, _storageKey, iv);
        }

        // ── Helpers ───────────────────────────────────────────────────────

        public bool HasSession(string contactId) => _sessions.ContainsKey(contactId);
        public void RemoveSession(string contactId) => _sessions.TryRemove(contactId, out _);

        private SessionKey GetSession(string contactId)
        {
            if (_sessions.TryGetValue(contactId, out var s)) return s;
            throw new InvalidOperationException(
                $"No AES session established for contact '{contactId}'. " +
                "Complete the RSA key-exchange handshake first.");
        }

        public void Dispose() => _rsa.Dispose();

        // ── Inner type ────────────────────────────────────────────────────

        private sealed record SessionKey(byte[] Key, byte[] IV);
    }
}
