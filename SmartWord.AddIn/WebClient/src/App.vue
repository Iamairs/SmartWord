<template>
  <div class="app-shell">
    <aside class="session-pane">
      <div class="session-header">
        <button class="primary" :disabled="state.busy" @click="createSession">新建会话</button>
      </div>
      <div class="session-list">
        <button
          v-for="session in state.sessions"
          :key="session.sessionId"
          class="session-item"
          :class="{ active: session.sessionId === state.activeSessionId }"
          :disabled="state.busy"
          @click="activateSession(session.sessionId)"
        >
          {{ session.title || "未命名会话" }}
        </button>
      </div>
    </aside>

    <main class="chat-pane">
      <header class="toolbar">
        <label class="field">
          <span>模型</span>
          <input v-model="state.modelOverride" type="text" :disabled="state.busy" />
        </label>

        <label class="field narrow">
          <span>Prompt</span>
          <input v-model="state.promptVersion" type="text" :disabled="state.busy" />
        </label>

        <label class="field narrow">
          <span>模式</span>
          <select v-model="state.modeLock" :disabled="state.busy">
            <option v-for="mode in state.modeOptions" :key="mode.key || 'auto'" :value="mode.key">
              {{ mode.label }}
            </option>
          </select>
        </label>
      </header>

      <section class="message-area">
        <div v-if="state.messages.length === 0" class="placeholder">
          暂无消息，输入内容后发送。
        </div>
        <article v-for="(message, idx) in state.messages" :key="idx" class="bubble" :class="bubbleClass(message.role)">
          <h4>{{ roleLabel(message.role) }}</h4>
          <pre>{{ message.content || "" }}</pre>
        </article>
      </section>

      <footer class="composer">
        <textarea
          ref="inputRef"
          v-model="state.input"
          :disabled="state.busy"
          placeholder="请输入你的问题或操作指令…"
          @keydown="onInputKeydown"
        />
        <div class="actions">
          <button class="primary" :disabled="state.busy" @click="submitTurn">发送</button>
          <button class="accent" :disabled="state.busy || !state.requireConfirm" @click="applyAction">确认执行</button>
          <button :disabled="state.busy || !state.requireConfirm" @click="cancelAction">取消</button>
        </div>
        <p class="status">{{ state.status }}</p>
      </footer>
    </main>
  </div>
</template>

<script setup>
import { onBeforeUnmount, onMounted, reactive, ref } from "vue";

const inputRef = ref(null);
const state = reactive({
  busy: false,
  sessions: [],
  messages: [],
  activeSessionId: "",
  pendingActionId: "",
  requireConfirm: false,
  modeOptions: [
    { key: "", label: "自动" },
    { key: "qa", label: "问答" },
    { key: "writing", label: "写作" },
    { key: "processing", label: "处理" },
    { key: "execute", label: "执行" }
  ],
  modeLock: "",
  modelOverride: "",
  promptVersion: "",
  input: "",
  status: "初始化中，请稍候..."
});

const rpc = createHostRpc();

window.smartwordFocusInput = () => {
  if (inputRef.value) {
    inputRef.value.focus();
  }
};

onMounted(async () => {
  await bootstrapAsync();
});

onBeforeUnmount(() => {
  try {
    delete window.smartwordFocusInput;
  } catch (_) {
    // 无需处理删除失败。
  }
});

async function bootstrapAsync() {
  if (!rpc.isAvailable()) {
    state.status = "当前页面需在 SmartWord Word 插件中运行。";
    return;
  }

  await runBusyAsync(async () => {
    const configPayload = await rpc.request("app.getConfig", {});
    applyConfig(configPayload);

    const sessionsPayload = await rpc.request("sessions.load", {});
    applySessionsPayload(sessionsPayload);
    state.status = state.sessions.length === 0 ? "暂无会话，请新建或直接发送消息。" : "会话加载完成。";
  }, "初始化失败");
}

function applyConfig(payload) {
  const models = Array.isArray(payload?.availableModels) ? payload.availableModels : [];
  state.modelOverride = payload?.defaultModel || models[0] || "";
  state.promptVersion = payload?.defaultPromptVersion || "";

  if (Array.isArray(payload?.modeOptions) && payload.modeOptions.length > 0) {
    state.modeOptions = payload.modeOptions;
  }
}

function applySessionsPayload(payload) {
  const sessions = Array.isArray(payload?.sessions) ? payload.sessions : [];
  state.sessions = sessions;

  const targetSessionId =
    payload?.activeSessionId ||
    state.activeSessionId ||
    (sessions.length > 0 ? sessions[0].sessionId || "" : "");
  state.activeSessionId = targetSessionId || "";

  const active = sessions.find((item) => item.sessionId === state.activeSessionId);
  state.messages = active && Array.isArray(active.messages) ? active.messages : [];
}

async function createSession() {
  await runBusyAsync(async () => {
    const payload = await rpc.request("sessions.create", {});
    applySessionsPayload(payload);
    state.pendingActionId = "";
    state.requireConfirm = false;
    state.status = "已创建新会话。";
  }, "新建会话失败");
}

async function activateSession(sessionId) {
  if (!sessionId || sessionId === state.activeSessionId) {
    return;
  }

  await runBusyAsync(async () => {
    const payload = await rpc.request("sessions.activate", { sessionId });
    applySessionsPayload(payload);
    state.pendingActionId = "";
    state.requireConfirm = false;
    state.status = "已切换会话。";
  }, "切换会话失败");
}

async function submitTurn() {
  const message = (state.input || "").trim();
  if (!message) {
    state.status = "请输入内容后再发送。";
    return;
  }

  await runBusyAsync(async () => {
    const payload = await rpc.request("turn.submit", {
      sessionId: state.activeSessionId,
      userMessage: message,
      modelOverride: state.modelOverride,
      promptVersion: state.promptVersion,
      modeLock: state.modeLock
    });

    applySessionsPayload(payload);
    const result = payload?.result || {};
    state.pendingActionId = result.pendingActionId || "";
    state.requireConfirm = !!result.requiresUserConfirmation && !!state.pendingActionId;
    state.status = state.requireConfirm
      ? `模式：${modeLabel(result.resolvedMode)}，已生成建议，请点击“确认执行”。`
      : `模式：${modeLabel(result.resolvedMode)}，已返回结果。`;
    state.input = "";
  }, "发送失败");
}

async function applyAction() {
  if (!state.activeSessionId || !state.pendingActionId) {
    state.status = "当前没有待执行动作。";
    return;
  }

  await runBusyAsync(async () => {
    const payload = await rpc.request("action.apply", {
      sessionId: state.activeSessionId,
      actionId: state.pendingActionId
    });

    applySessionsPayload(payload);
    const result = payload?.result || {};
    state.pendingActionId = "";
    state.requireConfirm = false;
    state.status = result.message || "执行完成。";
  }, "执行失败");
}

async function cancelAction() {
  state.pendingActionId = "";
  state.requireConfirm = false;
  state.status = "已取消待执行动作。";
  try {
    await rpc.request("action.cancelLocal", {});
  } catch (_) {
    // 取消动作属于本地行为，后端通知失败不影响用户。
  }
}

function onInputKeydown(event) {
  if (event.key === "Enter" && !event.shiftKey) {
    event.preventDefault();
    submitTurn();
  }
}

function roleLabel(role) {
  return String(role || "").toLowerCase() === "user" ? "你" : "SmartWord";
}

function bubbleClass(role) {
  return String(role || "").toLowerCase() === "user" ? "user" : "assistant";
}

function modeLabel(modeKey) {
  const key = modeKey || "writing";
  const found = state.modeOptions.find((item) => item.key === key);
  return found ? found.label : "写作";
}

async function runBusyAsync(callback, actionLabel) {
  if (state.busy) {
    return;
  }

  state.busy = true;
  state.status = "处理中，请稍候...";
  try {
    await callback();
  } catch (error) {
    state.status = `${actionLabel}：${normalizeErrorMessage(error)}`;
  } finally {
    state.busy = false;
  }
}

function normalizeErrorMessage(error) {
  if (!error) {
    return "未知错误";
  }

  if (typeof error === "string") {
    return error;
  }

  return error.message || "未知错误";
}

function createHostRpc() {
  const pending = new Map();
  let sequence = 1;
  const hasWebView = !!(window.chrome && window.chrome.webview);

  if (hasWebView) {
    window.chrome.webview.addEventListener("message", (event) => {
      const response = event?.data || {};
      const requestId = response.requestId || "";
      if (!requestId || !pending.has(requestId)) {
        return;
      }

      const waiter = pending.get(requestId);
      pending.delete(requestId);

      if (response.success) {
        waiter.resolve(response.payload || {});
      } else {
        const message = response?.error?.message || "请求失败";
        waiter.reject(new Error(message));
      }
    });
  }

  function isAvailable() {
    return hasWebView;
  }

  function request(method, payload) {
    if (!hasWebView) {
      return Promise.reject(new Error("WebView2 通道不可用。"));
    }

    const requestId = `req_${Date.now()}_${sequence++}`;
    const requestEnvelope = {
      version: "1.0",
      requestId,
      method,
      payload: payload || {}
    };

    return new Promise((resolve, reject) => {
      pending.set(requestId, { resolve, reject });
      try {
        window.chrome.webview.postMessage(requestEnvelope);
      } catch (error) {
        pending.delete(requestId);
        reject(error);
      }
    });
  }

  return {
    isAvailable,
    request
  };
}
</script>

