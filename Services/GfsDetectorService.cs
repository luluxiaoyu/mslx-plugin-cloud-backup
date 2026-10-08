using MSLX.Plugin.Cloud.Backup.Models;
using MSLX.SDK;
using Newtonsoft.Json.Linq;

namespace MSLX.Plugin.Cloud.Backup.Services;

/// <summary>
/// 零耦合 GFS 分级备份插件探测与归档提取服务
/// </summary>
public static class GfsDetectorService
{
    public const string GfsPluginId = "mslx-plugin-gfs-backup";

    /// <summary>
    /// 探测 GFS 插件是否安装及当前实例的 GFS 状态
    /// </summary>
    public static GfsStatusDto GetGfsStatus(uint instanceId)
    {
        var result = new GfsStatusDto
        {
            IsInstalled = false,
            Enabled = false
        };

        try
        {
            var bridge = SDK.MSLX.Config.GetPluginConfig(GfsPluginId);
            if (bridge == null) return result;

            var raw = bridge.ReadConfigKey($"instance_gfs_config_{instanceId}");
            if (raw == null)
            {
                // 插件已安装但该实例尚未配置
                result.IsInstalled = true;
                return result;
            }

            result.IsInstalled = true;
            if (raw is JObject jObj)
            {
                result.Enabled = jObj["Enabled"]?.Value<bool>() ?? false;
                result.KeepDailyDays = jObj["KeepDailyDays"]?.Value<int>() ?? 7;
                result.KeepWeeklyWeeks = jObj["KeepWeeklyWeeks"]?.Value<int>() ?? 4;
                result.KeepMonthlyMonths = jObj["KeepMonthlyMonths"]?.Value<int>() ?? 12;
            }
        }
        catch (Exception ex)
        {
            SDK.MSLX.Logger.Debug($"[CloudBackup] 探测 GFS 插件状态: {ex.Message}");
        }

        return result;
    }

    /// <summary>
    /// 在指定的备份目录下检索当前备份批次中由 GFS 提取的最新归档文件
    /// </summary>
    public static List<(string LocalPath, string Tier, string FileName)> DetectRecentGfsArchives(string backupDir, DateTime backupTime)
    {
        var archives = new List<(string, string, string)>();
        string archivesBase = Path.Combine(backupDir, "gfs-archives");
        if (!Directory.Exists(archivesBase)) return archives;

        DateTime backupUtc = backupTime.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(backupTime, DateTimeKind.Local).ToUniversalTime()
            : backupTime.ToUniversalTime();
        DateTime localTime = backupTime.ToLocalTime();

        string dateTagLocal = localTime.ToString("yyyyMMdd");
        string dateTagUtc = backupUtc.ToString("yyyyMMdd");

        int weekNumLocal = System.Globalization.ISOWeek.GetWeekOfYear(localTime);
        int weekYearLocal = System.Globalization.ISOWeek.GetYear(localTime);
        string weekTagLocal = $"{weekYearLocal}W{weekNumLocal:D2}";

        string monthTagLocal = localTime.ToString("yyyyMM");

        void CheckTier(string subDir, string tierName, Func<FileInfo, bool> isTagMatch)
        {
            string targetDir = Path.Combine(archivesBase, subDir);
            if (!Directory.Exists(targetDir)) return;

            var dir = new DirectoryInfo(targetDir);
            var files = dir.GetFiles("mslx-backup_*.zip");

            var matchedFiles = files.Where(f =>
            {
                // 1. 文件名标签必须符合对应层级（日/周/月）
                if (!isTagMatch(f)) return false;

                // 2. 检查文件物理修改/创建时间是否在本次备份窗口内（300 秒以内）
                double secDiffWrite = Math.Abs((f.LastWriteTimeUtc - backupUtc).TotalSeconds);
                double secDiffCreate = Math.Abs((f.CreationTimeUtc - backupUtc).TotalSeconds);
                if (secDiffWrite < 300 || secDiffCreate < 300)
                {
                    return true;
                }

                // 3. 结合文件名提取的精确时间戳进行二次校验（针对跨盘或硬链接继承时间属性的情况）
                var nameTime = BackupFilenameParser.ExtractTimestamp(f.Name);
                if (nameTime.HasValue)
                {
                    double secDiffName = Math.Abs((nameTime.Value - localTime).TotalSeconds);
                    if (secDiffName < 300)
                    {
                        return true;
                    }
                }

                return false;
            })
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .ToList();

            if (matchedFiles.Count > 0)
            {
                // 取最新匹配文件
                var file = matchedFiles[0];
                archives.Add((file.FullName, tierName, file.Name));
            }
        }

        CheckTier("daily", "daily", f => f.Name.Contains($"_{dateTagLocal}_") || f.Name.Contains($"_{dateTagUtc}_"));
        CheckTier("weekly", "weekly", f => f.Name.Contains($"_{weekTagLocal}_"));
        CheckTier("monthly", "monthly", f => f.Name.Contains($"_{monthTagLocal}_"));

        return archives;
    }
}
