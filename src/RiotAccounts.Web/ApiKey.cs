using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using RiotAccounts.Core;

namespace RiotAccounts.Web;

// The server keeps one secret, the Riot API key, encrypted with ASP.NET Core Data Protection (key ring in the data directory).
public sealed class DataProtectionSecretProtector(IDataProtectionProvider provider) : ISecretProtector
{
    private readonly IDataProtector protector = provider.CreateProtector("RiotAccounts.Store.Secrets.v1");
    public byte[] Protect(byte[] data) => protector.Protect(data);
    public byte[] Unprotect(byte[] data) => protector.Unprotect(data);
}

public sealed class ApiKey(Store store, ILogger<ApiKey> logger)
{
    private const string SecretId = "riot-api-key";

    // A key encrypted under a lost key ring cannot be read back; it counts as unregistered so the settings page asks for it again.
    public string? Get()
    {
        try { return store.GetSecret(SecretId); }
        catch (CryptographicException) { logger.LogWarning("保存済みのRiot APIキーを復号できません。設定画面で登録し直してください。"); return null; }
    }

    public bool Registered => !string.IsNullOrWhiteSpace(Get());
    public void Set(string key) => store.SetSecret(SecretId, key);
}
