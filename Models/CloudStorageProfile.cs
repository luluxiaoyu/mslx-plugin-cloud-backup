using System.Text.Json.Serialization;
using Newtonsoft.Json.Converters;

namespace MSLX.Plugin.Cloud.Backup.Models;

[Newtonsoft.Json.JsonConverter(typeof(StringEnumConverter))]
[System.Text.Json.Serialization.JsonConverter(typeof(JsonStringEnumConverter))]
public enum CloudStorageProviderType
{
    S3Compatible,
    WebDAV,
    FTP
}

/// <summary>
/// 用户级存储策略/凭证模型
/// </summary>
public class CloudStorageProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string UserId { get; set; } = string.Empty;
    public string Name { get; set; } = "未命名存储";
    public CloudStorageProviderType ProviderType { get; set; } = CloudStorageProviderType.S3Compatible;

    // ====== S3 兼容协议 ======
    public string? S3Endpoint { get; set; }
    public string? S3Region { get; set; } = "auto";
    public string? S3BucketName { get; set; }
    public string? S3AccessKey { get; set; }
    public string? S3SecretKey { get; set; }
    public bool S3ForcePathStyle { get; set; } = true;

    // ====== WebDAV 协议 ======
    public string? WebDavUrl { get; set; }
    public string? WebDavUsername { get; set; }
    public string? WebDavPassword { get; set; }
    public string? WebDavBasePath { get; set; } = "/";

    // ====== FTP / FTPS 协议 ======
    public string? FtpHost { get; set; }
    public int FtpPort { get; set; } = 21;
    public string? FtpUsername { get; set; }
    public string? FtpPassword { get; set; }
    public bool FtpUseSsl { get; set; } = false;
    public string? FtpBasePath { get; set; } = "/";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public bool HasS3Credentials { get; set; }
    public bool HasWebDavCredentials { get; set; }
    public bool HasFtpCredentials { get; set; }
}

/// <summary>
/// 用户持有的所有存储策略集合容器
/// </summary>
public class UserCloudStorageConfig
{
    public string UserId { get; set; } = string.Empty;
    public List<CloudStorageProfile> Profiles { get; set; } = new();
}
