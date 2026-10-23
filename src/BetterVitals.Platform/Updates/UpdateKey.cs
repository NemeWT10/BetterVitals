namespace BetterVitals.Platform.Updates;

/// <summary>Public half of the release-signing key (ECDSA P-256, DER SubjectPublicKeyInfo). Updates signed with anything else are refused.</summary>
public static class UpdateKey
{
    private const string PublicKeyBase64 =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEWw9qKIwwjkRcvUPxV2MesKa8h4ik6AjgcRokdU96IwDPBKLAdtde4Lqzu98b7VIqpGb6yo9iABz/6REiRltJLQ==";

    public static byte[] PublicKey { get; } = Convert.FromBase64String(PublicKeyBase64);
}
