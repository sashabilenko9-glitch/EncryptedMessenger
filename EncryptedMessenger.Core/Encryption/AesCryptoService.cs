using System.Security.Cryptography;
using System.Text;

namespace EncryptedMessenger.Core.Encryption
{
    /// <summary>
    /// Static helpers for AES-256-GCM (authenticated encryption) used for message
    /// and at-rest content. Unlike plain CBC, GCM detects any tampering with the
    /// ciphertext: decryption throws instead of silently returning corrupted data.
    ///
    /// The 12-byte nonce is NOT generated here — callers (<see cref="CryptoManager"/>)
    /// must supply a fresh, never-reused nonce per encryption for a given key, since
    /// nonce reuse under GCM breaks both confidentiality and authenticity.
    /// </summary>
    public static class AesCryptoService
    {
        public const int NonceSizeBytes = 12; // 96-bit, the recommended GCM nonce size
        public const int TagSizeBytes   = 16; // 128-bit authentication tag

        // ── Key generation ────────────────────────────────────────────────

        /// <summary>Generates a cryptographically random AES-256 key.</summary>
        public static byte[] GenerateKey() => RandomNumberGenerator.GetBytes(32);

        /// <summary>Generates a cryptographically random 96-bit GCM nonce.</summary>
        public static byte[] GenerateNonce() => RandomNumberGenerator.GetBytes(NonceSizeBytes);

        // ── String encrypt / decrypt ──────────────────────────────────────

        /// <summary>Encrypts a UTF-8 string; returns ciphertext with the auth tag appended.</summary>
        public static byte[] Encrypt(string plainText, byte[] key, byte[] nonce)
            => EncryptBytes(Encoding.UTF8.GetBytes(plainText), key, nonce);

        /// <summary>
        /// Decrypts ciphertext+tag back to a UTF-8 string.
        /// Throws <see cref="AuthenticationTagMismatchException"/> if the data was tampered with.
        /// </summary>
        public static string Decrypt(byte[] cipherAndTag, byte[] key, byte[] nonce)
            => Encoding.UTF8.GetString(DecryptBytes(cipherAndTag, key, nonce));

        // ── Binary encrypt / decrypt ──────────────────────────────────────

        /// <summary>Encrypts raw bytes; returns ciphertext with the auth tag appended.</summary>
        public static byte[] EncryptBytes(byte[] data, byte[] key, byte[] nonce)
        {
            using var aes = new AesGcm(key, TagSizeBytes);
            var cipher = new byte[data.Length];
            var tag = new byte[TagSizeBytes];
            aes.Encrypt(nonce, data, cipher, tag);

            var result = new byte[cipher.Length + TagSizeBytes];
            Buffer.BlockCopy(cipher, 0, result, 0, cipher.Length);
            Buffer.BlockCopy(tag, 0, result, cipher.Length, TagSizeBytes);
            return result;
        }

        /// <summary>
        /// Decrypts ciphertext+tag back to raw bytes.
        /// Throws <see cref="AuthenticationTagMismatchException"/> if the data was tampered with.
        /// </summary>
        public static byte[] DecryptBytes(byte[] cipherAndTag, byte[] key, byte[] nonce)
        {
            var cipherLen = cipherAndTag.Length - TagSizeBytes;
            var cipher = cipherAndTag[..cipherLen];
            var tag = cipherAndTag[cipherLen..];

            using var aes = new AesGcm(key, TagSizeBytes);
            var plain = new byte[cipherLen];
            aes.Decrypt(nonce, cipher, tag, plain);
            return plain;
        }
    }
}
