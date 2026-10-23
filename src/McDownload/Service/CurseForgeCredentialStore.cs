using McDownload.i18n;
using System.Security.Cryptography;
using System.Text;

namespace McDownload.Service;

public sealed class CurseForgeCredentialStore(string path)
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SCI.CurseForge.ApiKey.v1");

    public async Task<string?> LoadAsync()
    {
        if (!File.Exists(path)) return null;
        byte[] encrypted = await InstanceManager.ReadBytesAsync(path, 16 * 1024, default);
        byte[] plain = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
        try { return Validate(Encoding.UTF8.GetString(plain)); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public async Task SaveAsync(string key)
    {
        byte[] plain = Encoding.UTF8.GetBytes(Validate(key));
        try
        {
            byte[] encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            await InstanceManager.AtomicBytesAsync(path, encrypted, default);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public void Clear()
    {
        InstanceManager.CheckNoReparse(path);
        File.Delete(path);
    }

    public static string Validate(string key)
    {
        key = key.Trim();
        if (key.Length is < 1 or > 1024 || key.Any(x => x < '!' || x > '~'))
            throw new ArgumentException(Text.Get("CurseForgeCredentialStore", "InvalidApiKey"));
        return key;
    }
}
