using OeXYZ.Core;
using OeXYZ.Protocol;

namespace OeXYZ.Session;

public interface IIdentityProvider
{
    Task<MinecraftIdentity> GetIdentityAsync(
        AccountProfile profile,
        Action<string> status,
        CancellationToken cancellationToken);
}
