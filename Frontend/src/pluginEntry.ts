import './style.css';
import { CloudUploadIcon } from 'tdesign-icons-vue-next';
import InstanceCloudSyncTab from './views/InstanceCloudSyncTab.vue';

export const pluginConfig = {
  name: 'MSLX.Plugin.Cloud.Backup',
  version: '1.0.0',

  routes: [],

  extensions: [
    {
      slot: 'instance-settings-tab', // 注入到实例配置侧边栏新 Tab
      component: InstanceCloudSyncTab,
      label: '云备份同步',
      icon: CloudUploadIcon,
    },
  ],
};