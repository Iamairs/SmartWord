<template>
  <div class="sw-shell" :class="{ compact: state.layout.compact, generating: state.generating }">
    <header class="sw-brandbar">
      <div class="brand-left">
        <button
          class="ghost icon-only"
          :disabled="isUiLocked"
          title="切换会话栏"
          @click="toggleSessionDrawer"
        >
          ☰
        </button>
        <div class="brand-title-wrap">
          <h1>SmartWord</h1>
          <p>智能文档助理</p>
        </div>
      </div>

      <div class="brand-status">
        <span class="status-dot" :class="state.generating ? 'warn' : 'ok'"></span>
        <span>{{ state.generating ? "生成中" : "就绪" }}</span>
      </div>
    </header>

    <div class="sw-layout">
      <aside class="session-rail" :class="{ visible: !state.layout.compact || state.layout.sessionDrawerOpen }">
        <div class="session-actions">
          <button class="primary" :disabled="isUiLocked" @click="createSession">新建会话</button>
        </div>

        <div class="session-list">
          <button
            v-for="session in state.sessions"
            :key="session.sessionId"
            class="session-card"
            :class="{ active: session.sessionId === state.activeSessionId }"
            :disabled="isUiLocked"
            @click="activateSession(session.sessionId)"
          >
            <div class="title-line">
              <h3>{{ session.title || "未命名会话" }}</h3>
              <span
                v-if="session.latestPendingAction && session.latestPendingAction.actionId"
                class="pill warn"
              >
                待执行
              </span>
            </div>
            <p>{{ formatTime(session.updatedAtUtc) }}</p>
          </button>
        </div>
      </aside>

      <main class="workspace">
        <section class="timeline">
          <div v-if="displayMessages.length === 0" class="empty-state">
            <h3>开始你的第一条指令</h3>
            <p>可直接提问、改写或下达执行指令。问答模式将自动检索文档上下文。</p>
          </div>

          <article
            v-for="(message, idx) in displayMessages"
            :key="messageKey(message, idx)"
            class="msg"
            :class="bubbleClass(message.role)"
          >
            <header class="msg-head">
              <h4>{{ roleLabel(message.role) }}</h4>
              <time>{{ formatTime(message.timestampUtc) }}</time>
            </header>

            <div v-if="message.isThinking" class="thinking-box">
              <div class="dot-wave" aria-hidden="true">
                <span></span>
                <span></span>
                <span></span>
              </div>
              <p>{{ message.content }}</p>
            </div>

            <div v-else class="msg-body">
              <template v-for="(segment, segIndex) in messageSegments(message, idx)" :key="segIndex">
                <p v-if="segment.type === 'text'" class="msg-text">{{ segment.text }}</p>
                <section v-else-if="segment.type === 'mode'" class="mode-block">
                  <div class="mode-head">
                    <p class="mode-line">模式：{{ segment.modeName }}</p>
                    <button
                      v-if="segment.reason && segment.autoCollapse"
                      class="thought-toggle"
                      type="button"
                      @click="toggleModeReason(message, idx)"
                    >
                      {{ isModeReasonCollapsed(message, idx) ? "显示思路 ∨" : "隐藏思路 ∧" }}
                    </button>
                  </div>
                  <p
                    v-if="segment.reason && (!segment.autoCollapse || !isModeReasonCollapsed(message, idx))"
                    class="mode-reason"
                  >
                    {{ segment.reason }}
                  </p>
                </section>
                <section v-else-if="segment.type === 'preview'" class="preview-block">
                  <header class="preview-head">{{ segment.title }}</header>
                  <pre class="preview-body">{{ segment.text }}</pre>
                </section>
                <div v-else class="code-block" :class="{ vba: segment.lang === 'vba' }">
                  <details class="code-fold" :open="!shouldFoldCodeBlock(segment)">
                    <summary class="code-header">
                      <span>{{ segment.lang || "code" }}</span>
                      <span class="fold-hint">{{ shouldFoldCodeBlock(segment) ? "可折叠" : "代码预览" }}</span>
                    </summary>
                    <pre><code>{{ segment.text }}</code></pre>
                  </details>
                </div>
              </template>
            </div>

            <div v-if="showApplyButtonForMessage(message, idx)" class="msg-actions">
              <span class="action-hint">点击“确认执行”后才会修改文档</span>
              <button class="accent action-mini" :disabled="isUiLocked || !canApply" @click="applyAction">确认执行</button>
            </div>
          </article>
        </section>

        <footer class="composer">
          <div class="composer-inline">
            <input
              ref="inputRef"
              v-model="state.input"
              class="composer-input"
              type="text"
              :disabled="isInputDisabled"
              placeholder="请输入问题、改写意图或执行指令…"
              @keydown="onInputKeydown"
            />
          </div>

          <div class="composer-options">
            <select
              class="option-control"
              v-model="state.modelOverride"
              title="模型"
              :disabled="isUiLocked || state.availableModels.length === 0"
            >
              <option v-if="state.availableModels.length === 0" :value="state.modelOverride || ''">
                {{ state.modelOverride || "默认模型" }}
              </option>
              <option v-for="model in state.availableModels" :key="model" :value="model">
                {{ model }}
              </option>
            </select>

            <input
              class="option-control"
              v-model="state.promptVersion"
              type="text"
              title="Prompt"
              placeholder="Prompt"
              :disabled="isUiLocked"
            />

            <select class="option-control" v-model="state.modeLock" title="模式" :disabled="isUiLocked">
              <option v-for="mode in state.modeOptions" :key="mode.key || 'auto'" :value="mode.key">
                {{ mode.label }}
              </option>
            </select>

            <button
              class="primary option-send"
              :class="{ danger: state.generating }"
              :disabled="isSubmitDisabled"
              @click="submitOrCancelTurn"
            >
              {{ state.generating ? "取消生成" : "发送" }}
            </button>
          </div>

          <p class="status-line">{{ state.status }}</p>
        </footer>
      </main>
    </div>
  </div>
</template>

<script setup>
import { computed, onBeforeUnmount, onMounted, reactive, ref } from "vue";

const inputRef = ref(null);
const stageTimerRef = ref(null);

const state = reactive({
  busy: false,
  generating: false,
  cancelling: false,
  sessions: [],
  messages: [],
  activeSessionId: "",
  pendingActionId: "",
  requireConfirm: false,
  pendingActionMeta: {},
  uiHints: {
    canApply: false,
    canCancel: false,
    warningText: "",
    checks: []
  },
  availableModels: [],
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
  status: "初始化中，请稍候...",
  pendingTurnId: "",
  thinkingMessage: null,
  thinkingStages: [],
  thinkingStageIndex: 0,
  modeReasonCollapsed: {},
  layout: {
    compact: false,
    sessionDrawerOpen: true
  }
});

const rpc = createHostRpc();
const APPLY_HINT_PATTERN = /确认执行[\s\S]*修改文档/;
const MODE_LINE_PATTERN = /^模式：([^\r\n（(]+?)(?:[（(]([\s\S]*?)[）)])?\s*$/;
const PREVIEW_HEADER_PATTERNS = ["建议内容预览", "建议改写如下"];

const isUiLocked = computed(() => state.busy || state.generating || state.cancelling);
const isInputDisabled = computed(() => state.busy || state.generating);
const canApply = computed(() => !!state.uiHints.canApply && state.requireConfirm && !!state.pendingActionId);
const displayMessages = computed(() => {
  const list = Array.isArray(state.messages) ? [...state.messages] : [];
  if (state.thinkingMessage) {
    list.push(state.thinkingMessage);
  }

  return list;
});

const lastAssistantMessageIndex = computed(() => {
  for (let i = state.messages.length - 1; i >= 0; i--) {
    const role = String(state.messages[i]?.role || "").toLowerCase();
    if (role === "assistant") {
      return i;
    }
  }

  return -1;
});

const isSubmitDisabled = computed(() => {
  if (state.generating) {
    return state.cancelling || !state.pendingTurnId;
  }

  return state.busy || !(state.input || "").trim();
});

window.smartwordFocusInput = () => {
  if (inputRef.value) {
    inputRef.value.focus();
  }
};

onMounted(async () => {
  updateLayout();
  window.addEventListener("resize", updateLayout);
  await bootstrapAsync();
});

onBeforeUnmount(() => {
  window.removeEventListener("resize", updateLayout);
  stopThinkingIndicator();
  try {
    delete window.smartwordFocusInput;
  } catch (_) {
    // 忽略删除失败。
  }
});

function updateLayout() {
  const compact = window.innerWidth < 860;
  state.layout.compact = compact;
  if (!compact) {
    state.layout.sessionDrawerOpen = true;
  } else if (state.layout.sessionDrawerOpen !== false) {
    state.layout.sessionDrawerOpen = false;
  }
}

function toggleSessionDrawer() {
  if (!state.layout.compact) {
    return;
  }

  state.layout.sessionDrawerOpen = !state.layout.sessionDrawerOpen;
}

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
  state.availableModels = models;
  state.modelOverride = payload?.defaultModel || models[0] || state.modelOverride || "";
  state.promptVersion = payload?.defaultPromptVersion || "";

  if (!state.modelOverride && state.availableModels.length > 0) {
    state.modelOverride = state.availableModels[0];
  }

  if (Array.isArray(payload?.modeOptions) && payload.modeOptions.length > 0) {
    state.modeOptions = payload.modeOptions;
  }
}

function applySessionsPayload(payload) {
  const sessions = Array.isArray(payload?.sessions) ? payload.sessions : [];
  state.sessions = sessions;

  const targetSessionId = payload?.activeSessionId || state.activeSessionId || (sessions.length > 0 ? sessions[0].sessionId || "" : "");
  state.activeSessionId = targetSessionId || "";

  const active = sessions.find((item) => item.sessionId === state.activeSessionId) || null;
  state.messages = active && Array.isArray(active.messages) ? active.messages : [];

  const fromPayload = hasActionMeta(payload?.pendingActionMeta) ? payload.pendingActionMeta : null;
  const fromSession = hasActionMeta(active?.latestPendingAction) ? active.latestPendingAction : null;
  state.pendingActionMeta = fromPayload || fromSession || {};
  state.pendingActionId = state.pendingActionMeta?.actionId || "";
  state.requireConfirm = !!state.pendingActionId;

  applyUiHints(payload?.uiHints);
}

function applyUiHints(hints) {
  if (hints && typeof hints === "object") {
    state.uiHints = {
      canApply: !!hints.canApply,
      canCancel: !!hints.canCancel,
      warningText: hints.warningText || "",
      checks: Array.isArray(hints.checks) ? hints.checks : defaultChecks()
    };
    return;
  }

  state.uiHints = {
    canApply: !!state.pendingActionId && state.requireConfirm,
    canCancel: !!state.pendingActionId,
    warningText: state.pendingActionId ? "" : "当前无待执行动作，可继续提问或下达新指令。",
    checks: defaultChecks()
  };
}

function defaultChecks() {
  return [
    { key: "pending", label: "存在待执行动作", passed: !!state.pendingActionId },
    { key: "confirm", label: "当前轮次需要确认", passed: !!state.requireConfirm && !!state.pendingActionId },
    { key: "risk", label: "已评估执行风险", passed: !state.pendingActionId || !!state.pendingActionMeta?.riskLevel }
  ];
}

async function createSession() {
  await runBusyAsync(async () => {
    const payload = await rpc.request("sessions.create", {});
    applySessionsPayload(payload);
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
    state.status = "已切换会话。";

    if (state.layout.compact) {
      state.layout.sessionDrawerOpen = false;
    }
  }, "切换会话失败");
}

async function submitOrCancelTurn() {
  if (state.generating) {
    await cancelTurnAsync();
    return;
  }

  await submitTurnAsync();
}

async function submitTurnAsync() {
  const message = (state.input || "").trim();
  if (!message) {
    state.status = "请输入内容后再发送。";
    return;
  }

  if (state.busy || state.generating) {
    return;
  }

  const turnId = `turn_${Date.now()}_${Math.floor(Math.random() * 10000)}`;
  state.pendingTurnId = turnId;
  state.generating = true;
  state.cancelling = false;
  state.input = "";

  startThinkingIndicator(turnId);
  state.status = state.thinkingStages[0] || "正在生成回答...";

  try {
    const payload = await rpc.request("turn.submit", {
      sessionId: state.activeSessionId,
      turnId,
      userMessage: message,
      modelOverride: state.modelOverride,
      promptVersion: state.promptVersion,
      modeLock: state.modeLock 
    });

    applySessionsPayload(payload);

    const result = payload?.result || {};
    const cancelled = !!result.cancelled;
    if (cancelled) {
      state.status = result.message || "已取消本轮生成。";
      return;
    }

    state.pendingActionId = result.pendingActionId || state.pendingActionMeta?.actionId || "";
    state.requireConfirm = !!result.requiresUserConfirmation && !!state.pendingActionId;

    const resultMeta = hasActionMeta(result.pendingActionMeta) ? result.pendingActionMeta : null;
    if (resultMeta) {
      state.pendingActionMeta = resultMeta;
      state.pendingActionId = resultMeta.actionId || state.pendingActionId;
    }

    applyUiHints(result?.uiHints || payload?.uiHints);

    state.status = state.requireConfirm
      ? `模式：${modeLabel(result.resolvedMode)}，建议已生成。`
      : `模式：${modeLabel(result.resolvedMode)}，结果已返回。`;
  } catch (error) {
    state.status = `发送失败：${normalizeErrorMessage(error)}`;
  } finally {
    stopThinkingIndicator();
    state.generating = false;
    state.cancelling = false;
    state.pendingTurnId = "";
  }
}

async function cancelTurnAsync() {
  if (!state.generating || !state.pendingTurnId) {
    return;
  }

  state.cancelling = true;
  state.status = "正在取消当前生成...";

  try {
    const result = await rpc.request("turn.cancel", {
      sessionId: state.activeSessionId,
      turnId: state.pendingTurnId
    });

    if (result?.cancelled) {
      state.status = result.message || "已取消本轮生成。";
    } else {
      state.status = result?.message || "当前没有可取消的生成任务。";
    }
  } catch (error) {
    state.status = `取消失败：${normalizeErrorMessage(error)}`;
  } finally {
    state.cancelling = false;
  }
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
    state.pendingActionMeta = {};
    applyUiHints(result?.uiHints || payload?.uiHints);
    state.status = result.message || "执行完成。";
  }, "执行失败");
}

function startThinkingIndicator(turnId) {
  stopThinkingIndicator();

  state.thinkingStages = resolveThinkingStages();
  state.thinkingStageIndex = 0;

  const firstStage = state.thinkingStages[0] || "正在生成回答...";
  state.thinkingMessage = {
    role: "assistant",
    content: firstStage,
    timestampUtc: new Date().toISOString(),
    isThinking: true,
    turnId
  };

  stageTimerRef.value = window.setInterval(() => {
    if (!state.generating || !state.thinkingMessage) {
      return;
    }

    if (!state.thinkingStages || state.thinkingStages.length === 0) {
      return;
    }

    state.thinkingStageIndex = (state.thinkingStageIndex + 1) % state.thinkingStages.length;
    const stage = state.thinkingStages[state.thinkingStageIndex];
    state.thinkingMessage.content = stage;
    state.status = stage;
  }, 1300);
}

function stopThinkingIndicator() {
  if (stageTimerRef.value) {
    clearInterval(stageTimerRef.value);
    stageTimerRef.value = null;
  }

  state.thinkingMessage = null;
  state.thinkingStages = [];
  state.thinkingStageIndex = 0;
}

function resolveThinkingStages() {
  const mode = String(state.modeLock || "").toLowerCase();
  if (mode === "qa") {
    return ["正在检索文档…", "正在生成回答…", "正在整理输出…"];
  }

  return ["正在分析指令…", "正在生成内容…", "正在整理输出…"];
}

function shouldFoldCodeBlock(segment) {
  if (!segment || segment.type !== "code") {
    return false;
  }

  const text = String(segment.text || "");
  if (!text) {
    return false;
  }

  const lineCount = text.split(/\r?\n/).length;
  return lineCount > 16 || text.length > 900;
}

function showApplyButtonForMessage(message, idx) {
  const role = String(message?.role || "").toLowerCase();
  if (role !== "assistant") {
    return false;
  }

  if (idx !== lastAssistantMessageIndex.value) {
    return false;
  }

  if (!canApply.value || !state.pendingActionId || !state.requireConfirm) {
    return false;
  }

  return true;
}

function isModeReasonCollapsed(message, idx) {
  const key = messageKey(message, idx);
  return state.modeReasonCollapsed[key] !== false;
}

function toggleModeReason(message, idx) {
  const key = messageKey(message, idx);
  state.modeReasonCollapsed[key] = !isModeReasonCollapsed(message, idx);
}

function messageSegments(message, idx) {
  const segments = parseMessageSegments(message?.content || "");
  const shouldHideApplyHint = showApplyButtonForMessage(message, idx);

  return segments.flatMap((segment) => {
    if (segment.type !== "text") {
      return [segment];
    }

    const cleaned = shouldHideApplyHint ? removeApplyHintLines(segment.text || "") : String(segment.text || "");
    return parseTextMessageSegments(cleaned);
  });
}

function removeApplyHintLines(rawText) {
  return String(rawText || "")
    .split(/\r?\n/)
    .filter((line) => !APPLY_HINT_PATTERN.test(line.trim()))
    .join("\n")
    .trim();
}

function parseTextMessageSegments(rawText) {
  let text = String(rawText || "").trim();
  if (!text) {
    return [];
  }

  const sections = [];
  const lines = text.split(/\r?\n/);
  const firstLine = String(lines[0] || "").trim();
  const modeMatch = firstLine.match(MODE_LINE_PATTERN);

  if (modeMatch) {
    const modeName = String(modeMatch[1] || "").trim();
    const reason = String(modeMatch[2] || "").trim();
    sections.push({
      type: "mode",
      modeName: modeName || "自动",
      reason,
      autoCollapse: shouldAutoCollapseModeReason(reason)
    });

    lines.shift();
    text = lines.join("\n").trim();
  }

  if (!text) {
    return sections;
  }

  const textLines = text.split(/\r?\n/);
  const previewLineIndex = textLines.findIndex((line) => isPreviewHeaderLine(line));
  if (previewLineIndex < 0) {
    sections.push({ type: "text", text });
    return sections;
  }

  const beforePreview = textLines.slice(0, previewLineIndex).join("\n").trim();
  if (beforePreview) {
    sections.push({ type: "text", text: beforePreview });
  }

  const previewTitle = normalizePreviewTitle(textLines[previewLineIndex]);
  const previewLines = textLines.slice(previewLineIndex + 1);
  const hintIndex = previewLines.findIndex((line) => APPLY_HINT_PATTERN.test(String(line || "").trim()));
  const previewBodyLines = hintIndex >= 0 ? previewLines.slice(0, hintIndex) : previewLines;
  const trailingLines = hintIndex >= 0 ? previewLines.slice(hintIndex) : [];
  const previewText = previewBodyLines.join("\n").trim();
  sections.push({
    type: "preview",
    title: previewTitle,
    text: previewText || "（无预览内容）"
  });

  const trailingText = trailingLines.join("\n").trim();
  if (trailingText) {
    sections.push({ type: "text", text: trailingText });
  }

  return sections;
}

function isPreviewHeaderLine(line) {
  const value = String(line || "").trim().replace(/[：:]\s*$/, "");
  return PREVIEW_HEADER_PATTERNS.includes(value);
}

function normalizePreviewTitle(line) {
  const value = String(line || "").trim().replace(/[：:]\s*$/, "");
  if (value === "建议改写如下") {
    return "改写预览";
  }

  return "内容预览";
}

function shouldAutoCollapseModeReason(reason) {
  const text = String(reason || "").trim();
  if (!text) {
    return false;
  }

  return text.indexOf("用户已锁定模式") < 0;
}

function sanitizeMalformedCodeBlocks(rawText) {
    let text = String(rawText || "");
    if (!text) return text;

    // 修复大模型常见的 "空闭合代码块 + 重复声明语言" 的幻觉格式
    // 匹配场景例如：
    // ```VBA
    // ```
    // vba
    // 替换为标准的开头：
    // ```vba
    text = text.replace(/```([a-zA-Z0-9_-]*)\s*```\s*([a-zA-Z0-9_-]+)?/g, (match, firstLang, secondLang) => {
        // 优先取有值的语言标识，忽略大小写
        const lang = (firstLang || secondLang || "").toLowerCase();
        return `\`\`\`${lang}\n`;
    });

    return text;
}

function parseMessageSegments(content) {
  
  const text = sanitizeMalformedCodeBlocks(content);
  if (!text) {
    return [{ type: "text", text: "" }];
  }

  const segments = [];
  const fenceRegex = /```([a-zA-Z0-9_-]*)[ \t]*\r?\n?([\s\S]*?)```/g;
  let cursor = 0;
  let match;

  while ((match = fenceRegex.exec(text)) !== null) {
    const blockStart = match.index;
    if (blockStart > cursor) {
      pushTextSegment(segments, text.slice(cursor, blockStart));
    }

    const language = String(match[1] || "").trim().toLowerCase();
    const code = String(match[2] || "").replace(/^\r?\n/, "").replace(/\s+$/, "");
    segments.push({ type: "code", lang: language || "code", text: code });
    cursor = fenceRegex.lastIndex;
  }

  if (cursor < text.length) {
    pushTailSegment(segments, text.slice(cursor));
  }

  if (segments.length === 0) {
    return [{ type: "text", text }];
  }

  return segments;
}

function pushTextSegment(segments, rawText) {
  const text = String(rawText || "").trim();
  if (!text) {
    return;
  }

  segments.push({ type: "text", text });
}

function pushTailSegment(segments, rawText) {
  const tail = String(rawText || "");
  const fenceIndex = tail.indexOf("```");
  if (fenceIndex < 0) {
    pushTextSegment(segments, tail);
    return;
  }

  if (fenceIndex > 0) {
    pushTextSegment(segments, tail.slice(0, fenceIndex));
  }

  const codePayload = tail.slice(fenceIndex + 3);
  let language = "code";
  let body = codePayload;

  const languageMatch = codePayload.match(/^([a-zA-Z0-9_-]+)(?:[ \t]+|\r?\n)([\s\S]*)$/);
  if (languageMatch) {
    language = languageMatch[1].toLowerCase();
    body = languageMatch[2];
  }

  segments.push({
    type: "code",
    lang: language || "code",
    text: String(body || "").replace(/^\r?\n/, "").replace(/\s+$/, "")
  });
}

function onInputKeydown(event) {
  if (event.key === "Enter") {
    event.preventDefault();
    submitOrCancelTurn();
  }
}

function hasActionMeta(meta) {
  return !!(meta && typeof meta === "object" && meta.actionId);
}

function roleLabel(role) {
  return String(role || "").toLowerCase() === "user" ? "你" : "SmartWord";
}

function bubbleClass(role) {
  return String(role || "").toLowerCase() === "user" ? "user" : "assistant";
}

function messageKey(message, idx) {
  if (message?.isThinking) {
    return `thinking_${message.turnId || idx}`;
  }

  return `${message?.role || "role"}_${message?.timestampUtc || idx}_${idx}`;
}

function modeLabel(modeKey) {
  const key = modeKey || "writing";
  const found = state.modeOptions.find((item) => item.key === key);
  return found ? found.label : "写作";
}

function formatTime(iso) {
  if (!iso) {
    return "";
  }

  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) {
    return "";
  }

  return `${date.getHours().toString().padStart(2, "0")}:${date
    .getMinutes()
    .toString()
    .padStart(2, "0")}`;
}

async function runBusyAsync(callback, actionLabel) {
  if (state.busy || state.generating) {
    return;
  }

  state.busy = true;
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
