const SETTINGS_STORAGE_KEY = 'smartword-settings';

function hasWebView2Bridge() {
  return Boolean(
    window.chrome &&
      window.chrome.webview &&
      window.chrome.webview.hostObjects &&
      window.chrome.webview.hostObjects.bridge
  );
}

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

function normalizeSettings(settings) {
  const source = settings || {};
  const defaults = createDefaultSettings();

  return {
    baseUrl: source.baseUrl ?? source.BaseUrl ?? source.apiBaseUrl ?? source.ApiBaseUrl ?? defaults.baseUrl,
    apiKey: source.apiKey ?? source.ApiKey ?? defaults.apiKey,
    baseUrlHeavy: source.baseUrlHeavy ?? source.BaseUrlHeavy ?? defaults.baseUrlHeavy,
    apiKeyHeavy: source.apiKeyHeavy ?? source.ApiKeyHeavy ?? defaults.apiKeyHeavy,
    baseUrlLight: source.baseUrlLight ?? source.BaseUrlLight ?? defaults.baseUrlLight,
    apiKeyLight: source.apiKeyLight ?? source.ApiKeyLight ?? defaults.apiKeyLight,
    lightModel: source.lightModel ?? source.LightModel ?? defaults.lightModel,
    heavyModel: source.heavyModel ?? source.HeavyModel ?? defaults.heavyModel,
    requireConfirmationForScripts:
      source.requireConfirmationForScripts ??
      source.RequireConfirmationForScripts ??
      defaults.requireConfirmationForScripts,
    customInstructions:
      source.customInstructions ?? source.CustomInstructions ?? defaults.customInstructions
  };
}

function createMockResponse(request, notify) {
  const modeEvent = {
    type: 'mode_detected',
    detectedMode: request.manualMode || 'ask',
    isAutoRouted: !request.manualMode
  };

  const chunkEvent = {
    type: 'stream_chunk',
    content: `已收到请求：${request.content}`
  };

  const completedEvent = {
    type: 'task_completed',
    message: '当前为浏览器降级模式，设置与对话请求将走本地模拟。'
  };

  window.setTimeout(() => notify(modeEvent), 120);
  window.setTimeout(() => notify(chunkEvent), 260);
  window.setTimeout(() => notify(completedEvent), 420);
}

const listeners = new Set();

function emitEvent(payload) {
  listeners.forEach((listener) => listener(payload));
}

if (window.chrome && window.chrome.webview) {
  window.chrome.webview.addEventListener('message', (event) => {
    emitEvent(event.data);
  });
}

async function callBridge(methodName, ...args) {
  const bridge = window.chrome.webview.hostObjects.bridge;
  const result = bridge[methodName](...args);
  return await result;
}

export const hostBridge = {
  get isAvailable() {
    return hasWebView2Bridge();
  },

  onAgentEvent(handler) {
    listeners.add(handler);
    return () => listeners.delete(handler);
  },

  async getSettings() {
    if (this.isAvailable) {
      const raw = await callBridge('GetSettingsJson');
      return normalizeSettings(JSON.parse(raw || '{}'));
    }

    const cached = window.localStorage.getItem(SETTINGS_STORAGE_KEY);
    return normalizeSettings(cached ? JSON.parse(cached) : {});
  },

  async saveSettings(settings) {
    const normalized = normalizeSettings(settings);

    if (this.isAvailable) {
      const raw = await callBridge('SaveSettingsJson', JSON.stringify(normalized));
      const result = JSON.parse(raw || '{}');
      if (result.success === false) {
        throw new Error(result.message || '设置保存失败');
      }

      return normalizeSettings(result.settings || normalized);
    }

    window.localStorage.setItem(SETTINGS_STORAGE_KEY, JSON.stringify(normalized));
    return normalized;
  },

  async sendMessage(request) {
    if (this.isAvailable) {
      const payload = JSON.stringify(request);
      window.chrome.webview.hostObjects.bridge.SendMessageAsync(payload);
      return;
    }

    console.warn('WebView2 HostObject 不可用，已回退到本地模拟。');
    createMockResponse(request, emitEvent);
  },

  async navigateToParagraph(paragraphIndex) {
    if (!this.isAvailable) {
      return;
    }

    await callBridge('NavigateToParagraph', paragraphIndex);
  }
};
