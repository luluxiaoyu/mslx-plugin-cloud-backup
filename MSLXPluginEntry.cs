using Microsoft.AspNetCore.Mvc.ApplicationParts;
using MSLX.Plugin.Cloud.Backup.Services;
using MSLX.SDK;
using MSLX.SDK.Interfaces;

[assembly: ApplicationPart("MSLX.Plugin.Cloud.Backup")]

namespace MSLX.Plugin.Cloud.Backup;

public class MSLXPluginEntry : IPlugin
{
    public static MSLXPluginEntry Instance { get; private set; } = null!;
    public string Id => "mslx-plugin-cloud-backup";
    public string Name => "存档云端备份同步";
    public string Description => "备份自动同步至云端对象存储与远端主机（支持 S3兼容、WebDAV、FTP/FTPS、SFTP/SSH），支持联动GFS备份插件管理，支持用户策略隔离与本地/远端超额自动滚动清理。";
    public string Version => "1.0.3";
    public string Icon => "icon.png";
    public string MinSDKVersion => "1.6.4";
    public string Developer => "xiaoyu";
    public string AuthorUrl => "https://github.com/luluxiaoyu/mslx-plugin-cloud-backup";
    public string PluginUrl => "https://mslx-plugins.mslmc.net/plugins/mslx-plugin-cloud-backup";

    public void OnPluginInitialize(IServiceProvider serviceProvider)
    {
        Instance = this;
    }

    public void OnLoad()
    {
        try
        {
            SDK.MSLX.Logger.Info("========================================");
            SDK.MSLX.Logger.Info("[MSLX 云端备份同步插件] 已成功加载！");
            SDK.MSLX.Logger.Info("已挂载事件总线，监听宿主备份生成流水线...");
            SDK.MSLX.Logger.Info("========================================");

            // 启动时自动迁移历史明文凭据至 AES-256 加密存储
            CloudBackupEngine.MigrateLegacyPlaintextCredentials();

            SDK.MSLX.Events.OnBackupCompleted += HandleBackupCompleted;
        }
        catch (Exception ex)
        {
            SDK.MSLX.Logger.Error($"[CloudBackup] 插件初始化失败: {ex.Message}");
        }
    }

    private void HandleBackupCompleted(object? sender, SDK.Events.BackupCompletedEventArgs e)
    {
        // 异步执行云端上传流水线，避免阻塞宿主主执行流
        Task.Run(async () =>
        {
            await CloudBackupEngine.ProcessBackupCompletedAsync(e);
        });
    }

    public void OnUnload()
    {
        SDK.MSLX.Events.OnBackupCompleted -= HandleBackupCompleted;
        SDK.MSLX.Logger.Info("[MSLX 云端备份同步插件] 已安全卸载。");
    }

    public void OnRegisterEndpoints(IEndpointRouteBuilder endpoints)
    {
    }

    public void OnRegisterServices(IServiceCollection services)
    {
    }
}