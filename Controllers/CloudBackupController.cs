using Microsoft.AspNetCore.Mvc;
using MSLX.Plugin.Cloud.Backup.Models;
using MSLX.Plugin.Cloud.Backup.Services;
using MSLX.Plugin.Cloud.Backup.Services.Security;
using MSLX.SDK;
using MSLX.SDK.Models;
using Newtonsoft.Json.Linq;

namespace MSLX.Plugin.Cloud.Backup.Controllers;

[ApiController]
[Route("api/plugins/cloud-backup")]
public class CloudBackupController : ControllerBase
{
    private string GetCurrentUserId()
    {
        return User?.FindFirst("UserId")?.Value ?? string.Empty;
    }

    #region 用户级存储策略维护
    /// <summary>
    /// 获取当前登录用户的全部云存储策略
    /// </summary>
    [HttpGet("user/profiles")]
    public IActionResult GetUserProfiles()
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Unauthorized(new ApiResponse<object> { Code = 401, Message = "未登录或登录凭证已过期" });
        }

        var config = CloudBackupEngine.GetUserConfig(userId);
        var maskedList = config.Profiles.Select(PluginCryptoService.MaskProfileForFrontend).ToList();

        return Ok(new ApiResponse<List<CloudStorageProfile>>
        {
            Code = 200,
            Message = "获取成功",
            Data = maskedList
        });
    }

    /// <summary>
    /// 保存/新增用户云存储策略（置空即不修改已有凭据）
    /// </summary>
    [HttpPost("user/profile")]
    public IActionResult SaveUserProfile([FromBody] CloudStorageProfile profile)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Unauthorized(new ApiResponse<object> { Code = 401, Message = "未登录或登录凭证已过期" });
        }

        if (string.IsNullOrWhiteSpace(profile.Name))
        {
            return BadRequest(new ApiResponse<object> { Code = 400, Message = "策略名称不能为空" });
        }

        var config = CloudBackupEngine.GetUserConfig(userId);
        profile.UserId = userId; // 强制绑定当前用户

        int existingIndex = config.Profiles.FindIndex(p => p.Id == profile.Id);
        if (existingIndex >= 0)
        {
            var existing = config.Profiles[existingIndex];

            // 置空则保留已有加密凭据
            if (string.IsNullOrWhiteSpace(profile.S3AccessKey)) profile.S3AccessKey = existing.S3AccessKey;
            if (string.IsNullOrWhiteSpace(profile.S3SecretKey)) profile.S3SecretKey = existing.S3SecretKey;

            if (string.IsNullOrWhiteSpace(profile.WebDavUsername)) profile.WebDavUsername = existing.WebDavUsername;
            if (string.IsNullOrWhiteSpace(profile.WebDavPassword)) profile.WebDavPassword = existing.WebDavPassword;

            if (string.IsNullOrWhiteSpace(profile.FtpUsername)) profile.FtpUsername = existing.FtpUsername;
            if (string.IsNullOrWhiteSpace(profile.FtpPassword)) profile.FtpPassword = existing.FtpPassword;

            profile.CreatedAt = existing.CreatedAt;
            profile.UpdatedAt = DateTime.UtcNow;
            config.Profiles[existingIndex] = profile;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(profile.Id))
            {
                profile.Id = Guid.NewGuid().ToString("N");
            }
            profile.CreatedAt = DateTime.UtcNow;
            profile.UpdatedAt = DateTime.UtcNow;
            config.Profiles.Add(profile);
        }

        CloudBackupEngine.SaveUserConfig(userId, config);

        return Ok(new ApiResponse<CloudStorageProfile>
        {
            Code = 200,
            Message = "存储策略保存成功",
            Data = PluginCryptoService.MaskProfileForFrontend(profile)
        });
    }

    /// <summary>
    /// 删除用户某个云存储策略
    /// </summary>
    [HttpDelete("user/profile/{profileId}")]
    public IActionResult DeleteUserProfile(string profileId)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Unauthorized(new ApiResponse<object> { Code = 401, Message = "未登录或登录凭证已过期" });
        }

        var config = CloudBackupEngine.GetUserConfig(userId);
        int removedCount = config.Profiles.RemoveAll(p => p.Id == profileId);

        if (removedCount > 0)
        {
            CloudBackupEngine.SaveUserConfig(userId, config);
            return Ok(new ApiResponse<object> { Code = 200, Message = "存储策略已删除" });
        }

        return NotFound(new ApiResponse<object> { Code = 404, Message = "存储策略不存在或已被删除" });
    }

    /// <summary>
    /// 测试存储策略连通性
    /// </summary>
    [HttpPost("test-connection")]
    public async Task<IActionResult> TestConnection([FromBody] CloudStorageProfile profile)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Unauthorized(new ApiResponse<object> { Code = 401, Message = "未登录或登录凭证已过期" });
        }

        // 若测试已有策略且凭据留空，自动合并库中凭据
        if (!string.IsNullOrWhiteSpace(profile.Id))
        {
            var existing = CloudBackupEngine.GetUserProfile(userId, profile.Id);
            if (existing != null)
            {
                if (string.IsNullOrWhiteSpace(profile.S3AccessKey)) profile.S3AccessKey = existing.S3AccessKey;
                if (string.IsNullOrWhiteSpace(profile.S3SecretKey)) profile.S3SecretKey = existing.S3SecretKey;

                if (string.IsNullOrWhiteSpace(profile.WebDavUsername)) profile.WebDavUsername = existing.WebDavUsername;
                if (string.IsNullOrWhiteSpace(profile.WebDavPassword)) profile.WebDavPassword = existing.WebDavPassword;

                if (string.IsNullOrWhiteSpace(profile.FtpUsername)) profile.FtpUsername = existing.FtpUsername;
                if (string.IsNullOrWhiteSpace(profile.FtpPassword)) profile.FtpPassword = existing.FtpPassword;
            }
        }

        // SSRF 防范：禁止访问云元数据服务及私有敏感保留地址
        if (!IsEndpointSafe(profile))
        {
            return BadRequest(new ApiResponse<TestConnectionResult>
            {
                Code = 400,
                Message = "安全校验失败：目标地址受限（禁止访问云元数据服务或受限地址）",
                Data = new TestConnectionResult { Success = false, Message = "受限的目标地址" }
            });
        }

        try
        {
            var decryptedProfile = PluginCryptoService.DecryptProfileClone(profile);
            var provider = StorageProviderFactory.GetProvider(decryptedProfile.ProviderType);
            var result = await provider.TestConnectionAsync(decryptedProfile);

            return Ok(new ApiResponse<TestConnectionResult>
            {
                Code = result.Success ? 200 : 400,
                Message = result.Message,
                Data = result
            });
        }
        catch (Exception ex)
        {
            return Ok(new ApiResponse<TestConnectionResult>
            {
                Code = 500,
                Message = $"测试异常: {ex.Message}",
                Data = new TestConnectionResult { Success = false, Message = ex.Message }
            });
        }
    }

    private static bool IsEndpointSafe(CloudStorageProfile profile)
    {
        string? targetHost = null;
        try
        {
            targetHost = profile.ProviderType switch
            {
                CloudStorageProviderType.S3Compatible => !string.IsNullOrWhiteSpace(profile.S3Endpoint) ? new Uri(profile.S3Endpoint).Host : null,
                CloudStorageProviderType.WebDAV => !string.IsNullOrWhiteSpace(profile.WebDavUrl) ? new Uri(profile.WebDavUrl).Host : null,
                CloudStorageProviderType.FTP => profile.FtpHost,
                _ => null
            };
        }
        catch
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(targetHost)) return true;

        targetHost = targetHost.Trim().ToLowerInvariant().Trim('[', ']');

        // 拦截云厂商元数据地址，防止凭证泄露
        if (targetHost.Equals("169.254.169.254") || targetHost.StartsWith("169.254."))
        {
            return false;
        }
        if (targetHost.Equals("100.100.100.200") || targetHost.Equals("fd00:ec2::254"))
        {
            return false;
        }
        if (targetHost.Equals("metadata.google.internal") || targetHost.Equals("metadata"))
        {
            return false;
        }

        // 允许内网私有地址（NAS、自建 MinIO、FTP 等）
        return true;
    }
    #endregion

    #region 实例级同步配置与远端文件管理
    /// <summary>
    /// 探测指定实例的 GFS 状态
    /// </summary>
    [HttpGet("instance/gfs-status")]
    public IActionResult GetInstanceGfsStatus([FromQuery] uint instanceId)
    {
        var status = GfsDetectorService.GetGfsStatus(instanceId);
        return Ok(new ApiResponse<GfsStatusDto>
        {
            Code = 200,
            Message = "获取成功",
            Data = status
        });
    }

    /// <summary>
    /// 获取指定实例的云同步策略
    /// </summary>
    [HttpGet("instance/config")]
    public IActionResult GetInstanceConfig([FromQuery] uint instanceId)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Unauthorized(new ApiResponse<object> { Code = 401, Message = "未登录或登录凭证已过期" });
        }

        if (!SDK.MSLX.Config.Users.HasResourcePermission(userId, "server", (int)instanceId))
        {
            return StatusCode(403, new ApiResponse<object> { Code = 403, Message = "无权访问该实例" });
        }

        var config = CloudBackupEngine.GetInstanceConfig(instanceId);
        return Ok(new ApiResponse<InstanceCloudSyncConfig>
        {
            Code = 200,
            Message = "获取成功",
            Data = config
        });
    }

    /// <summary>
    /// 保存实例云同步配置（含严格用户隔离校验与 GFS 适配）
    /// </summary>
    [HttpPost("instance/config")]
    public IActionResult SaveInstanceConfig([FromQuery] uint instanceId, [FromBody] SaveInstanceConfigRequest req)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Unauthorized(new ApiResponse<object> { Code = 401, Message = "未登录或登录凭证已过期" });
        }

        // 校验实例权限
        if (!SDK.MSLX.Config.Users.HasResourcePermission(userId, "server", (int)instanceId))
        {
            return StatusCode(403, new ApiResponse<object> { Code = 403, Message = "无权配置该实例" });
        }

        // 校验存储策略归属
        if (req.Enabled && !string.IsNullOrWhiteSpace(req.ProfileId))
        {
            var userConfig = CloudBackupEngine.GetUserConfig(userId);
            bool belongsToCurrentUser = userConfig.Profiles.Any(p => p.Id == req.ProfileId);

            if (!belongsToCurrentUser)
            {
                return StatusCode(403, new ApiResponse<object>
                {
                    Code = 403,
                    Message = "安全校验失败：禁止将实例绑定到其他用户或不存在的存储策略！"
                });
            }
        }

        var currentConfig = CloudBackupEngine.GetInstanceConfig(instanceId);
        if (!string.IsNullOrEmpty(currentConfig.OwnerUserId) && currentConfig.OwnerUserId != userId)
        {
            SDK.MSLX.Logger.Warn($"[CloudBackup] 实例 [{instanceId}] 的配置归属用户发生变更: {currentConfig.OwnerUserId} -> {userId}，由当前用户重新接管绑定策略。");
        }

        currentConfig.Enabled = req.Enabled;
        currentConfig.SyncMode = req.SyncMode;
        currentConfig.ProfileId = req.ProfileId;
        currentConfig.RemotePathPattern = string.IsNullOrWhiteSpace(req.RemotePathPattern) ? "/mslx-backups/{serverName}/" : req.RemotePathPattern;
        currentConfig.MaxLocalKeep = req.MaxLocalKeep;
        currentConfig.MaxRemoteKeep = req.MaxRemoteKeep;
        currentConfig.DeleteLocalAfterUpload = req.DeleteLocalAfterUpload;
        currentConfig.InheritGfsKeep = req.InheritGfsKeep;
        currentConfig.RemoteKeepDaily = req.RemoteKeepDaily;
        currentConfig.RemoteKeepWeekly = req.RemoteKeepWeekly;
        currentConfig.RemoteKeepMonthly = req.RemoteKeepMonthly;
        currentConfig.OwnerUserId = userId; // 标记所有者为当前用户

        CloudBackupEngine.SaveInstanceConfig(instanceId, currentConfig);

        return Ok(new ApiResponse<InstanceCloudSyncConfig>
        {
            Code = 200,
            Message = "云端同步配置已更新",
            Data = currentConfig
        });
    }

    /// <summary>
    /// 获取指定实例在远端已保存的备份文件列表（聚合常规与 GFS 日/周/月归档）
    /// </summary>
    [HttpGet("instance/remote-backups")]
    public async Task<IActionResult> GetRemoteBackups([FromQuery] uint instanceId)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Unauthorized(new ApiResponse<object> { Code = 401, Message = "未登录或登录凭证已过期" });
        }

        if (!SDK.MSLX.Config.Users.HasResourcePermission(userId, "server", (int)instanceId))
        {
            return StatusCode(403, new ApiResponse<object> { Code = 403, Message = "无权访问该实例" });
        }

        var syncConfig = CloudBackupEngine.GetInstanceConfig(instanceId);
        if (string.IsNullOrWhiteSpace(syncConfig.OwnerUserId) || string.IsNullOrWhiteSpace(syncConfig.ProfileId))
        {
            return Ok(new ApiResponse<List<RemoteBackupItem>>
            {
                Code = 200,
                Message = "尚未绑定存储策略",
                Data = new List<RemoteBackupItem>()
            });
        }

        var profile = CloudBackupEngine.GetUserProfile(syncConfig.OwnerUserId, syncConfig.ProfileId);
        if (profile == null)
        {
            return Ok(new ApiResponse<List<RemoteBackupItem>>
            {
                Code = 200,
                Message = "绑定的存储策略已失效",
                Data = new List<RemoteBackupItem>()
            });
        }

        var server = SDK.MSLX.Config.Servers.GetServer(instanceId);
        string serverName = server?.Name ?? $"Server_{instanceId}";

        string baseFilePath = CloudBackupEngine.ResolveRemotePath(syncConfig.RemotePathPattern, instanceId, serverName, "placeholder.zip", DateTime.Now);
        string todayDir = CloudBackupEngine.GetRemoteDirectory(baseFilePath);
        string invariantBase = CloudBackupEngine.GetInvariantBaseDir(syncConfig.RemotePathPattern, instanceId, serverName);
        string remoteDir = string.IsNullOrEmpty(invariantBase)
            ? todayDir
            : invariantBase;

        var provider = StorageProviderFactory.GetProvider(profile.ProviderType);
        var allItems = new List<RemoteBackupItem>();

        // 扫描常规备份目录（排除 GFS 归档目录）
        var regularFiles = await provider.ListFilesAsync(profile, remoteDir);
        foreach (var f in regularFiles.Where(x => x.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                                                !x.FullPath.Contains("/gfs-archives/", StringComparison.OrdinalIgnoreCase) &&
                                                !x.FullPath.StartsWith("gfs-archives/", StringComparison.OrdinalIgnoreCase)))
        {
            f.Tier = "regular";
            allItems.Add(f);
        }

        // 基准目录无文件时回退检索当天目录
        if (allItems.Count == 0 && !string.IsNullOrEmpty(todayDir) && !string.Equals(todayDir, remoteDir, StringComparison.OrdinalIgnoreCase))
        {
            var todayFiles = await provider.ListFilesAsync(profile, todayDir);
            foreach (var f in todayFiles.Where(x => x.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                                                   !x.FullPath.Contains("/gfs-archives/", StringComparison.OrdinalIgnoreCase) &&
                                                   !x.FullPath.StartsWith("gfs-archives/", StringComparison.OrdinalIgnoreCase)))
            {
                f.Tier = "regular";
                allItems.Add(f);
            }
        }

        // 扫描 GFS 归档目录
        if (syncConfig.SyncMode != CloudSyncTargetMode.RegularOnly)
        {
            async Task ScanGfsTier(string tier)
            {
                string tierDir = string.IsNullOrEmpty(remoteDir) ? $"gfs-archives/{tier}" : $"{remoteDir}/gfs-archives/{tier}";
                var tierFiles = await provider.ListFilesAsync(profile, tierDir);
                foreach (var f in tierFiles.Where(x => x.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
                {
                    f.Tier = tier;
                    allItems.Add(f);
                }
            }

            await ScanGfsTier("daily");
            await ScanGfsTier("weekly");
            await ScanGfsTier("monthly");
        }

        // 去重并按修改时间倒序排列
        var sortedItems = allItems
            .DistinctBy(f => f.FullPath.ToLowerInvariant())
            .OrderByDescending(f => f.LastModified ?? DateTime.MinValue)
            .ToList();

        return Ok(new ApiResponse<List<RemoteBackupItem>>
        {
            Code = 200,
            Message = "获取成功",
            Data = sortedItems
        });
    }

    /// <summary>
    /// 删除指定的远端备份文件（严格校验目录归属与路径遍历）
    /// </summary>
    [HttpPost("instance/delete-remote-backup")]
    public async Task<IActionResult> DeleteRemoteBackup([FromQuery] uint instanceId, [FromBody] JObject req)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Unauthorized(new ApiResponse<object> { Code = 401, Message = "未登录或登录凭证已过期" });
        }

        if (!SDK.MSLX.Config.Users.HasResourcePermission(userId, "server", (int)instanceId))
        {
            return StatusCode(403, new ApiResponse<object> { Code = 403, Message = "无权操作该实例" });
        }

        string fullPath = req["fullPath"]?.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return BadRequest(new ApiResponse<object> { Code = 400, Message = "文件路径不能为空" });
        }

        var syncConfig = CloudBackupEngine.GetInstanceConfig(instanceId);
        var profile = CloudBackupEngine.GetUserProfile(syncConfig.OwnerUserId, syncConfig.ProfileId);
        if (profile == null)
        {
            return BadRequest(new ApiResponse<object> { Code = 400, Message = "存储策略不存在" });
        }

        var server = SDK.MSLX.Config.Servers.GetServer(instanceId);
        string serverName = server?.Name ?? $"Server_{instanceId}";
        string invariantBase = CloudBackupEngine.GetInvariantBaseDir(syncConfig.RemotePathPattern, instanceId, serverName);

        // 路径安全校验
        if (!ValidatePathBelongsToInstance(fullPath, invariantBase))
        {
            SDK.MSLX.Logger.Warn($"[CloudBackup] 拦截可疑的删除远端文件请求: 用户 [{userId}] 尝试在实例 [{instanceId}] 删除非法路径 [{fullPath}] (基准目录: [{invariantBase}])");
            return BadRequest(new ApiResponse<object> { Code = 400, Message = "非法的文件路径或越权操作！" });
        }

        var provider = StorageProviderFactory.GetProvider(profile.ProviderType);
        bool success = await provider.DeleteFileAsync(profile, fullPath);

        if (success)
        {
            return Ok(new ApiResponse<object> { Code = 200, Message = "远端备份文件已删除" });
        }

        return BadRequest(new ApiResponse<object> { Code = 400, Message = "删除远端文件失败" });
    }

    private static bool ValidatePathBelongsToInstance(string path, string allowedBaseDir)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string normalizedPath = path.Replace('\\', '/').Trim('/');
        if (normalizedPath.Contains("..")) return false;

        string normalizedBase = allowedBaseDir.Replace('\\', '/').Trim('/');
        if (string.IsNullOrEmpty(normalizedBase))
        {
            // 空基准目录拒绝删除
            return false;
        }

        return normalizedPath.StartsWith(normalizedBase + "/", StringComparison.OrdinalIgnoreCase) ||
               normalizedPath.Equals(normalizedBase, StringComparison.OrdinalIgnoreCase);
    }
    #endregion
}
