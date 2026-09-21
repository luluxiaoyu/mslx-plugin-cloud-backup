using System.Diagnostics;
using System.Security.Authentication;
using FluentFTP;
using FluentFTP.Exceptions;
using MSLX.Plugin.Cloud.Backup.Models;

namespace MSLX.Plugin.Cloud.Backup.Services.Providers;

/// <summary>
/// FTP / FTPS 远程存储驱动
/// </summary>
public class FtpStorageProvider : ICloudStorageProvider
{
    public CloudStorageProviderType ProviderType => CloudStorageProviderType.FTP;

    private static AsyncFtpClient CreateFtpClient(CloudStorageProfile profile)
    {
        var config = new FtpConfig
        {
            EncryptionMode = profile.FtpUseSsl ? FtpEncryptionMode.Explicit : FtpEncryptionMode.None,
            ValidateAnyCertificate = true,
            SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            ConnectTimeout = 15000,
            DataConnectionConnectTimeout = 15000,
            ReadTimeout = 30000
        };

        var client = new AsyncFtpClient(
            profile.FtpHost ?? "127.0.0.1",
            profile.FtpUsername ?? "anonymous",
            profile.FtpPassword ?? "",
            profile.FtpPort > 0 ? profile.FtpPort : 21,
            config
        );

        return client;
    }

    private static string CombineFtpPath(string basePath, string relativePath)
    {
        basePath = (basePath ?? "/").TrimEnd('/');
        relativePath = (relativePath ?? "").Replace('\\', '/').TrimStart('/');
        if (string.IsNullOrEmpty(basePath))
        {
            return $"/{relativePath}";
        }
        return $"{basePath}/{relativePath}";
    }

    public async Task<TestConnectionResult> TestConnectionAsync(CloudStorageProfile profile, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            if (string.IsNullOrWhiteSpace(profile.FtpHost))
            {
                return new TestConnectionResult { Success = false, Message = "FTP 主机地址不能为空" };
            }

            await using var client = CreateFtpClient(profile);
            await client.AutoConnect(ct);
            sw.Stop();

            string dir = profile.FtpBasePath ?? "/";
            bool dirExists = await client.DirectoryExists(dir, ct);
            if (!dirExists && !string.IsNullOrEmpty(dir) && dir != "/")
            {
                try
                {
                    await client.CreateDirectory(dir, ct);
                    dirExists = await client.DirectoryExists(dir, ct);
                }
                catch { }
            }

            return new TestConnectionResult
            {
                Success = true,
                Message = $"FTP 连接成功！目标根目录: {dir}，耗时: {sw.ElapsedMilliseconds} ms",
                LatencyMs = sw.ElapsedMilliseconds
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new TestConnectionResult
            {
                Success = false,
                Message = $"FTP 连接测试失败: {ex.Message}",
                LatencyMs = sw.ElapsedMilliseconds
            };
        }
    }

    public async Task<bool> UploadFileAsync(CloudStorageProfile profile, string localFilePath, string remoteFilePath, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (!File.Exists(localFilePath))
        {
            throw new FileNotFoundException($"本地文件不存在: {localFilePath}");
        }

        await using var client = CreateFtpClient(profile);
        await client.AutoConnect(ct);

        string targetRemotePath = CombineFtpPath(profile.FtpBasePath ?? "/", remoteFilePath);

        IProgress<FtpProgress>? ftpProgress = null;
        if (progress != null)
        {
            ftpProgress = new Progress<FtpProgress>(p =>
            {
                if (p.Progress > 0)
                {
                    progress.Report(p.Progress);
                }
            });
        }

        var status = await client.UploadFile(
            localFilePath,
            targetRemotePath,
            FtpRemoteExists.Overwrite,
            true, // 递归创建父目录
            FtpVerify.None,
            ftpProgress,
            ct
        );

        return status == FtpStatus.Success;
    }

    public async Task<List<RemoteBackupItem>> ListFilesAsync(CloudStorageProfile profile, string remoteDir, CancellationToken ct = default)
    {
        var items = new List<RemoteBackupItem>();
        if (string.IsNullOrWhiteSpace(profile.FtpHost)) return items;

        try
        {
            await using var client = CreateFtpClient(profile);
            await client.AutoConnect(ct);

            string targetDir = CombineFtpPath(profile.FtpBasePath ?? "/", remoteDir);
            if (!await client.DirectoryExists(targetDir, ct))
            {
                return items;
            }

            var listing = await client.GetListing(targetDir, FtpListOption.Recursive, ct);
            string cleanBasePath = (profile.FtpBasePath ?? "").Trim('/');

            foreach (var item in listing)
            {
                if (item.Type == FtpObjectType.File)
                {
                    DateTime? lastMod = item.Modified != DateTime.MinValue ? item.Modified : null;
                    if (lastMod == null)
                    {
                        lastMod = BackupFilenameParser.ExtractTimestamp(item.Name);
                    }

                    // 规范化相对路径
                    string cleanRelativePath;
                    string normalizedFullName = (item.FullName ?? item.Name).Replace('\\', '/').Trim('/');
                    if (!string.IsNullOrEmpty(cleanBasePath) && normalizedFullName.StartsWith(cleanBasePath, StringComparison.OrdinalIgnoreCase))
                    {
                        cleanRelativePath = normalizedFullName.Substring(cleanBasePath.Length).TrimStart('/');
                    }
                    else
                    {
                        cleanRelativePath = normalizedFullName;
                    }

                    items.Add(new RemoteBackupItem
                    {
                        FileName = item.Name,
                        FullPath = cleanRelativePath,
                        SizeBytes = item.Size,
                        FormattedSize = FormatSize(item.Size),
                        LastModified = lastMod
                    });
                }
            }
        }
        catch (Exception ex)
        {
            SDK.MSLX.Logger.Warn($"[CloudBackup] FTP 获取列表失败: {ex.Message}");
        }

        return items;
    }

    public async Task<bool> DeleteFileAsync(CloudStorageProfile profile, string remoteFilePath, CancellationToken ct = default)
    {
        try
        {
            await using var client = CreateFtpClient(profile);
            await client.AutoConnect(ct);

            string basePath = (profile.FtpBasePath ?? "").TrimEnd('/');
            string relative = (remoteFilePath ?? "").Replace('\\', '/').TrimStart('/');

            string targetRemotePath;
            string cleanBasePath = basePath.Trim('/');
            if (!string.IsNullOrEmpty(cleanBasePath) && relative.StartsWith(cleanBasePath, StringComparison.OrdinalIgnoreCase))
            {
                targetRemotePath = $"/{relative.TrimStart('/')}";
            }
            else
            {
                targetRemotePath = CombineFtpPath(basePath, relative);
            }

            await client.DeleteFile(targetRemotePath, ct);
            return true;
        }
        catch (FtpMissingObjectException)
        {
            return true; // 目标不存在视为成功
        }
        catch (Exception ex)
        {
            SDK.MSLX.Logger.Warn($"[CloudBackup] FTP 删除文件失败: {ex.Message}");
            return false;
        }
    }

    private static string FormatSize(long bytes)
    {
        return bytes switch
        {
            >= 1073741824 => $"{bytes / 1024.0 / 1024.0 / 1024.0:F2} GB",
            >= 1048576 => $"{bytes / 1024.0 / 1024.0:F2} MB",
            >= 1024 => $"{bytes / 1024.0:F2} KB",
            _ => $"{bytes} B"
        };
    }
}
