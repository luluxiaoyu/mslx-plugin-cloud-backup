using System.Diagnostics;
using System.Text;
using MSLX.Plugin.Cloud.Backup.Models;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace MSLX.Plugin.Cloud.Backup.Services.Providers;

/// <summary>
/// SFTP (基于 SSH) 远程存储驱动
/// </summary>
public class SftpStorageProvider : ICloudStorageProvider
{
    public CloudStorageProviderType ProviderType => CloudStorageProviderType.SFTP;

    private static SftpClient CreateSftpClient(CloudStorageProfile profile)
    {
        string host = profile.SftpHost ?? "127.0.0.1";
        int port = profile.SftpPort > 0 ? profile.SftpPort : 22;
        string username = profile.SftpUsername ?? "root";

        AuthenticationMethod authMethod;
        if (string.Equals(profile.SftpAuthType, "PrivateKey", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(profile.SftpPrivateKey))
        {
            var keyBytes = Encoding.UTF8.GetBytes(profile.SftpPrivateKey.Trim());
            var keyStream = new MemoryStream(keyBytes);
            PrivateKeyFile keyFile;
            if (!string.IsNullOrEmpty(profile.SftpPassphrase))
            {
                keyFile = new PrivateKeyFile(keyStream, profile.SftpPassphrase);
            }
            else
            {
                keyFile = new PrivateKeyFile(keyStream);
            }

            authMethod = new PrivateKeyAuthenticationMethod(username, keyFile);
        }
        else
        {
            authMethod = new PasswordAuthenticationMethod(username, profile.SftpPassword ?? "");
        }

        var connectionInfo = new Renci.SshNet.ConnectionInfo(host, port, username, authMethod)
        {
            Timeout = TimeSpan.FromSeconds(15)
        };

        return new SftpClient(connectionInfo);
    }

    /// <summary>
    /// 解析目标远程路径，按路径段边界严格判断是否已包含根目录，防止子串误匹配
    /// </summary>
    private static string ResolveTargetRemotePath(string? basePath, string? relativePath)
    {
        string cleanBase = (basePath ?? "").TrimEnd('/');
        string cleanRel = (relativePath ?? "").Replace('\\', '/').TrimStart('/');

        string segBase = cleanBase.Trim('/');
        if (string.IsNullOrEmpty(segBase))
        {
            return $"/{cleanRel}";
        }

        string normBase = "/" + segBase + "/";
        string normRel = "/" + cleanRel.Trim('/') + "/";

        if (normRel.StartsWith(normBase, StringComparison.OrdinalIgnoreCase))
        {
            return $"/{cleanRel.TrimStart('/')}";
        }

        return $"{cleanBase}/{cleanRel}";
    }

    private static void EnsureDirectoryRecursive(SftpClient client, string remoteDir)
    {
        if (string.IsNullOrWhiteSpace(remoteDir) || remoteDir == "/" || remoteDir == ".") return;

        var parts = remoteDir.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        string current = remoteDir.StartsWith('/') ? "" : ".";

        foreach (var part in parts)
        {
            current = $"{current}/{part}";
            if (!client.Exists(current))
            {
                try
                {
                    client.CreateDirectory(current);
                }
                catch
                {
                    if (!client.Exists(current)) throw;
                }
            }
        }
    }

    public async Task<TestConnectionResult> TestConnectionAsync(CloudStorageProfile profile, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            if (string.IsNullOrWhiteSpace(profile.SftpHost))
            {
                return new TestConnectionResult { Success = false, Message = "SFTP 主机地址不能为空" };
            }

            return await Task.Run(() =>
            {
                using var client = CreateSftpClient(profile);
                client.Connect();
                sw.Stop();

                string dir = profile.SftpBasePath ?? "/";
                bool dirExists = false;
                if (!string.IsNullOrWhiteSpace(dir) && dir != "/")
                {
                    dirExists = client.Exists(dir);
                }
                else
                {
                    dirExists = true;
                }

                return new TestConnectionResult
                {
                    Success = true,
                    Message = dirExists
                        ? $"SFTP 连接成功！目标根目录: {dir} (已就绪)，耗时: {sw.ElapsedMilliseconds} ms"
                        : $"SFTP 连接成功！目标根目录: {dir} (尚未创建，首次同步时将自动创建)，耗时: {sw.ElapsedMilliseconds} ms",
                    LatencyMs = sw.ElapsedMilliseconds
                };
            }, ct);
        }
        catch (SshAuthenticationException authEx)
        {
            sw.Stop();
            return new TestConnectionResult
            {
                Success = false,
                Message = $"SFTP 认证失败（账号、密码或私钥/短语错误）: {authEx.Message}",
                LatencyMs = sw.ElapsedMilliseconds
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new TestConnectionResult
            {
                Success = false,
                Message = $"SFTP 连接测试失败: {ex.Message}",
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

        return await Task.Run(() =>
        {
            using var client = CreateSftpClient(profile);
            client.Connect();

            string targetRemotePath = ResolveTargetRemotePath(profile.SftpBasePath, remoteFilePath);

            string? remoteDir = Path.GetDirectoryName(targetRemotePath)?.Replace('\\', '/');
            if (!string.IsNullOrWhiteSpace(remoteDir) && remoteDir != "/")
            {
                EnsureDirectoryRecursive(client, remoteDir);
            }

            var fileInfo = new FileInfo(localFilePath);
            long totalBytes = fileInfo.Length;

            using var fs = File.OpenRead(localFilePath);
            client.UploadFile(fs, targetRemotePath, canOverride: true, uploaded =>
            {
                if (totalBytes > 0 && progress != null)
                {
                    double pct = Math.Min(100.0, (double)uploaded / totalBytes * 100.0);
                    progress.Report(pct);
                }
            });

            return true;
        }, ct);
    }

    public async Task<List<RemoteBackupItem>> ListFilesAsync(CloudStorageProfile profile, string remoteDir, CancellationToken ct = default)
    {
        var items = new List<RemoteBackupItem>();
        try
        {
            return await Task.Run(() =>
            {
                using var client = CreateSftpClient(profile);
                client.Connect();

                string targetDir = ResolveTargetRemotePath(profile.SftpBasePath, remoteDir);

                if (!client.Exists(targetDir))
                {
                    return items;
                }

                string cleanBasePath = (profile.SftpBasePath ?? "").Trim('/');
                CollectFilesRecursive(client, targetDir, cleanBasePath, items, currentDepth: 0);

                return items;
            }, ct);
        }
        catch (Exception ex)
        {
            SDK.MSLX.Logger.Warn($"[CloudBackup] SFTP 获取列表失败: {ex.Message}");
        }

        return items;
    }

    /// <summary>
    /// 递归遍历 SFTP 子目录收集备份文件（支持 {date}、{month} 等子目录模板向下钻取）
    /// </summary>
    private static void CollectFilesRecursive(
        SftpClient client,
        string currentDir,
        string cleanBasePath,
        List<RemoteBackupItem> items,
        int currentDepth,
        int maxDepth = 4,
        int maxCount = 2000)
    {
        if (currentDepth > maxDepth || items.Count >= maxCount) return;

        IEnumerable<ISftpFile> entries;
        try
        {
            entries = client.ListDirectory(currentDir);
        }
        catch
        {
            return;
        }

        foreach (var item in entries)
        {
            if (item.Name == "." || item.Name == "..") continue;

            if (item.IsDirectory)
            {
                CollectFilesRecursive(client, item.FullName, cleanBasePath, items, currentDepth + 1, maxDepth, maxCount);
            }
            else if (item.IsRegularFile &&
                (item.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                 item.Name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)))
            {
                DateTime? lastMod = item.LastWriteTimeUtc != DateTime.MinValue ? item.LastWriteTimeUtc : null;
                if (lastMod == null)
                {
                    lastMod = BackupFilenameParser.ExtractTimestamp(item.Name);
                }

                string normalizedFullName = item.FullName.Replace('\\', '/').Trim('/');
                string cleanRelativePath;
                string normBase = "/" + cleanBasePath + "/";
                string normItem = "/" + normalizedFullName + "/";

                if (!string.IsNullOrEmpty(cleanBasePath) && normItem.StartsWith(normBase, StringComparison.OrdinalIgnoreCase))
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
                    SizeBytes = item.Length,
                    FormattedSize = FormatSize(item.Length),
                    LastModified = lastMod
                });

                if (items.Count >= maxCount) return;
            }
        }
    }

    public async Task<bool> DeleteFileAsync(CloudStorageProfile profile, string remoteFilePath, CancellationToken ct = default)
    {
        try
        {
            return await Task.Run(() =>
            {
                using var client = CreateSftpClient(profile);
                client.Connect();

                string targetRemotePath = ResolveTargetRemotePath(profile.SftpBasePath, remoteFilePath);

                if (client.Exists(targetRemotePath))
                {
                    client.DeleteFile(targetRemotePath);
                }
                return true;
            }, ct);
        }
        catch (Exception ex)
        {
            SDK.MSLX.Logger.Warn($"[CloudBackup] SFTP 删除文件失败: {ex.Message}");
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
