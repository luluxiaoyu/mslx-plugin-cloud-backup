using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using MSLX.Plugin.Cloud.Backup.Models;

namespace MSLX.Plugin.Cloud.Backup.Services.Providers;

/// <summary>
/// WebDAV 存储驱动（支持 AList, Nextcloud, 坚果云, NAS WebDAV 等）
/// </summary>
public class WebDavStorageProvider : ICloudStorageProvider
{
    public CloudStorageProviderType ProviderType => CloudStorageProviderType.WebDAV;

    private static HttpClient CreateHttpClient(CloudStorageProfile profile)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        };

        var client = new HttpClient(handler);
        if (!string.IsNullOrEmpty(profile.WebDavUsername))
        {
            var authBytes = Encoding.UTF8.GetBytes($"{profile.WebDavUsername}:{profile.WebDavPassword ?? ""}");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(authBytes));
        }

        return client;
    }

    /// <summary>
    /// 对路径段逐段进行安全 URL 编码拼接
    /// </summary>
    private static string CombineAndEncodeUrl(string baseUrl, string path)
    {
        baseUrl = (baseUrl ?? "").TrimEnd('/');
        var segments = (path ?? "").Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var encodedSegments = segments.Select(Uri.EscapeDataString);
        string joined = string.Join('/', encodedSegments);
        return string.IsNullOrEmpty(joined) ? baseUrl : $"{baseUrl}/{joined}";
    }

    public async Task<TestConnectionResult> TestConnectionAsync(CloudStorageProfile profile, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            if (string.IsNullOrWhiteSpace(profile.WebDavUrl))
            {
                return new TestConnectionResult { Success = false, Message = "WebDAV 服务地址 (URL) 不能为空" };
            }

            using var client = CreateHttpClient(profile);

            // 探测服务连通性与凭证
            string rootUrl = profile.WebDavUrl.TrimEnd('/');
            var rootRequest = new HttpRequestMessage(new HttpMethod("PROPFIND"), rootUrl);
            rootRequest.Headers.Add("Depth", "0");

            HttpResponseMessage rootResponse;
            try
            {
                rootResponse = await client.SendAsync(rootRequest, ct);
            }
            catch (Exception ex)
            {
                sw.Stop();
                return new TestConnectionResult
                {
                    Success = false,
                    Message = $"无法连接到 WebDAV 服务器: {ex.Message}",
                    LatencyMs = sw.ElapsedMilliseconds
                };
            }

            if (rootResponse.StatusCode == HttpStatusCode.Unauthorized || rootResponse.StatusCode == HttpStatusCode.Forbidden)
            {
                sw.Stop();
                return new TestConnectionResult
                {
                    Success = false,
                    Message = $"WebDAV 认证失败: 用户名或密码错误 (HTTP {(int)rootResponse.StatusCode})",
                    LatencyMs = sw.ElapsedMilliseconds
                };
            }

            // 探测目标存储目录
            string basePath = (profile.WebDavBasePath ?? "").Trim('/');
            string targetUrl = CombineAndEncodeUrl(profile.WebDavUrl, profile.WebDavBasePath ?? "");

            var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), targetUrl);
            request.Headers.Add("Depth", "0");
            var response = await client.SendAsync(request, ct);

            // 目录不存在时尝试自动创建
            if (response.StatusCode == HttpStatusCode.NotFound && !string.IsNullOrEmpty(basePath))
            {
                await EnsureDirectoryAsync(client, profile.WebDavUrl, basePath, ct);

                // 重新探测
                var retryReq = new HttpRequestMessage(new HttpMethod("PROPFIND"), targetUrl);
                retryReq.Headers.Add("Depth", "0");
                response = await client.SendAsync(retryReq, ct);
            }

            sw.Stop();

            if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.MultiStatus)
            {
                return new TestConnectionResult
                {
                    Success = true,
                    Message = $"WebDAV 连接成功！目标目录: {profile.WebDavBasePath ?? "/"}，响应耗时: {sw.ElapsedMilliseconds} ms",
                    LatencyMs = sw.ElapsedMilliseconds
                };
            }

            return new TestConnectionResult
            {
                Success = false,
                Message = $"WebDAV 服务端返回状态码: {(int)response.StatusCode} ({response.ReasonPhrase})",
                LatencyMs = sw.ElapsedMilliseconds
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new TestConnectionResult
            {
                Success = false,
                Message = $"WebDAV 连接测试异常: {ex.Message}",
                LatencyMs = sw.ElapsedMilliseconds
            };
        }
    }

    private static async Task EnsureDirectoryAsync(HttpClient client, string baseUrl, string dirPath, CancellationToken ct)
    {
        var parts = dirPath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = baseUrl.TrimEnd('/');

        foreach (var part in parts)
        {
            current = $"{current}/{Uri.EscapeDataString(part)}";
            try
            {
                var mkcolReq = new HttpRequestMessage(new HttpMethod("MKCOL"), current);
                await client.SendAsync(mkcolReq, ct);
            }
            catch
            {
                // 忽略已存在异常
            }
        }
    }

    public async Task<bool> UploadFileAsync(CloudStorageProfile profile, string localFilePath, string remoteFilePath, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (!File.Exists(localFilePath))
        {
            throw new FileNotFoundException($"本地文件不存在: {localFilePath}");
        }

        using var client = CreateHttpClient(profile);

        string basePath = (profile.WebDavBasePath ?? "").TrimEnd('/');
        string relative = remoteFilePath.Replace('\\', '/').TrimStart('/');
        string fullRelative = string.IsNullOrEmpty(basePath) ? relative : $"{basePath.TrimStart('/')}/{relative}";

        string? dir = Path.GetDirectoryName(fullRelative)?.Replace('\\', '/');
        if (!string.IsNullOrWhiteSpace(dir))
        {
            await EnsureDirectoryAsync(client, profile.WebDavUrl ?? "", dir, ct);
        }

        string uploadUrl = CombineAndEncodeUrl(profile.WebDavUrl ?? "", fullRelative);

        await using var fileStream = new FileStream(localFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        using var content = new StreamContent(fileStream, 81920);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        var request = new HttpRequestMessage(HttpMethod.Put, uploadUrl)
        {
            Content = content
        };

        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        progress?.Report(100.0);

        return response.IsSuccessStatusCode;
    }

    public async Task<List<RemoteBackupItem>> ListFilesAsync(CloudStorageProfile profile, string remoteDir, CancellationToken ct = default)
    {
        var items = new List<RemoteBackupItem>();
        if (string.IsNullOrWhiteSpace(profile.WebDavUrl)) return items;

        using var client = CreateHttpClient(profile);
        string basePath = (profile.WebDavBasePath ?? "").TrimEnd('/');
        string relative = remoteDir.Replace('\\', '/').TrimStart('/');
        string fullRelative = string.IsNullOrEmpty(basePath) ? relative : $"{basePath.TrimStart('/')}/{relative}";

        string targetUrl = CombineAndEncodeUrl(profile.WebDavUrl, fullRelative);

        var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), targetUrl);
        request.Headers.Add("Depth", "1");

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, ct);
        }
        catch (Exception ex)
        {
            SDK.MSLX.Logger.Warn($"[CloudBackup] WebDAV PROPFIND 请求异常 ({targetUrl}): {ex.Message}");
            return items;
        }

        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.MultiStatus)
        {
            if (response.StatusCode != HttpStatusCode.NotFound)
            {
                SDK.MSLX.Logger.Warn($"[CloudBackup] WebDAV PROPFIND 返回非成功状态码: {(int)response.StatusCode} ({response.ReasonPhrase}) for {targetUrl}");
            }
            return items;
        }

        string xmlString = await response.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(xmlString)) return items;

        try
        {
            var doc = XDocument.Parse(xmlString);
            var subCollections = new List<string>();

            // 规范化当前目录路径，剔除目录自身
            string targetPath;
            try
            {
                targetPath = new Uri(targetUrl).AbsolutePath;
            }
            catch
            {
                targetPath = targetUrl;
            }
            targetPath = WebUtility.UrlDecode(targetPath).TrimEnd('/');

            // 提取 response 节点
            var responseNodes = doc.Descendants().Where(e => e.Name.LocalName.Equals("response", StringComparison.OrdinalIgnoreCase));

            foreach (var resp in responseNodes)
            {
                var hrefNode = resp.Descendants().FirstOrDefault(e => e.Name.LocalName.Equals("href", StringComparison.OrdinalIgnoreCase));
                var href = hrefNode?.Value ?? "";
                if (string.IsNullOrWhiteSpace(href)) continue;

                string itemPath;
                try
                {
                    if (href.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || href.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    {
                        itemPath = new Uri(href).AbsolutePath;
                    }
                    else
                    {
                        itemPath = href;
                    }
                }
                catch
                {
                    itemPath = href;
                }
                itemPath = WebUtility.UrlDecode(itemPath).TrimEnd('/');

                // 忽略当前目录自身
                if (string.Equals(itemPath, targetPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var propNodes = resp.Descendants().Where(e => e.Name.LocalName.Equals("prop", StringComparison.OrdinalIgnoreCase)).ToList();

                // 判断是否为目录
                bool isCollection = false;
                foreach (var prop in propNodes)
                {
                    var resourcetype = prop.Descendants().FirstOrDefault(e => e.Name.LocalName.Equals("resourcetype", StringComparison.OrdinalIgnoreCase));
                    if (resourcetype != null && resourcetype.Descendants().Any(e => e.Name.LocalName.Equals("collection", StringComparison.OrdinalIgnoreCase)))
                    {
                        isCollection = true;
                        break;
                    }
                }

                string entryName = itemPath.Substring(itemPath.LastIndexOf('/') + 1);
                if (string.IsNullOrEmpty(entryName)) continue;

                if (isCollection)
                {
                    if (!entryName.Equals("gfs-archives", StringComparison.OrdinalIgnoreCase))
                    {
                        subCollections.Add(entryName);
                    }
                    continue;
                }

                // 文件处理
                long size = 0;
                DateTime? lastMod = null;

                foreach (var prop in propNodes)
                {
                    if (size == 0)
                    {
                        var clNode = prop.Descendants().FirstOrDefault(e => e.Name.LocalName.Equals("getcontentlength", StringComparison.OrdinalIgnoreCase));
                        if (clNode != null && long.TryParse(clNode.Value, out var parsedSize))
                        {
                            size = parsedSize;
                        }
                    }

                    if (lastMod == null)
                    {
                        var lmNode = prop.Descendants().FirstOrDefault(e => e.Name.LocalName.Equals("getlastmodified", StringComparison.OrdinalIgnoreCase));
                        if (lmNode != null && DateTime.TryParse(lmNode.Value, out var parsedDate))
                        {
                            lastMod = parsedDate;
                        }
                    }
                }

                if (lastMod == null)
                {
                    lastMod = BackupFilenameParser.ExtractTimestamp(entryName);
                }

                // 规范化相对路径
                string cleanRelativePath;
                string cleanBasePath = basePath.Trim('/');
                string normItemPath = "/" + itemPath.Trim('/') + "/";
                string normBase = "/" + cleanBasePath + "/";

                if (!string.IsNullOrEmpty(cleanBasePath) && normItemPath.Contains(normBase, StringComparison.OrdinalIgnoreCase))
                {
                    int idx = normItemPath.IndexOf(normBase, StringComparison.OrdinalIgnoreCase);
                    cleanRelativePath = normItemPath.Substring(idx + normBase.Length).Trim('/');
                }
                else
                {
                    cleanRelativePath = $"{relative}/{entryName}".TrimStart('/');
                }

                items.Add(new RemoteBackupItem
                {
                    FileName = entryName,
                    FullPath = cleanRelativePath,
                    SizeBytes = size,
                    FormattedSize = FormatSize(size),
                    LastModified = lastMod
                });
            }

            // 递归扫描子目录文件（排除 GFS 归档）
            if (!relative.Contains("gfs-archives", StringComparison.OrdinalIgnoreCase) && subCollections.Count > 0)
            {
                foreach (var subDir in subCollections.Take(50))
                {
                    try
                    {
                        string nextRelative = string.IsNullOrEmpty(relative) ? subDir : $"{relative}/{subDir}";
                        var subItems = await ListFilesAsync(profile, nextRelative, ct);
                        items.AddRange(subItems);
                    }
                    catch (Exception ex)
                    {
                        SDK.MSLX.Logger.Warn($"[CloudBackup] 展开扫描子目录 [{subDir}] 异常: {ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            SDK.MSLX.Logger.Warn($"[CloudBackup] WebDAV ListFilesAsync 解析 XML 异常: {ex.Message}");
        }

        return items;
    }

    public async Task<bool> DeleteFileAsync(CloudStorageProfile profile, string remoteFilePath, CancellationToken ct = default)
    {
        using var client = CreateHttpClient(profile);

        string basePath = (profile.WebDavBasePath ?? "").TrimEnd('/');
        string relative = remoteFilePath.Replace('\\', '/').TrimStart('/');

        // 处理 basePath 相对路径
        string fullRelative;
        string cleanBasePath = basePath.Trim('/');
        string normRelative = "/" + relative.Trim('/') + "/";
        string normBase = "/" + cleanBasePath + "/";
        if (!string.IsNullOrEmpty(cleanBasePath) && normRelative.StartsWith(normBase, StringComparison.OrdinalIgnoreCase))
        {
            fullRelative = relative.Trim('/');
        }
        else
        {
            fullRelative = string.IsNullOrEmpty(basePath) ? relative : $"{cleanBasePath}/{relative}";
        }

        string deleteUrl = CombineAndEncodeUrl(profile.WebDavUrl ?? "", fullRelative);
        try
        {
            var response = await client.DeleteAsync(deleteUrl, ct);
            return response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound;
        }
        catch (Exception ex)
        {
            SDK.MSLX.Logger.Warn($"[CloudBackup] WebDAV 删除文件失败: {ex.Message}");
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
