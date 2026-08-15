using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace OeXYZ.Protocol;

public sealed class MinecraftServicesClient
{
    private readonly HttpClient httpClient;

    public MinecraftServicesClient(HttpClient? httpClient = null)
    {
        this.httpClient = httpClient ?? SharedHttpClient;
    }

    public async Task JoinServerAsync(MinecraftIdentity identity, string serverHash, CancellationToken cancellationToken)
    {
        if (!identity.IsOnline) throw new InvalidOperationException("An online identity is required for the session server.");
        using HttpRequestMessage request = new(HttpMethod.Post, "https://sessionserver.mojang.com/session/minecraft/join")
        {
            Content = JsonContent.Create(new
            {
                accessToken = identity.AccessToken,
                selectedProfile = identity.PlayerUuid.ToString("N"),
                serverId = serverHash
            })
        };
        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode) return;
        string detail = TerminalTextSanitizer.Sanitize(
            await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        throw new HttpRequestException($"Minecraft session server rejected the join ({(int)response.StatusCode}): {detail}");
    }

    public async Task<PlayerCertificate> FetchPlayerCertificateAsync(string accessToken, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, "https://api.minecraftservices.com/player/certificates");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        CertificateDocument document = await response.Content.ReadFromJsonAsync<CertificateDocument>(JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Minecraft services returned an empty player certificate.");

        RSA privateKey = RSA.Create();
        privateKey.ImportFromPem(document.KeyPair.PrivateKey);
        RSA publicKey = RSA.Create();
        publicKey.ImportFromPem(document.KeyPair.PublicKey);
        byte[] publicDer = publicKey.ExportSubjectPublicKeyInfo();
        publicKey.Dispose();
        return new PlayerCertificate(
            privateKey,
            publicDer,
            Convert.FromBase64String(document.PublicKeySignature),
            Convert.FromBase64String(document.PublicKeySignatureV2),
            document.ExpiresAt);
    }

    private static readonly HttpClient SharedHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private sealed record CertificateDocument(
        CertificateKeyPair KeyPair,
        string PublicKeySignature,
        string PublicKeySignatureV2,
        DateTimeOffset ExpiresAt);

    private sealed record CertificateKeyPair(string PrivateKey, string PublicKey);
}
