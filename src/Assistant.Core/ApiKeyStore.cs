using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Assistant.Core;

/// <summary>Encrypts/decrypts a secret for storage on this machine.</summary>
public interface IKeyProtector
{
    byte[] Protect(byte[] plain);
    byte[] Unprotect(byte[] protectedData);
}

/// <summary>Windows DPAPI: only this Windows user on this PC can decrypt it.</summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiProtector : IKeyProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("claude-live-assistant/api-key");
    public byte[] Protect(byte[] plain) => ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
    public byte[] Unprotect(byte[] data) => ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);
}

/// <summary>Fallback for non-Windows systems: the file is only readable by the current user.</summary>
public sealed class UserOnlyFileProtector : IKeyProtector
{
    public byte[] Protect(byte[] plain) => plain;
    public byte[] Unprotect(byte[] data) => data;
}

/// <summary>Stores the user's Claude API key on disk, protected for the current user.</summary>
public sealed class ApiKeyStore
{
    private readonly string _path;
    private readonly IKeyProtector _protector;

    public ApiKeyStore(string? directory = null, IKeyProtector? protector = null)
    {
        _path = Path.Combine(directory ?? AppPaths.ConfigDirectory, "api-key.bin");
        _protector = protector ?? DefaultProtector();
    }

    private static IKeyProtector DefaultProtector() =>
        OperatingSystem.IsWindows() ? new DpapiProtector() : new UserOnlyFileProtector();

    public bool HasKey => File.Exists(_path);

    public string? Load()
    {
        try
        {
            var text = Encoding.UTF8.GetString(_protector.Unprotect(File.ReadAllBytes(_path))).Trim();
            return text.Length > 0 ? text : null;
        }
        catch (Exception ex) when (ex is IOException or CryptographicException or UnauthorizedAccessException)
        {
            return null; // missing, or encrypted for another user/PC: ask for the key again
        }
    }

    public void Save(string apiKey)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllBytes(temp, _protector.Protect(Encoding.UTF8.GetBytes(apiKey.Trim())));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temp, _path, overwrite: true);
    }

    public void Delete()
    {
        try { File.Delete(_path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>"sk-ant-…a1b2": enough to recognise the key, not enough to use it.</summary>
    public static string Mask(string key)
    {
        key = key.Trim();
        if (key.Length <= 12) return new string('•', key.Length);
        return key[..7] + "…" + key[^4..];
    }
}
