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

    private static string CombineSftpPath(string basePath, string relativePath)
    {
        basePath = (basePath ?? "/").TrimEnd('/');
        relativePath = (relativePath ?? "").Replace('\\', '/').TrimStart('/');
        if (string.IsNullOrEmpty(basePath))
        {
            return $"/{relativePath}";
        }
        return $"{basePath}/{relativePath}";
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
                if (!string.IsNullOrWhiteSpace(dir) && dir != "/")
                {
                    EnsureDirectoryRecursive(client, dir);
                }

                return new TestConnectionResult
                {
                    Success = true,
                    Message = $"SFTP 连接成功！目标根目录: {dir}，耗时: {sw.ElapsedMilliseconds} ms",
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

            string basePath = (profile.SftpBasePath ?? "").TrimEnd('/');
            string relative = (remoteFilePath ?? "").Replace('\\', '/').TrimStart('/');

            string targetRemotePath;
            string cleanBasePath = basePath.Trim('/');
            if (!string.IsNullOrEmpty(cleanBasePath) && relative.StartsWith(cleanBasePath, StringComparison.OrdinalIgnoreCase))
            {
                targetRemotePath = $"/{relative.TrimStart('/')}";
            }
            else
            {
                targetRemotePath = CombineSftpPath(basePath, relative);
            }

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

                string basePath = (profile.SftpBasePath ?? "").TrimEnd('/');
                string relative = (remoteDir ?? "").Replace('\\', '/').TrimStart('/');

                string targetDir;
                string cleanBasePath = basePath.Trim('/');
                if (!string.IsNullOrEmpty(cleanBasePath) && relative.StartsWith(cleanBasePath, StringComparison.OrdinalIgnoreCase))
                {
                    targetDir = $"/{relative.TrimStart('/')}";
                }
                else
                {
                    targetDir = CombineSftpPath(basePath, relative);
                }

                if (!client.Exists(targetDir))
                {
                    return items;
                }

                var files = client.ListDirectory(targetDir);
                foreach (var item in files)
                {
                    if (item.IsRegularFile && !item.IsDirectory &&
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
                            SizeBytes = item.Length,
                            FormattedSize = FormatSize(item.Length),
                            LastModified = lastMod
                        });
                    }
                }

                return items;
            }, ct);
        }
        catch (Exception ex)
        {
            SDK.MSLX.Logger.Warn($"[CloudBackup] SFTP 获取列表失败: {ex.Message}");
        }

        return items;
    }

    public async Task<bool> DeleteFileAsync(CloudStorageProfile profile, string remoteFilePath, CancellationToken ct = default)
    {
        try
        {
            return await Task.Run(() =>
            {
                using var client = CreateSftpClient(profile);
                client.Connect();

                string basePath = (profile.SftpBasePath ?? "").TrimEnd('/');
                string relative = (remoteFilePath ?? "").Replace('\\', '/').TrimStart('/');

                string targetRemotePath;
                string cleanBasePath = basePath.Trim('/');
                if (!string.IsNullOrEmpty(cleanBasePath) && relative.StartsWith(cleanBasePath, StringComparison.OrdinalIgnoreCase))
                {
                    targetRemotePath = $"/{relative.TrimStart('/')}";
                }
                else
                {
                    targetRemotePath = CombineSftpPath(basePath, relative);
                }

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
