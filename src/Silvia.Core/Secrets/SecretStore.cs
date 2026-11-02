using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Silvia.Core.Secrets;

/// <summary>
/// Secrets such as the chat API key, kept in a file that only the current Windows user can decrypt
/// (DPAPI, current-user scope). Values never go into <c>catalog.db</c> or a project file, so exporting
/// a project cannot leak them.
/// </summary>
public sealed class SecretStore(string path)
{
    public const string DeepSeekKey = "deepseek-api-key";

    private readonly Lock _gate = new();

    /// <summary>The default location: <c>secrets.bin</c> in the data folder.</summary>
    public static SecretStore InDataFolder(string dataRoot) => new(Path.Combine(dataRoot, "secrets.bin"));

    public string? Get(string name)
    {
        lock (_gate)
        {
            var all = Load();
            return all.TryGetValue(name, out var value) ? value : null;
        }
    }

    public void Set(string name, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        lock (_gate)
        {
            var all = Load();
            all[name] = value;
            Save(all);
        }
    }

    public bool Remove(string name)
    {
        lock (_gate)
        {
            var all = Load();
            if (!all.Remove(name))
            {
                return false;
            }

            Save(all);
            return true;
        }
    }

    private Dictionary<string, string> Load()
    {
        if (!File.Exists(path))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        if (!OperatingSystem.IsWindows())
        {
            throw NotWindows();
        }

        var protectedBytes = File.ReadAllBytes(path);
        var clear = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(clear) ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
        }
    }

    private void Save(Dictionary<string, string> all)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw NotWindows();
        }

        var clear = JsonSerializer.SerializeToUtf8Bytes(all);
        try
        {
            var protectedBytes = ProtectedData.Protect(clear, Entropy, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            // Write beside the target and move it in, so a crash never leaves a half-written secret file.
            var temporary = path + ".tmp";
            File.WriteAllBytes(temporary, protectedBytes);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
        }
    }

    private static PlatformNotSupportedException NotWindows() =>
        new("Secrets are protected with DPAPI, which needs Windows.");

    // Entropy ties the blob to this application, so another DPAPI user of the same account cannot misuse it.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Silvia.SecretStore.v1");
}
