using System.Security.Cryptography;
using System.Text.RegularExpressions;
using EncryptedMessenger.Core.Encryption;

namespace EncryptedMessenger.Tests
{
    public class VerificationCodeTests : IDisposable
    {
        private readonly TempDir _dir = new();
        public void Dispose() => _dir.Dispose();

        [Fact]
        public void Code_IsSymmetric_Deterministic_AndFormattedAs8GroupsOf5()
        {
            var a = _dir.NewKeys("a").PublicKeyXml;
            var b = _dir.NewKeys("b").PublicKeyXml;

            var ab = VerificationCode.Compute(a, b);

            Assert.Equal(ab, VerificationCode.Compute(b, a));
            Assert.Equal(ab, VerificationCode.Compute(a, b));
            Assert.Matches(new Regex(@"^\d{5}( \d{5}){7}$"), ab);
        }

        [Fact]
        public void Code_DiffersUnderManInTheMiddle()
        {
            var alice = _dir.NewKeys("alice").PublicKeyXml;
            var bob = _dir.NewKeys("bob").PublicKeyXml;
            var mallory1 = _dir.NewKeys("m1").PublicKeyXml;
            var mallory2 = _dir.NewKeys("m2").PublicKeyXml;

            Assert.NotEqual(VerificationCode.Compute(alice, mallory1), VerificationCode.Compute(mallory2, bob));
        }

        /// <summary>
        /// The property that defeats the birthday attack: a key's own 20 digits appear in EVERY
        /// code it is part of, so an attacker must hit that fixed target (second preimage).
        /// </summary>
        [Fact]
        public void EachKey_ContributesTheSame20Digits_ToEveryCode()
        {
            var alice = _dir.NewKeys("alice").PublicKeyXml;
            var withBob = Digits(VerificationCode.Compute(alice, _dir.NewKeys("bob").PublicKeyXml));
            var withMallory = Digits(VerificationCode.Compute(alice, _dir.NewKeys("m").PublicKeyXml));

            var aliceHalf = new[] { withBob[..20], withBob[20..] }.Single(h => withMallory.Contains(h));
            Assert.Equal(20, aliceHalf.Length);
        }

        private static string Digits(string code) => code.Replace(" ", "");
    }

    public class AesCryptoTests
    {
        [Fact]
        public void RoundTrip_ReturnsPlaintext()
        {
            var key = AesCryptoService.GenerateKey();
            var nonce = AesCryptoService.GenerateNonce();

            var cipher = AesCryptoService.Encrypt("hallo 👋", key, nonce);

            Assert.Equal("hallo 👋", AesCryptoService.Decrypt(cipher, key, nonce));
        }

        [Fact]
        public void TamperedCiphertext_Throws()
        {
            var key = AesCryptoService.GenerateKey();
            var nonce = AesCryptoService.GenerateNonce();
            var cipher = AesCryptoService.Encrypt("secret", key, nonce);

            cipher[0] ^= 0x01;

            Assert.ThrowsAny<CryptographicException>(() => AesCryptoService.Decrypt(cipher, key, nonce));
        }
    }

    public class CryptoManagerTests : IDisposable
    {
        private readonly TempDir _dir = new();
        public void Dispose() => _dir.Dispose();

        [Fact]
        public void StorageEncryption_RoundTrips_AndUsesFreshNonces()
        {
            using var crypto = _dir.NewKeys("k");

            var c1 = crypto.EncryptForStorage("same text");
            var c2 = crypto.EncryptForStorage("same text");

            Assert.NotEqual(c1, c2);   // fresh nonce per encryption (GCM nonce reuse would be fatal)
            Assert.Equal("same text", crypto.DecryptForStorage(c1));
        }

        [Fact]
        public void SessionKeyExchange_BetweenTwoManagers_Works()
        {
            using var alice = _dir.NewKeys("alice");
            using var bob = _dir.NewKeys("bob");

            var blob = alice.CreateAndEncryptSessionKey("s-alice", bob.PublicKeyXml);
            bob.DecryptAndStoreSessionKey("s-bob", blob);

            Assert.Equal("hi", bob.DecryptMessage(alice.EncryptMessage("hi", "s-alice"), "s-bob"));
        }

        [Fact]
        public void KeyFile_IsDpapiProtected_NotPlainXml()
        {
            _dir.NewKeys("k").Dispose();
            Assert.DoesNotContain("<RSAKeyValue>", File.ReadAllText(_dir.File("k.key")));
        }

        [Fact]
        public void UndecryptableKeyFile_Throws_AndIsLeftUntouched()
        {
            var path = _dir.File("foreign.key");
            var garbage = RandomNumberGenerator.GetBytes(300);   // stands in for another user's DPAPI blob
            File.WriteAllBytes(path, garbage);

            Assert.ThrowsAny<CryptographicException>(() => new CryptoManager(path, _dir.File("foreign.storage")));
            Assert.Equal(garbage, File.ReadAllBytes(path));
        }

        [Fact]
        public void LegacyPlaintextKeys_AreMigratedToDpapi()
        {
            using (var rsa = RSA.Create(2048)) File.WriteAllText(_dir.File("legacy.key"), rsa.ToXmlString(true));
            File.WriteAllText(_dir.File("legacy.storage"), Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));

            using var crypto = new CryptoManager(_dir.File("legacy.key"), _dir.File("legacy.storage"));

            Assert.DoesNotContain("<RSAKeyValue>", File.ReadAllText(_dir.File("legacy.key")));
            Assert.Equal("x", crypto.DecryptForStorage(crypto.EncryptForStorage("x")));
        }
    }
}
