import { defineStore } from 'pinia';
import { hostBridge } from '../bridge/hostBridge';

function createDefaultSettings() {
  return {
    baseUrl: 'https://api.openai.com/v1',
    apiKey: '',
    apiKeyDisplay: '',
    hasApiKey: false,
    baseUrlHeavy: '',
    apiKeyHeavy: '',
    apiKeyHeavyDisplay: '',
    hasApiKeyHeavy: false,
    baseUrlLight: '',
    apiKeyLight: '',
    apiKeyLightDisplay: '',
    hasApiKeyLight: false,
    lightModel: 'gpt-4.1-mini',
    heavyModel: 'gpt-4.1',
    permissionMode: 'confirm_writes',
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
    isTestingConnection: false,
    isPanelOpen: false,
    saveMessage: '',
    saveMessageType: 'info',
    connectionTestResult: null
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
        this.connectionTestResult = null;
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
        this.connectionTestResult = null;
      } catch (error) {
        this.saveMessage = `设置保存失败：${error.message || '未知错误'}`;
        this.saveMessageType = 'error';
        throw error;
      } finally {
        this.isSaving = false;
      }
    },

    async testConnection() {
      this.isTestingConnection = true;
      this.connectionTestResult = null;

      try {
        const result = await hostBridge.testModelConnection(this.form);
        this.connectionTestResult = result;
        this.saveMessage = result?.message || '连接测试完成。';
        this.saveMessageType = result?.success ? 'success' : 'error';
        return result;
      } catch (error) {
        this.connectionTestResult = {
          success: false,
          message: error.message || '连接测试失败'
        };
        this.saveMessage = `连接测试失败：${error.message || '未知错误'}`;
        this.saveMessageType = 'error';
        throw error;
      } finally {
        this.isTestingConnection = false;
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
