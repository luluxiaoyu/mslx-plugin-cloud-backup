using System.Diagnostics;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using MSLX.Plugin.Cloud.Backup.Models;

namespace MSLX.Plugin.Cloud.Backup.Services.Providers;

/// <summary>
/// S3 兼容对象存储驱动（支持 Cloudflare R2, MinIO, AWS S3, Backblaze B2 等）
/// </summary>
public class S3StorageProvider : ICloudStorageProvider
{
    public CloudStorageProviderType ProviderType => CloudStorageProviderType.S3Compatible;

    private static AmazonS3Client CreateClient(CloudStorageProfile profile)
    {
        var credentials = new BasicAWSCredentials(profile.S3AccessKey ?? "", profile.S3SecretKey ?? "");
        var config = new AmazonS3Config
        {
            ForcePathStyle = profile.S3ForcePathStyle
        };

        if (!string.IsNullOrWhiteSpace(profile.S3Endpoint))
        {
            config.ServiceURL = profile.S3Endpoint.TrimEnd('/');
        }

        if (!string.IsNullOrWhiteSpace(profile.S3Region))
        {
            config.AuthenticationRegion = profile.S3Region;
        }

        return new AmazonS3Client(credentials, config);
    }

    private static string NormalizeKey(string key)
    {
        // S3 对象 Key 不以 '/' 开头
        return key.Replace('\\', '/').TrimStart('/');
    }

    public async Task<TestConnectionResult> TestConnectionAsync(CloudStorageProfile profile, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            if (string.IsNullOrWhiteSpace(profile.S3BucketName))
            {
                return new TestConnectionResult { Success = false, Message = "S3 存储桶名称 (Bucket) 不能为空" };
            }

            using var client = CreateClient(profile);
            var req = new ListObjectsV2Request
            {
                BucketName = profile.S3BucketName,
                MaxKeys = 1
            };

            await client.ListObjectsV2Async(req, ct);
            sw.Stop();

            return new TestConnectionResult
            {
                Success = true,
                Message = $"S3 连接成功！响应耗时: {sw.ElapsedMilliseconds} ms",
                LatencyMs = sw.ElapsedMilliseconds
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new TestConnectionResult
            {
                Success = false,
                Message = $"S3 连接测试失败: {ex.Message}",
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

        using var client = CreateClient(profile);
        var s3Key = NormalizeKey(remoteFilePath);

        var putRequest = new PutObjectRequest
        {
            BucketName = profile.S3BucketName,
            Key = s3Key,
            FilePath = localFilePath,
            UseChunkEncoding = false,
            DisablePayloadSigning = true
        };

        if (progress != null)
        {
            putRequest.StreamTransferProgress += (sender, args) =>
            {
                if (args.TotalBytes > 0)
                {
                    double pct = (double)args.TransferredBytes / args.TotalBytes * 100.0;
                    progress.Report(pct);
                }
            };
        }

        var response = await client.PutObjectAsync(putRequest, ct);
        return (int)response.HttpStatusCode >= 200 && (int)response.HttpStatusCode < 300;
    }

    public async Task<List<RemoteBackupItem>> ListFilesAsync(CloudStorageProfile profile, string remoteDir, CancellationToken ct = default)
    {
        var items = new List<RemoteBackupItem>();
        if (string.IsNullOrWhiteSpace(profile.S3BucketName)) return items;

        using var client = CreateClient(profile);
        var prefix = NormalizeKey(remoteDir);
        if (!string.IsNullOrEmpty(prefix) && !prefix.EndsWith('/'))
        {
            prefix += "/";
        }

        string? continuationToken = null;
        do
        {
            var request = new ListObjectsV2Request
            {
                BucketName = profile.S3BucketName,
                Prefix = prefix,
                ContinuationToken = continuationToken
            };

            var response = await client.ListObjectsV2Async(request, ct);
            if (response.S3Objects != null)
            {
                foreach (var s3Obj in response.S3Objects)
                {
                    // 排除文件夹标记对象
                    if (s3Obj.Key.EndsWith('/')) continue;

                    string fileName = Path.GetFileName(s3Obj.Key);
                    long size = s3Obj.Size ?? 0;
                    string formatted = FormatSize(size);

                    items.Add(new RemoteBackupItem
                    {
                        FileName = fileName,
                        FullPath = s3Obj.Key,
                        SizeBytes = size,
                        FormattedSize = formatted,
                        LastModified = s3Obj.LastModified
                    });
                }
            }

            continuationToken = response.IsTruncated ?? false ? response.NextContinuationToken : null;
        } while (!string.IsNullOrEmpty(continuationToken));

        return items;
    }

    public async Task<bool> DeleteFileAsync(CloudStorageProfile profile, string remoteFilePath, CancellationToken ct = default)
    {
        using var client = CreateClient(profile);
        var s3Key = NormalizeKey(remoteFilePath);

        var request = new DeleteObjectRequest
        {
            BucketName = profile.S3BucketName,
            Key = s3Key
        };

        var response = await client.DeleteObjectAsync(request, ct);
        return (int)response.HttpStatusCode >= 200 && (int)response.HttpStatusCode < 300;
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
