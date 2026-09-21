import request from 'mslx-request';
import type {
  CloudStorageProfile,
  InstanceCloudSyncConfig,
  RemoteBackupItem,
  TestConnectionResult,
  GfsStatusDto
} from '../types/cloudBackup';

const BASE_URL = '/api/plugins/cloud-backup';

export const cloudBackupApi = {
  // 用户存储策略
  getUserProfiles: async (): Promise<CloudStorageProfile[]> => {
    const res: any = await request.get({ url: `${BASE_URL}/user/profiles` });
    return res || [];
  },

  saveUserProfile: async (profile: CloudStorageProfile): Promise<CloudStorageProfile> => {
    const res: any = await request.post({
      url: `${BASE_URL}/user/profile`,
      data: profile,
    });
    return res;
  },

  deleteUserProfile: async (profileId: string): Promise<boolean> => {
    await request.delete({ url: `${BASE_URL}/user/profile/${profileId}` });
    return true;
  },

  testConnection: async (profile: CloudStorageProfile): Promise<TestConnectionResult> => {
    const res: any = await request.post({
      url: `${BASE_URL}/test-connection`,
      data: profile,
    });
    return res;
  },

  // 探测 GFS 状态
  getGfsStatus: async (instanceId: number): Promise<GfsStatusDto> => {
    const res: any = await request.get({ url: `${BASE_URL}/instance/gfs-status?instanceId=${instanceId}` });
    return res || { isInstalled: false, enabled: false, keepDailyDays: 7, keepWeeklyWeeks: 4, keepMonthlyMonths: 12 };
  },

  // 实例云同步配置
  getInstanceConfig: async (instanceId: number): Promise<InstanceCloudSyncConfig> => {
    const res: any = await request.get({ url: `${BASE_URL}/instance/config?instanceId=${instanceId}` });
    return res;
  },

  saveInstanceConfig: async (instanceId: number, config: Partial<InstanceCloudSyncConfig>): Promise<InstanceCloudSyncConfig> => {
    const res: any = await request.post({
      url: `${BASE_URL}/instance/config?instanceId=${instanceId}`,
      data: config,
    });
    return res;
  },

  // 远端文件管理
  getRemoteBackups: async (instanceId: number): Promise<RemoteBackupItem[]> => {
    const res: any = await request.get({ url: `${BASE_URL}/instance/remote-backups?instanceId=${instanceId}` });
    return res || [];
  },

  deleteRemoteBackup: async (instanceId: number, fullPath: string): Promise<boolean> => {
    await request.post({
      url: `${BASE_URL}/instance/delete-remote-backup?instanceId=${instanceId}`,
      data: { fullPath },
    });
    return true;
  },
};
