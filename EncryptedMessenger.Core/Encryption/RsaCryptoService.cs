using System.Security.Cryptography;

namespace EncryptedMessenger.Core.Encryption
{
    /// <summary>
    /// Wraps RSA-2048 operations used for the initial key exchange.
    /// Each user generates one key pair per install; the public key is shared
    /// with every peer during the handshake.
    /// </summary>
    public sealed class RsaCryptoService : IDisposable
    {
        private readonly RSA _rsa;

        /// <summary>Creates a fresh RSA-2048 key pair.</summary>
        public RsaCryptoService()
        {
            _rsa = RSA.Create(2048);
        }

        /// <summary>Loads an existing RSA key pair from its XML representation.</summary>
        public RsaCryptoService(string privateKeyXml)
        {
            _rsa = RSA.Create();
            _rsa.FromXmlString(privateKeyXml);
        }

        // ── Key export ───────────────────────────────────────────────────

        /// <summary>Returns the public key in XML – safe to transmit to peers.</summary>
        public string GetPublicKeyXml()  => _rsa.ToXmlString(includePrivateParameters: false);

        /// <summary>Returns the full key pair in XML – MUST stay local.</summary>
        public string GetPrivateKeyXml() => _rsa.ToXmlString(includePrivateParameters: true);

        // ── Asymmetric encrypt / decrypt ─────────────────────────────────

        /// <summary>
        /// Encrypts <paramref name="data"/> with the peer's public key.
        /// Used only to protect the AES session key during the handshake.
        /// OAEP+SHA256 is used (no PKCS#1 v1.5 padding oracle risk).
        /// </summary>
        public byte[] EncryptWithPublicKey(byte[] data, string peerPublicKeyXml)
        {
            using var rsa = RSA.Create();
            rsa.FromXmlString(peerPublicKeyXml);
            return rsa.Encrypt(data, RSAEncryptionPadding.OaepSHA256);
        }

        /// <summary>Decrypts data that was encrypted with this instance's public key.</summary>
        public byte[] DecryptWithPrivateKey(byte[] cipherData)
            => _rsa.Decrypt(cipherData, RSAEncryptionPadding.OaepSHA256);

        // ── Digital signature (optional integrity check) ──────────────────

        public byte[] Sign(byte[] data)
            => _rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        public bool Verify(byte[] data, byte[] signature, string signerPublicKeyXml)
        {
            using var rsa = RSA.Create();
            rsa.FromXmlString(signerPublicKeyXml);
            return rsa.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }

        public void Dispose() => _rsa.Dispose();
    }
}
