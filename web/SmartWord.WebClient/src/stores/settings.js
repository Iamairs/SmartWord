import { defineStore } from 'pinia';
import { hostBridge } from '../bridge/hostBridge';

function createDefaultSettings() {
  return {
    baseUrl: 'https://api.openai.com/v1',
    apiKey: '',
    baseUrlHeavy: '',
    apiKeyHeavy: '',
    baseUrlLight: '',
    apiKeyLight: '',
    lightModel: 'gpt-4.1-mini',
    heavyModel: 'gpt-4.1',
    requireConfirmationForScripts: true,
    customInstructions: ''
  };
}

export const useSettingsStore = defineStore('settings', {
  state: () => ({
    form: createDefaultSettings(),
    isLoaded: false,
    isLoading: false,
    isSaving: false,
    isPanelOpen: false,
    saveMessage: '',
    saveMessageType: 'info'
  }),
  actions: {
    async loadSettings() {
      this.isLoading = true;

      try {
        const settings = await hostBridge.getSettings();
        this.form = {
          ...createDefaultSettings(),
          ...settings
        };
        this.isLoaded = true;
        this.saveMessage = '';
      } catch (error) {
        this.saveMessage = `设置加载失败：${error.message || '未知错误'}`;
        this.saveMessageType = 'error';
      } finally {
        this.isLoading = false;
      }
    },

    async saveSettings() {
      this.isSaving = true;

      try {
        const savedSettings = await hostBridge.saveSettings(this.form);
        this.form = {
          ...createDefaultSettings(),
          ...savedSettings
        };
        this.saveMessage = '设置已保存，新请求会立即使用最新配置。';
        this.saveMessageType = 'success';
      } catch (error) {
        this.saveMessage = `设置保存失败：${error.message || '未知错误'}`;
        this.saveMessageType = 'error';
        throw error;
      } finally {
        this.isSaving = false;
      }
    },

    openPanel() {
      this.isPanelOpen = true;
    },

    closePanel() {
      this.isPanelOpen = false;
    },

    togglePanel() {
      this.isPanelOpen = !this.isPanelOpen;
    }
  }
});
