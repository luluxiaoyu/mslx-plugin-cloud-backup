using System.Text.RegularExpressions;
using MSLX.Plugin.Cloud.Backup.Models;
using MSLX.Plugin.Cloud.Backup.Services.Providers;
using MSLX.Plugin.Cloud.Backup.Services.Security;
using MSLX.SDK;
using MSLX.SDK.Events;
using MSLX.SDK.Models.Files;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MSLX.Plugin.Cloud.Backup.Services;

/// <summary>
/// 云备份与生命周期滚动调度核心引擎（含 GFS 联动适配）
/// </summary>
public class CloudBackupEngine
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, SemaphoreSlim> _instanceLocks = new();
    private static CancellationTokenSource _pluginLifetimeCts = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, CancellationTokenSource> _activeUploads = new();

    public static void InitializeLifecycle()
    {
        _pluginLifetimeCts = new CancellationTokenSource();
    }

    public static void ShutdownLifecycle()
    {
        try
        {
            _pluginLifetimeCts.Cancel();
        }
        catch { }

        foreach (var (taskId, cts) in _activeUploads)
        {
            try
            {
                cts.Cancel();
                SDK.MSLX.Tasks?.SetFailed(taskId, "插件已卸载或停用，任务终止");
            }
            catch { }
        }
        _activeUploads.Clear();
    }

    public static string GetUserConfigKey(string userId) => $"user_cloud_storage_{userId}";
    public static string GetInstanceConfigKey(uint instanceId) => $"instance_cloud_sync_{instanceId}";

    #region 配置存取与用户隔离校验
    /// <summary>
    /// 读取指定用户的全部云存储配置
    /// </summary>
    private static readonly Newtonsoft.Json.JsonSerializer _jsonSerializer = Newtonsoft.Json.JsonSerializer.Create(new JsonSerializerSettings
    {
        Converters = { new Newtonsoft.Json.Converters.StringEnumConverter() }
    });

    public static UserCloudStorageConfig GetUserConfig(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId)) return new UserCloudStorageConfig { UserId = userId };

        try
        {
            var raw = MSLXPluginEntry.Instance.Config().ReadConfigKey(GetUserConfigKey(userId));
            if (raw == null) return new UserCloudStorageConfig { UserId = userId };

            if (raw is JObject jObj)
            {
                return jObj.ToObject<UserCloudStorageConfig>(_jsonSerializer) ?? new UserCloudStorageConfig { UserId = userId };
            }
            if (raw is JValue jVal && jVal.Value is string str && !string.IsNullOrWhiteSpace(str))
            {
                return JsonConvert.DeserializeObject<UserCloudStorageConfig>(str, new Newtonsoft.Json.Converters.StringEnumConverter()) ?? new UserCloudStorageConfig { UserId = userId };
            }
        }
        catch (Exception ex)
        {
            SDK.MSLX.Logger.Warn($"[CloudBackup] 读取用户 [{userId}] 存储配置失败: {ex.Message}");
        }

        return new UserCloudStorageConfig { UserId = userId };
    }

    /// <summary>
    /// 保存指定用户的云存储配置
    /// </summary>
    public static void SaveUserConfig(string userId, UserCloudStorageConfig config)
    {
        config.UserId = userId;
        foreach (var profile in config.Profiles)
        {
            PluginCryptoService.EncryptProfileInPlace(profile);
        }
        MSLXPluginEntry.Instance.Config().WriteConfigKey(GetUserConfigKey(userId), JObject.FromObject(config, _jsonSerializer));
    }

    /// <summary>
    /// 获取指定用户的某个特定存储策略（解密凭据供内部驱动使用）
    /// </summary>
    public static CloudStorageProfile? GetUserProfile(string userId, string profileId)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(profileId)) return null;
        var userConfig = GetUserConfig(userId);
        var profile = userConfig.Profiles.FirstOrDefault(p => p.Id == profileId);
        return profile != null ? PluginCryptoService.DecryptProfileClone(profile) : null;
    }

    /// <summary>
    /// 读取实例的云同步策略
    /// </summary>
    public static InstanceCloudSyncConfig GetInstanceConfig(uint instanceId)
    {
        try
        {
            var raw = MSLXPluginEntry.Instance.Config().ReadConfigKey(GetInstanceConfigKey(instanceId));
            if (raw == null) return new InstanceCloudSyncConfig { InstanceId = instanceId };

            if (raw is JObject jObj)
            {
                return jObj.ToObject<InstanceCloudSyncConfig>(_jsonSerializer) ?? new InstanceCloudSyncConfig { InstanceId = instanceId };
            }
            if (raw is JValue jVal && jVal.Value is string str && !string.IsNullOrWhiteSpace(str))
            {
                return JsonConvert.DeserializeObject<InstanceCloudSyncConfig>(str, new Newtonsoft.Json.Converters.StringEnumConverter()) ?? new InstanceCloudSyncConfig { InstanceId = instanceId };
            }
        }
        catch (Exception ex)
        {
            SDK.MSLX.Logger.Warn($"[CloudBackup] 读取实例 [{instanceId}] 云同步配置失败: {ex.Message}");
        }

        return new InstanceCloudSyncConfig { InstanceId = instanceId };
    }

    /// <summary>
    /// 保存实例的云同步策略
    /// </summary>
    public static void SaveInstanceConfig(uint instanceId, InstanceCloudSyncConfig config)
    {
        config.InstanceId = instanceId;
        MSLXPluginEntry.Instance.Config().WriteConfigKey(GetInstanceConfigKey(instanceId), JObject.FromObject(config, _jsonSerializer));
    }

    /// <summary>
    /// 扫描并自动将未加密的历史明文凭据加密升级落盘
    /// </summary>
    public static void MigrateLegacyPlaintextCredentials()
    {
        try
        {
            var allConfig = MSLXPluginEntry.Instance.Config().ReadConfig();
            if (allConfig == null) return;

            int migratedCount = 0;
            foreach (var prop in allConfig.Properties())
            {
                if (prop.Name.StartsWith("user_cloud_storage_", StringComparison.OrdinalIgnoreCase))
                {
                    string userId = prop.Name.Substring("user_cloud_storage_".Length);
                    var userConfig = GetUserConfig(userId);
                    if (userConfig.Profiles == null || userConfig.Profiles.Count == 0) continue;

                    bool needsMigration = false;
                    foreach (var profile in userConfig.Profiles)
                    {
                        if (PluginCryptoService.IsPlaintext(profile.S3AccessKey) ||
                            PluginCryptoService.IsPlaintext(profile.S3SecretKey) ||
                            PluginCryptoService.IsPlaintext(profile.WebDavUsername) ||
                            PluginCryptoService.IsPlaintext(profile.WebDavPassword) ||
                            PluginCryptoService.IsPlaintext(profile.FtpUsername) ||
                            PluginCryptoService.IsPlaintext(profile.FtpPassword) ||
                            PluginCryptoService.IsPlaintext(profile.SftpUsername) ||
                            PluginCryptoService.IsPlaintext(profile.SftpPassword) ||
                            PluginCryptoService.IsPlaintext(profile.SftpPrivateKey) ||
                            PluginCryptoService.IsPlaintext(profile.SftpPassphrase))
                        {
                            needsMigration = true;
                            break;
                        }
                    }

                    if (needsMigration)
                    {
                        SaveUserConfig(userId, userConfig);
                        migratedCount++;
                    }
                }
            }

            if (migratedCount > 0)
            {
                SDK.MSLX.Logger.Info($"[CloudBackup] 启动检查：已自动将 {migratedCount} 个用户的历史明文存储凭据加密落盘。");
            }
        }
        catch (Exception ex)
        {
            SDK.MSLX.Logger.Warn($"[CloudBackup] 检查迁移历史明文凭据异常: {ex.Message}");
        }
    }
    #endregion

    #region 路径占位符解析与目录推导
    /// <summary>
    /// 解析远端路径模板，支持：{serverName}, {instanceId}, {date}, {year}, {month}, {day}
    /// </summary>
    public static string ResolveRemotePath(string template, uint instanceId, string serverName, string fileName, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            template = "/mslx-backups/{serverName}/";
        }

        string safeServerName = Regex.Replace(serverName, @"[\\/:*?""<>|]", "_");

        string resolvedDir = template
            .Replace("{serverName}", safeServerName, StringComparison.OrdinalIgnoreCase)
            .Replace("{instanceId}", instanceId.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{date}", now.ToString("yyyyMMdd"), StringComparison.OrdinalIgnoreCase)
            .Replace("{year}", now.ToString("yyyy"), StringComparison.OrdinalIgnoreCase)
            .Replace("{month}", now.ToString("MM"), StringComparison.OrdinalIgnoreCase)
            .Replace("{day}", now.ToString("dd"), StringComparison.OrdinalIgnoreCase);

        resolvedDir = resolvedDir.Replace('\\', '/').Trim('/');
        if (string.IsNullOrEmpty(resolvedDir))
        {
            return fileName;
        }

        return $"{resolvedDir}/{fileName}";
    }

    /// <summary>
    /// 获取远端目录部分（去除文件名）
    /// </summary>
    public static string GetRemoteDirectory(string resolvedRemoteFilePath)
    {
        resolvedRemoteFilePath = resolvedRemoteFilePath.Replace('\\', '/');
        int lastSlash = resolvedRemoteFilePath.LastIndexOf('/');
        if (lastSlash >= 0)
        {
            return resolvedRemoteFilePath.Substring(0, lastSlash);
        }
        return "";
    }

    /// <summary>
    /// 获取远端不变量基准目录（截断动态时间占位符之前的稳定前缀，用于扫描历史与防越权校验）
    /// </summary>
    public static string GetInvariantBaseDir(string template, uint instanceId, string serverName)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            template = "/mslx-backups/{serverName}/";
        }

        string safeServerName = Regex.Replace(serverName, @"[\\/:*?""<>|]", "_");
        string baseDir = template
            .Replace("{serverName}", safeServerName, StringComparison.OrdinalIgnoreCase)
            .Replace("{instanceId}", instanceId.ToString(), StringComparison.OrdinalIgnoreCase);

        // 截断第一个动态日期占位符之前的路径作为基准目录
        int firstDynamicIdx = -1;
        string[] dynamicPlaceholders = { "{date}", "{year}", "{month}", "{day}" };
        foreach (var ph in dynamicPlaceholders)
        {
            int idx = baseDir.IndexOf(ph, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0 && (firstDynamicIdx < 0 || idx < firstDynamicIdx))
            {
                firstDynamicIdx = idx;
            }
        }

        if (firstDynamicIdx >= 0)
        {
            string prefix = baseDir.Substring(0, firstDynamicIdx).Replace('\\', '/');
            // 回退到上一级有效目录
            int lastSlash = prefix.LastIndexOf('/');
            if (lastSlash >= 0)
            {
                baseDir = prefix.Substring(0, lastSlash);
            }
            else
            {
                baseDir = "";
            }
        }

        baseDir = baseDir.Replace('\\', '/').Trim('/');
        return baseDir;
    }

    /// <summary>
    /// 获取远端 GFS 归档存储相对路径: {invariantBase}/gfs-archives/{tier}/{fileName}
    /// </summary>
    public static string GetRemoteGfsPath(string template, uint instanceId, string serverName, string tier, string fileName, DateTime? now = null)
    {
        string invariantBase = GetInvariantBaseDir(template, instanceId, serverName);
        return string.IsNullOrEmpty(invariantBase)
            ? $"gfs-archives/{tier}/{fileName}"
            : $"{invariantBase}/gfs-archives/{tier}/{fileName}";
    }
    #endregion

    #region 备份完成核心流转与 GFS 协同
    /// <summary>
    /// 响应原生备份完成事件并执行流式上传与滚动清理（支持 GFS 联动与单实例并发锁）
    /// </summary>
    public static async Task ProcessBackupCompletedAsync(BackupCompletedEventArgs e)
    {
        var sem = _instanceLocks.GetOrAdd(e.InstanceId, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync();
        try
        {
            var syncConfig = GetInstanceConfig(e.InstanceId);
            if (!syncConfig.Enabled)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(syncConfig.OwnerUserId) || string.IsNullOrWhiteSpace(syncConfig.ProfileId))
            {
                SDK.MSLX.Logger.Warn($"[CloudBackup] 实例 [{e.InstanceId}] 开启了云备份但未绑定有效的存储策略，跳过上传。");
                return;
            }

            // 校验存储策略有效性
            var profile = GetUserProfile(syncConfig.OwnerUserId, syncConfig.ProfileId);
            if (profile == null)
            {
                SDK.MSLX.Logger.Warn($"[CloudBackup] 实例 [{e.InstanceId}] 绑定的用户存储策略 [{syncConfig.ProfileId}] 已失效或被删除。");
                syncConfig.LastSyncStatus = "Failed";
                syncConfig.LastSyncMessage = "绑定的存储策略已失效或被删除";
                SaveInstanceConfig(e.InstanceId, syncConfig);
                return;
            }

            string sourceFile = e.BackupFilePath;
            string serverName = e.ServerInfo?.Name ?? $"Server_{e.InstanceId}";
            string fileName = e.BackupFileName;
            if (string.IsNullOrEmpty(fileName))
            {
                fileName = Path.GetFileName(sourceFile);
            }

            string backupDir = Path.GetDirectoryName(sourceFile) ?? "";
            string remoteFilePath = ResolveRemotePath(syncConfig.RemotePathPattern, e.InstanceId, serverName, fileName, e.Timestamp);
            string remoteDir = GetRemoteDirectory(remoteFilePath);
            string invariantBase = GetInvariantBaseDir(syncConfig.RemotePathPattern, e.InstanceId, serverName);
            var provider = StorageProviderFactory.GetProvider(profile.ProviderType);

            // 探测 GFS 状态
            var gfsStatus = GfsDetectorService.GetGfsStatus(e.InstanceId);
            bool isGfsActive = gfsStatus.IsInstalled && gfsStatus.Enabled;

            SDK.MSLX.Logger.Info($"[CloudBackup] 实例 [{serverName}] 触发云备份流转，同步模式: {syncConfig.SyncMode} (GFS活跃: {isGfsActive})");

            bool anyUploadSuccess = false;
            bool hasError = false;
            string lastErrorMessage = "";

            // GFS 归档上传
            if (isGfsActive && syncConfig.SyncMode != CloudSyncTargetMode.RegularOnly)
            {
                // 等待 GFS 插件完成本地归档提取
                await Task.Delay(1500);

                var gfsArchives = GfsDetectorService.DetectRecentGfsArchives(backupDir, e.Timestamp);
                if (gfsArchives.Any())
                {
                    SDK.MSLX.Logger.Info($"[CloudBackup] 探测到本轮提取的 GFS 归档 ({gfsArchives.Count} 个)，准备同步至云端...");

                    foreach (var (localPath, tier, gfsFileName) in gfsArchives)
                    {
                        string remoteGfsFile = GetRemoteGfsPath(syncConfig.RemotePathPattern, e.InstanceId, serverName, tier, gfsFileName, e.Timestamp);
                        string remoteTierDir = string.IsNullOrEmpty(invariantBase) ? $"gfs-archives/{tier}" : $"{invariantBase}/gfs-archives/{tier}";

                        // 远端防重传与未完成文件完整性核验
                        try
                        {
                            long localSize = new FileInfo(localPath).Length;
                            var existingFiles = await provider.ListFilesAsync(profile, remoteTierDir);
                            var exist = existingFiles.FirstOrDefault(f => f.FileName.Equals(gfsFileName, StringComparison.OrdinalIgnoreCase));
                            if (exist != null)
                            {
                                if (exist.SizeBytes == localSize && localSize > 0)
                                {
                                    SDK.MSLX.Logger.Info($"[CloudBackup] 远端已存在完整且相同的 GFS {tier} 归档 ({gfsFileName}, {exist.FormattedSize})，自动跳过重复上传。");
                                    anyUploadSuccess = true;
                                    continue;
                                }
                                else
                                {
                                    SDK.MSLX.Logger.Warn($"[CloudBackup] 探测到远端存在未完成/损坏的 GFS 归档 ({gfsFileName}, 远端: {exist.SizeBytes} 字节, 本地: {localSize} 字节)，正在清理并重新上传...");
                                    try
                                    {
                                        await provider.DeleteFileAsync(profile, remoteGfsFile);
                                    }
                                    catch (Exception delEx)
                                    {
                                        SDK.MSLX.Logger.Warn($"[CloudBackup] 清理远端未完成归档失败: {delEx.Message}");
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            SDK.MSLX.Logger.Debug($"[CloudBackup] 检查远端归档文件是否存在异常: {ex.Message}");
                        }

                        SDK.MSLX.Logger.Info($"[CloudBackup] 正在上传 GFS {tier} 归档: {gfsFileName} -> {remoteGfsFile}");

                        try
                        {
                            bool success = await UploadWithBackgroundTaskAsync(
                                provider,
                                profile,
                                localPath,
                                remoteGfsFile,
                                $"同步 GFS {tier} 归档: {serverName}",
                                gfsFileName,
                                e.InstanceId,
                                syncConfig.OwnerUserId ?? ""
                            );
                            if (success)
                            {
                                anyUploadSuccess = true;
                                SDK.MSLX.Logger.Info($"[CloudBackup] GFS {tier} 归档上传成功: {gfsFileName}");

                                // 计算当前分层保留份数
                                int keepCount = syncConfig.InheritGfsKeep
                                    ? (tier == "daily" ? gfsStatus.KeepDailyDays : tier == "weekly" ? gfsStatus.KeepWeeklyWeeks : gfsStatus.KeepMonthlyMonths)
                                    : (tier == "daily" ? syncConfig.RemoteKeepDaily : tier == "weekly" ? syncConfig.RemoteKeepWeekly : syncConfig.RemoteKeepMonthly);

                                if (keepCount > 0)
                                {
                                    await PurgeRemoteBackupsAsync(provider, profile, remoteTierDir, keepCount, excludeGfsArchives: false);
                                }

                                // 上传后删除本地 GFS 归档
                                if (syncConfig.DeleteLocalAfterUpload)
                                {
                                    try
                                    {
                                        File.Delete(localPath);
                                        SDK.MSLX.Logger.Info($"[CloudBackup] 已删除本地 GFS 归档文件: {localPath}");
                                    }
                                    catch (Exception ex)
                                    {
                                        SDK.MSLX.Logger.Warn($"[CloudBackup] 移除本地 GFS 归档失败: {ex.Message}");
                                    }
                                }
                            }
                            else
                            {
                                hasError = true;
                                lastErrorMessage = $"GFS {tier} 归档上传失败";
                                SDK.MSLX.Logger.Error($"[CloudBackup] GFS {tier} 归档上传未成功: {gfsFileName}");
                            }
                        }
                        catch (Exception ex)
                        {
                            hasError = true;
                            lastErrorMessage = $"GFS 归档上传异常 ({tier}): {ex.Message}";
                            SDK.MSLX.Logger.Error($"[CloudBackup] GFS 归档上传失败 ({tier}): {ex.Message}");
                        }
                    }
                }
                else
                {
                    SDK.MSLX.Logger.Info($"[CloudBackup] 本次备份未触发 GFS 周期提取 (按策略属于非节点备份)。");
                }
            }

            // 常规备份上传
            if (syncConfig.SyncMode == CloudSyncTargetMode.RegularOnly || syncConfig.SyncMode == CloudSyncTargetMode.Both)
            {
                if (File.Exists(sourceFile))
                {
                    bool skipUpload = false;
                    long localSize = new FileInfo(sourceFile).Length;

                    // 远端文件完整性与防重传检查
                    try
                    {
                        var existingFiles = await provider.ListFilesAsync(profile, remoteDir);
                        var exist = existingFiles.FirstOrDefault(f => f.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase));
                        if (exist != null)
                        {
                            if (exist.SizeBytes == localSize && localSize > 0)
                            {
                                SDK.MSLX.Logger.Info($"[CloudBackup] 远端已存在完整且相同的常规备份 ({fileName}, {exist.FormattedSize})，自动跳过重复上传。");
                                anyUploadSuccess = true;
                                skipUpload = true;
                            }
                            else
                            {
                                SDK.MSLX.Logger.Warn($"[CloudBackup] 探测到远端存在未完成/损坏的备份文件 ({fileName}, 远端: {exist.SizeBytes} 字节, 本地: {localSize} 字节)，正在清理并重新上传...");
                                try
                                {
                                    await provider.DeleteFileAsync(profile, remoteFilePath);
                                }
                                catch (Exception delEx)
                                {
                                    SDK.MSLX.Logger.Warn($"[CloudBackup] 清理远端残缺文件失败: {delEx.Message}");
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        SDK.MSLX.Logger.Debug($"[CloudBackup] 检查远端常规备份文件异常: {ex.Message}");
                    }

                    bool regularSuccess = skipUpload;
                    if (!skipUpload)
                    {
                        SDK.MSLX.Logger.Info($"[CloudBackup] 正在上传常规定时备份: {fileName} -> {remoteFilePath}");
                        regularSuccess = await UploadWithBackgroundTaskAsync(
                            provider,
                            profile,
                            sourceFile,
                            remoteFilePath,
                            $"同步云端备份: {serverName}",
                            fileName,
                            e.InstanceId,
                            syncConfig.OwnerUserId ?? ""
                        );
                    }
                    if (regularSuccess)
                    {
                        anyUploadSuccess = true;
                        SDK.MSLX.Logger.Info($"[CloudBackup] 常规备份上传成功: {fileName}");

                        // 远端常规备份滚动清理（排除 GFS 归档目录）
                        if (syncConfig.MaxRemoteKeep > 0)
                        {
                            string purgeDir = string.IsNullOrEmpty(invariantBase) ? remoteDir : invariantBase;
                            await PurgeRemoteBackupsAsync(provider, profile, purgeDir, syncConfig.MaxRemoteKeep, excludeGfsArchives: true);
                        }

                        // 本地备份清理
                        if (syncConfig.DeleteLocalAfterUpload)
                        {
                            try
                            {
                                File.Delete(sourceFile);
                                SDK.MSLX.Logger.Info($"[CloudBackup] 已删除本地备份文件: {sourceFile}");
                            }
                            catch (Exception ex)
                            {
                                SDK.MSLX.Logger.Warn($"[CloudBackup] 移除本地备份失败: {ex.Message}");
                            }
                        }
                        else if (syncConfig.MaxLocalKeep > 0)
                        {
                            PurgeLocalBackups(backupDir, syncConfig.MaxLocalKeep);
                        }
                    }
                    else
                    {
                        hasError = true;
                        lastErrorMessage = "常规定时备份上传失败";
                        SDK.MSLX.Logger.Error($"[CloudBackup] 常规备份上传驱动返回失败: {fileName}");
                    }
                }
            }

            // 更新状态
            syncConfig.LastSyncTime = DateTime.UtcNow;
            if (hasError)
            {
                syncConfig.LastSyncStatus = "Failed";
                syncConfig.LastSyncMessage = $"{lastErrorMessage} ({DateTime.Now:yyyy-MM-dd HH:mm:ss})";
            }
            else if (anyUploadSuccess)
            {
                syncConfig.LastSyncStatus = "Success";
                syncConfig.LastSyncMessage = $"同步成功 ({DateTime.Now:yyyy-MM-dd HH:mm:ss})";
            }
            else
            {
                syncConfig.LastSyncStatus = "Idle";
                syncConfig.LastSyncMessage = "本轮无待上传归档";
            }
            SaveInstanceConfig(e.InstanceId, syncConfig);
        }
        catch (Exception ex)
        {
            SDK.MSLX.Logger.Error($"[CloudBackup] 云备份处理流转异常: {ex.Message}", ex);
        }
        finally
        {
            sem.Release();
        }
    }

    /// <summary>
    /// 远端历史备份滚动清理（支持排除 GFS 归档文件，防止常规备份清理误伤 GFS 周期快照）
    /// </summary>
    public static async Task PurgeRemoteBackupsAsync(ICloudStorageProvider provider, CloudStorageProfile profile, string remoteDir, int maxKeep, bool excludeGfsArchives = false)
    {
        if (maxKeep <= 0) return;

        try
        {
            var files = await provider.ListFilesAsync(profile, remoteDir);
            var query = files.Where(f => f.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
            if (excludeGfsArchives)
            {
                query = query.Where(f => !f.FullPath.Contains("/gfs-archives/", StringComparison.OrdinalIgnoreCase) &&
                                         !f.FullPath.StartsWith("gfs-archives/", StringComparison.OrdinalIgnoreCase));
            }

            var backupFiles = query
                .OrderByDescending(f => f.LastModified ?? DateTime.MinValue)
                .ToList();

            if (backupFiles.Count > maxKeep)
            {
                var expiredFiles = backupFiles.Skip(maxKeep).ToList();
                SDK.MSLX.Logger.Info($"[CloudBackup] 目录 [{remoteDir}] 备份数 {backupFiles.Count} 超过上限 {maxKeep}，正在清理 {expiredFiles.Count} 份旧备份...");

                foreach (var exp in expiredFiles)
                {
                    try
                    {
                        await provider.DeleteFileAsync(profile, exp.FullPath);
                        SDK.MSLX.Logger.Info($"[CloudBackup] 已清理远端过期备份: {exp.FileName}");
                    }
                    catch (Exception ex)
                    {
                        SDK.MSLX.Logger.Warn($"[CloudBackup] 删除远端备份 {exp.FileName} 失败: {ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            SDK.MSLX.Logger.Warn($"[CloudBackup] 检索远端清理历史备份失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 本地历史备份滚动清理
    /// </summary>
    public static void PurgeLocalBackups(string localDir, int maxKeep)
    {
        if (!Directory.Exists(localDir) || maxKeep <= 0) return;

        try
        {
            var dirInfo = new DirectoryInfo(localDir);
            var files = dirInfo.GetFiles("*.zip")
                .OrderByDescending(f => f.CreationTime)
                .ToList();

            if (files.Count > maxKeep)
            {
                var expiredFiles = files.Skip(maxKeep).ToList();
                foreach (var f in expiredFiles)
                {
                    try
                    {
                        f.Delete();
                        SDK.MSLX.Logger.Info($"[CloudBackup] 本地常规备份数超 {maxKeep} 份，已自动移除最旧本地备份: {f.Name}");
                    }
                    catch (Exception ex)
                    {
                        SDK.MSLX.Logger.Warn($"[CloudBackup] 清理本地超期备份 {f.Name} 失败: {ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            SDK.MSLX.Logger.Warn($"[CloudBackup] 本地生命周期清理异常: {ex.Message}");
        }
    }

    /// <summary>
    /// 包装后台任务进度的云端上传辅助方法
    /// </summary>
    private static async Task<bool> UploadWithBackgroundTaskAsync(
        ICloudStorageProvider provider,
        CloudStorageProfile profile,
        string localPath,
        string remotePath,
        string title,
        string targetName,
        uint instanceId,
        string userId)
    {
        string? taskId = null;
        CancellationToken taskCancelToken = CancellationToken.None;

        try
        {
            if (SDK.MSLX.Tasks != null)
            {
                var (task, token) = SDK.MSLX.Tasks.CreateTask(
                    userId: userId ?? "",
                    instanceId: instanceId,
                    type: TaskType.Plugin,
                    title: title,
                    targetName: targetName
                );
                taskId = task?.Id;
                taskCancelToken = token;
            }
        }
        catch (Exception ex)
        {
            SDK.MSLX.Logger.Warn($"[CloudBackup] 注册后台任务管理器记录失败: {ex.Message}");
        }

        var progress = new Progress<double>(ratio =>
        {
            if (!string.IsNullOrEmpty(taskId))
            {
                int percent = ratio <= 1.0 && ratio > 0 ? (int)(ratio * 100) : (int)ratio;
                percent = Math.Clamp(percent, 0, 100);
                try
                {
                    SDK.MSLX.Tasks?.UpdateProgress(taskId, percent, $"正在同步至云端: {percent}%");
                }
                catch { }
            }
        });

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(taskCancelToken, _pluginLifetimeCts.Token);
        if (!string.IsNullOrEmpty(taskId))
        {
            _activeUploads[taskId] = linkedCts;
        }

        try
        {
            bool success = await provider.UploadFileAsync(profile, localPath, remotePath, progress, linkedCts.Token);
            if (!string.IsNullOrEmpty(taskId))
            {
                if (success)
                {
                    SDK.MSLX.Tasks?.SetSuccess(taskId, "云端备份同步完成");
                }
                else
                {
                    SDK.MSLX.Tasks?.SetFailed(taskId, "云端上传未成功");
                }
            }
            return success;
        }
        catch (Exception ex)
        {
            if (!string.IsNullOrEmpty(taskId))
            {
                if (_pluginLifetimeCts.IsCancellationRequested)
                {
                    SDK.MSLX.Tasks?.SetFailed(taskId, "插件已卸载或停用，任务终止");
                }
                else if (taskCancelToken.IsCancellationRequested)
                {
                    SDK.MSLX.Tasks?.SetFailed(taskId, "用户已取消");
                }
                else
                {
                    SDK.MSLX.Tasks?.SetFailed(taskId, ex.Message);
                }
            }
            throw;
        }
        finally
        {
            if (!string.IsNullOrEmpty(taskId))
            {
                _activeUploads.TryRemove(taskId, out _);
            }
        }
    }
    #endregion
}
