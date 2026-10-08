using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MSLX.Plugin.Cloud.Backup.Models;
using MSLX.SDK;
using MSLX.Plugin.Cloud.Backup.Services;

namespace MSLX.Plugin.Cloud.Backup.Services.Providers;

public class CloudreveStorageProvider : ICloudStorageProvider
{
    public CloudStorageProviderType ProviderType => CloudStorageProviderType.CloudreveV4;

    private static readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromMinutes(30)
    };

    private class CloudreveSessionCache
    {
        public string Token { get; set; } = string.Empty;
        public DateTime ExpiresAt { get; set; }
    }

    private static readonly Dictionary<string, CloudreveSessionCache> _sessions = new();
    private static readonly object _sessionLock = new();

    private async Task<string> GetTokenAsync(CloudStorageProfile profile, CancellationToken ct)
    {
        lock (_sessionLock)
        {
            if (_sessions.TryGetValue(profile.Id, out var cache))
            {
                if (cache.ExpiresAt > DateTime.UtcNow.AddMinutes(5))
                {
                    return cache.Token;
                }
            }
        }

        var baseUrl = profile.CloudreveUrl?.TrimEnd('/');
        if (string.IsNullOrEmpty(baseUrl))
            throw new Exception("Cloudreve 服务地址未配置");

        var loginReq = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/v4/session/token");
        loginReq.Content = JsonContent.Create(new
        {
            email = profile.CloudreveEmail,
            password = profile.CloudrevePassword
        });

        var res = await _httpClient.SendAsync(loginReq, ct);
        if (!res.IsSuccessStatusCode)
        {
            throw new Exception($"Cloudreve 登录请求失败，HTTP 状态码: {res.StatusCode}");
        }

        var resStr = await res.Content.ReadAsStringAsync(ct);
        var resDoc = JsonDocument.Parse(resStr);
        if (resDoc.RootElement.TryGetProperty("code", out var codeEl) && codeEl.GetInt32() != 0)
        {
            var msg = resDoc.RootElement.TryGetProperty("msg", out var msgEl) ? msgEl.GetString() : "未知错误";
            throw new Exception($"Cloudreve 登录失败: {msg}");
        }

        var data = resDoc.RootElement.GetProperty("data");
        var token = data.GetProperty("token").GetProperty("access_token").GetString() ?? "";

        lock (_sessionLock)
        {
            _sessions[profile.Id] = new CloudreveSessionCache
            {
                Token = token,
                ExpiresAt = DateTime.UtcNow.AddHours(2)
            };
        }

        return token;
    }

    private string BuildCloudreveUri(string basePath, string relativePath)
    {
        basePath = (basePath ?? "").Trim('/');
        relativePath = (relativePath ?? "").Trim('/');
        var combined = string.IsNullOrEmpty(basePath) ? relativePath : $"{basePath}/{relativePath}";
        var segments = combined.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
        var encodedSegments = segments.Select(Uri.EscapeDataString);
        return $"cloudreve://my/{string.Join("/", encodedSegments)}";
    }

    public async Task<TestConnectionResult> TestConnectionAsync(CloudStorageProfile profile, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var token = await GetTokenAsync(profile, ct);
            var baseUrl = profile.CloudreveUrl?.TrimEnd('/');

            var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/v4/user/me");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var res = await _httpClient.SendAsync(req, ct);
            res.EnsureSuccessStatusCode();

            return new TestConnectionResult
            {
                Success = true,
                Message = "已成功连接至 Cloudreve v4 网盘",
                LatencyMs = sw.ElapsedMilliseconds
            };
        }
        catch (Exception ex)
        {
            return new TestConnectionResult
            {
                Success = false,
                Message = ex.Message,
                LatencyMs = sw.ElapsedMilliseconds
            };
        }
    }

    public async Task<bool> UploadFileAsync(CloudStorageProfile profile, string localFilePath, string remoteFilePath, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var token = await GetTokenAsync(profile, ct);
        var baseUrl = profile.CloudreveUrl?.TrimEnd('/');
        var basePath = string.IsNullOrEmpty(profile.CloudreveBasePath) ? "/MSLX-Backups" : profile.CloudreveBasePath;

        // 1. 递归创建父目录
        var remoteDir = Path.GetDirectoryName(remoteFilePath)?.Replace('\\', '/') ?? "";
        if (!string.IsNullOrWhiteSpace(remoteDir) && remoteDir != "." && remoteDir != "/")
        {
            var dirUri = BuildCloudreveUri(basePath, remoteDir);
            var createDirReq = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/v4/file/create");
            createDirReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            createDirReq.Content = JsonContent.Create(new
            {
                uri = dirUri,
                type = "folder",
                err_on_conflict = false
            });
            await _httpClient.SendAsync(createDirReq, ct);
        }

        // 2. 发起上传会话
        var fileInfo = new FileInfo(localFilePath);
        var fileUri = BuildCloudreveUri(basePath, remoteFilePath);

        var initPayload = new Dictionary<string, object>
        {
            ["uri"] = fileUri,
            ["size"] = fileInfo.Length,
            ["last_modified"] = new DateTimeOffset(fileInfo.LastWriteTimeUtc).ToUnixTimeMilliseconds(),
            ["entity_type"] = "version"
        };
        if (!string.IsNullOrEmpty(profile.CloudrevePolicyId))
        {
            initPayload["policy_id"] = profile.CloudrevePolicyId;
        }

        var initReq = new HttpRequestMessage(HttpMethod.Put, $"{baseUrl}/api/v4/file/upload");
        initReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        initReq.Content = JsonContent.Create(initPayload);

        var initRes = await _httpClient.SendAsync(initReq, ct);
        initRes.EnsureSuccessStatusCode();

        var initResDoc = JsonDocument.Parse(await initRes.Content.ReadAsStringAsync(ct));
        if (initResDoc.RootElement.TryGetProperty("code", out var codeEl) && codeEl.GetInt32() != 0)
        {
            var msg = initResDoc.RootElement.TryGetProperty("msg", out var m) ? m.GetString() : "无法创建上传会话";
            throw new Exception($"Cloudreve 初始化上传会话失败: {msg}");
        }

        var initData = initResDoc.RootElement.GetProperty("data");
        var sessionId = initData.GetProperty("session_id").GetString() ?? "";
        var chunkSize = initData.GetProperty("chunk_size").GetInt64();

        var uploadUrls = new List<string>();
        if (initData.TryGetProperty("upload_urls", out var uploadUrlsEl) && uploadUrlsEl.ValueKind == JsonValueKind.Array)
        {
            uploadUrls = uploadUrlsEl.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => !string.IsNullOrEmpty(x)).ToList();
        }

        var credential = initData.TryGetProperty("credential", out var credEl) ? credEl.GetString() : null;
        var policyType = initData.GetProperty("storage_policy").GetProperty("type").GetString() ?? "local";
        var isRelay = initData.GetProperty("storage_policy").TryGetProperty("relay", out var relayEl) && relayEl.GetBoolean();

        string? completeUrl = null;
        if (initData.TryGetProperty("completeURL", out var compUrlEl))
            completeUrl = compUrlEl.GetString();
        else if (initData.TryGetProperty("complete_url", out var compUrlEl2))
            completeUrl = compUrlEl2.GetString();

        string? callbackSecret = null;
        if (initData.TryGetProperty("callback_secret", out var cbSecretEl))
            callbackSecret = cbSecretEl.GetString();
        else if (initData.TryGetProperty("callbackSecret", out var cbSecretEl2))
            callbackSecret = cbSecretEl2.GetString();

        // 3. 执行分片上传与完结流程
        try
        {
            using var fs = new FileStream(localFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            long totalBytes = fileInfo.Length;
            long uploadedBytes = 0;
            int chunkIndex = 0;

            if (isRelay || policyType == "local")
            {
                // 本地存储或中继：直接向 Cloudreve API 发送分片二进制流
                long effectiveChunkSize = chunkSize > 0 ? chunkSize : totalBytes;
                if (effectiveChunkSize <= 0) effectiveChunkSize = 4 * 1024 * 1024;
                byte[] buffer = new byte[Math.Min(effectiveChunkSize, totalBytes > 0 ? totalBytes : 4 * 1024 * 1024)];

                while (uploadedBytes < totalBytes)
                {
                    ct.ThrowIfCancellationRequested();
                    int toRead = (int)Math.Min(buffer.Length, totalBytes - uploadedBytes);
                    int bytesRead = await fs.ReadAsync(buffer, 0, toRead, ct);
                    if (bytesRead == 0) break;

                    using var content = new ByteArrayContent(buffer, 0, bytesRead);
                    content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                    content.Headers.ContentLength = bytesRead;

                    var chunkReq = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/v4/file/upload/{sessionId}/{chunkIndex}");
                    chunkReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    chunkReq.Content = content;

                    var chunkRes = await _httpClient.SendAsync(chunkReq, ct);
                    chunkRes.EnsureSuccessStatusCode();

                    var chunkDoc = JsonDocument.Parse(await chunkRes.Content.ReadAsStringAsync(ct));
                    if (chunkDoc.RootElement.TryGetProperty("code", out var code) && code.GetInt32() != 0)
                    {
                        var msg = chunkDoc.RootElement.TryGetProperty("msg", out var m) ? m.GetString() : "分片校验失败";
                        throw new Exception($"Cloudreve 本地分片写入失败: {msg}");
                    }

                    uploadedBytes += bytesRead;
                    chunkIndex++;
                    progress?.Report(totalBytes > 0 ? (double)uploadedBytes / totalBytes : 1.0);
                }
            }
            else if (policyType == "remote")
            {
                // 从机存储节点上传
                long effectiveChunkSize = chunkSize > 0 ? chunkSize : totalBytes;
                if (effectiveChunkSize <= 0) effectiveChunkSize = 4 * 1024 * 1024;
                byte[] buffer = new byte[Math.Min(effectiveChunkSize, totalBytes > 0 ? totalBytes : 4 * 1024 * 1024)];
                var chunkBaseUrl = uploadUrls.FirstOrDefault() ?? "";

                while (uploadedBytes < totalBytes)
                {
                    ct.ThrowIfCancellationRequested();
                    int toRead = (int)Math.Min(buffer.Length, totalBytes - uploadedBytes);
                    int bytesRead = await fs.ReadAsync(buffer, 0, toRead, ct);
                    if (bytesRead == 0) break;

                    using var content = new ByteArrayContent(buffer, 0, bytesRead);
                    content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                    content.Headers.ContentLength = bytesRead;

                    var chunkReq = new HttpRequestMessage(HttpMethod.Post, $"{chunkBaseUrl}?chunk={chunkIndex}");
                    if (!string.IsNullOrEmpty(credential))
                    {
                        chunkReq.Headers.TryAddWithoutValidation("Authorization", credential);
                    }
                    chunkReq.Content = content;

                    var chunkRes = await _httpClient.SendAsync(chunkReq, ct);
                    chunkRes.EnsureSuccessStatusCode();

                    var chunkDoc = JsonDocument.Parse(await chunkRes.Content.ReadAsStringAsync(ct));
                    if (chunkDoc.RootElement.TryGetProperty("code", out var code) && code.GetInt32() != 0)
                    {
                        var msg = chunkDoc.RootElement.TryGetProperty("msg", out var m) ? m.GetString() : "从机写入失败";
                        throw new Exception($"Cloudreve 从机分片写入失败: {msg}");
                    }

                    uploadedBytes += bytesRead;
                    chunkIndex++;
                    progress?.Report(totalBytes > 0 ? (double)uploadedBytes / totalBytes : 1.0);
                }
            }
            else if (policyType == "onedrive")
            {
                // OneDrive 策略
                long effectiveChunkSize = chunkSize > 0 ? chunkSize : 10 * 1024 * 1024;
                byte[] buffer = new byte[Math.Min(effectiveChunkSize, totalBytes > 0 ? totalBytes : 10 * 1024 * 1024)];
                var uploadUrl = uploadUrls.FirstOrDefault() ?? "";

                while (uploadedBytes < totalBytes)
                {
                    ct.ThrowIfCancellationRequested();
                    int toRead = (int)Math.Min(buffer.Length, totalBytes - uploadedBytes);
                    int bytesRead = await fs.ReadAsync(buffer, 0, toRead, ct);
                    if (bytesRead == 0) break;

                    using var content = new ByteArrayContent(buffer, 0, bytesRead);
                    content.Headers.ContentLength = bytesRead;
                    content.Headers.TryAddWithoutValidation("Content-Range", $"bytes {uploadedBytes}-{uploadedBytes + bytesRead - 1}/{totalBytes}");

                    var chunkReq = new HttpRequestMessage(HttpMethod.Put, uploadUrl);
                    chunkReq.Content = content;

                    var chunkRes = await _httpClient.SendAsync(chunkReq, ct);
                    chunkRes.EnsureSuccessStatusCode();

                    uploadedBytes += bytesRead;
                    chunkIndex++;
                    progress?.Report(totalBytes > 0 ? (double)uploadedBytes / totalBytes : 1.0);
                }

                if (!string.IsNullOrEmpty(callbackSecret))
                {
                    var cbReq = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/v4/callback/onedrive/{sessionId}/{callbackSecret}");
                    cbReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    cbReq.Content = new StringContent("{}", Encoding.UTF8, "application/json");
                    var cbRes = await _httpClient.SendAsync(cbReq, ct);
                    cbRes.EnsureSuccessStatusCode();
                }
            }
            else
            {
                // S3 / OSS / COS / OBS / KS3 等对象存储直传
                long effectiveChunkSize = chunkSize > 0 ? chunkSize : totalBytes;
                if (effectiveChunkSize <= 0) effectiveChunkSize = 4 * 1024 * 1024;
                byte[] buffer = new byte[Math.Min(effectiveChunkSize, totalBytes > 0 ? totalBytes : 4 * 1024 * 1024)];
                var etags = new List<string>();

                while (uploadedBytes < totalBytes)
                {
                    ct.ThrowIfCancellationRequested();
                    int toRead = (int)Math.Min(buffer.Length, totalBytes - uploadedBytes);
                    int bytesRead = await fs.ReadAsync(buffer, 0, toRead, ct);
                    if (bytesRead == 0) break;

                    using var content = new ByteArrayContent(buffer, 0, bytesRead);
                    content.Headers.ContentLength = bytesRead;

                    string chunkUrl = chunkIndex < uploadUrls.Count ? uploadUrls[chunkIndex] : (uploadUrls.FirstOrDefault() ?? "");
                    var chunkReq = new HttpRequestMessage(HttpMethod.Put, chunkUrl);
                    chunkReq.Content = content;

                    var chunkRes = await _httpClient.SendAsync(chunkReq, ct);
                    chunkRes.EnsureSuccessStatusCode();

                    string etagVal = "";
                    if (chunkRes.Headers.TryGetValues("ETag", out var etagValues))
                    {
                        etagVal = etagValues.FirstOrDefault() ?? "";
                    }
                    else if (chunkRes.Content.Headers.TryGetValues("ETag", out var cEtags))
                    {
                        etagVal = cEtags.FirstOrDefault() ?? "";
                    }
                    etags.Add(etagVal);

                    uploadedBytes += bytesRead;
                    chunkIndex++;
                    progress?.Report(totalBytes > 0 ? (double)uploadedBytes / totalBytes : 1.0);
                }

                // 完结对象存储多段上传
                if (!string.IsNullOrEmpty(completeUrl))
                {
                    var xmlBuilder = new StringBuilder();
                    xmlBuilder.Append("<CompleteMultipartUpload>");
                    for (int i = 0; i < etags.Count; i++)
                    {
                        var etag = etags[i];
                        if (!etag.StartsWith("\"") && !etag.EndsWith("\""))
                        {
                            etag = $"\"{etag}\"";
                        }
                        xmlBuilder.Append($"<Part><PartNumber>{i + 1}</PartNumber><ETag>{etag}</ETag></Part>");
                    }
                    xmlBuilder.Append("</CompleteMultipartUpload>");

                    var compReq = new HttpRequestMessage(HttpMethod.Post, completeUrl);
                    compReq.Content = new StringContent(xmlBuilder.ToString(), Encoding.UTF8, "application/xml");
                    var compRes = await _httpClient.SendAsync(compReq, ct);
                    compRes.EnsureSuccessStatusCode();
                }

                // 回调通知 Cloudreve 网盘完成转正，更新状态并刷新文件大小
                if (!string.IsNullOrEmpty(callbackSecret))
                {
                    string cbType = policyType;
                    if (cbType != "cos" && cbType != "obs" && cbType != "onedrive")
                    {
                        cbType = "s3";
                    }

                    string callbackUrl = $"{baseUrl}/api/v4/callback/{cbType}/{sessionId}/{callbackSecret}";
                    var cbReqGet = new HttpRequestMessage(HttpMethod.Get, callbackUrl);
                    cbReqGet.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    var cbRes = await _httpClient.SendAsync(cbReqGet, ct);
                    if (!cbRes.IsSuccessStatusCode)
                    {
                        var cbReqPost = new HttpRequestMessage(HttpMethod.Post, callbackUrl);
                        cbReqPost.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                        cbReqPost.Content = new StringContent("{}", Encoding.UTF8, "application/json");
                        await _httpClient.SendAsync(cbReqPost, ct);
                    }
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            try { SDK.MSLX.Logger?.Error($"[CloudBackup] Cloudreve 文件上传未完成: {ex.Message}"); } catch { }
            // 清理服务端占位文件与未完成会话
            try
            {
                var delReq = new HttpRequestMessage(HttpMethod.Delete, $"{baseUrl}/api/v4/file/upload");
                delReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                delReq.Content = JsonContent.Create(new
                {
                    id = sessionId,
                    uri = fileUri
                });
                await _httpClient.SendAsync(delReq, CancellationToken.None);
            }
            catch { }
            throw;
        }
    }

    public async Task<List<RemoteBackupItem>> ListFilesAsync(CloudStorageProfile profile, string remoteDir, CancellationToken ct = default)
    {
        var token = await GetTokenAsync(profile, ct);
        var baseUrl = profile.CloudreveUrl?.TrimEnd('/');
        var basePath = string.IsNullOrEmpty(profile.CloudreveBasePath) ? "/MSLX-Backups" : profile.CloudreveBasePath;

        var dirUri = BuildCloudreveUri(basePath, remoteDir);

        var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/v4/file?uri={Uri.EscapeDataString(dirUri)}&page=0&page_size=1000");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var res = await _httpClient.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode) return new List<RemoteBackupItem>();

        var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        if (doc.RootElement.TryGetProperty("code", out var codeEl) && codeEl.GetInt32() != 0)
        {
            return new List<RemoteBackupItem>();
        }

        var items = new List<RemoteBackupItem>();
        if (!doc.RootElement.TryGetProperty("data", out var dataEl)) return items;
        if (!dataEl.TryGetProperty("files", out var filesEl)) return items;

        foreach (var obj in filesEl.EnumerateArray())
        {
            // 0: 文件, 1: 目录
            if (obj.TryGetProperty("type", out var typeEl) && typeEl.GetInt32() != 0)
            {
                continue;
            }

            var name = obj.GetProperty("name").GetString() ?? "";
            var size = obj.GetProperty("size").GetInt64();

            DateTime mtime = DateTime.UtcNow;
            if (obj.TryGetProperty("updated_at", out var upEl) && upEl.ValueKind == JsonValueKind.String)
            {
                if (DateTime.TryParse(upEl.GetString(), out var dt))
                {
                    mtime = dt;
                }
            }

            items.Add(new RemoteBackupItem
            {
                FileName = name,
                FullPath = $"{remoteDir.TrimEnd('/')}/{name}",
                SizeBytes = size,
                FormattedSize = Math.Round(size / 1024.0 / 1024.0, 2) + " MB",
                LastModified = mtime
            });
        }

        return items;
    }

    public async Task<bool> DeleteFileAsync(CloudStorageProfile profile, string remoteFilePath, CancellationToken ct = default)
    {
        var token = await GetTokenAsync(profile, ct);
        var baseUrl = profile.CloudreveUrl?.TrimEnd('/');
        var basePath = string.IsNullOrEmpty(profile.CloudreveBasePath) ? "/MSLX-Backups" : profile.CloudreveBasePath;

        var fileUri = BuildCloudreveUri(basePath, remoteFilePath);

        var req = new HttpRequestMessage(HttpMethod.Delete, $"{baseUrl}/api/v4/file");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Content = JsonContent.Create(new
        {
            uris = new[] { fileUri },
            unlink = false,
            skip_soft_delete = true
        });

        var res = await _httpClient.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode) return false;

        var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        return doc.RootElement.TryGetProperty("code", out var code) && code.GetInt32() == 0;
    }
}
