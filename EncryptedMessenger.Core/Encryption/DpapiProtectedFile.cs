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
        /// over from a pre-DPAPI build: if unprotecting fails AND the raw bytes pass
        /// <paramref name="isLegacyPlainText"/>, the file is re-saved DPAPI-protected,
        /// so this only happens once per file.
        ///
        /// If unprotecting fails and the content is NOT a recognisable legacy key, the
        /// file was protected by another Windows account or machine (copied data folder,
        /// service running as a different user). It is left untouched — overwriting it
        /// would destroy the only copy of the key — and an exception is thrown.
        /// </summary>
        public static string ReadText(string path, Func<string, bool> isLegacyPlainText)
        {
            var bytes = File.ReadAllBytes(path);
            try
            {
                var plainBytes = ProtectedData.Unprotect(bytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plainBytes);
            }
            catch (CryptographicException ex)
            {
                string legacyPlainText;
                try
                {
                    legacyPlainText = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
                }
                catch (DecoderFallbackException)
                {
                    legacyPlainText = string.Empty;
                }

                if (!isLegacyPlainText(legacyPlainText))
                    throw new CryptographicException(
                        $"'{path}' cannot be decrypted by the current Windows account. It was probably " +
                        "created by another user or on another machine. The file was left unchanged.", ex);

                WriteText(path, legacyPlainText);
                return legacyPlainText;
            }
        }
    }
}
