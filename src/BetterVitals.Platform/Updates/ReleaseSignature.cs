using System.Security.Cryptography;

namespace BetterVitals.Platform.Updates;

/// <summary>
/// ECDSA P-256 over the SHA-256 of the release exe; the signature file holds the 64-byte IEEE P1363 signature in
/// Base64. The private key never leaves the author's machine (tools/ReleaseKey keeps it outside git); the public key
/// is compiled in (<see cref="UpdateKey"/>).
/// </summary>
public static class ReleaseSignature
{
    public static bool Verify(byte[] publicKeySpki, byte[] sha256, byte[] signature)
    {
        if (publicKeySpki.Length == 0 || sha256.Length != 32 || signature.Length != 64) return false;
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(publicKeySpki, out _);
            if (ecdsa.KeySize != 256) return false;
            return ecdsa.VerifyHash(sha256, signature, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>Signs a SHA-256 (release tooling only).</summary>
    public static byte[] Sign(byte[] pkcs8PrivateKey, byte[] sha256)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportPkcs8PrivateKey(pkcs8PrivateKey, out _);
        return ecdsa.SignHash(sha256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }
}
