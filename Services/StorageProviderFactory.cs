using MSLX.Plugin.Cloud.Backup.Models;
using MSLX.Plugin.Cloud.Backup.Services.Providers;

namespace MSLX.Plugin.Cloud.Backup.Services;

public static class StorageProviderFactory
{
    private static readonly Dictionary<CloudStorageProviderType, ICloudStorageProvider> _providers = new()
    {
        [CloudStorageProviderType.S3Compatible] = new S3StorageProvider(),
        [CloudStorageProviderType.WebDAV] = new WebDavStorageProvider(),
        [CloudStorageProviderType.FTP] = new FtpStorageProvider(),
        [CloudStorageProviderType.SFTP] = new SftpStorageProvider()
    };

    public static ICloudStorageProvider GetProvider(CloudStorageProviderType type)
    {
        if (_providers.TryGetValue(type, out var provider))
        {
            return provider;
        }

        throw new NotSupportedException($"暂不支持的存储协议类型: {type}");
    }
}
