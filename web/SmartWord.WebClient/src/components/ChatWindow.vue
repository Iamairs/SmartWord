<template>
  <div class="chat-window">
    <header class="chat-header">
      <div>
        <p class="eyebrow">SmartWord</p>
        <h1>文档副驾</h1>
        <p class="settings-summary">{{ settingsSummary }}</p>
      </div>
      <div class="header-actions">
        <button class="ghost-button" type="button" @click="settingsStore.togglePanel()">
          {{ settingsStore.isPanelOpen ? '收起设置' : '设置' }}
        </button>
        <span class="mode-pill">{{ modeBadgeText }}</span>
      </div>
    </header>

    <section v-if="settingsStore.isPanelOpen" class="settings-panel">
      <div class="settings-panel__header">
        <div>
          <h2>连接设置</h2>
          <p>保存后，新请求会直接使用最新配置。</p>
        </div>
        <button class="ghost-button ghost-button--small" type="button" @click="settingsStore.closePanel()">
          关闭
        </button>
      </div>

      <div class="settings-grid">
        <label class="settings-field">
          <span>默认 Base URL</span>
          <input v-model.trim="settingsStore.form.baseUrl" type="text" />
        </label>

        <label class="settings-field">
          <span>默认 API Key</span>
          <input v-model.trim="settingsStore.form.apiKey" type="password" autocomplete="off" />
        </label>

        <label class="settings-field">
          <span>轻量 Base URL</span>
          <input v-model.trim="settingsStore.form.baseUrlLight" type="text" />
        </label>

        <label class="settings-field">
          <span>轻量 API Key</span>
          <input v-model.trim="settingsStore.form.apiKeyLight" type="password" autocomplete="off" />
        </label>

        <label class="settings-field">
          <span>轻量模型</span>
          <input v-model.trim="settingsStore.form.lightModel" type="text" />
        </label>

        <label class="settings-field">
          <span>重量 Base URL</span>
          <input v-model.trim="settingsStore.form.baseUrlHeavy" type="text" />
        </label>

        <label class="settings-field">
          <span>重量 API Key</span>
          <input v-model.trim="settingsStore.form.apiKeyHeavy" type="password" autocomplete="off" />
        </label>

        <label class="settings-field">
          <span>重量模型</span>
          <input v-model.trim="settingsStore.form.heavyModel" type="text" />
        </label>

        <label class="settings-field">
          <span>执行权限</span>
          <select v-model="settingsStore.form.permissionMode">
            <option v-for="option in permissionOptions" :key="option.value" :value="option.value">
              {{ option.label }}
            </option>
          </select>
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
      </div>

      <div class="settings-panel__footer">
        <p
          v-if="settingsStore.saveMessage"
          class="settings-message"
          :class="`settings-message--${settingsStore.saveMessageType}`"
        >
          {{ settingsStore.saveMessage }}
        </p>
        <button
          class="send-button send-button--full"
          type="button"
          :disabled="settingsStore.isSaving"
          @click="saveSettings"
        >
          {{ settingsStore.isSaving ? '保存中...' : '保存设置' }}
        </button>
      </div>
    </section>

    <section class="message-list" ref="messageListRef" @click="handleMessageListClick">
      <ThoughtActionTrace :tool-calls="chatStore.activeToolCalls" />
      <ContentPreviewPanel
        v-if="chatStore.pendingConfirmation"
        :confirmation="chatStore.pendingConfirmation"
        @confirm="confirmPendingToolCall"
        @skip="skipPendingToolCall"
        @cancel="cancelCurrentRun"
      />
      <ChangesSummaryPanel
        v-if="chatStore.completedTaskChanges.length"
        :changes="chatStore.completedTaskChanges"
        @navigate="navigateToParagraph"
      />
      <TodoBoardRecoveryPanel
        v-if="chatStore.pendingTodoRecovery"
        :recovery="chatStore.pendingTodoRecovery"
        @recover="submitTodoRecoveryDecision"
      />
      <TodoBoardPausePanel
        v-if="chatStore.pendingTodoPause"
        :pause="chatStore.pendingTodoPause"
        @resume="resumePausedTodoRun"
      />
      <TodoBoardPanel
        v-if="shouldShowTodoBoard"
        :board="chatStore.activeTodoBoard"
        :current-todo-id="chatStore.currentTodoId"
        :notice="chatStore.todoBoardNotice"
      />

      <article
        v-for="message in chatStore.messages"
        :key="message.id"
        class="message-card"
        :class="`message-card--${message.role}`"
      >
        <div class="message-meta">
          <span>{{ message.role === 'user' ? '你' : 'SmartWord' }}</span>
          <span>{{ message.timestamp }}</span>
        </div>
        <div class="message-body" v-html="renderMessage(message)"></div>
      </article>

      <!-- Plan 模式：采访问题面板 -->
      <div v-if="chatStore.pendingQuestion" class="question-panel">
        <p class="question-panel__text">{{ chatStore.pendingQuestion.question }}</p>
        <div class="question-panel__options">
          <button
            v-for="(opt, idx) in chatStore.pendingQuestion.options"
            :key="idx"
            class="question-option-btn"
            type="button"
            @click="submitQuestionAnswer(opt)"
          >{{ opt }}</button>
        </div>
        <div class="question-panel__custom">
          <input
            v-model="customQuestionAnswer"
            type="text"
            placeholder="或输入自定义回答..."
            @keydown.enter="submitQuestionAnswer(customQuestionAnswer)"
          />
          <button type="button" class="send-button" @click="submitQuestionAnswer(customQuestionAnswer)">发送</button>
        </div>
        <button type="button" class="ghost-button ghost-button--full" @click="cancelCurrentRun">
          取消规划
        </button>
      </div>

      <!-- Plan 模式：执行计划面板 -->
      <div v-if="chatStore.activePlan && !chatStore.pendingQuestion" class="plan-panel">
        <p class="plan-panel__desc">{{ chatStore.activePlan.taskDescription }}</p>
        <ul class="plan-panel__todo">
          <li
            v-for="(item, idx) in chatStore.activePlan.todoList"
            :key="idx"
            :class="['todo-item', `todo-item--${item.status || 'pending'}`]"
          >{{ item.description || item }}</li>
        </ul>
        <div v-if="chatStore.activePlan.riskNotes && chatStore.activePlan.riskNotes.length" class="plan-panel__risks">
          <span v-for="(r, i) in chatStore.activePlan.riskNotes" :key="i" class="risk-note">{{ r }}</span>
        </div>
        <button class="send-button send-button--full" type="button" @click="executePlan">开始执行</button>
      </div>

      <div v-if="chatStore.isLoading" class="message-card message-card--assistant">
        <div class="message-meta">
          <span>SmartWord</span>
          <span>处理中</span>
        </div>
        <div class="message-body">
          <span class="typing-dot"></span>
          <span class="typing-dot"></span>
          <span class="typing-dot"></span>
        </div>
      </div>
    </section>

    <form class="composer" @submit.prevent="submitMessage">
      <label class="composer-label" for="chat-input">输入自然语言指令</label>
      <div class="mode-selector">
        <p class="mode-selector__label">运行模式</p>
        <div class="mode-selector__options">
          <button
            v-for="option in modeOptions"
            :key="option.value"
            class="mode-option"
            :class="{ 'mode-option--active': chatStore.currentMode === option.value }"
            type="button"
            :disabled="chatStore.isLoading"
            @click="chatStore.setMode(option.value)"
          >
            {{ option.label }}
          </button>
        </div>
      </div>
      <textarea
        id="chat-input"
        v-model.trim="draft"
        class="composer-input"
        rows="4"
        maxlength="3000"
        :disabled="chatStore.isLoading"
        :placeholder="composerPlaceholder"
      ></textarea>
      <div class="composer-footer">
        <p class="environment-hint">{{ environmentHint }}</p>
        <button
          v-if="chatStore.isLoading"
          class="ghost-button composer-cancel-button"
          type="button"
          :disabled="isCancelling"
          @click="cancelCurrentRun"
        >
          {{ isCancelling ? '取消中...' : '取消' }}
        </button>
        <button v-else class="send-button" type="submit" :disabled="isSubmitDisabled">
          发送
        </button>
      </div>
    </form>
  </div>
</template>

<script setup>
import { computed, nextTick, onMounted, onUnmounted, ref, watch } from 'vue';
import { marked } from 'marked';
import { useChatStore } from '../stores/chat';
import { useSettingsStore } from '../stores/settings';
import { hostBridge } from '../bridge/hostBridge';
import ChangesSummaryPanel from './ChangesSummaryPanel.vue';
import ContentPreviewPanel from './ContentPreviewPanel.vue';
import ThoughtActionTrace from './ThoughtActionTrace.vue';
import TodoBoardPanel from './TodoBoardPanel.vue';
import TodoBoardPausePanel from './TodoBoardPausePanel.vue';
import TodoBoardRecoveryPanel from './TodoBoardRecoveryPanel.vue';

const chatStore = useChatStore();
const settingsStore = useSettingsStore();
const draft = ref('');
const messageListRef = ref(null);
const customQuestionAnswer = ref('');
const isCancelling = ref(false);
let unsubscribeAgentEvent = null;
const modeOptions = [
  { value: 'ask', label: '对话交流' },
  { value: 'plan', label: '规划任务' },
  { value: 'agent', label: '自主执行' }
];
const permissionOptions = [
  { value: 'read_only', label: '只读模式' },
  { value: 'confirm_writes', label: '写入前确认' },
  { value: 'auto_safe_writes', label: '自动安全写入' },
  { value: 'full_auto', label: '全自动执行' }
];

marked.setOptions({
  breaks: true,
  gfm: true
});

const modeLabel = computed(() => {
  const labels = {
    ask: '对话交流',
    plan: '规划任务',
    agent: '自主执行'
  };

  return labels[chatStore.currentMode] || '对话交流';
});

const modeBadgeText = computed(() => {
  return `当前模式 · ${modeLabel.value}`;
});

const composerPlaceholder = computed(() => {
  const placeholders = {
    ask: '例如：总结当前文档的主要结构',
    plan: '例如：先规划一下这份报告应该如何修改',
    agent: '例如：直接帮我把标题层级统一一下'
  };

  return placeholders[chatStore.currentMode] || placeholders.ask;
});

const settingsSummary = computed(() => {
  if (!settingsStore.isLoaded) {
    return '正在加载连接设置...';
  }

  const permission = permissionOptions.find((item) => item.value === settingsStore.form.permissionMode);
  return `轻量：${settingsStore.form.lightModel} / 重量：${settingsStore.form.heavyModel} / ${permission?.label || '写入前确认'}`;
});

const shouldShowTodoBoard = computed(() => {
  if (!chatStore.todoBoardVisible || chatStore.currentMode !== 'agent' || !chatStore.activeTodoBoard) {
    return false;
  }

  return Array.isArray(chatStore.activeTodoBoard.items) && chatStore.activeTodoBoard.items.length > 0;
});

const environmentHint = computed(() => {
  return hostBridge.isAvailable
    ? '当前运行在 WebView2 环境，设置会保存到本地应用目录。'
    : '当前为浏览器预览模式，设置会暂存到本地 localStorage。';
});

const isSubmitDisabled = computed(() => {
  return chatStore.isLoading || draft.value.length === 0 || settingsStore.isLoading;
});

function renderMessage(message) {
  const html = marked.parse(message?.content || '');
  if (!message || message.role !== 'assistant') {
    return html;
  }

  return html.replace(/\[(\d+)\]/g, (fullMatch, rawRef) => {
    const ref = Number(rawRef);
    const citation = chatStore.findCitation(ref);
    if (!citation) {
      return fullMatch;
    }

    const title = `段落 ${citation.paragraphIndex}`;
    return `<button class="citation-anchor" type="button" data-citation-ref="${ref}" title="${title}">[${ref}]</button>`;
  });
}

async function saveSettings() {
  try {
    await settingsStore.saveSettings();
  } catch {
    // 错误信息已由 store 写入状态提示，这里不重复处理。
  }
}

async function submitMessage() {
  if (isSubmitDisabled.value) {
    return;
  }

  const request = {
    content: draft.value,
    manualMode: chatStore.currentMode,
    permissionMode: settingsStore.form.permissionMode,
    requireConfirmationForScripts: requireConfirmationForPermission(settingsStore.form.permissionMode),
    customInstructions: settingsStore.form.customInstructions
  };

  chatStore.appendUserMessage(draft.value);
  draft.value = '';
  chatStore.startLoading();

  try {
    await hostBridge.sendMessage(request);
  } catch (error) {
    chatStore.finishLoading();
    chatStore.appendAssistantMessage(`请求发送失败：${error.message || '未知错误'}`);
  }
}

async function respondToPendingToolCall(confirmed) {
  if (!chatStore.pendingConfirmation) {
    return;
  }

  chatStore.setPendingConfirmationSubmitting(true);
  try {
    await hostBridge.confirmToolCall(chatStore.pendingConfirmation.toolCallId, confirmed);
  } catch (error) {
    chatStore.appendAssistantMessage(`确认写操作失败：${error.message || '未知错误'}`);
    chatStore.clearPendingConfirmation();
  } finally {
    chatStore.setPendingConfirmationSubmitting(false);
  }
}

async function confirmPendingToolCall() {
  await respondToPendingToolCall(true);
}

async function skipPendingToolCall() {
  await respondToPendingToolCall(false);
}

async function navigateToParagraph(paragraphIndex) {
  await hostBridge.navigateToParagraph(paragraphIndex);
}

async function submitTodoRecoveryDecision(decision) {
  const recovery = chatStore.pendingTodoRecovery;
  if (!recovery) {
    return;
  }

  chatStore.setPendingTodoRecoverySubmitting(true);
  try {
    await hostBridge.submitTodoBoardRecoveryDecision(recovery.recoveryRequestId, decision);
    chatStore.resumeLoadingAfterTodoRecovery();
  } catch (error) {
    chatStore.setPendingTodoRecoverySubmitting(false);
    chatStore.appendAssistantMessage(`提交恢复决策失败：${error.message || '未知错误'}`);
  }
}

async function cancelCurrentRun() {
  if (!chatStore.isLoading && !chatStore.pendingConfirmation && !chatStore.pendingQuestion) {
    return;
  }

  isCancelling.value = true;
  try {
    await hostBridge.cancelCurrentRun();
  } catch (error) {
    chatStore.appendAssistantMessage(`取消任务失败：${error.message || '未知错误'}`);
    isCancelling.value = false;
  }
}

async function submitQuestionAnswer(answer) {
  const q = chatStore.pendingQuestion;
  if (!q || !answer?.trim()) return;
  chatStore.clearPendingQuestion();
  customQuestionAnswer.value = '';
  chatStore.appendUserMessage(answer.trim());
  chatStore.startLoading();
  await hostBridge.submitQuestionAnswer(q.questionId, answer.trim());
}

async function executePlan() {
  const plan = chatStore.activePlan;
  if (!plan) return;
  const context = [
    `## 任务说明\n${plan.taskDescription}`,
    `## 待办清单\n${(plan.todoList || []).map((t, i) => `${i + 1}. ${t.description || t}`).join('\n')}`,
    plan.riskNotes?.length ? `## 风险提示\n${plan.riskNotes.join('\n')}` : ''
  ].filter(Boolean).join('\n\n');
  chatStore.activePlan = null;
  chatStore.messages = [];
  chatStore.appendUserMessage('请按照以下计划执行任务：\n\n' + context);
  chatStore.startLoading();
  await hostBridge.sendMessage({
    content: '请按照以下计划执行任务：\n\n' + context,
    manualMode: 'agent',
    maxIterations: 100,
    permissionMode: settingsStore.form.permissionMode,
    requireConfirmationForScripts: requireConfirmationForPermission(settingsStore.form.permissionMode),
    activePlan: plan
  });
}

function requireConfirmationForPermission(permissionMode) {
  return !['auto_safe_writes', 'full_auto'].includes(permissionMode);
}

function getCancellationMessage() {
  return chatStore.currentMode === 'agent'
    ? '已取消当前任务。已验证的改动会保留，未验证的当前步骤会回滚。'
    : '已停止当前回答。';
}

function buildResumePrompt(decision) {
  switch (decision) {
    case 'rebuild_from_active_plan':
      return '请按当前计划重建任务板，并继续执行尚未完成的任务。';
    case 'discard_and_create_empty':
      return '请丢弃旧任务板并新建空板，然后根据当前目标继续执行。';
    case 'recover_existing':
    default:
      return '请继续执行当前未完成的任务，保持已完成进度，不要重复已经完成的步骤。';
  }
}

async function resumePausedTodoRun(decision) {
  const pause = chatStore.pendingTodoPause;
  if (!pause) {
    return;
  }

  if (decision === 'rebuild_from_active_plan' && !chatStore.lastApprovedPlan) {
    chatStore.appendAssistantMessage('当前没有可用于重建的计划，请先重新规划或直接继续现有任务。');
    return;
  }

  chatStore.setPendingTodoPauseSubmitting(true);
  const resumePrompt = buildResumePrompt(decision);
  chatStore.appendUserMessage(resumePrompt);
  chatStore.startLoading();

  try {
    await hostBridge.sendMessage({
      content: resumePrompt,
      manualMode: 'agent',
      maxIterations: 100,
      permissionMode: settingsStore.form.permissionMode,
      requireConfirmationForScripts: requireConfirmationForPermission(settingsStore.form.permissionMode),
      activePlan: chatStore.lastApprovedPlan,
      todoBoardDecision: decision
    });
  } catch (error) {
    chatStore.finishLoading();
    chatStore.appendAssistantMessage(`继续执行失败：${error.message || '未知错误'}`);
  }
}

function handleAgentEvent(event) {
  switch (event.type) {
    case 'stream_chunk':
      chatStore.appendAssistantChunk(event.content || '');
      break;
    case 'tool_call_started':
      chatStore.startToolCall(event.toolCallId, event.toolName, event.toolInput, {
        requiresConfirmation: event.requiresConfirmation === true,
        operationDescription: event.operationDescription || ''
      });
      break;
    case 'tool_call_completed':
      chatStore.completeToolCall(event.toolCallId, event.toolSuccess, event.toolOutput);
      break;
    case 'tool_call_denied':
      chatStore.completeToolCall(event.toolCallId, false, event.toolOutput, 'denied');
      break;
    case 'tool_call_skipped':
      chatStore.completeToolCall(event.toolCallId, false, event.toolOutput, 'skipped');
      break;
    case 'change_executed':
      chatStore.recordChangeExecuted(event);
      break;
    case 'change_applied':
      chatStore.recordChangeApplied(event);
      break;
    case 'change_unverified':
      chatStore.recordChangeUnverified(event);
      break;
    case 'change_verification_failed':
      chatStore.recordChangeVerificationFailed(event);
      break;
    case 'change_repair_required':
      chatStore.recordChangeRepairRequired(event);
      break;
    case 'mode_detected':
      chatStore.setMode(event.detectedMode);
      break;
    case 'question_asked':
      chatStore.setPendingQuestion({
        questionId: event.toolCallId,
        question: event.content || '',
        options: Array.isArray(event.questionOptions) ? event.questionOptions : []
      });
      chatStore.finishLoading();
      break;
    case 'plan_ready':
      chatStore.setPlan(event.planJson);
      chatStore.finishLoading();
      break;
    case 'todo_board_recovery_required':
      chatStore.setTodoRecovery({
        recoveryRequestId: event.recoveryRequestId,
        boardJson: event.boardJson,
        recoveryReason: event.recoveryReason,
        lastRunOutcome: event.lastRunOutcome,
        lastErrorSummary: event.lastErrorSummary,
        hasActivePlan: event.hasActivePlan === true,
        canRecoverExisting: event.canRecoverExisting !== false
      });
      chatStore.finishLoading();
      break;
    case 'todo_board_paused':
      chatStore.setTodoPause({
        message: event.message,
        boardJson: event.boardJson,
        lastRunOutcome: event.lastRunOutcome,
        lastErrorSummary: event.lastErrorSummary,
        hasActivePlan: event.hasActivePlan === true,
        canRecoverExisting: event.canRecoverExisting !== false
      });
      chatStore.finishLoading();
      break;
    case 'todo_board_ready':
      chatStore.clearPendingTodoRecovery();
      chatStore.clearPendingTodoPause();
      chatStore.setTodoBoard(
        event.boardJson,
        event.currentTodoId || '',
        event.todoBoardUpdateKind || 'ready',
        event.message || ''
      );
      break;
    case 'todo_board_updated':
      chatStore.setTodoBoard(
        event.boardJson,
        event.currentTodoId || '',
        event.todoBoardUpdateKind || 'unknown',
        event.message || ''
      );
      break;
    case 'max_iterations_reached':
      chatStore.finishLoading();
      isCancelling.value = false;
      if (event.message) {
        chatStore.appendAssistantMessage(event.message);
      }
      break;
    case 'todo_reminder_injected':
      chatStore.setTodoBoard(
        event.boardJson,
        event.currentTodoId || '',
        event.todoBoardUpdateKind || 'reminder',
        event.message || ''
      );
      break;
    case 'progress_update':
      if (event.message) chatStore.updatePlanProgress(event.message);
      break;
    case 'task_completed':
      chatStore.setCitations(event.citations);
      chatStore.clearPendingTodoRecovery();
      chatStore.clearPendingTodoPause();
      chatStore.finishLoading();
      chatStore.finalizeTaskChanges();
      isCancelling.value = false;
      if (event.message) {
        chatStore.appendAssistantMessage(event.message);
      }
      break;
    case 'cancelled':
      chatStore.setCitations(event.citations);
      chatStore.clearPendingRunWaits();
      chatStore.finishLoading();
      chatStore.finalizeTaskChanges();
      chatStore.appendAssistantMessage(getCancellationMessage());
      isCancelling.value = false;
      break;
    case 'document_not_writable':
    case 'document_mismatch':
    case 'error':
      chatStore.setCitations(event.citations);
      chatStore.clearPendingTodoRecovery();
      chatStore.clearPendingTodoPause();
      chatStore.finishLoading();
      chatStore.finalizeTaskChanges();
      isCancelling.value = false;
      if (event.message) {
        chatStore.appendAssistantMessage(event.message);
      }
      break;
    default:
      break;
  }
}

async function handleMessageListClick(event) {
  const target = event.target.closest('[data-citation-ref]');
  if (!target) {
    return;
  }

  const ref = Number(target.dataset.citationRef);
  const citation = chatStore.findCitation(ref);
  if (!citation) {
    return;
  }

  await navigateToParagraph(citation.paragraphIndex);
}

async function scrollToBottom() {
  await nextTick();
  const element = messageListRef.value;
  if (!element) {
    return;
  }

  element.scrollTop = element.scrollHeight;
}

onMounted(async () => {
  unsubscribeAgentEvent = hostBridge.onAgentEvent(handleAgentEvent);
  await settingsStore.loadSettings();

  if (!chatStore.messages.length) {
    chatStore.appendAssistantMessage(
      '已完成前端初始化。你现在可以直接聊天，也可以先在“设置”里保存长期使用的模型与接口配置。'
    );
  }
});

onUnmounted(() => {
  if (typeof unsubscribeAgentEvent === 'function') {
    unsubscribeAgentEvent();
  }
});

watch(
  () => [
    chatStore.messages.length,
    chatStore.activeToolCalls.length,
    chatStore.pendingQuestion ? chatStore.pendingQuestion.questionId : '',
    chatStore.pendingTodoRecovery ? chatStore.pendingTodoRecovery.recoveryRequestId : '',
    chatStore.pendingTodoPause ? chatStore.pendingTodoPause.message : '',
    chatStore.activePlan ? JSON.stringify(chatStore.activePlan) : '',
    chatStore.isLoading
  ],
  () => {
    scrollToBottom();
  }
);

watch(
  () => settingsStore.form.permissionMode,
  (permissionMode) => {
    settingsStore.form.requireConfirmationForScripts = requireConfirmationForPermission(permissionMode);
  }
);
</script>

<style scoped>
:global(*) {
  box-sizing: border-box;
}

:global(html, body, #app) {
  margin: 0;
  min-height: 100%;
  font-family: "Segoe UI", "Microsoft YaHei UI", sans-serif;
  background:
    radial-gradient(circle at top, rgba(140, 184, 255, 0.24), transparent 42%),
    linear-gradient(180deg, #f7f9fc 0%, #eef3f8 100%);
  color: #18314f;
}

:global(body) {
  min-width: 280px;
}

.chat-window {
  width: 280px;
  min-height: 100vh;
  padding: 14px;
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.chat-header {
  display: flex;
  justify-content: space-between;
  align-items: flex-start;
  gap: 8px;
  padding: 14px;
  border: 1px solid rgba(89, 118, 161, 0.18);
  border-radius: 18px;
  background: rgba(255, 255, 255, 0.88);
  box-shadow: 0 14px 36px rgba(24, 49, 79, 0.08);
}

.eyebrow {
  margin: 0 0 4px;
  font-size: 11px;
  letter-spacing: 0.12em;
  text-transform: uppercase;
  color: #6f86a3;
}

.chat-header h1 {
  margin: 0;
  font-size: 22px;
  line-height: 1.1;
}

.settings-summary {
  margin: 6px 0 0;
  font-size: 11px;
  line-height: 1.5;
  color: #60758f;
}

.header-actions {
  display: flex;
  flex-direction: column;
  align-items: flex-end;
  gap: 8px;
}

.mode-pill {
  padding: 6px 10px;
  border-radius: 999px;
  background: #18314f;
  color: #ffffff;
  font-size: 12px;
  font-weight: 600;
  text-align: center;
}

.ghost-button {
  border: 1px solid rgba(89, 118, 161, 0.22);
  border-radius: 12px;
  background: rgba(255, 255, 255, 0.82);
  color: #244464;
  font: inherit;
  font-size: 12px;
  padding: 8px 10px;
  cursor: pointer;
}

.ghost-button--small {
  padding: 6px 8px;
}

.ghost-button--full {
  width: 100%;
}

.settings-panel {
  padding: 14px;
  border-radius: 18px;
  background: rgba(255, 255, 255, 0.92);
  border: 1px solid rgba(89, 118, 161, 0.16);
  box-shadow: 0 18px 40px rgba(24, 49, 79, 0.08);
}

.settings-panel__header {
  display: flex;
  justify-content: space-between;
  align-items: flex-start;
  gap: 8px;
  margin-bottom: 12px;
}

.settings-panel__header h2 {
  margin: 0;
  font-size: 16px;
}

.settings-panel__header p {
  margin: 4px 0 0;
  font-size: 11px;
  line-height: 1.5;
  color: #60758f;
}

.settings-grid {
  display: flex;
  flex-direction: column;
  gap: 10px;
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
  padding: 10px 12px;
  border: 1px solid rgba(89, 118, 161, 0.26);
  border-radius: 14px;
  font: inherit;
  color: inherit;
  background: #f7f9fc;
}

.settings-field textarea {
  resize: vertical;
  min-height: 88px;
}

.settings-field input:focus,
.settings-field select:focus,
.settings-field textarea:focus {
  outline: 2px solid rgba(40, 81, 125, 0.25);
  border-color: rgba(40, 81, 125, 0.42);
}

.settings-field--checkbox {
  flex-direction: row;
  align-items: center;
  gap: 8px;
  padding: 4px 2px;
}

.settings-field--checkbox input {
  width: auto;
  margin: 0;
}

.settings-panel__footer {
  margin-top: 12px;
  display: flex;
  flex-direction: column;
  gap: 10px;
}

.settings-message {
  margin: 0;
  font-size: 11px;
  line-height: 1.5;
}

.settings-message--success {
  color: #2d6a4f;
}

.settings-message--error {
  color: #b42318;
}

.send-button--full {
  width: 100%;
}

.message-list {
  flex: 1;
  min-height: 0;
  display: flex;
  flex-direction: column;
  gap: 10px;
  overflow-y: auto;
  padding-right: 4px;
}

.message-card {
  padding: 12px;
  border-radius: 16px;
  border: 1px solid rgba(89, 118, 161, 0.16);
  box-shadow: 0 12px 24px rgba(24, 49, 79, 0.06);
  word-break: break-word;
}

.message-card--assistant {
  background: rgba(255, 255, 255, 0.92);
}

.message-card--user {
  background: linear-gradient(135deg, #18314f 0%, #28517d 100%);
  color: #ffffff;
}

.message-meta {
  display: flex;
  justify-content: space-between;
  gap: 10px;
  margin-bottom: 8px;
  font-size: 11px;
  opacity: 0.78;
}

.message-body {
  font-size: 13px;
  line-height: 1.6;
}

.message-body :deep(p) {
  margin: 0 0 8px;
}

.message-body :deep(p:last-child) {
  margin-bottom: 0;
}

.message-body :deep(code) {
  padding: 1px 4px;
  border-radius: 6px;
  background: rgba(24, 49, 79, 0.08);
  font-size: 12px;
}

.message-card--user .message-body :deep(code) {
  background: rgba(255, 255, 255, 0.14);
}

.message-body :deep(.citation-anchor) {
  border: none;
  padding: 0 2px;
  background: transparent;
  color: #d96f32;
  font: inherit;
  cursor: pointer;
}

.composer {
  padding: 14px;
  border-radius: 18px;
  background: rgba(255, 255, 255, 0.92);
  border: 1px solid rgba(89, 118, 161, 0.16);
  box-shadow: 0 18px 40px rgba(24, 49, 79, 0.08);
}

.composer-label {
  display: block;
  margin-bottom: 8px;
  font-size: 12px;
  font-weight: 600;
  color: #395372;
}

.mode-selector {
  margin-bottom: 10px;
}

.mode-selector__label {
  margin: 0 0 8px;
  font-size: 11px;
  color: #60758f;
}

.mode-selector__options {
  display: grid;
  grid-template-columns: repeat(3, minmax(0, 1fr));
  gap: 6px;
}

.mode-option {
  min-height: 38px;
  padding: 8px 6px;
  border: 1px solid rgba(89, 118, 161, 0.22);
  border-radius: 12px;
  background: rgba(255, 255, 255, 0.84);
  color: #395372;
  font: inherit;
  font-size: 11px;
  font-weight: 600;
  line-height: 1.3;
  cursor: pointer;
  transition:
    transform 0.16s ease,
    box-shadow 0.16s ease,
    border-color 0.16s ease,
    background 0.16s ease;
}

.mode-option--active {
  border-color: rgba(24, 49, 79, 0.48);
  background: linear-gradient(135deg, #18314f 0%, #28517d 100%);
  color: #ffffff;
  box-shadow: 0 10px 18px rgba(24, 49, 79, 0.18);
}

.composer-input {
  width: 100%;
  resize: none;
  padding: 10px 12px;
  border: 1px solid rgba(89, 118, 161, 0.26);
  border-radius: 14px;
  font: inherit;
  color: inherit;
  background: #f7f9fc;
}

.composer-input:focus {
  outline: 2px solid rgba(40, 81, 125, 0.25);
  border-color: rgba(40, 81, 125, 0.42);
}

.composer-footer {
  margin-top: 10px;
  display: flex;
  justify-content: space-between;
  align-items: flex-end;
  gap: 10px;
}

.environment-hint {
  margin: 0;
  max-width: 130px;
  font-size: 11px;
  line-height: 1.5;
  color: #60758f;
}

.send-button {
  min-width: 76px;
  padding: 10px 14px;
  border: none;
  border-radius: 12px;
  background: linear-gradient(135deg, #d96f32 0%, #ee8d32 100%);
  color: #ffffff;
  font: inherit;
  font-weight: 600;
  cursor: pointer;
  transition: transform 0.16s ease, box-shadow 0.16s ease;
  box-shadow: 0 10px 18px rgba(217, 111, 50, 0.24);
}

.composer-cancel-button {
  min-width: 76px;
  background: #ffffff;
}

.send-button:hover:enabled,
.ghost-button:hover:enabled {
  transform: translateY(-1px);
}

.send-button:disabled,
.ghost-button:disabled {
  cursor: not-allowed;
  opacity: 0.65;
}

.typing-dot {
  display: inline-block;
  width: 6px;
  height: 6px;
  margin-right: 6px;
  border-radius: 50%;
  background: #28517d;
  animation: pulse 1s infinite ease-in-out;
}

.typing-dot:nth-child(2) {
  animation-delay: 0.2s;
}

.typing-dot:nth-child(3) {
  animation-delay: 0.4s;
  margin-right: 0;
}

@keyframes pulse {
  0%,
  80%,
  100% {
    transform: translateY(0);
    opacity: 0.4;
  }

  40% {
    transform: translateY(-3px);
    opacity: 1;
  }
}
</style>
