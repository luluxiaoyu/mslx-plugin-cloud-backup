<script setup lang="ts">
import { ref, watch } from 'vue';
import { MessagePlugin, DialogPlugin } from 'tdesign-vue-next';
import {
  CloudIcon,
  HardDriveIcon,
  ServerIcon,
  AddIcon,
  EditIcon,
  DeleteIcon,
  CheckCircleFilledIcon,
  ErrorCircleFilledIcon,
  RefreshIcon,
  CheckIcon,
  CloseIcon,
} from 'tdesign-icons-vue-next';
import { cloudBackupApi } from '../api/cloudBackup';
import { normalizeProviderType, type CloudStorageProfile, type TestConnectionResult } from '../types/cloudBackup';

const props = defineProps<{
  visible: boolean;
}>();

const emit = defineEmits<{
  (e: 'update:visible', val: boolean): void;
  (e: 'changed'): void;
}>();

const profileList = ref<CloudStorageProfile[]>([]);
const listLoading = ref(false);
const isEditing = ref(false);
const saving = ref(false);
const testing = ref(false);
const testResult = ref<TestConnectionResult | null>(null);

const defaultProfile = (): CloudStorageProfile => ({
  id: '',
  name: '',
  providerType: 'S3Compatible',
  s3Endpoint: '',
  s3Region: 'auto',
  s3BucketName: '',
  s3AccessKey: '',
  s3SecretKey: '',
  s3ForcePathStyle: true,
  webDavUrl: '',
  webDavUsername: '',
  webDavPassword: '',
  webDavBasePath: '/',
  ftpHost: '',
  ftpPort: 21,
  ftpUsername: '',
  ftpPassword: '',
  ftpUseSsl: false,
  ftpBasePath: '/',
  hasS3Credentials: false,
  hasWebDavCredentials: false,
  hasFtpCredentials: false,
});

const currentForm = ref<CloudStorageProfile>(defaultProfile());

const fetchProfiles = async () => {
  listLoading.value = true;
  try {
    const rawList = await cloudBackupApi.getUserProfiles();
    profileList.value = rawList.map(p => ({
      ...p,
      providerType: normalizeProviderType(p.providerType)
    }));
  } catch (err: any) {
    MessagePlugin.error(err?.message || '获取存储策略列表失败');
  } finally {
    listLoading.value = false;
  }
};

watch(
  () => props.visible,
  (val) => {
    if (val) {
      isEditing.value = false;
      testResult.value = null;
      fetchProfiles();
    }
  },
  { immediate: true }
);

const handleClose = () => {
  emit('update:visible', false);
};

const handleCreateNew = () => {
  currentForm.value = defaultProfile();
  currentForm.value.providerType = 'S3Compatible';
  testResult.value = null;
  isEditing.value = true;
};

const handleEdit = (profile: CloudStorageProfile) => {
  const cloned = JSON.parse(JSON.stringify(profile));
  cloned.providerType = normalizeProviderType(cloned.providerType);
  cloned.s3AccessKey = '';
  cloned.s3SecretKey = '';
  cloned.webDavUsername = '';
  cloned.webDavPassword = '';
  cloned.ftpUsername = '';
  cloned.ftpPassword = '';
  currentForm.value = cloned;
  testResult.value = null;
  isEditing.value = true;
};

const handleDelete = (profile: CloudStorageProfile) => {
  const confirmDialog = DialogPlugin.confirm({
    header: '确认删除存储策略?',
    body: `确定要删除存储策略「${profile.name}」吗？已绑定此策略的实例将暂停自动云端备份。`,
    theme: 'danger',
    confirmBtn: { content: '确认删除', theme: 'danger' },
    onConfirm: async () => {
      confirmDialog.hide();
      try {
        await cloudBackupApi.deleteUserProfile(profile.id);
        MessagePlugin.success('策略已成功删除');
        await fetchProfiles();
        emit('changed');
        if (isEditing.value && currentForm.value.id === profile.id) {
          isEditing.value = false;
        }
      } catch (err: any) {
        MessagePlugin.error(err?.message || '删除策略失败');
      }
    },
    onClose: () => confirmDialog.hide(),
  });
};

const handleTestConnection = async () => {
  if (!currentForm.value.name) {
    MessagePlugin.warning('请先填写策略名称');
    return;
  }
  testing.value = true;
  testResult.value = null;
  try {
    const res = await cloudBackupApi.testConnection(currentForm.value);
    testResult.value = res;
    if (res.success) {
      MessagePlugin.success(res.message || '连接测试成功！');
    } else {
      MessagePlugin.error(res.message || '连接测试未成功');
    }
  } catch (err: any) {
    testResult.value = {
      success: false,
      message: err?.message || '连接测试异常',
      latencyMs: 0,
    };
    MessagePlugin.error(err?.message || '连接测试异常');
  } finally {
    testing.value = false;
  }
};

const handleSave = async () => {
  if (!currentForm.value.name) {
    MessagePlugin.warning('请输入策略名称');
    return;
  }

  // 校验各协议基础字段
  if (currentForm.value.providerType === 'S3Compatible') {
    if (!currentForm.value.s3BucketName) {
      MessagePlugin.warning('请输入 S3 存储桶 (Bucket) 名称');
      return;
    }
  } else if (currentForm.value.providerType === 'WebDAV') {
    if (!currentForm.value.webDavUrl) {
      MessagePlugin.warning('请输入 WebDAV 服务地址 (URL)');
      return;
    }
  } else if (currentForm.value.providerType === 'FTP') {
    if (!currentForm.value.ftpHost) {
      MessagePlugin.warning('请输入 FTP 主机地址');
      return;
    }
  }

  saving.value = true;
  try {
    await cloudBackupApi.saveUserProfile(currentForm.value);
    MessagePlugin.success('存储策略保存成功！');
    isEditing.value = false;
    await fetchProfiles();
    emit('changed');
  } catch (err: any) {
    MessagePlugin.error(err?.message || '保存存储策略失败');
  } finally {
    saving.value = false;
  }
};
</script>

<template>
  <t-dialog
    :attach="'body'"
    :visible="visible"
    width="760px"
    :footer="false"
    header="云端存储策略"
    @close="handleClose"
  >
    <div class="py-2 min-h-[380px]">
      <!-- 策略列表视图 -->
      <div v-if="!isEditing" class="flex flex-col">
        <div class="flex items-center justify-between pb-3 mb-4 border-b border-zinc-200/60 dark:border-zinc-700/60">
          <div class="flex items-center gap-2">
            <span class="text-xs text-[var(--td-text-color-secondary)]">已配置存储策略</span>
            <t-tag size="small" variant="light" theme="primary" class="!text-[10px] !h-4.5 !leading-4.5 !px-1.5 font-bold">
              {{ profileList.length }}
            </t-tag>
          </div>
          <t-button theme="primary" size="small" class="!rounded-lg shadow-sm" @click="handleCreateNew">
            <template #icon><add-icon /></template> 新建存储策略
          </t-button>
        </div>

        <t-loading :loading="listLoading">
          <div v-if="profileList.length === 0" class="flex flex-col items-center justify-center py-12 text-center">
            <server-icon class="text-4xl text-zinc-300 dark:text-zinc-600 mb-3" />
            <div class="text-sm font-medium text-zinc-700 dark:text-zinc-300">当前尚未配置任何云存储策略</div>
            <div class="text-xs text-zinc-400 mt-1 max-w-sm">
              支持 S3 兼容对象存储（Cloudflare R2 / MinIO 等）、WebDAV 与 FTP / FTPS
            </div>
            <t-button theme="primary" variant="outline" size="small" class="mt-4 !rounded-lg" @click="handleCreateNew">
              <template #icon><add-icon /></template> 立即创建第一条存储策略
            </t-button>
          </div>

          <div v-else class="grid grid-cols-1 md:grid-cols-2 gap-3">
            <div
              v-for="item in profileList"
              :key="item.id"
              class="p-3.5 rounded-xl border border-zinc-200/70 dark:border-zinc-700/60 bg-white/60 dark:bg-zinc-800/40 hover:border-[var(--color-primary)] transition-all flex flex-col justify-between shadow-2xs"
            >
              <div>
                <div class="flex items-center justify-between mb-2">
                  <div class="flex items-center gap-2.5 flex-1 min-w-0 pr-2">
                    <div
                      class="w-8 h-8 rounded-lg flex items-center justify-center text-lg shrink-0"
                      :class="normalizeProviderType(item.providerType) === 'S3Compatible'
                        ? 'bg-blue-50 text-blue-600 dark:bg-blue-950/40 dark:text-blue-400'
                        : normalizeProviderType(item.providerType) === 'WebDAV'
                          ? 'bg-emerald-50 text-emerald-600 dark:bg-emerald-950/40 dark:text-emerald-400'
                          : 'bg-amber-50 text-amber-600 dark:bg-amber-950/40 dark:text-amber-400'"
                    >
                      <cloud-icon v-if="normalizeProviderType(item.providerType) === 'S3Compatible'" />
                      <hard-drive-icon v-else-if="normalizeProviderType(item.providerType) === 'WebDAV'" />
                      <server-icon v-else />
                    </div>
                    <div class="flex-1 min-w-0">
                      <div class="flex items-center gap-1.5 flex-wrap">
                        <span class="text-sm font-bold text-[var(--td-text-color-primary)] leading-snug truncate max-w-[150px]">
                          {{ item.name }}
                        </span>
                        <t-tag
                          size="small"
                          variant="light"
                          :theme="normalizeProviderType(item.providerType) === 'S3Compatible' ? 'primary' : normalizeProviderType(item.providerType) === 'WebDAV' ? 'success' : 'warning'"
                          class="!text-[10px] !h-4.5 !leading-4.5 !px-2 !inline-flex items-center justify-center text-center font-medium shrink-0 !w-auto"
                        >
                          {{ normalizeProviderType(item.providerType) === 'S3Compatible' ? 'S3 兼容' : normalizeProviderType(item.providerType) === 'WebDAV' ? 'WebDAV' : 'FTP / FTPS' }}
                        </t-tag>
                      </div>
                    </div>
                  </div>

                  <div class="flex items-center gap-1 shrink-0">
                    <t-button variant="text" shape="square" size="small" class="!rounded-md" @click="handleEdit(item)">
                      <edit-icon />
                    </t-button>
                    <t-button variant="text" shape="square" size="small" theme="danger" class="!rounded-md hover:!bg-red-500/10" @click="handleDelete(item)">
                      <delete-icon />
                    </t-button>
                  </div>
                </div>

                <div class="text-[11px] text-zinc-500 dark:text-zinc-400 space-y-1 font-mono pt-2 border-t border-zinc-100 dark:border-zinc-700/40">
                  <div v-if="normalizeProviderType(item.providerType) === 'S3Compatible'" class="truncate">
                    桶: {{ item.s3BucketName }}
                    <span v-if="item.s3Region" class="text-zinc-400">({{ item.s3Region }})</span>
                  </div>
                  <div v-else-if="normalizeProviderType(item.providerType) === 'WebDAV'" class="truncate">
                    {{ item.webDavUrl }}
                  </div>
                  <div v-else class="truncate">
                    {{ item.ftpHost }}:{{ item.ftpPort }}
                  </div>
                </div>
              </div>
            </div>
          </div>
        </t-loading>
      </div>

      <!-- 编辑/新建表单视图 -->
      <div v-else class="flex flex-col">
        <div class="flex items-center justify-between pb-3 mb-4 border-b border-zinc-200/60 dark:border-zinc-700/60">
          <div class="text-sm font-bold text-[var(--td-text-color-primary)]">
            {{ currentForm.id ? '编辑云端存储策略' : '新建云端存储策略' }}
          </div>
          <t-button variant="text" size="small" class="!rounded-lg" @click="isEditing = false">
            <template #icon><close-icon /></template> 返回列表
          </t-button>
        </div>

        <div class="space-y-4">
          <!-- 防浏览器自动嗅探并填充账号密码的蜜罐字段 -->
          <div style="position: absolute; top: -9999px; left: -9999px; width: 0; height: 0; overflow: hidden;" aria-hidden="true">
            <input type="text" name="fake_username_remember" tabindex="-1" autocomplete="username" />
            <input type="password" name="fake_password_remember" tabindex="-1" autocomplete="current-password" />
          </div>

          <!-- 策略名称 -->
          <div class="flex flex-col md:flex-row md:items-center justify-between gap-2">
            <span class="text-xs font-medium text-[var(--td-text-color-primary)] w-32 shrink-0">策略友好名称 <span class="text-red-500">*</span></span>
            <t-input v-model="currentForm.name" placeholder="例如：我的 Cloudflare R2 / 家用群晖 WebDAV" class="flex-1" />
          </div>

          <!-- 协议单选 -->
          <div class="flex flex-col md:flex-row md:items-center justify-between gap-2">
            <span class="text-xs font-medium text-[var(--td-text-color-primary)] w-32 shrink-0">存储协议类型 <span class="text-red-500">*</span></span>
            <t-radio-group v-model="currentForm.providerType" variant="default-filled" class="flex-1">
              <t-radio-button value="S3Compatible">S3 兼容对象存储</t-radio-button>
              <t-radio-button value="WebDAV">WebDAV 网盘</t-radio-button>
              <t-radio-button value="FTP">FTP / FTPS</t-radio-button>
            </t-radio-group>
          </div>

          <!-- S3 兼容字段 -->
          <template v-if="currentForm.providerType === 'S3Compatible'">
            <div class="flex flex-col md:flex-row md:items-start justify-between gap-2">
              <div class="w-32 shrink-0 pt-1.5">
                <span class="text-xs font-medium text-[var(--td-text-color-primary)]">服务地址 (Endpoint)</span>
              </div>
              <div class="flex-1">
                <t-input v-model="currentForm.s3Endpoint" placeholder="原生 AWS S3 可留空；第三方 R2 / MinIO / COS 等需填写" />
                <div class="text-[11px] text-[var(--td-text-color-placeholder)] mt-1">
                  原生 AWS S3 可留空（自动按区域解析）；第三方（如 Cloudflare R2、自建 MinIO、腾讯云 COS 等）需填入完整的终结点 URL。
                </div>
              </div>
            </div>

            <div class="flex flex-col md:flex-row md:items-center justify-between gap-2">
              <span class="text-xs font-medium text-[var(--td-text-color-primary)] w-32 shrink-0">存储桶 (Bucket) <span class="text-red-500">*</span></span>
              <t-input v-model="currentForm.s3BucketName" placeholder="例如：mslx-backups" class="flex-1" />
            </div>

            <div class="flex flex-col md:flex-row md:items-center justify-between gap-2">
              <span class="text-xs font-medium text-[var(--td-text-color-primary)] w-32 shrink-0">区域 (Region)</span>
              <t-input v-model="currentForm.s3Region" placeholder="R2/MinIO 填 auto，AWS 填 us-east-1 等" class="flex-1" />
            </div>

            <div class="flex flex-col md:flex-row md:items-center justify-between gap-2">
              <span class="text-xs font-medium text-[var(--td-text-color-primary)] w-32 shrink-0">Access Key ID</span>
              <t-input
                v-model="currentForm.s3AccessKey"
                name="cloud_storage_s3_key"
                autocomplete="new-password"
                :placeholder="currentForm.hasS3Credentials ? '已加密存储（若不修改请留空）' : '输入访问密钥 Access Key'"
                class="flex-1"
              />
            </div>

            <div class="flex flex-col md:flex-row md:items-center justify-between gap-2">
              <span class="text-xs font-medium text-[var(--td-text-color-primary)] w-32 shrink-0">Secret Access Key</span>
              <t-input
                v-model="currentForm.s3SecretKey"
                type="password"
                name="cloud_storage_s3_secret"
                autocomplete="new-password"
                :placeholder="currentForm.hasS3Credentials ? '已加密存储（若不修改请留空）' : '输入私有访问密钥 Secret Key'"
                class="flex-1"
              />
            </div>

            <div class="flex flex-col md:flex-row md:items-center justify-between gap-2">
              <span class="text-xs font-medium text-[var(--td-text-color-primary)] w-32 shrink-0">路径样式 (PathStyle)</span>
              <div class="flex-1 flex items-center gap-2">
                <t-switch v-model="currentForm.s3ForcePathStyle" />
                <span class="text-xs text-zinc-400">适用于 MinIO、Cloudflare R2 等兼容 S3 协议的自建端</span>
              </div>
            </div>
          </template>

          <!-- WebDAV 字段 -->
          <template v-else-if="currentForm.providerType === 'WebDAV'">
            <div class="flex flex-col md:flex-row md:items-center justify-between gap-2">
              <span class="text-xs font-medium text-[var(--td-text-color-primary)] w-32 shrink-0">WebDAV 地址 <span class="text-red-500">*</span></span>
              <t-input v-model="currentForm.webDavUrl" placeholder="例如：https://pan.example.com/dav 或 http://192.168.1.20:5005" class="flex-1" />
            </div>

            <div class="flex flex-col md:flex-row md:items-center justify-between gap-2">
              <span class="text-xs font-medium text-[var(--td-text-color-primary)] w-32 shrink-0">根路径</span>
              <t-input v-model="currentForm.webDavBasePath" placeholder="默认 /，如 /MinecraftBackups" class="flex-1" />
            </div>

            <div class="flex flex-col md:flex-row md:items-center justify-between gap-2">
              <span class="text-xs font-medium text-[var(--td-text-color-primary)] w-32 shrink-0">用户名</span>
              <t-input
                v-model="currentForm.webDavUsername"
                name="cloud_storage_webdav_user"
                autocomplete="new-password"
                :placeholder="currentForm.hasWebDavCredentials ? '已加密存储（若不修改请留空）' : 'WebDAV 登录用户名'"
                class="flex-1"
              />
            </div>

            <div class="flex flex-col md:flex-row md:items-center justify-between gap-2">
              <span class="text-xs font-medium text-[var(--td-text-color-primary)] w-32 shrink-0">密码</span>
              <t-input
                v-model="currentForm.webDavPassword"
                type="password"
                name="cloud_storage_webdav_pass"
                autocomplete="new-password"
                :placeholder="currentForm.hasWebDavCredentials ? '已加密存储（若不修改请留空）' : 'WebDAV 登录密码'"
                class="flex-1"
              />
            </div>
          </template>

          <!-- FTP 字段 -->
          <template v-else>
            <div class="flex flex-col md:flex-row md:items-center justify-between gap-2">
              <span class="text-xs font-medium text-[var(--td-text-color-primary)] w-32 shrink-0">FTP 主机地址 <span class="text-red-500">*</span></span>
              <t-input v-model="currentForm.ftpHost" placeholder="例如：192.168.1.100 或 ftp.example.com" class="flex-1" />
            </div>

            <div class="flex flex-col md:flex-row md:items-center justify-between gap-2">
              <span class="text-xs font-medium text-[var(--td-text-color-primary)] w-32 shrink-0">FTP 端口</span>
              <t-input-number v-model="currentForm.ftpPort" :min="1" :max="65535" size="small" class="!w-32" />
            </div>

            <div class="flex flex-col md:flex-row md:items-center justify-between gap-2">
              <span class="text-xs font-medium text-[var(--td-text-color-primary)] w-32 shrink-0">基础存放目录</span>
              <t-input v-model="currentForm.ftpBasePath" placeholder="默认 /，如 /backups" class="flex-1" />
            </div>

            <div class="flex flex-col md:flex-row md:items-center justify-between gap-2">
              <span class="text-xs font-medium text-[var(--td-text-color-primary)] w-32 shrink-0">账号</span>
              <t-input
                v-model="currentForm.ftpUsername"
                name="cloud_storage_ftp_user"
                autocomplete="new-password"
                :placeholder="currentForm.hasFtpCredentials ? '已加密存储（若不修改请留空）' : 'FTP 账号'"
                class="flex-1"
              />
            </div>

            <div class="flex flex-col md:flex-row md:items-center justify-between gap-2">
              <span class="text-xs font-medium text-[var(--td-text-color-primary)] w-32 shrink-0">密码</span>
              <t-input
                v-model="currentForm.ftpPassword"
                type="password"
                name="cloud_storage_ftp_pass"
                autocomplete="new-password"
                :placeholder="currentForm.hasFtpCredentials ? '已加密存储（若不修改请留空）' : 'FTP 密码'"
                class="flex-1"
              />
            </div>

            <div class="flex flex-col md:flex-row md:items-center justify-between gap-2">
              <span class="text-xs font-medium text-[var(--td-text-color-primary)] w-32 shrink-0">FTPS / SSL 加密</span>
              <div class="flex-1">
                <t-switch v-model="currentForm.ftpUseSsl" />
              </div>
            </div>
          </template>

          <!-- 连通性测试结果反馈框 -->
          <div
            v-if="testResult"
            class="flex items-start gap-2.5 p-3 rounded-xl border text-xs leading-relaxed"
            :class="testResult.success ? 'border-emerald-300/80 bg-emerald-50/70 text-emerald-800 dark:bg-emerald-950/30 dark:border-emerald-700/50 dark:text-emerald-300' : 'border-red-300/80 bg-red-50/70 text-red-800 dark:bg-red-950/30 dark:border-red-700/50 dark:text-red-300'"
          >
            <check-circle-filled-icon v-if="testResult.success" class="text-base text-emerald-500 shrink-0 mt-0.5" />
            <error-circle-filled-icon v-else class="text-base text-red-500 shrink-0 mt-0.5" />
            <div>
              <div class="font-bold">{{ testResult.success ? '连通性测试成功' : '连通性测试未通过' }}</div>
              <div class="mt-0.5 opacity-90">{{ testResult.message }}</div>
            </div>
          </div>

          <!-- 底部动作条 -->
          <div class="flex items-center justify-between pt-4 border-t border-zinc-200/60 dark:border-zinc-700/60">
            <t-button variant="outline" size="small" :loading="testing" class="!rounded-lg" @click="handleTestConnection">
              <template #icon><refresh-icon /></template> 测试连接
            </t-button>

            <div class="flex items-center gap-2">
              <t-button variant="base" size="small" class="!rounded-lg" @click="isEditing = false">
                取消
              </t-button>
              <t-button theme="primary" size="small" :loading="saving" class="!rounded-lg shadow-sm" @click="handleSave">
                <template #icon><check-icon /></template> 保存策略
              </t-button>
            </div>
          </div>
        </div>
      </div>
    </div>
  </t-dialog>
</template>
