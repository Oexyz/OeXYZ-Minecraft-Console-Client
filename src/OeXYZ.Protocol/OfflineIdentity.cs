using System.Security.Cryptography;
using System.Text;

namespace OeXYZ.Protocol;

public static class OfflineIdentity
{
    public static Guid CreateUuid(string username)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + username));
        hash[6] = (byte)((hash[6] & 0x0F) | 0x30);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash, bigEndian: true);
    }
}
