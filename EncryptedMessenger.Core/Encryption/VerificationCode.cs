using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace EncryptedMessenger.Core.Encryption
{
    /// <summary>
    /// 40-digit verification code ("12345 67890 … 12345", 8 groups of 5) that two users
    /// compare out-of-band (phone, in person) to confirm no one sits between them.
    /// Same construction as Signal's safety number, shorter (Signal: 2 × 30 digits).
    ///
    /// Each public key gets its OWN 20 digits; the code is both halves in sorted order, so
    /// both sides see the same code. With a man-in-the-middle, Alice sees
    /// sort(digits(Alice), digits(Mallory₁)) and Bob sees sort(digits(Mallory₂), digits(Bob)):
    /// for these to match, Mallory needs a key whose 20 digits equal those of Alice's
    /// ALREADY FIXED key — a second-preimage search, ~10²⁰ tries.
    ///
    /// Why not one short code hashed from both keys together: then Mallory can vary BOTH of
    /// her keys and only needs ANY collision between the two sides' candidate lists. By the
    /// birthday paradox that takes ~√N tries per side — for 12 digits about 10⁶ each, which is
    /// cheap (a new RSA key only needs a new exponent, d = e⁻¹ mod φ(n)). Per-key halves
    /// remove that freedom: each half must hit a fixed target.
    /// </summary>
    public static class VerificationCode
    {
        // Domain separation: these bytes make the hash specific to this purpose and version,
        // so it can never coincide with a SHA-256 of the same key computed for something else.
        private static readonly byte[] Domain = Encoding.UTF8.GetBytes("EncryptedMessenger/verification-code/v1");

        private const int DigitsPerKey = 20;
        private static readonly UInt128 Modulus = UInt128.Parse("100000000000000000000"); // 10²⁰

        public static string Compute(string publicKeyXmlA, string publicKeyXmlB)
        {
            var a = DigitsFor(publicKeyXmlA);
            var b = DigitsFor(publicKeyXmlB);

            // Canonical order, so both sides show the same 40 digits whoever computes them.
            var combined = string.CompareOrdinal(a, b) <= 0 ? a + b : b + a;

            // 8 groups of 5 — short enough to read aloud group by group.
            return string.Join(' ', Enumerable.Range(0, combined.Length / 5).Select(i => combined.Substring(i * 5, 5)));
        }

        /// <summary>The 20 digits that belong to one public key.</summary>
        private static string DigitsFor(string publicKeyXml)
        {
            var digest = SHA256.HashData([.. Domain, .. Encoding.UTF8.GetBytes(publicKeyXml)]);

            // First 16 bytes as a 128-bit number, reduced to 20 digits. The modulo bias is
            // negligible: 2¹²⁸ is ~3.4·10¹⁸ times larger than 10²⁰.
            var number = BinaryPrimitives.ReadUInt128BigEndian(digest) % Modulus;
            return number.ToString("D" + DigitsPerKey);   // leading zeros kept: always 20 digits
        }
    }
}
