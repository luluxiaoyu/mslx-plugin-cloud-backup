using System.Text.Json.Serialization;
using Newtonsoft.Json.Converters;

namespace MSLX.Plugin.Cloud.Backup.Models;

[Newtonsoft.Json.JsonConverter(typeof(StringEnumConverter))]
[System.Text.Json.Serialization.JsonConverter(typeof(JsonStringEnumConverter))]
public enum CloudSyncTargetMode
{
    /// <summary>
    /// 仅同步常规定时备份
    /// </summary>
    RegularOnly = 0,

    /// <summary>
    /// 仅同步 GFS 长线归档 (日/周/月，推荐最省流量模式)
    /// </summary>
    GfsOnly = 1,

    /// <summary>
    /// 双轨同步 (常规备份 + GFS 归档)
    /// </summary>
    Both = 2
}

/// <summary>
/// 实例级云同步策略模型
/// </summary>
public class InstanceCloudSyncConfig
{
    public uint InstanceId { get; set; }

    /// <summary>
    /// 策略归属的用户 ID（强制隔离鉴权，防止越权使用他人策略）
    /// </summary>
    public string OwnerUserId { get; set; } = string.Empty;

    /// <summary>
    /// 是否开启自动云备份同步
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// 同步目标模式（仅常规 / 仅 GFS 归档 / 双轨同步）
    /// </summary>
    public CloudSyncTargetMode SyncMode { get; set; } = CloudSyncTargetMode.GfsOnly;

    /// <summary>
    /// 绑定的用户云存储策略 ID
    /// </summary>
    public string ProfileId { get; set; } = string.Empty;

    /// <summary>
    /// 远端存储相对路径模板，例如：/mslx-backups/{serverName}/
    /// 支持变量：{serverName}, {instanceId}, {date}, {year}, {month}, {day}
    /// </summary>
    public string RemotePathPattern { get; set; } = "/mslx-backups/{serverName}/";

    /// <summary>
    /// 本地保留最大份数（默认 5，<= 0 表示不限制/不额外清理）
    /// </summary>
    public int MaxLocalKeep { get; set; } = 5;

    /// <summary>
    /// 常规备份远端保留最大份数（默认 10，<= 0 表示永久保留）
    /// </summary>
    public int MaxRemoteKeep { get; set; } = 10;

    /// <summary>
    /// 上传成功后是否自动删除本地该备份文件（适用于轻量磁盘 VPS）
    /// </summary>
    public bool DeleteLocalAfterUpload { get; set; } = false;

    #region GFS 归档联动专属配置
    /// <summary>
    /// 是否直接继承本地 GFS 的保留天/周/月数
    /// </summary>
    public bool InheritGfsKeep { get; set; } = true;

    /// <summary>
    /// 远端日备份保留最大份数（自定义模式时生效）
    /// </summary>
    public int RemoteKeepDaily { get; set; } = 7;

    /// <summary>
    /// 远端周备份保留最大份数（自定义模式时生效）
    /// </summary>
    public int RemoteKeepWeekly { get; set; } = 4;

    /// <summary>
    /// 远端月备份保留最大份数（自定义模式时生效）
    /// </summary>
    public int RemoteKeepMonthly { get; set; } = 12;
    #endregion

    /// <summary>
    /// 最近一次同步时间
    /// </summary>
    public DateTime? LastSyncTime { get; set; }

    /// <summary>
    /// 最近一次同步状态: Success, Failed, Running 等
    /// </summary>
    public string? LastSyncStatus { get; set; }

    /// <summary>
    /// 最近一次同步信息
    /// </summary>
    public string? LastSyncMessage { get; set; }
}
