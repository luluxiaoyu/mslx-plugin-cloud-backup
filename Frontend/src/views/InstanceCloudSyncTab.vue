<script setup lang="ts">
import { ref, computed, watch, onMounted } from 'vue';
import { MessagePlugin, DialogPlugin } from 'tdesign-vue-next';
import {
  CloudUploadIcon,
  SettingIcon,
  RefreshIcon,
  DeleteIcon,
  CheckCircleFilledIcon,
  ErrorCircleFilledIcon,
  TimeIcon,
  FileIcon,
  SearchIcon,
  CalendarIcon,
  HistoryIcon,
  HelpCircleIcon
} from 'tdesign-icons-vue-next';
import { cloudBackupApi } from '../api/cloudBackup';
import {
  normalizeProviderType,
  normalizeSyncMode,
  type CloudStorageProfile,
  type InstanceCloudSyncConfig,
  type RemoteBackupItem,
  type GfsStatusDto
} from '../types/cloudBackup';
import CloudStorageProfileModal from './CloudStorageProfileModal.vue';

const props = defineProps<{
  serverId?: number;
  instanceId?: number;
}>();

const currentInstanceId = computed(() => props.instanceId || props.serverId || 0);

const loading = ref(false);
const saving = ref(false);
const remoteLoading = ref(false);
const showProfileModal = ref(false);
const searchKeyword = ref('');
const currentTab = ref<string>('all');

const userProfiles = ref<CloudStorageProfile[]>([]);

const gfsStatus = ref<GfsStatusDto>({
  isInstalled: false,
  enabled: false,
  keepDailyDays: 7,
  keepWeeklyWeeks: 4,
  keepMonthlyMonths: 12,
});

const formConfig = ref<InstanceCloudSyncConfig>({
  instanceId: currentInstanceId.value,
  enabled: false,
  syncMode: 'GfsOnly',
  profileId: '',
  remotePathPattern: '/mslx-backups/{serverName}/',
  maxLocalKeep: 5,
  maxRemoteKeep: 10,
  deleteLocalAfterUpload: false,
  inheritGfsKeep: true,
  remoteKeepDaily: 7,
  remoteKeepWeekly: 4,
  remoteKeepMonthly: 12,
});

// 本地备份保留策略
const localRetentionMode = computed({
  get: () => {
    if (formConfig.value.deleteLocalAfterUpload) return 'deleteImmediate';
    if (formConfig.value.maxLocalKeep === 0) return 'keepAll';
    return 'keepN';
  },
  set: (val: string) => {
    if (val === 'deleteImmediate') {
      formConfig.value.deleteLocalAfterUpload = true;
    } else if (val === 'keepAll') {
      formConfig.value.deleteLocalAfterUpload = false;
      formConfig.value.maxLocalKeep = 0;
    } else {
      formConfig.value.deleteLocalAfterUpload = false;
      if (!formConfig.value.maxLocalKeep || formConfig.value.maxLocalKeep <= 0) {
        formConfig.value.maxLocalKeep = 5;
      }
    }
  }
});

const remoteBackups = ref<RemoteBackupItem[]>([]);

// 统计各周期备份数量
const regularCount = computed(() => remoteBackups.value.filter(x => x.tier === 'regular').length);
const dailyCount = computed(() => remoteBackups.value.filter(x => x.tier === 'daily').length);
const weeklyCount = computed(() => remoteBackups.value.filter(x => x.tier === 'weekly').length);
const monthlyCount = computed(() => remoteBackups.value.filter(x => x.tier === 'monthly').length);

const totalRemoteSizeStr = computed(() => {
  const bytes = remoteBackups.value.reduce((acc, cur) => acc + (cur.sizeBytes || 0), 0);
  if (bytes >= 1073741824) return (bytes / (1024 * 1024 * 1024)).toFixed(2) + ' GB';
  if (bytes >= 1048576) return (bytes / (1024 * 1024)).toFixed(2) + ' MB';
  if (bytes >= 1024) return (bytes / 1024).toFixed(2) + ' KB';
  return bytes + ' B';
});

const formatDateTime = (val?: string | Date | null) => {
  if (!val) return '-';
  try {
    const d = new Date(val);
    if (isNaN(d.getTime())) return String(val);
    return d.toLocaleString();
  } catch {
    return String(val);
  }
};

const filteredRemoteBackups = computed(() => {
  let list = remoteBackups.value;
  if (currentTab.value !== 'all') {
    list = list.filter(item => item.tier === currentTab.value);
  }
  if (searchKeyword.value.trim()) {
    const q = searchKeyword.value.trim().toLowerCase();
    list = list.filter(item =>
      item.fileName.toLowerCase().includes(q) ||
      item.fullPath.toLowerCase().includes(q)
    );
  }
  return list;
});

const placeholders = [
  { label: '{serverName}', desc: '实例名' },
  { label: '{instanceId}', desc: 'ID' },
  { label: '{date}', desc: '日期' },
  { label: '{year}', desc: '年' },
  { label: '{month}', desc: '月' },
  { label: '{day}', desc: '日' },
];

const insertPlaceholder = (tag: string) => {
  formConfig.value.remotePathPattern = (formConfig.value.remotePathPattern || '') + tag;
};

const fetchProfiles = async () => {
  try {
    const raw = await cloudBackupApi.getUserProfiles();
    userProfiles.value = raw.map(p => ({
      ...p,
      providerType: normalizeProviderType(p.providerType)
    }));
  } catch (err: any) {
    console.error('[CloudBackup] 获取存储策略失败:', err);
  }
};

const fetchGfsStatus = async () => {
  if (!currentInstanceId.value) return;
  try {
    gfsStatus.value = await cloudBackupApi.getGfsStatus(currentInstanceId.value);
  } catch (err: any) {
    console.error('[CloudBackup] 探测 GFS 状态失败:', err);
  }
};

const fetchInstanceConfig = async () => {
  if (!currentInstanceId.value) return;
  try {
    const res = await cloudBackupApi.getInstanceConfig(currentInstanceId.value);
    if (res) {
      formConfig.value = {
        ...res,
        syncMode: normalizeSyncMode(res.syncMode),
        remotePathPattern: res.remotePathPattern || '/mslx-backups/{serverName}/',
        maxLocalKeep: res.maxLocalKeep ?? 5,
        maxRemoteKeep: res.maxRemoteKeep ?? 10,
        deleteLocalAfterUpload: res.deleteLocalAfterUpload ?? false,
        inheritGfsKeep: res.inheritGfsKeep ?? true,
        remoteKeepDaily: res.remoteKeepDaily ?? 7,
        remoteKeepWeekly: res.remoteKeepWeekly ?? 4,
        remoteKeepMonthly: res.remoteKeepMonthly ?? 12,
      };
    }
  } catch (err: any) {
    console.error('[CloudBackup] 获取实例云同步配置失败:', err);
  }
};

const fetchRemoteBackups = async () => {
  if (!currentInstanceId.value) return;
  remoteLoading.value = true;
  try {
    remoteBackups.value = await cloudBackupApi.getRemoteBackups(currentInstanceId.value);
  } catch (err: any) {
    console.error('[CloudBackup] 获取远端文件失败:', err);
  } finally {
    remoteLoading.value = false;
  }
};

const initData = async () => {
  if (!currentInstanceId.value) return;
  loading.value = true;
  try {
    await Promise.all([fetchProfiles(), fetchGfsStatus(), fetchInstanceConfig()]);
    if (formConfig.value.profileId) {
      await fetchRemoteBackups();
    }
  } finally {
    loading.value = false;
  }
};

const handleSaveConfig = async () => {
  if (formConfig.value.enabled && !formConfig.value.profileId) {
    MessagePlugin.warning('开启自动同步时必须选择绑定的存储策略');
    return;
  }

  saving.value = true;
  try {
    const res = await cloudBackupApi.saveInstanceConfig(currentInstanceId.value, formConfig.value);
    MessagePlugin.success('云同步配置已保存');
    formConfig.value = {
      ...formConfig.value,
      ...res,
      syncMode: normalizeSyncMode(res?.syncMode ?? formConfig.value.syncMode),
    };
    if (formConfig.value.profileId) {
      await fetchRemoteBackups();
    }
  } catch (err: any) {
    MessagePlugin.error(err?.message || '保存配置失败');
  } finally {
    saving.value = false;
  }
};

const handleDeleteRemoteFile = (item: RemoteBackupItem) => {
  const tierName = item.tier === 'daily' ? '日备份' : item.tier === 'weekly' ? '周备份' : item.tier === 'monthly' ? '月备份' : '常规备份';
  const confirm = DialogPlugin.confirm({
    header: '确认删除远端备份?',
    body: `您确定要从云端删除 ${tierName} "${item.fileName}" 吗？此操作不可恢复。`,
    theme: 'danger',
    confirmBtn: { content: '确认删除', theme: 'danger' },
    onConfirm: async () => {
      confirm.hide();
      try {
        await cloudBackupApi.deleteRemoteBackup(currentInstanceId.value, item.fullPath);
        MessagePlugin.success('远端备份已删除');
        await fetchRemoteBackups();
      } catch (err: any) {
        MessagePlugin.error(err?.message || '删除远端文件失败');
      }
    },
    onClose: () => confirm.hide(),
  });
};

const onProfileChanged = async () => {
  await fetchProfiles();
  if (formConfig.value.profileId) {
    await fetchRemoteBackups();
  }
};

watch(() => currentInstanceId.value, () => initData());
onMounted(() => initData());

const columns = [
  { colKey: 'tier', title: '类型', width: 90 },
  { colKey: 'fileName', title: '文件名', ellipsis: true },
  { colKey: 'formattedSize', title: '文件大小', width: 100 },
  { colKey: 'lastModified', title: '云端更新时间', width: 170 },
  { colKey: 'op', title: '操作', width: 70, fixed: 'right' },
];
</script>

<template>
  <div class="flex flex-col mx-auto w-full pb-8">

    <!-- 顶部 Section 标头：完全匹配宿主样式 -->
    <div class="flex items-center gap-2 mt-5 mb-4 pb-2 border-b border-zinc-200/60 dark:border-zinc-700/60">
      <div class="w-1 h-4 bg-[var(--color-primary)] rounded-full"></div>
      <h2 class="text-base font-bold text-[var(--td-text-color-primary)] m-0 flex items-center">
        <cloud-upload-icon class="mr-1.5 text-[var(--color-primary)]" />
        云端同步设置
      </h2>
    </div>

    <!-- 状态引导横幅 -->
    <div
      v-if="!formConfig.enabled"
      class="flex items-start gap-3 p-3.5 md:p-4 mb-4 rounded-xl border border-zinc-200/70 bg-zinc-50/70 dark:bg-zinc-800/30 dark:border-zinc-700/50 text-xs shadow-xs"
    >
      <error-circle-filled-icon class="text-base text-zinc-400 shrink-0 mt-0.5" />
      <div class="flex flex-col text-xs leading-relaxed">
        <span class="font-bold text-zinc-800 dark:text-zinc-200 text-sm">云端自动备份同步已停用</span>
        <span class="text-zinc-600 dark:text-zinc-400 mt-0.5">
          启用后，每次实例备份完成时将自动上传到云端对象存储或远程服务器，实现异地容灾与超额滚动清理。
        </span>
      </div>
    </div>

    <div
      v-else
      class="flex flex-col md:flex-row md:items-center justify-between p-3 px-4 mb-4 rounded-xl border border-emerald-300/80 bg-emerald-50/70 dark:bg-emerald-950/30 dark:border-emerald-700/50 text-xs shadow-xs gap-2"
    >
      <div class="flex items-center gap-3">
        <check-circle-filled-icon class="text-base text-emerald-500 shrink-0" />
        <div class="text-emerald-800 dark:text-emerald-300">
          <b>云端同步已就绪</b>：
          <span v-if="formConfig.syncMode === 'GfsOnly'">仅同步日、周、月 GFS 归档。</span>
          <span v-else-if="formConfig.syncMode === 'Both'">常规备份与 GFS 归档均同步至云端。</span>
          <span v-else>仅同步常规备份。</span>
          <span v-if="formConfig.lastSyncTime" class="ml-1 text-emerald-600 dark:text-emerald-400">
            最近同步: {{ formatDateTime(formConfig.lastSyncTime) }} ({{ formConfig.lastSyncStatus === 'Success' ? '成功' : formConfig.lastSyncMessage }})
          </span>
        </div>
      </div>
    </div>

    <!-- 表单区域：按宿主 GeneralSettings 行模式渲染 -->
    <div class="flex flex-col">

      <!-- 功能开关行 -->
      <div class="flex flex-col md:flex-row md:items-start justify-between p-3 md:p-4 border-b border-dashed border-zinc-100 dark:border-zinc-800/60 hover:bg-zinc-50/50 dark:hover:bg-zinc-800/20 transition-colors rounded-xl">
        <div class="flex-1 pr-0 md:pr-8 mb-3 md:mb-0 min-w-[200px]">
          <div class="text-sm font-medium text-[var(--td-text-color-primary)] leading-snug">云端自动同步</div>
          <div class="text-xs text-[var(--td-text-color-secondary)] mt-1 leading-relaxed">
            启用后，每次生成备份时将自动上传至指定云存储
          </div>
        </div>
        <div class="w-full md:w-[340px] shrink-0 flex items-center justify-end">
          <t-switch v-model="formConfig.enabled" size="large" />
        </div>
      </div>

      <template v-if="formConfig.enabled">

        <!-- 存储策略选择行 -->
        <div class="flex flex-col md:flex-row md:items-start justify-between p-3 md:p-4 border-b border-dashed border-zinc-100 dark:border-zinc-800/60 hover:bg-zinc-50/50 dark:hover:bg-zinc-800/20 transition-colors rounded-xl">
          <div class="flex-1 pr-0 md:pr-8 mb-3 md:mb-0 min-w-[200px]">
            <div class="text-sm font-medium text-[var(--td-text-color-primary)] leading-snug flex items-center gap-1.5">
              关联云存储目标
              <t-tooltip content="支持 S3 兼容对象存储、WebDAV 与 FTP / FTPS 协议。">
                <span class="text-xs text-zinc-400 hover:text-zinc-500 cursor-help flex items-center gap-1">
                  <help-circle-icon />
                </span>
              </t-tooltip>
            </div>
            <div class="text-xs text-[var(--td-text-color-secondary)] mt-1 leading-relaxed">
              选择本实例备份上传的目标存储服务
            </div>
          </div>
          <div class="w-full md:w-[360px] shrink-0 flex items-center gap-2">
            <t-select
              v-model="formConfig.profileId"
              placeholder="请选择云存储策略..."
              clearable
              class="flex-1"
            >
              <t-option
                v-for="p in userProfiles"
                :key="p.id"
                :value="p.id"
                :label="`${p.name} (${normalizeProviderType(p.providerType) === 'S3Compatible' ? 'S3 兼容' : normalizeProviderType(p.providerType) === 'WebDAV' ? 'WebDAV' : 'FTP / FTPS'})`"
              />
            </t-select>

            <t-button
              variant="outline"
              theme="primary"
              class="!rounded-lg shrink-0"
              @click="showProfileModal = true"
            >
              <template #icon><setting-icon /></template> 管理策略
            </t-button>
          </div>
        </div>

        <!-- 远端存储路径模板行 -->
        <div class="flex flex-col md:flex-row md:items-start justify-between p-3 md:p-4 border-b border-dashed border-zinc-100 dark:border-zinc-800/60 hover:bg-zinc-50/50 dark:hover:bg-zinc-800/20 transition-colors rounded-xl">
          <div class="flex-1 pr-0 md:pr-8 mb-3 md:mb-0 min-w-[200px]">
            <div class="text-sm font-medium text-[var(--td-text-color-primary)] leading-snug">远端存储路径模板</div>
            <div class="text-xs text-[var(--td-text-color-secondary)] mt-1 leading-relaxed">
              支持变量占位符以自定义不同实例的存储路径
            </div>
            <div class="flex flex-wrap items-center gap-1.5 mt-2">
              <span class="text-[11px] text-zinc-400">点击插入:</span>
              <t-tag
                v-for="item in placeholders"
                :key="item.label"
                size="small"
                variant="light"
                class="!cursor-pointer hover:!border-[var(--color-primary)] transition-colors"
                @click="insertPlaceholder(item.label)"
              >
                {{ item.label }} ({{ item.desc }})
              </t-tag>
            </div>
          </div>
          <div class="w-full md:w-[360px] shrink-0 flex items-center">
            <t-input
              v-model="formConfig.remotePathPattern"
              placeholder="/mslx-backups/{serverName}/"
            />
          </div>
        </div>

        <!-- GFS 联动同步模式选择行 (检测到 GFS 插件时显示) -->
        <div v-if="gfsStatus.isInstalled" class="flex flex-col md:flex-row md:items-start justify-between p-3 md:p-4 border-b border-dashed border-zinc-100 dark:border-zinc-800/60 hover:bg-zinc-50/50 dark:hover:bg-zinc-800/20 transition-colors rounded-xl">
          <div class="flex-1 pr-0 md:pr-8 mb-3 md:mb-0 min-w-[200px]">
            <div class="text-sm font-medium text-[var(--td-text-color-primary)] leading-snug flex items-center gap-1.5">
              <history-icon class="text-[var(--color-primary)]" />
              GFS 分层归档同步模式
              <t-tag v-if="gfsStatus.enabled" size="small" theme="success" variant="light">GFS已启用</t-tag>
              <t-tag v-else size="small" theme="default" variant="light">GFS未启用</t-tag>
            </div>
            <div class="text-xs text-[var(--td-text-color-secondary)] mt-1 leading-relaxed">
              选择是否对日/周/月长线历史归档执行独立的分层云端同步
            </div>
          </div>
          <div class="w-full md:w-[360px] shrink-0 flex items-center justify-start md:justify-end">
            <t-radio-group v-model="formConfig.syncMode" variant="default-filled">
              <t-radio-button value="GfsOnly">仅 GFS 归档 (推荐)</t-radio-button>
              <t-radio-button value="Both">双轨同步</t-radio-button>
              <t-radio-button value="RegularOnly">仅常规备份</t-radio-button>
            </t-radio-group>
          </div>
        </div>

        <!-- GFS 独立保留份数卡片行 (当选择 GfsOnly 或 Both 时) -->
        <div v-if="gfsStatus.isInstalled && formConfig.syncMode !== 'RegularOnly'" class="p-3 md:p-4 border-b border-dashed border-zinc-100 dark:border-zinc-800/60 rounded-xl">
          <div class="flex flex-col md:flex-row md:items-center justify-between mb-3">
            <div>
              <div class="text-sm font-medium text-[var(--td-text-color-primary)] leading-snug">
                远端 GFS 归档保留策略
              </div>
              <div class="text-xs text-[var(--td-text-color-secondary)] mt-1 leading-relaxed">
                归档保存在 <code>gfs-archives/{daily,weekly,monthly}</code>，可独立或跟随本地 GFS 策略执行滚动清理。
              </div>
            </div>
            <div class="mt-2 md:mt-0 flex items-center gap-2">
              <span class="text-xs text-zinc-500">继承本地 GFS 份数:</span>
              <t-switch v-model="formConfig.inheritGfsKeep" />
            </div>
          </div>

          <!-- 三层远端保留卡片 -->
          <div class="grid grid-cols-1 md:grid-cols-3 gap-3 my-2">
            <!-- 1. 日备份 -->
            <div class="p-3.5 rounded-xl border border-zinc-200/70 dark:border-zinc-700/60 bg-white/60 dark:bg-zinc-800/40 flex flex-col justify-between">
              <div>
                <div class="flex items-center justify-between mb-1.5">
                  <span class="text-xs font-bold text-[var(--color-primary)] flex items-center gap-1.5">
                    <calendar-icon /> 远端日备份
                  </span>
                  <t-tag theme="primary" variant="light" size="small">
                    {{ formConfig.inheritGfsKeep ? `继承 ${gfsStatus.keepDailyDays} 份` : '自定义' }}
                  </t-tag>
                </div>
                <div class="text-[11px] text-[var(--td-text-color-secondary)] leading-relaxed mb-3">
                  每天保存 1 份快照，超出设定份数后自动删除最旧快照。
                </div>
              </div>
              <div class="flex items-center justify-between pt-2 border-t border-zinc-100 dark:border-zinc-700/40">
                <span class="text-xs text-zinc-600 dark:text-zinc-300">云端保留上限</span>
                <div class="w-[120px]">
                  <t-input-number
                    v-model="formConfig.remoteKeepDaily"
                    :min="1"
                    :max="365"
                    size="small"
                    theme="column"
                    suffix="份"
                    :disabled="formConfig.inheritGfsKeep"
                  />
                </div>
              </div>
            </div>

            <!-- 2. 周备份 -->
            <div class="p-3.5 rounded-xl border border-zinc-200/70 dark:border-zinc-700/60 bg-white/60 dark:bg-zinc-800/40 flex flex-col justify-between">
              <div>
                <div class="flex items-center justify-between mb-1.5">
                  <span class="text-xs font-bold text-emerald-600 dark:text-emerald-400 flex items-center gap-1.5">
                    <calendar-icon /> 远端周备份
                  </span>
                  <t-tag theme="success" variant="light" size="small">
                    {{ formConfig.inheritGfsKeep ? `继承 ${gfsStatus.keepWeeklyWeeks} 份` : '自定义' }}
                  </t-tag>
                </div>
                <div class="text-[11px] text-[var(--td-text-color-secondary)] leading-relaxed mb-3">
                  每周保存 1 份快照，超出设定份数后自动删除最旧快照。
                </div>
              </div>
              <div class="flex items-center justify-between pt-2 border-t border-zinc-100 dark:border-zinc-700/40">
                <span class="text-xs text-zinc-600 dark:text-zinc-300">云端保留上限</span>
                <div class="w-[120px]">
                  <t-input-number
                    v-model="formConfig.remoteKeepWeekly"
                    :min="1"
                    :max="365"
                    size="small"
                    theme="column"
                    suffix="份"
                    :disabled="formConfig.inheritGfsKeep"
                  />
                </div>
              </div>
            </div>

            <!-- 3. 月备份 -->
            <div class="p-3.5 rounded-xl border border-zinc-200/70 dark:border-zinc-700/60 bg-white/60 dark:bg-zinc-800/40 flex flex-col justify-between">
              <div>
                <div class="flex items-center justify-between mb-1.5">
                  <span class="text-xs font-bold text-amber-600 dark:text-amber-400 flex items-center gap-1.5">
                    <calendar-icon /> 远端月备份
                  </span>
                  <t-tag theme="warning" variant="light" size="small">
                    {{ formConfig.inheritGfsKeep ? `继承 ${gfsStatus.keepMonthlyMonths} 份` : '自定义' }}
                  </t-tag>
                </div>
                <div class="text-[11px] text-[var(--td-text-color-secondary)] leading-relaxed mb-3">
                  每月保存 1 份快照，超出设定份数后自动删除最旧快照。
                </div>
              </div>
              <div class="flex items-center justify-between pt-2 border-t border-zinc-100 dark:border-zinc-700/40">
                <span class="text-xs text-zinc-600 dark:text-zinc-300">云端保留上限</span>
                <div class="w-[120px]">
                  <t-input-number
                    v-model="formConfig.remoteKeepMonthly"
                    :min="1"
                    :max="365"
                    size="small"
                    theme="column"
                    suffix="份"
                    :disabled="formConfig.inheritGfsKeep"
                  />
                </div>
              </div>
            </div>
          </div>
        </div>

        <!-- 常规备份生命周期区域（仅在非 GfsOnly 模式下展示） -->
        <template v-if="formConfig.syncMode !== 'GfsOnly'">
          <!-- 远端常规备份保留控制行 -->
          <div class="flex flex-col md:flex-row md:items-start justify-between p-3 md:p-4 border-b border-dashed border-zinc-100 dark:border-zinc-800/60 hover:bg-zinc-50/50 dark:hover:bg-zinc-800/20 transition-colors rounded-xl">
            <div class="flex-1 pr-0 md:pr-8 mb-3 md:mb-0 min-w-[200px]">
              <div class="text-sm font-medium text-[var(--td-text-color-primary)] leading-snug">远端常规备份保留</div>
              <div class="text-xs text-[var(--td-text-color-secondary)] mt-1 leading-relaxed">
                云端常规备份的最大保留份数，超出后自动滚动清理最早的历史备份（设为 0 表示不自动清理）
              </div>
            </div>
            <div class="w-full md:w-[360px] shrink-0 flex items-center justify-start md:justify-end gap-2">
              <span class="text-xs text-zinc-500">云端保留上限:</span>
              <t-input-number
                v-model="formConfig.maxRemoteKeep"
                :min="0"
                :max="1000"
                size="small"
                theme="column"
                suffix="份"
                class="!w-28"
              />
            </div>
          </div>

          <!-- 本地备份保留策略行（彻底整合原互斥冲突的“保留份数”与“上传后删除本地文件”） -->
          <div class="flex flex-col md:flex-row md:items-start justify-between p-3 md:p-4 border-b border-dashed border-zinc-100 dark:border-zinc-800/60 hover:bg-zinc-50/50 dark:hover:bg-zinc-800/20 transition-colors rounded-xl">
            <div class="flex-1 pr-0 md:pr-8 mb-3 md:mb-0 min-w-[200px]">
              <div class="text-sm font-medium text-[var(--td-text-color-primary)] leading-snug">本地备份保留策略</div>
              <div class="text-xs text-[var(--td-text-color-secondary)] mt-1 leading-relaxed">
                <span v-if="localRetentionMode === 'keepN'">本地仅保留最新备份以备快速还原，更早的旧备份在上传后自动滚动清理。</span>
                <span v-else-if="localRetentionMode === 'deleteImmediate'">备份成功上传至云端后立即删除本地副本，实现零本地磁盘占用（适用于磁盘空间紧张的服务器）。</span>
                <span v-else>本地备份文件永久保留，不执行自动清理。</span>
              </div>
            </div>
            <div class="w-full md:w-[380px] shrink-0 flex flex-wrap items-center justify-start md:justify-end gap-2">
              <t-radio-group v-model="localRetentionMode" variant="default-filled">
                <t-radio-button value="keepN">保留最近副本</t-radio-button>
                <t-radio-button value="deleteImmediate">上传后删除本地</t-radio-button>
                <t-radio-button value="keepAll">不自动清理</t-radio-button>
              </t-radio-group>
              <t-input-number
                v-if="localRetentionMode === 'keepN'"
                v-model="formConfig.maxLocalKeep"
                :min="1"
                :max="1000"
                size="small"
                theme="column"
                suffix="份"
                class="!w-24"
              />
            </div>
          </div>
        </template>

        <!-- 仅 GFS 归档模式时的提示说明行 -->
        <div v-else class="p-3 md:p-4 border-b border-dashed border-zinc-100 dark:border-zinc-800/60 rounded-xl text-xs text-[var(--td-text-color-secondary)] leading-relaxed">
          <div class="flex items-center gap-2 text-zinc-600 dark:text-zinc-400">
            <help-circle-icon class="text-sm text-[var(--color-primary)] shrink-0" />
            <span>当前为「仅 GFS 归档」模式：仅将日/周/月长线归档快照同步至云端，常规定时备份不上传至云端，本地文件不受云端滚动策略影响。如需同步常规备份，请在上方切换为「双轨同步」或「仅常规备份」。</span>
          </div>
        </div>

        <!-- 保存配置按钮行：完全匹配 GFS 按钮行 -->
        <div class="flex justify-end p-3 md:p-4">
          <t-button theme="primary" :loading="saving" class="!rounded-lg shadow-sm" @click="handleSaveConfig">
            保存云同步配置
          </t-button>
        </div>

      </template>

    </div>

    <!-- 远端历史备份清单标头：完全复用 GFS 布局 -->
    <div class="flex items-center gap-2 mt-8 mb-4 pb-2 border-b border-zinc-200/60 dark:border-zinc-700/60">
      <div class="w-1 h-4 bg-[var(--color-primary)] rounded-full"></div>
      <h2 class="text-base font-bold text-[var(--td-text-color-primary)] m-0">远端备份管理</h2>
    </div>

    <div class="flex flex-col md:flex-row md:justify-between md:items-center py-2 pr-0 md:pr-2 gap-3">
      <div class="flex-1 min-w-[200px]">
        <div class="text-sm font-bold text-[var(--td-text-color-primary)] leading-snug">云端文件列表</div>
        <div class="text-xs text-[var(--td-text-color-secondary)] mt-0.5 leading-relaxed">
          当前云存储已沉淀 <b class="text-zinc-700 dark:text-zinc-300">{{ remoteBackups.length }}</b> 份历史备份（总占用: {{ totalRemoteSizeStr }}）。
        </div>
      </div>

      <div class="shrink-0 flex items-center gap-2">
        <t-input
          v-model="searchKeyword"
          placeholder="搜索文件名或路径..."
          clearable
          size="small"
          class="!w-48"
        >
          <template #prefix-icon><search-icon /></template>
        </t-input>

        <t-button theme="primary" variant="outline" size="small" class="!rounded-lg shadow-sm" :loading="remoteLoading" @click="fetchRemoteBackups">
          <template #icon><refresh-icon /></template> 刷新
        </t-button>
      </div>
    </div>

    <!-- 分类 Tabs：完全复用 GFS 样式 -->
    <div class="mt-3">
      <t-tabs v-model="currentTab" theme="card" size="medium">
        <t-tab-panel value="all">
          <template #label>
            <div class="flex items-center gap-1.5">
              <span>全部备份</span>
              <t-tag size="small" variant="light" theme="default">{{ remoteBackups.length }}</t-tag>
            </div>
          </template>
        </t-tab-panel>

        <t-tab-panel value="daily">
          <template #label>
            <div class="flex items-center gap-1.5">
              <span>日备份</span>
              <t-tag size="small" variant="light" theme="primary">{{ dailyCount }}</t-tag>
            </div>
          </template>
        </t-tab-panel>

        <t-tab-panel value="weekly">
          <template #label>
            <div class="flex items-center gap-1.5">
              <span>周备份</span>
              <t-tag size="small" variant="light" theme="success">{{ weeklyCount }}</t-tag>
            </div>
          </template>
        </t-tab-panel>

        <t-tab-panel value="monthly">
          <template #label>
            <div class="flex items-center gap-1.5">
              <span>月备份</span>
              <t-tag size="small" variant="light" theme="warning">{{ monthlyCount }}</t-tag>
            </div>
          </template>
        </t-tab-panel>

        <t-tab-panel value="regular">
          <template #label>
            <div class="flex items-center gap-1.5">
              <span>常规备份</span>
              <t-tag size="small" variant="light" theme="default">{{ regularCount }}</t-tag>
            </div>
          </template>
        </t-tab-panel>
      </t-tabs>
    </div>

    <!-- 表格容器：完全统一 GFS 与 BackupManager 样式 -->
    <div class="mt-2 border border-zinc-200/60 dark:border-zinc-700/60 rounded-xl overflow-hidden shadow-sm bg-white/50 dark:bg-zinc-900/20">
      <t-table
        row-key="fullPath"
        :data="filteredRemoteBackups"
        :columns="columns"
        :loading="remoteLoading"
        stripe
        hover
        size="small"
      >
        <template #tier="{ row }">
          <div class="flex items-center">
            <t-tag
              v-if="row.tier === 'daily'"
              theme="primary"
              variant="light"
              size="small"
              class="!inline-flex !items-center !justify-center !w-auto !px-2.5 !rounded-md !font-medium"
            >
              日备份
            </t-tag>
            <t-tag
              v-else-if="row.tier === 'weekly'"
              theme="success"
              variant="light"
              size="small"
              class="!inline-flex !items-center !justify-center !w-auto !px-2.5 !rounded-md !font-medium"
            >
              周备份
            </t-tag>
            <t-tag
              v-else-if="row.tier === 'monthly'"
              theme="warning"
              variant="light"
              size="small"
              class="!inline-flex !items-center !justify-center !w-auto !px-2.5 !rounded-md !font-medium"
            >
              月备份
            </t-tag>
            <t-tag
              v-else
              theme="default"
              variant="light"
              size="small"
              class="!inline-flex !items-center !justify-center !w-auto !px-2.5 !rounded-md !font-medium"
            >
              常规
            </t-tag>
          </div>
        </template>

        <template #fileName="{ row }">
          <div class="flex items-center font-mono text-[13px] text-zinc-700 dark:text-zinc-300 break-all">
            <file-icon class="mr-1.5 text-[var(--color-primary)] shrink-0" />
            <span>{{ row.fileName }}</span>
          </div>
        </template>

        <template #lastModified="{ row }">
          <div class="flex items-center text-[var(--td-text-color-secondary)] text-[13px]">
            <time-icon class="mr-1.5 shrink-0" />
            {{ row.lastModified ? new Date(row.lastModified).toLocaleString() : '-' }}
          </div>
        </template>

        <template #op="{ row }">
          <t-button variant="text" shape="square" theme="danger" class="!rounded-md hover:!bg-red-500/10 transition-colors" @click="handleDeleteRemoteFile(row)">
            <delete-icon />
          </t-button>
        </template>

        <template #empty>
          <div class="p-8 text-center text-sm font-medium text-[var(--td-text-color-secondary)]">
            <span v-if="searchKeyword">未找到匹配的云端备份文件</span>
            <span v-else>云端目标目录下暂无对应备份文件，或尚未绑定有效策略</span>
          </div>
        </template>
      </t-table>
    </div>

    <!-- 弹窗：维护存储策略（挂载在 body 避免层叠被截断） -->
    <CloudStorageProfileModal
      v-model:visible="showProfileModal"
      @changed="onProfileChanged"
    />
  </div>
</template>
