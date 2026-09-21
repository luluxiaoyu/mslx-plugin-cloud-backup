using System.Globalization;
using System.Text.RegularExpressions;

namespace MSLX.Plugin.Cloud.Backup.Services;

/// <summary>
/// 备份文件名时间解析器（用于 FTP 等缺失 LastModified 属性时的可靠时间回退）
/// </summary>
public static class BackupFilenameParser
{
    private static readonly Regex DailyRegex = new(@"mslx-backup_daily_(\d{8})_(\d{6})", RegexOptions.Compiled);
    private static readonly Regex MonthlyRegex = new(@"mslx-backup_monthly_(\d{6})_(\d{6})", RegexOptions.Compiled);
    private static readonly Regex RegularRegex = new(@"mslx-backup_(\d{8})_(\d{6})", RegexOptions.Compiled);

    public static DateTime? ExtractTimestamp(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;

        // 常规备份
        var mRegular = RegularRegex.Match(fileName);
        if (mRegular.Success)
        {
            string datePart = mRegular.Groups[1].Value;
            string timePart = mRegular.Groups[2].Value;
            if (DateTime.TryParseExact($"{datePart}{timePart}", "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            {
                return dt;
            }
        }

        // GFS 日归档
        var mDaily = DailyRegex.Match(fileName);
        if (mDaily.Success)
        {
            string datePart = mDaily.Groups[1].Value;
            string timePart = mDaily.Groups[2].Value;
            if (DateTime.TryParseExact($"{datePart}{timePart}", "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            {
                return dt;
            }
        }

        // GFS 月归档
        var mMonthly = MonthlyRegex.Match(fileName);
        if (mMonthly.Success)
        {
            string ym = mMonthly.Groups[1].Value;
            string dh = mMonthly.Groups[2].Value;
            if (DateTime.TryParseExact($"{ym}{dh}", "yyyyMMddHHmm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            {
                return dt;
            }
        }

        return null;
    }
}
