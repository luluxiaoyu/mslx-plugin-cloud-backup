namespace MSLX.Plugin.Cloud.Backup.Models;

/// <summary>
/// 远端存储上的备份文件条目
/// </summary>
public class RemoteBackupItem
{
    public string FileName { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;

    /// <summary>
    /// 归档分层: "regular", "daily", "weekly", "monthly"
    /// </summary>
    public string Tier { get; set; } = "regular";

    public long SizeBytes { get; set; }
    public string FormattedSize { get; set; } = string.Empty;
    public DateTime? LastModified { get; set; }
}

/// <summary>
/// 存储连通性测试结果
/// </summary>
public class TestConnectionResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public long LatencyMs { get; set; }
}

/// <summary>
/// GFS 插件安装与配置探测信息 DTO
/// </summary>
public class GfsStatusDto
{
    public bool IsInstalled { get; set; }
    public bool Enabled { get; set; }
    public int KeepDailyDays { get; set; } = 7;
    public int KeepWeeklyWeeks { get; set; } = 4;
    public int KeepMonthlyMonths { get; set; } = 12;
}

/// <summary>
/// 前端保存实例同步配置请求 DTO
/// </summary>
public class SaveInstanceConfigRequest
{
    public bool Enabled { get; set; }
    public CloudSyncTargetMode SyncMode { get; set; } = CloudSyncTargetMode.GfsOnly;
    public string ProfileId { get; set; } = string.Empty;
    public string RemotePathPattern { get; set; } = "/mslx-backups/{serverName}/";
    public int MaxLocalKeep { get; set; } = 5;
    public int MaxRemoteKeep { get; set; } = 10;
    public bool DeleteLocalAfterUpload { get; set; } = false;

    // GFS 专属
    public bool InheritGfsKeep { get; set; } = true;
    public int RemoteKeepDaily { get; set; } = 7;
    public int RemoteKeepWeekly { get; set; } = 4;
    public int RemoteKeepMonthly { get; set; } = 12;
}
