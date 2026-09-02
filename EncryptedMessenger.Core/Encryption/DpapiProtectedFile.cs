using System.Security.Cryptography;
using System.Text;

namespace EncryptedMessenger.Core.Encryption
{
    /// <summary>
    /// Reads/writes small text files (RSA private key, local storage key) protected
    /// with Windows DPAPI, CurrentUser scope. The file on disk is only readable by
    /// whoever is logged into this Windows account on this machine — copying the
    /// file elsewhere (another user, another PC) yields useless ciphertext.
    /// </summary>
    internal static class DpapiProtectedFile
    {
        public static void WriteText(string path, string plainText)
        {
            var plainBytes = Encoding.UTF8.GetBytes(plainText);
            var protectedBytes = ProtectedData.Protect(plainBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(path, protectedBytes);
        }

        /// <summary>
        /// Reads a DPAPI-protected file. Transparently migrates a plaintext file left
        /// over from a pre-DPAPI build: if unprotecting fails, the bytes are read back
        /// as plain UTF-8 text (the old format) and the file is immediately re-saved
        /// DPAPI-protected, so this only happens once per file.
        /// </summary>
        public static string ReadText(string path)
        {
            var bytes = File.ReadAllBytes(path);
            try
            {
                var plainBytes = ProtectedData.Unprotect(bytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plainBytes);
            }
            catch (CryptographicException)
            {
                var legacyPlainText = Encoding.UTF8.GetString(bytes);
                WriteText(path, legacyPlainText);
                return legacyPlainText;
            }
        }
    }
}
