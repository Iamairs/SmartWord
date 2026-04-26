<template>
  <section class="settings-panel">
    <div class="settings-panel__header">
      <div>
        <h2>连接设置</h2>
        <p>基础配置用于日常请求，高级配置只在需要单独路由模型时调整。</p>
      </div>
      <button class="ghost-button ghost-button--small" type="button" @click="settingsStore.closePanel()">
        关闭
      </button>
    </div>

    <div class="settings-section">
      <h3>基础设置</h3>
      <label class="settings-field">
        <span>服务商预设</span>
        <select v-model="providerPreset" @change="applyProviderPreset">
          <option v-for="provider in providerOptions" :key="provider.value" :value="provider.value">
            {{ provider.label }}
          </option>
        </select>
      </label>

      <label class="settings-field">
        <span>默认 Base URL</span>
        <input v-model.trim="settingsStore.form.baseUrl" type="text" />
      </label>

      <label class="settings-field">
        <span>默认 API Key</span>
        <input
          v-model.trim="settingsStore.form.apiKey"
          type="password"
          autocomplete="off"
          :placeholder="settingsStore.form.hasApiKey ? '已保存，输入新 Key 可替换' : '填写 API Key'"
        />
      </label>

      <label class="settings-field">
        <span>轻量模型</span>
        <input v-model.trim="settingsStore.form.lightModel" type="text" />
      </label>

      <label class="settings-field">
        <span>重量模型</span>
        <input v-model.trim="settingsStore.form.heavyModel" type="text" />
      </label>

      <label class="settings-field">
        <span>执行权限</span>
        <select v-model="settingsStore.form.permissionMode" @change="handlePermissionChange">
          <option v-for="option in permissionOptions" :key="option.value" :value="option.value">
            {{ option.label }}
          </option>
        </select>
      </label>
      <p class="permission-help">{{ selectedPermission.description }}</p>
    </div>

    <details class="settings-section settings-section--advanced">
      <summary>高级设置</summary>
      <label class="settings-field">
        <span>轻量 Base URL</span>
        <input v-model.trim="settingsStore.form.baseUrlLight" type="text" />
      </label>

      <label class="settings-field">
        <span>轻量 API Key</span>
        <input
          v-model.trim="settingsStore.form.apiKeyLight"
          type="password"
          autocomplete="off"
          :placeholder="settingsStore.form.hasApiKeyLight ? '已保存，输入新 Key 可替换' : '留空则使用默认 Key'"
        />
      </label>

      <label class="settings-field">
        <span>重量 Base URL</span>
        <input v-model.trim="settingsStore.form.baseUrlHeavy" type="text" />
      </label>

      <label class="settings-field">
        <span>重量 API Key</span>
        <input
          v-model.trim="settingsStore.form.apiKeyHeavy"
          type="password"
          autocomplete="off"
          :placeholder="settingsStore.form.hasApiKeyHeavy ? '已保存，输入新 Key 可替换' : '留空则使用默认 Key'"
        />
      </label>

      <label class="settings-field settings-field--textarea">
        <span>自定义系统指令</span>
        <textarea
          v-model.trim="settingsStore.form.customInstructions"
          rows="4"
          maxlength="2000"
          placeholder="例如：优先使用简洁正式的中文回答。"
        ></textarea>
      </label>
    </details>

    <div v-if="settingsStore.connectionTestResult" class="diagnostic-card" :class="diagnosticClass">
      <strong>{{ settingsStore.connectionTestResult.success ? '连接可用' : '连接需要处理' }}</strong>
      <p>{{ settingsStore.connectionTestResult.message }}</p>
      <ul v-if="settingsStore.connectionTestResult.routes?.length">
        <li v-for="route in settingsStore.connectionTestResult.routes" :key="route.mode">
          {{ route.mode }}：{{ route.selectedModel || '未配置模型' }} /
          {{ route.enableToolCalling ? '支持工具' : '不支持工具' }}
        </li>
      </ul>
    </div>

    <div class="settings-panel__footer">
      <p
        v-if="settingsStore.saveMessage"
        class="settings-message"
        :class="`settings-message--${settingsStore.saveMessageType}`"
      >
        {{ settingsStore.saveMessage }}
      </p>
      <div class="settings-actions">
        <button
          class="ghost-button"
          type="button"
          :disabled="settingsStore.isTestingConnection"
          @click="testConnection"
        >
          {{ settingsStore.isTestingConnection ? '测试中...' : '测试连接' }}
        </button>
        <button
          class="send-button"
          type="button"
          :disabled="settingsStore.isSaving"
          @click="saveSettings"
        >
          {{ settingsStore.isSaving ? '保存中...' : '保存设置' }}
        </button>
      </div>
    </div>
  </section>
</template>

<script setup>
import { computed, ref } from 'vue';
import { useSettingsStore } from '../stores/settings';

const settingsStore = useSettingsStore();
const providerPreset = ref('openai');

const providerOptions = [
  { value: 'openai', label: 'OpenAI', baseUrl: 'https://api.openai.com/v1' },
  { value: 'azure', label: 'Azure OpenAI', baseUrl: '' },
  { value: 'siliconflow', label: 'SiliconFlow / DeepSeek', baseUrl: 'https://api.siliconflow.cn/v1' },
  { value: 'ollama', label: 'Ollama 本地', baseUrl: 'http://localhost:11434/v1' },
  { value: 'custom', label: '自定义', baseUrl: '' }
];

const permissionOptions = [
  {
    value: 'read_only',
    label: '只读模式',
    description: '不会修改文档，适合总结、问答、审阅和风险检查。'
  },
  {
    value: 'confirm_writes',
    label: '写入前确认',
    description: '每次修改前都需要你确认，推荐用于正式文档。'
  },
  {
    value: 'auto_safe_writes',
    label: '自动安全写入',
    description: '标准补丁可自动执行，脚本类高风险写入仍会要求确认。'
  },
  {
    value: 'full_auto',
    label: '全自动执行',
    description: 'Agent 可连续修改文档，仅建议在副本或低风险文档中使用。'
  }
];

const selectedPermission = computed(() => {
  return permissionOptions.find((item) => item.value === settingsStore.form.permissionMode) || permissionOptions[1];
});

const diagnosticClass = computed(() => {
  return settingsStore.connectionTestResult?.success
    ? 'diagnostic-card--success'
    : 'diagnostic-card--error';
});

function applyProviderPreset() {
  const provider = providerOptions.find((item) => item.value === providerPreset.value);
  if (!provider || !provider.baseUrl) {
    return;
  }

  settingsStore.form.baseUrl = provider.baseUrl;
}

function handlePermissionChange() {
  if (settingsStore.form.permissionMode !== 'full_auto') {
    return;
  }

  const confirmed = window.confirm('全自动执行会允许 Agent 连续修改文档。建议仅在副本或低风险文档中使用。确定开启吗？');
  if (!confirmed) {
    settingsStore.form.permissionMode = 'confirm_writes';
  }
}

async function saveSettings() {
  await settingsStore.saveSettings();
}

async function testConnection() {
  await settingsStore.testConnection();
}
</script>

<style scoped>
.settings-panel {
  padding: 14px;
  border-radius: 12px;
  background: rgba(255, 255, 255, 0.94);
  border: 1px solid rgba(89, 118, 161, 0.16);
  box-shadow: 0 12px 28px rgba(24, 49, 79, 0.08);
}

.settings-panel__header,
.settings-actions {
  display: flex;
  justify-content: space-between;
  align-items: flex-start;
  gap: 8px;
}

.settings-panel__header h2 {
  margin: 0;
  font-size: 16px;
}

.settings-panel__header p,
.permission-help,
.settings-message,
.diagnostic-card p,
.diagnostic-card li {
  margin: 4px 0 0;
  font-size: 11px;
  line-height: 1.5;
  color: #60758f;
}

.settings-section {
  margin-top: 14px;
  display: flex;
  flex-direction: column;
  gap: 10px;
}

.settings-section h3,
.settings-section summary {
  margin: 0;
  font-size: 13px;
  font-weight: 700;
  color: #18314f;
}

.settings-section--advanced summary {
  cursor: pointer;
  margin-bottom: 10px;
}

.settings-field {
  display: flex;
  flex-direction: column;
  gap: 6px;
  font-size: 12px;
  color: #395372;
}

.settings-field input,
.settings-field select,
.settings-field textarea {
  width: 100%;
  padding: 9px 10px;
  border: 1px solid rgba(89, 118, 161, 0.26);
  border-radius: 10px;
  font: inherit;
  color: inherit;
  background: #f7f9fc;
}

.settings-field textarea {
  resize: vertical;
  min-height: 88px;
}

.diagnostic-card {
  margin-top: 12px;
  padding: 10px;
  border-radius: 10px;
  border: 1px solid rgba(89, 118, 161, 0.16);
  background: #f7f9fc;
}

.diagnostic-card--success {
  border-color: rgba(45, 106, 79, 0.24);
  background: rgba(45, 106, 79, 0.06);
}

.diagnostic-card--error {
  border-color: rgba(180, 35, 24, 0.24);
  background: rgba(180, 35, 24, 0.06);
}

.diagnostic-card ul {
  margin: 8px 0 0;
  padding-left: 18px;
}

.settings-panel__footer {
  margin-top: 12px;
}

.settings-message--success {
  color: #2d6a4f;
}

.settings-message--error {
  color: #b42318;
}

.ghost-button,
.send-button {
  min-height: 36px;
  border-radius: 10px;
  font: inherit;
  font-size: 12px;
  font-weight: 600;
  cursor: pointer;
}

.ghost-button {
  border: 1px solid rgba(89, 118, 161, 0.22);
  background: rgba(255, 255, 255, 0.82);
  color: #244464;
  padding: 7px 10px;
}

.ghost-button--small {
  padding: 6px 8px;
}

.send-button {
  min-width: 92px;
  border: none;
  padding: 8px 12px;
  background: #d96f32;
  color: #ffffff;
}

button:disabled {
  cursor: not-allowed;
  opacity: 0.65;
}
</style>
