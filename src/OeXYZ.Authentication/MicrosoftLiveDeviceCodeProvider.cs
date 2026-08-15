using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using XboxAuthNet.Game;
using XboxAuthNet.Game.Authenticators;
using XboxAuthNet.Game.OAuth;
using XboxAuthNet.OAuth;

namespace OeXYZ.Authentication;

internal sealed class MicrosoftLiveDeviceCodeProvider : IAuthenticationProvider
{
    private readonly MicrosoftOAuthBuilder oauth;
    private readonly MicrosoftOAuthClientInfo clientInfo;
    private readonly Action<MicrosoftDeviceCodePrompt> promptHandler;

    public MicrosoftLiveDeviceCodeProvider(
        MicrosoftOAuthClientInfo clientInfo,
        Action<MicrosoftDeviceCodePrompt> promptHandler)
    {
        ArgumentNullException.ThrowIfNull(clientInfo);
        this.promptHandler = promptHandler ?? throw new ArgumentNullException(nameof(promptHandler));
        this.clientInfo = clientInfo;
        oauth = new MicrosoftOAuthBuilder(clientInfo);
    }

    public IAuthenticator Authenticate() => AuthenticateSilently();

    public IAuthenticator AuthenticateInteractively() => new MicrosoftLiveDeviceCodeAuthenticator(
        oauth.SessionSource,
        oauth.LoginHintSource,
        clientInfo,
        promptHandler);

    public IAuthenticator AuthenticateSilently() => oauth.Silent();

    public IAuthenticator ClearSession() => oauth.Signout();

    public IAuthenticator Signout() => oauth.Signout();

    public ISessionValidator CreateSessionValidator() => oauth.Validator();
}

internal sealed class MicrosoftLiveDeviceCodeAuthenticator : MicrosoftOAuth
{
    private readonly Action<MicrosoftDeviceCodePrompt> promptHandler;

    public MicrosoftLiveDeviceCodeAuthenticator(
        XboxAuthNet.Game.SessionStorages.ISessionSource<MicrosoftOAuthResponse> sessionSource,
        XboxAuthNet.Game.SessionStorages.ISessionSource<string> loginHintSource,
        MicrosoftOAuthClientInfo clientInfo,
        Action<MicrosoftDeviceCodePrompt> promptHandler)
        : base(new MicrosoftOAuthParameters(
            clientInfo,
            sessionSource,
            loginHintSource)) =>
        this.promptHandler = promptHandler;

    protected override async ValueTask<MicrosoftOAuthResponse?> Authenticate(
        AuthenticateContext context,
        MicrosoftOAuthParameters parameters)
    {
        MicrosoftLiveDeviceCodeClient client = new(
            context.HttpClient,
            parameters.ClientInfo.ClientId,
            parameters.ClientInfo.Scopes);
        return await client.AuthenticateAsync(promptHandler, context.CancellationToken).ConfigureAwait(false);
    }
}

internal sealed class MicrosoftLiveDeviceCodeClient
{
    internal const string DeviceCodeEndpoint = "https://login.live.com/oauth20_connect.srf";
    internal const string TokenEndpoint = "https://login.live.com/oauth20_token.srf";
    internal const int MaximumResponseBytes = 64 * 1024;
    private const int MaximumDeviceCodeLength = 4096;
    private const int MaximumUserCodeLength = 64;
    private const int MaximumErrorLength = 128;
    private const int MaximumPollSeconds = 30;

    private readonly HttpClient httpClient;
    private readonly string clientId;
    private readonly string scopes;
    private readonly Func<DateTimeOffset> utcNow;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;

    public MicrosoftLiveDeviceCodeClient(HttpClient httpClient, string clientId, string scopes)
        : this(httpClient, clientId, scopes, () => DateTimeOffset.UtcNow, Task.Delay)
    {
    }

    internal MicrosoftLiveDeviceCodeClient(
        HttpClient httpClient,
        string clientId,
        string scopes,
        Func<DateTimeOffset> utcNow,
        Func<TimeSpan, CancellationToken, Task> delay)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.clientId = RequireBounded(clientId, 128, "Microsoft client ID");
        this.scopes = RequireBounded(scopes, 1024, "Microsoft OAuth scopes");
        this.utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
        this.delay = delay ?? throw new ArgumentNullException(nameof(delay));
    }

    public async Task<MicrosoftOAuthResponse> AuthenticateAsync(
        Action<MicrosoftDeviceCodePrompt> promptHandler,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(promptHandler);
        LiveDeviceCode code = await RequestDeviceCodeAsync(cancellationToken).ConfigureAwait(false);
        DateTimeOffset expiresOn = utcNow().AddSeconds(code.ExpiresInSeconds);
        promptHandler(new MicrosoftDeviceCodePrompt(code.UserCode, code.VerificationUri.AbsoluteUri, expiresOn));

        TimeSpan interval = TimeSpan.FromSeconds(code.IntervalSeconds);
        while (utcNow() < expiresOn)
        {
            await delay(interval, cancellationToken).ConfigureAwait(false);
            PollResult result = await PollAsync(code.DeviceCode, cancellationToken).ConfigureAwait(false);
            if (result.Response is not null) return result.Response;
            if (result.SlowDown)
                interval = TimeSpan.FromSeconds(Math.Min(MaximumPollSeconds, interval.TotalSeconds + 5));
        }

        throw new MicrosoftDeviceAuthenticationException(
            "Microsoft device sign-in expired before it was completed. Start the login again.");
    }

    private async Task<LiveDeviceCode> RequestDeviceCodeAsync(CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = CreateFormRequest(DeviceCodeEndpoint, new Dictionary<string, string>
        {
            ["scope"] = scopes,
            ["client_id"] = clientId,
            ["response_type"] = "device_code"
        });
        using HttpResponseMessage response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        string body = await ReadBoundedBodyAsync(response, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw CreateServiceException(response.StatusCode, body, "Microsoft rejected the device-code request");

        try
        {
            using JsonDocument document = JsonDocument.Parse(body, JsonOptions);
            JsonElement root = document.RootElement;
            string deviceCode = GetRequiredString(root, "device_code", MaximumDeviceCodeLength);
            string userCode = GetRequiredString(root, "user_code", MaximumUserCodeLength);
            string verification = GetRequiredString(root, "verification_uri", 2048);
            if (!Uri.TryCreate(verification, UriKind.Absolute, out Uri? verificationUri) ||
                verificationUri.Scheme != Uri.UriSchemeHttps ||
                !IsMicrosoftVerificationHost(verificationUri.Host))
            {
                throw new MicrosoftDeviceAuthenticationException(
                    "Microsoft returned an invalid device-login verification URL.");
            }

            int expiresIn = GetBoundedInt(root, "expires_in", 30, 3600);
            int interval = GetBoundedInt(root, "interval", 1, MaximumPollSeconds);
            return new LiveDeviceCode(deviceCode, userCode, verificationUri, expiresIn, interval);
        }
        catch (JsonException exception)
        {
            throw new MicrosoftDeviceAuthenticationException(
                "Microsoft returned a malformed device-code response.", exception);
        }
    }

    private async Task<PollResult> PollAsync(string deviceCode, CancellationToken cancellationToken)
    {
        string endpoint = TokenEndpoint + "?client_id=" + Uri.EscapeDataString(clientId);
        using HttpRequestMessage request = CreateFormRequest(endpoint, new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["device_code"] = deviceCode,
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code"
        });
        using HttpResponseMessage response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        string body = await ReadBoundedBodyAsync(response, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            MicrosoftOAuthResponse token = MicrosoftOAuthResponse.FromHttpResponse(
                body,
                (int)response.StatusCode,
                response.ReasonPhrase);
            if (string.IsNullOrWhiteSpace(token.AccessToken) || string.IsNullOrWhiteSpace(token.RawRefreshToken))
                throw new MicrosoftDeviceAuthenticationException(
                    "Microsoft sign-in completed without reusable access and refresh tokens.");
            return new PollResult(token, false);
        }

        string error = ReadErrorCode(body);
        return error switch
        {
            "authorization_pending" => new PollResult(null, false),
            "slow_down" => new PollResult(null, true),
            "authorization_declined" => throw new MicrosoftDeviceAuthenticationException(
                "Microsoft device sign-in was declined."),
            "expired_token" or "code_expired" => throw new MicrosoftDeviceAuthenticationException(
                "Microsoft device sign-in expired. Start the login again."),
            "bad_verification_code" => throw new MicrosoftDeviceAuthenticationException(
                "Microsoft rejected the temporary device code. Start the login again."),
            _ => throw CreateServiceException(response.StatusCode, body, "Microsoft device sign-in failed")
        };
    }

    private static HttpRequestMessage CreateFormRequest(string endpoint, Dictionary<string, string> fields)
    {
        HttpRequestMessage request = new(HttpMethod.Post, endpoint)
        {
            Content = new FormUrlEncodedContent(fields)
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("OeXYZ-Minecraft-Console-Client/1.3");
        return request;
    }

    private static async Task<string> ReadBoundedBodyAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > MaximumResponseBytes)
            throw new MicrosoftDeviceAuthenticationException(
                "Microsoft returned an unexpectedly large authentication response.");

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            using MemoryStream output = new();
            while (true)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0) break;
                if (output.Length + read > MaximumResponseBytes)
                    throw new MicrosoftDeviceAuthenticationException(
                        "Microsoft returned an unexpectedly large authentication response.");
                output.Write(buffer, 0, read);
            }
            return Encoding.UTF8.GetString(output.GetBuffer(), 0, checked((int)output.Length));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static MicrosoftDeviceAuthenticationException CreateServiceException(
        HttpStatusCode statusCode,
        string body,
        string prefix)
    {
        string code = ReadErrorCode(body);
        string suffix = code.Length == 0 ? string.Empty : $" ({code})";
        return new MicrosoftDeviceAuthenticationException(
            $"{prefix}{suffix}; HTTP {(int)statusCode}.");
    }

    private static string ReadErrorCode(string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body, JsonOptions);
            if (!document.RootElement.TryGetProperty("error", out JsonElement element) ||
                element.ValueKind != JsonValueKind.String)
                return string.Empty;
            string value = element.GetString() ?? string.Empty;
            if (value.Length is 0 or > MaximumErrorLength) return string.Empty;
            return value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-')
                ? value
                : string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    private static string GetRequiredString(JsonElement root, string property, int maximumLength)
    {
        if (!root.TryGetProperty(property, out JsonElement value) || value.ValueKind != JsonValueKind.String)
            throw new MicrosoftDeviceAuthenticationException(
                $"Microsoft omitted the required '{property}' device-login field.");
        return RequireBounded(value.GetString(), maximumLength, $"Microsoft '{property}' field");
    }

    private static int GetBoundedInt(JsonElement root, string property, int minimum, int maximum)
    {
        if (!root.TryGetProperty(property, out JsonElement value) || !value.TryGetInt32(out int result) ||
            result < minimum || result > maximum)
            throw new MicrosoftDeviceAuthenticationException(
                $"Microsoft returned an invalid '{property}' device-login value.");
        return result;
    }

    private static string RequireBounded(string? value, int maximumLength, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength ||
            value.IndexOfAny(['\r', '\n', '\0']) >= 0)
            throw new MicrosoftDeviceAuthenticationException($"The {name} is missing or invalid.");
        return value;
    }

    private static bool IsMicrosoftVerificationHost(string host) =>
        host.Equals("microsoft.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".microsoft.com", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("live.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".live.com", StringComparison.OrdinalIgnoreCase);

    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 16
    };

    private sealed record LiveDeviceCode(
        string DeviceCode,
        string UserCode,
        Uri VerificationUri,
        int ExpiresInSeconds,
        int IntervalSeconds);

    private sealed record PollResult(MicrosoftOAuthResponse? Response, bool SlowDown);
}

internal sealed class MicrosoftDeviceAuthenticationException : Exception
{
    public MicrosoftDeviceAuthenticationException(string message) : base(message)
    {
    }

    public MicrosoftDeviceAuthenticationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
