using OeXYZ.Core;
using OeXYZ.Protocol;

namespace OeXYZ.Session;

public enum AuthenticationInteractionMode
{
    InteractiveAllowed,
    SilentOnly
}

public sealed class AuthenticationInteractionRequiredException : Exception
{
    public AuthenticationInteractionRequiredException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

public interface IIdentityProvider
{
    Task<MinecraftIdentity> GetIdentityAsync(
        AccountProfile profile,
        Action<string> status,
        CancellationToken cancellationToken,
        AuthenticationInteractionMode interactionMode = AuthenticationInteractionMode.InteractiveAllowed);
}
