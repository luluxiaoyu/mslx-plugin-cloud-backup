using System.Security.Cryptography;
using System.Text;
using MSLX.Plugin.Cloud.Backup.Models;
using MSLX.SDK;

namespace MSLX.Plugin.Cloud.Backup.Services.Security;

public static class PluginCryptoService
{
    private const string EncPrefix = "enc:v1:";
    private const int KeySizeBytes = 32; // 256-bit AES
    private const int NonceSizeBytes = 12; // Standard GCM nonce
    private const int TagSizeBytes = 16; // Standard GCM tag

    private static readonly object _keyLock = new();
    private static byte[]? _cachedKey;

    private static void LogInfo(string msg)
    {
        try { SDK.MSLX.Logger?.Info(msg); } catch {}
    }

    private static void LogWarn(string msg)
    {
        try { SDK.MSLX.Logger?.Warn(msg); } catch {}
    }

    private static void LogError(string msg)
    {
        try { SDK.MSLX.Logger?.Error(msg); } catch {}
    }

    private static byte[] GetOrCreateMasterKey()
    {
        if (_cachedKey != null) return _cachedKey;

        lock (_keyLock)
        {
            if (_cachedKey != null) return _cachedKey;

            string dataDir;
            try
            {
                dataDir = MSLXPluginEntry.Instance != null
                    ? MSLXPluginEntry.Instance.Config().GetDataPath()
                    : Path.Combine(AppContext.BaseDirectory, "PluginsData", "mslx-plugin-cloud-backup");
            }
            catch
            {
                dataDir = Path.Combine(AppContext.BaseDirectory, "PluginsData", "mslx-plugin-cloud-backup");
            }

            if (!Directory.Exists(dataDir))
            {
                Directory.CreateDirectory(dataDir);
            }

            string keyFilePath = Path.Combine(dataDir, ".master.key");

            if (File.Exists(keyFilePath))
            {
                try
                {
                    var existingKey = File.ReadAllBytes(keyFilePath);
                    if (existingKey.Length == KeySizeBytes)
                    {
                        EnsureFilePermissions(keyFilePath);
                        _cachedKey = existingKey;
                        return _cachedKey;
                    }
                }
                catch (Exception ex)
                {
                    LogWarn($"[CloudBackup] 读取现有主密钥文件失败，将重新生成: {ex.Message}");
                }
            }

            // 生成新的 256 位随机主密钥
            var newKey = new byte[KeySizeBytes];
            RandomNumberGenerator.Fill(newKey);
            File.WriteAllBytes(keyFilePath, newKey);
            EnsureFilePermissions(keyFilePath);

            _cachedKey = newKey;
            LogInfo("[CloudBackup] 已生成插件专属高权限独立加密密钥 (.master.key)");
            return _cachedKey;
        }
    }

    private static void EnsureFilePermissions(string filePath)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                // Linux / macOS: 0600 (仅当前用户读写)
                File.SetUnixFileMode(filePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            else
            {
                // Windows: 标记为隐藏文件
                var attr = File.GetAttributes(filePath);
                if (!attr.HasFlag(FileAttributes.Hidden))
                {
                    File.SetAttributes(filePath, attr | FileAttributes.Hidden);
                }
            }
        }
        catch
        {
        }
    }

    public static string Encrypt(string? plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return string.Empty;
        if (plainText.StartsWith(EncPrefix, StringComparison.Ordinal)) return plainText;

        try
        {
            byte[] key = GetOrCreateMasterKey();
            byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);

            byte[] nonce = new byte[NonceSizeBytes];
            RandomNumberGenerator.Fill(nonce);

            byte[] cipherBytes = new byte[plainBytes.Length];
            byte[] tag = new byte[TagSizeBytes];

            using var aesGcm = new AesGcm(key, TagSizeBytes);
            aesGcm.Encrypt(nonce, plainBytes, cipherBytes, tag);

            // 组合 Payload: Nonce(12) + Tag(16) + Ciphertext
            byte[] payload = new byte[nonce.Length + tag.Length + cipherBytes.Length];
            Buffer.BlockCopy(nonce, 0, payload, 0, nonce.Length);
            Buffer.BlockCopy(tag, 0, payload, nonce.Length, tag.Length);
            Buffer.BlockCopy(cipherBytes, 0, payload, nonce.Length + tag.Length, cipherBytes.Length);

            return $"{EncPrefix}{Convert.ToBase64String(payload)}";
        }
        catch (Exception ex)
        {
            LogError($"[CloudBackup] 凭据加密失败: {ex.Message}");
            return plainText;
        }
    }

    public static bool IsPlaintext(string? val)
    {
        return !string.IsNullOrEmpty(val) && !val.StartsWith(EncPrefix, StringComparison.Ordinal);
    }

    public static string Decrypt(string? cipherText)
    {
        if (string.IsNullOrEmpty(cipherText)) return string.Empty;
        if (!cipherText.StartsWith(EncPrefix, StringComparison.Ordinal))
        {
            // 向前兼容历史未加密明文
            return cipherText;
        }

        try
        {
            byte[] key = GetOrCreateMasterKey();
            string b64 = cipherText.Substring(EncPrefix.Length);
            byte[] payload = Convert.FromBase64String(b64);

            if (payload.Length < NonceSizeBytes + TagSizeBytes)
            {
                return string.Empty;
            }

            byte[] nonce = new byte[NonceSizeBytes];
            byte[] tag = new byte[TagSizeBytes];
            int cipherLen = payload.Length - NonceSizeBytes - TagSizeBytes;
            byte[] cipherBytes = new byte[cipherLen];

            Buffer.BlockCopy(payload, 0, nonce, 0, NonceSizeBytes);
            Buffer.BlockCopy(payload, NonceSizeBytes, tag, 0, TagSizeBytes);
            Buffer.BlockCopy(payload, NonceSizeBytes + TagSizeBytes, cipherBytes, 0, cipherLen);

            byte[] plainBytes = new byte[cipherLen];
            using var aesGcm = new AesGcm(key, TagSizeBytes);
            aesGcm.Decrypt(nonce, cipherBytes, tag, plainBytes);

            return Encoding.UTF8.GetString(plainBytes);
        }
        catch (Exception ex)
        {
            LogError($"[CloudBackup] 凭据解密失败: {ex.Message}");
            return string.Empty;
        }
    }

    /// <summary>
    /// 对策略的敏感凭证字段（ID、密钥、密码、用户名）执行落盘前加密
    /// </summary>
    public static void EncryptProfileInPlace(CloudStorageProfile profile)
    {
        if (!string.IsNullOrEmpty(profile.S3AccessKey))
            profile.S3AccessKey = Encrypt(profile.S3AccessKey);
        if (!string.IsNullOrEmpty(profile.S3SecretKey))
            profile.S3SecretKey = Encrypt(profile.S3SecretKey);

        if (!string.IsNullOrEmpty(profile.WebDavUsername))
            profile.WebDavUsername = Encrypt(profile.WebDavUsername);
        if (!string.IsNullOrEmpty(profile.WebDavPassword))
            profile.WebDavPassword = Encrypt(profile.WebDavPassword);

        if (!string.IsNullOrEmpty(profile.FtpUsername))
            profile.FtpUsername = Encrypt(profile.FtpUsername);
        if (!string.IsNullOrEmpty(profile.FtpPassword))
            profile.FtpPassword = Encrypt(profile.FtpPassword);

        if (!string.IsNullOrEmpty(profile.SftpUsername))
            profile.SftpUsername = Encrypt(profile.SftpUsername);
        if (!string.IsNullOrEmpty(profile.SftpPassword))
            profile.SftpPassword = Encrypt(profile.SftpPassword);
        if (!string.IsNullOrEmpty(profile.SftpPrivateKey))
            profile.SftpPrivateKey = Encrypt(profile.SftpPrivateKey);
        if (!string.IsNullOrEmpty(profile.SftpPassphrase))
            profile.SftpPassphrase = Encrypt(profile.SftpPassphrase);
    }

    /// <summary>
    /// 克隆策略并解密所有敏感凭据，供后台存储驱动使用
    /// </summary>
    public static CloudStorageProfile DecryptProfileClone(CloudStorageProfile source)
    {
        var clone = new CloudStorageProfile
        {
            Id = source.Id,
            UserId = source.UserId,
            Name = source.Name,
            ProviderType = source.ProviderType,

            S3Endpoint = source.S3Endpoint,
            S3Region = source.S3Region,
            S3BucketName = source.S3BucketName,
            S3AccessKey = Decrypt(source.S3AccessKey),
            S3SecretKey = Decrypt(source.S3SecretKey),
            S3ForcePathStyle = source.S3ForcePathStyle,

            WebDavUrl = source.WebDavUrl,
            WebDavUsername = Decrypt(source.WebDavUsername),
            WebDavPassword = Decrypt(source.WebDavPassword),
            WebDavBasePath = source.WebDavBasePath,

            FtpHost = source.FtpHost,
            FtpPort = source.FtpPort,
            FtpUsername = Decrypt(source.FtpUsername),
            FtpPassword = Decrypt(source.FtpPassword),
            FtpUseSsl = source.FtpUseSsl,
            FtpBasePath = source.FtpBasePath,

            SftpHost = source.SftpHost,
            SftpPort = source.SftpPort,
            SftpUsername = Decrypt(source.SftpUsername),
            SftpAuthType = source.SftpAuthType,
            SftpPassword = Decrypt(source.SftpPassword),
            SftpPrivateKey = Decrypt(source.SftpPrivateKey),
            SftpPassphrase = Decrypt(source.SftpPassphrase),
            SftpBasePath = source.SftpBasePath,

            CreatedAt = source.CreatedAt,
            UpdatedAt = source.UpdatedAt
        };

        return clone;
    }

    /// <summary>
    /// 克隆策略并彻底脱敏敏感凭据，供前端 API 查询接口返回
    /// </summary>
    public static CloudStorageProfile MaskProfileForFrontend(CloudStorageProfile source)
    {
        var clone = new CloudStorageProfile
        {
            Id = source.Id,
            UserId = source.UserId,
            Name = source.Name,
            ProviderType = source.ProviderType,

            S3Endpoint = source.S3Endpoint,
            S3Region = source.S3Region,
            S3BucketName = source.S3BucketName,
            S3ForcePathStyle = source.S3ForcePathStyle,

            WebDavUrl = source.WebDavUrl,
            WebDavBasePath = source.WebDavBasePath,

            FtpHost = source.FtpHost,
            FtpPort = source.FtpPort,
            FtpUseSsl = source.FtpUseSsl,
            FtpBasePath = source.FtpBasePath,

            SftpHost = source.SftpHost,
            SftpPort = source.SftpPort,
            SftpAuthType = source.SftpAuthType,
            SftpBasePath = source.SftpBasePath,

            CreatedAt = source.CreatedAt,
            UpdatedAt = source.UpdatedAt,

            // 前端回显：敏感字段全清空
            S3AccessKey = string.Empty,
            S3SecretKey = string.Empty,
            WebDavUsername = string.Empty,
            WebDavPassword = string.Empty,
            FtpUsername = string.Empty,
            FtpPassword = string.Empty,
            SftpUsername = string.Empty,
            SftpPassword = string.Empty,
            SftpPrivateKey = string.Empty,
            SftpPassphrase = string.Empty,

            // 标记凭据是否已配置，供前端展示状态
            HasS3Credentials = !string.IsNullOrWhiteSpace(source.S3AccessKey) || !string.IsNullOrWhiteSpace(source.S3SecretKey),
            HasWebDavCredentials = !string.IsNullOrWhiteSpace(source.WebDavUsername) || !string.IsNullOrWhiteSpace(source.WebDavPassword),
            HasFtpCredentials = !string.IsNullOrWhiteSpace(source.FtpUsername) || !string.IsNullOrWhiteSpace(source.FtpPassword),
            HasSftpCredentials = !string.IsNullOrWhiteSpace(source.SftpPassword) || !string.IsNullOrWhiteSpace(source.SftpPrivateKey)
        };

        return clone;
    }
}
