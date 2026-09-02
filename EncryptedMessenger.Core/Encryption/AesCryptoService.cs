using System.Security.Cryptography;
using System.Text;

namespace EncryptedMessenger.Core.Encryption
{
    /// <summary>
    /// Static helpers for AES-256-CBC encryption used for message content.
    /// Each session gets a fresh 256-bit key + 128-bit IV.
    /// </summary>
    public static class AesCryptoService
    {
        // ── Key generation ────────────────────────────────────────────────

        /// <summary>Generates a cryptographically random AES-256 key and IV.</summary>
        public static (byte[] Key, byte[] IV) GenerateKeyAndIV()
        {
            using var aes = Aes.Create();
            aes.KeySize = 256;
            aes.GenerateKey();
            aes.GenerateIV();
            return (aes.Key, aes.IV);
        }

        // ── String encrypt / decrypt ──────────────────────────────────────

        /// <summary>Encrypts a UTF-8 string; returns raw cipher bytes.</summary>
        public static byte[] Encrypt(string plainText, byte[] key, byte[] iv)
        {
            using var aes = CreateAes(key, iv);
            using var encryptor = aes.CreateEncryptor();
            using var ms  = new MemoryStream();
            using (var cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write))
            using (var sw = new StreamWriter(cs, Encoding.UTF8))
                sw.Write(plainText);

            return ms.ToArray();
        }

        /// <summary>Decrypts cipher bytes back to a UTF-8 string.</summary>
        public static string Decrypt(byte[] cipherBytes, byte[] key, byte[] iv)
        {
            using var aes = CreateAes(key, iv);
            using var decryptor = aes.CreateDecryptor();
            using var ms  = new MemoryStream(cipherBytes);
            using var cs  = new CryptoStream(ms, decryptor, CryptoStreamMode.Read);
            using var sr  = new StreamReader(cs, Encoding.UTF8);
            return sr.ReadToEnd();
        }

        // ── Binary encrypt / decrypt ──────────────────────────────────────

        public static byte[] EncryptBytes(byte[] data, byte[] key, byte[] iv)
        {
            using var aes = CreateAes(key, iv);
            using var encryptor = aes.CreateEncryptor();
            using var ms  = new MemoryStream();
            using (var cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write))
                cs.Write(data, 0, data.Length);

            return ms.ToArray();
        }

        public static byte[] DecryptBytes(byte[] cipherBytes, byte[] key, byte[] iv)
        {
            using var aes = CreateAes(key, iv);
            using var decryptor = aes.CreateDecryptor();
            using var ms     = new MemoryStream(cipherBytes);
            using var cs     = new CryptoStream(ms, decryptor, CryptoStreamMode.Read);
            using var output = new MemoryStream();
            cs.CopyTo(output);
            return output.ToArray();
        }

        // ── Private helper ────────────────────────────────────────────────

        private static Aes CreateAes(byte[] key, byte[] iv)
        {
            var aes = Aes.Create();
            aes.Key     = key;
            aes.IV      = iv;
            aes.Mode    = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            return aes;
        }
    }
}
