export type CloudStorageProviderType = 'S3Compatible' | 'WebDAV' | 'FTP';
export type CloudSyncTargetMode = 'RegularOnly' | 'GfsOnly' | 'Both';

export const normalizeProviderType = (type: any): CloudStorageProviderType => {
  if (type === 'WebDAV' || type === 1 || type === '1') return 'WebDAV';
  if (type === 'FTP' || type === 2 || type === '2') return 'FTP';
  return 'S3Compatible';
};

export const normalizeSyncMode = (mode: any): CloudSyncTargetMode => {
  if (mode === 'Both' || mode === 2 || mode === '2') return 'Both';
  if (mode === 'RegularOnly' || mode === 0 || mode === '0') return 'RegularOnly';
  return 'GfsOnly';
};

export interface CloudStorageProfile {
  id: string;
  userId?: string;
  name: string;
  providerType: CloudStorageProviderType;

  // S3
  s3Endpoint?: string;
  s3Region?: string;
  s3BucketName?: string;
  s3AccessKey?: string;
  s3SecretKey?: string;
  s3ForcePathStyle?: boolean;

  // WebDAV
  webDavUrl?: string;
  webDavUsername?: string;
  webDavPassword?: string;
  webDavBasePath?: string;

  // FTP
  ftpHost?: string;
  ftpPort?: number;
  ftpUsername?: string;
  ftpPassword?: string;
  ftpUseSsl?: boolean;
  ftpBasePath?: string;

  createdAt?: string;
  updatedAt?: string;

  hasS3Credentials?: boolean;
  hasWebDavCredentials?: boolean;
  hasFtpCredentials?: boolean;
}

export interface InstanceCloudSyncConfig {
  instanceId: number;
  ownerUserId?: string;
  enabled: boolean;
  syncMode: CloudSyncTargetMode;
  profileId: string;
  remotePathPattern: string;
  maxLocalKeep: number;
  maxRemoteKeep: number;
  deleteLocalAfterUpload: boolean;

  // GFS 专属
  inheritGfsKeep: boolean;
  remoteKeepDaily: number;
  remoteKeepWeekly: number;
  remoteKeepMonthly: number;

  lastSyncTime?: string;
  lastSyncStatus?: string;
  lastSyncMessage?: string;
}

export interface RemoteBackupItem {
  fileName: string;
  fullPath: string;
  tier: 'regular' | 'daily' | 'weekly' | 'monthly';
  sizeBytes: number;
  formattedSize: string;
  lastModified?: string;
}

export interface TestConnectionResult {
  success: boolean;
  message: string;
  latencyMs: number;
}

export interface GfsStatusDto {
  isInstalled: boolean;
  enabled: boolean;
  keepDailyDays: number;
  keepWeeklyWeeks: number;
  keepMonthlyMonths: number;
}
