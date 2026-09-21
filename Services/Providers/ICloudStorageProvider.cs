using MSLX.Plugin.Cloud.Backup.Models;

namespace MSLX.Plugin.Cloud.Backup.Services.Providers;

/// <summary>
/// 云端/远程存储驱动统一接口
/// </summary>
public interface ICloudStorageProvider
{
    CloudStorageProviderType ProviderType { get; }

    /// <summary>
    /// 测试连接与读写权限
    /// </summary>
    Task<TestConnectionResult> TestConnectionAsync(CloudStorageProfile profile, CancellationToken ct = default);

    /// <summary>
    /// 流式上传本地文件至远端路径
    /// </summary>
    Task<bool> UploadFileAsync(CloudStorageProfile profile, string localFilePath, string remoteFilePath, IProgress<double>? progress = null, CancellationToken ct = default);

    /// <summary>
    /// 列出远端指定目录下的备份文件列表
    /// </summary>
    Task<List<RemoteBackupItem>> ListFilesAsync(CloudStorageProfile profile, string remoteDir, CancellationToken ct = default);

    /// <summary>
    /// 删除远端文件
    /// </summary>
    Task<bool> DeleteFileAsync(CloudStorageProfile profile, string remoteFilePath, CancellationToken ct = default);
}
