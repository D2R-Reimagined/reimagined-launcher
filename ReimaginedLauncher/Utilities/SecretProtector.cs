using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ReimaginedLauncher.Utilities;

/// <summary>
/// Cross-platform at-rest protector for short secrets (e.g. the Nexus API key).
/// Uses AES-256-GCM with a key derived (via HKDF-SHA256) from a stable per-machine
/// identifier combined with the current user name. Output is a base64 string with
/// an <see cref="EncryptedPrefix"/> tag so legacy plaintext values can still be
/// recognised and transparently migrated on the next save.
/// </summary>
public static class SecretProtector
{
    private const string EncryptedPrefix = "enc:v1:";
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;
    private static readonly byte[] HkdfInfo = Encoding.UTF8.GetBytes("ReimaginedLauncher.SecretProtector.v1");
    private static readonly byte[] HkdfSalt = Encoding.UTF8.GetBytes("ReimaginedLauncher.Salt.v1");

    /// <summary>
    /// Encrypts the supplied plaintext. Returns <c>null</c> for null/whitespace input,
    /// or the original value untouched if encryption is unavailable on this machine.
    /// </summary>
    public static string? Protect(string? plaintext)
    {
        if (string.IsNullOrWhiteSpace(plaintext))
            return plaintext;

        if (plaintext.StartsWith(EncryptedPrefix, StringComparison.Ordinal))
            return plaintext;

        try
        {
            var key = DeriveKey();
            var nonce = RandomNumberGenerator.GetBytes(NonceSize);
            var plainBytes = Encoding.UTF8.GetBytes(plaintext);
            var cipher = new byte[plainBytes.Length];
            var tag = new byte[TagSize];

            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(nonce, plainBytes, cipher, tag);

            var output = new byte[NonceSize + TagSize + cipher.Length];
            Buffer.BlockCopy(nonce, 0, output, 0, NonceSize);
            Buffer.BlockCopy(tag, 0, output, NonceSize, TagSize);
            Buffer.BlockCopy(cipher, 0, output, NonceSize + TagSize, cipher.Length);

            return EncryptedPrefix + Convert.ToBase64String(output);
        }
        catch
        {
            // If anything blocks key derivation/encryption, fall back to plaintext
            // rather than losing the user's API key. The next successful save will
            // re-attempt encryption.
            return plaintext;
        }
    }

    /// <summary>
    /// Decrypts a value previously produced by <see cref="Protect"/>. Values without
    /// the encrypted prefix are returned untouched (legacy plaintext migration path).
    /// </summary>
    public static string? Unprotect(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
            return stored;

        if (!stored.StartsWith(EncryptedPrefix, StringComparison.Ordinal))
            return stored;

        try
        {
            var payload = Convert.FromBase64String(stored.Substring(EncryptedPrefix.Length));
            if (payload.Length < NonceSize + TagSize)
                return null;

            var nonce = new byte[NonceSize];
            var tag = new byte[TagSize];
            var cipher = new byte[payload.Length - NonceSize - TagSize];
            Buffer.BlockCopy(payload, 0, nonce, 0, NonceSize);
            Buffer.BlockCopy(payload, NonceSize, tag, 0, TagSize);
            Buffer.BlockCopy(payload, NonceSize + TagSize, cipher, 0, cipher.Length);

            var plain = new byte[cipher.Length];
            using var aes = new AesGcm(DeriveKey(), TagSize);
            aes.Decrypt(nonce, cipher, tag, plain);

            return Encoding.UTF8.GetString(plain);
        }
        catch
        {
            // Tampered, corrupt, or moved between users/machines – treat as unset
            // so the user is prompted to re-enter rather than crashing the launcher.
            return null;
        }
    }

    private static byte[] DeriveKey()
    {
        var ikm = BuildInputKeyingMaterial();
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, KeySize, HkdfSalt, HkdfInfo);
    }

    private static byte[] BuildInputKeyingMaterial()
    {
        var machine = GetMachineSecret();
        var user = Encoding.UTF8.GetBytes(Environment.UserName ?? string.Empty);

        var combined = new byte[machine.Length + 1 + user.Length];
        Buffer.BlockCopy(machine, 0, combined, 0, machine.Length);
        combined[machine.Length] = 0x00;
        Buffer.BlockCopy(user, 0, combined, machine.Length + 1, user.Length);
        return combined;
    }

    private static byte[] GetMachineSecret()
    {
        var fromOs = TryReadOsMachineId();
        if (fromOs is { Length: > 0 })
            return Encoding.UTF8.GetBytes(fromOs);

        return GetOrCreateLocalMachineKey();
    }

    private static string? TryReadOsMachineId()
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return ReadWindowsMachineGuid();

            if (OperatingSystem.IsLinux())
            {
                foreach (var path in new[] { "/etc/machine-id", "/var/lib/dbus/machine-id" })
                {
                    if (File.Exists(path))
                    {
                        var value = File.ReadAllText(path).Trim();
                        if (!string.IsNullOrWhiteSpace(value))
                            return value;
                    }
                }
            }
            // macOS does not expose machine-id; fall through to the local key file.
        }
        catch
        {
            // Permission issues / missing registry / etc. – fall back to the local file.
        }

        return null;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string? ReadWindowsMachineGuid()
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
            @"SOFTWARE\Microsoft\Cryptography");
        return key?.GetValue("MachineGuid") as string;
    }

    private static byte[] GetOrCreateLocalMachineKey()
    {
        var dir = SettingsManager.AppDirectoryPath;
        var keyPath = Path.Combine(dir, ".machine-key");

        try
        {
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            if (File.Exists(keyPath))
            {
                var existing = File.ReadAllBytes(keyPath);
                if (existing.Length >= KeySize)
                    return existing;
            }

            var generated = RandomNumberGenerator.GetBytes(KeySize);
            File.WriteAllBytes(keyPath, generated);
            return generated;
        }
        catch
        {
            // Last-resort static seed: still better than plaintext on disk because
            // it requires the attacker to also know which launcher produced it.
            return Encoding.UTF8.GetBytes("ReimaginedLauncher.fallback-seed.v1");
        }
    }
}
