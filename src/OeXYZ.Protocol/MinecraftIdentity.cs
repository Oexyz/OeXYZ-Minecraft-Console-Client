using System.Security.Cryptography;

namespace OeXYZ.Protocol;

public sealed record MinecraftIdentity(
    string Username,
    Guid PlayerUuid,
    string? AccessToken = null,
    PlayerCertificate? Certificate = null)
{
    public bool IsOnline => !string.IsNullOrWhiteSpace(AccessToken);

    public static MinecraftIdentity Offline(string username) =>
        new(username, OfflineIdentity.CreateUuid(username));
}

public sealed class PlayerCertificate : IDisposable
{
    public PlayerCertificate(
        RSA privateKey,
        byte[] publicKeyDer,
        byte[] publicKeySignature,
        byte[] publicKeySignatureV2,
        DateTimeOffset expiresAt)
    {
        PrivateKey = privateKey;
        PublicKeyDer = publicKeyDer;
        PublicKeySignature = publicKeySignature;
        PublicKeySignatureV2 = publicKeySignatureV2;
        ExpiresAt = expiresAt;
    }

    public RSA PrivateKey { get; }
    public byte[] PublicKeyDer { get; }
    public byte[] PublicKeySignature { get; }
    public byte[] PublicKeySignatureV2 { get; }
    public DateTimeOffset ExpiresAt { get; }

    public void Dispose() => PrivateKey.Dispose();
}
