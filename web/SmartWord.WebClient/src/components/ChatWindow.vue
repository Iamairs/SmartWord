<template>
  <div class="chat-window">
    <header class="app-bar">
      <button
        class="icon-button menu-button"
        type="button"
        :aria-expanded="isNavigationOpen"
        aria-controls="smartword-navigation"
        @click="toggleNavigation"
      >
        <span class="menu-icon" aria-hidden="true"><i></i><i></i><i></i></span>
        <span class="sr-only">打开导航</span>
      </button>
      <div class="app-bar__title">
        <p>SmartWord</p>
        <h1>{{ viewTitle }}</h1>
      </div>
      <span v-if="activeView === 'chat'" class="mode-pill">{{ modeLabel }}</span>
    </header>

    <main class="app-content">
      <section v-if="activeView === 'chat'" class="chat-view">
        <section class="message-list" ref="messageListRef" @click="handleMessageListClick">
          <ThoughtActionTrace :tool-calls="chatStore.activeToolCalls" />
          <ContentPreviewPanel
            v-if="chatStore.pendingConfirmation"
            :confirmation="chatStore.pendingConfirmation"
            @confirm="confirmPendingToolCall"
            @remember="rememberPendingToolCall"
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
            <button type="button" class="ghost-button ghost-button--full" @click="cancelCurrentRun">取消规划</button>
          </div>

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

          <div v-if="chatStore.isLoading" class="message-card message-card--assistant message-card--loading">
            <div class="message-meta"><span>SmartWord</span><span>处理中</span></div>
            <div class="message-body"><span class="typing-dot"></span><span class="typing-dot"></span><span class="typing-dot"></span></div>
          </div>
        </section>

        <form class="composer" @submit.prevent="submitMessage">
          <div class="mode-selector" role="group" aria-label="运行模式">
            <button
              v-for="option in modeOptions"
              :key="option.value"
              class="mode-option"
              :class="{ 'mode-option--active': chatStore.currentMode === option.value }"
              type="button"
              :disabled="chatStore.isLoading"
              :aria-pressed="chatStore.currentMode === option.value"
              :title="option.hint"
              @click="chatStore.setMode(option.value)"
            >
              {{ option.label }}
            </button>
          </div>
          <div class="composer-main">
            <textarea
              id="chat-input"
              ref="composerInputRef"
              v-model="draft"
              class="composer-input"
              rows="1"
              maxlength="3000"
              :disabled="chatStore.isLoading"
              :placeholder="composerPlaceholder"
              @input="syncComposerHeight"
              @keydown="handleComposerKeydown"
            ></textarea>
            <button
              v-if="chatStore.isLoading"
              class="composer-action composer-action--stop"
              type="button"
              :disabled="isCancelling"
              @click="cancelCurrentRun"
            >{{ isCancelling ? '取消中' : '停止' }}</button>
            <button v-else class="composer-action composer-action--send" type="submit" :disabled="isSubmitDisabled">发送</button>
          </div>
          <div class="composer-tools">
            <details v-if="skillsStore.enabledItems.length" class="skill-popover">
              <summary>Skill <span>{{ skillsStore.selectedSkillNames.length }}/3</span></summary>
              <div class="skill-selector__chips">
                <button
                  v-for="skill in skillsStore.enabledItems"
                  :key="skill.name"
                  class="skill-chip"
                  :class="{ 'skill-chip--active': skillsStore.selectedSkillNames.includes(skill.name) }"
                  type="button"
                  :disabled="chatStore.isLoading"
                  @click="skillsStore.toggleSelectedSkill(skill.name)"
                >{{ skill.displayName || skill.name }}</button>
              </div>
            </details>
            <span class="composer-hint">{{ composerHint }}</span>
            <span v-if="draft.length" class="composer-counter">{{ draft.length }}/3000</span>
          </div>
        </form>
      </section>

      <section v-else class="page-view">
        <QuickActionsPanel v-if="activeView === 'quick-actions'" @select="submitQuickAction" />
        <TaskHistoryPanel v-else-if="activeView === 'history'" @navigate="navigateToParagraph" />
        <SkillPanel v-else-if="activeView === 'skills'" :show-close-button="false" />
        <SettingsPanel v-else-if="activeView === 'settings'" :show-close-button="false" />
      </section>
    </main>

    <div v-if="isNavigationOpen" class="navigation-backdrop" @click="closeNavigation"></div>
    <aside
      id="smartword-navigation"
      class="navigation-drawer"
      :class="{ 'navigation-drawer--open': isNavigationOpen }"
      :aria-hidden="!isNavigationOpen"
      :inert="!isNavigationOpen"
      aria-label="主导航"
    >
      <div class="navigation-drawer__header">
        <div>
          <p>SmartWord</p>
          <strong>文档副驾</strong>
        </div>
        <button class="icon-button" type="button" aria-label="关闭导航" @click="closeNavigation">×</button>
      </div>
      <nav class="navigation-list">
        <button
          v-for="item in navigationItems"
          :key="item.id"
          class="navigation-item"
          :class="{ 'navigation-item--active': activeView === item.id }"
          type="button"
          :aria-current="activeView === item.id ? 'page' : undefined"
          @click="navigateToView(item.id)"
        >
          <span class="navigation-item__icon" aria-hidden="true">{{ item.icon }}</span>
          <span>{{ item.label }}</span>
        </button>
      </nav>
      <div class="navigation-drawer__footer">
        <span>{{ settingsStore.form.lightModel || '模型未配置' }}</span>
        <span>{{ settingsSummary }}</span>
      </div>
    </aside>
  </div>
</template>

<script setup>
import { computed, nextTick, onMounted, onUnmounted, ref, watch } from 'vue';
import { marked } from 'marked';
import { useChatStore } from '../stores/chat';
import { useSettingsStore } from '../stores/settings';
import { useTaskHistoryStore } from '../stores/taskHistory';
import { useSkillsStore } from '../stores/skills';
import { hostBridge } from '../bridge/hostBridge';
import ChangesSummaryPanel from './ChangesSummaryPanel.vue';
import ContentPreviewPanel from './ContentPreviewPanel.vue';
import QuickActionsPanel from './QuickActionsPanel.vue';
import SettingsPanel from './SettingsPanel.vue';
import SkillPanel from './SkillPanel.vue';
import TaskHistoryPanel from './TaskHistoryPanel.vue';
import ThoughtActionTrace from './ThoughtActionTrace.vue';
import TodoBoardPanel from './TodoBoardPanel.vue';
import TodoBoardPausePanel from './TodoBoardPausePanel.vue';
import TodoBoardRecoveryPanel from './TodoBoardRecoveryPanel.vue';

const chatStore = useChatStore();
const settingsStore = useSettingsStore();
const taskHistoryStore = useTaskHistoryStore();
const skillsStore = useSkillsStore();
const draft = ref('');
const messageListRef = ref(null);
const composerInputRef = ref(null);
const customQuestionAnswer = ref('');
const isCancelling = ref(false);
const activeView = ref('chat');
const isNavigationOpen = ref(false);
const renderCache = new Map();
let unsubscribeAgentEvent = null;
let pendingAssistantChunk = '';
let chunkFrameId = 0;
const modeOptions = [
  { value: 'ask', label: '问答', hint: '只读' },
  { value: 'plan', label: '规划', hint: '先确认' },
  { value: 'agent', label: '执行', hint: '可写入' }
];
const navigationItems = [
  { id: 'chat', label: '聊天', icon: 'AI' },
  { id: 'quick-actions', label: '常用任务', icon: 'GO' },
  { id: 'history', label: '任务历史', icon: 'HI' },
  { id: 'skills', label: 'Skill', icon: 'SK' },
  { id: 'settings', label: '设置', icon: 'SE' }
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

const viewTitle = computed(() => {
  const titles = {
    chat: '聊天',
    'quick-actions': '常用任务',
    history: '任务历史',
    skills: 'Skill',
    settings: '设置'
  };

  return titles[activeView.value] || '聊天';
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
  return `${settingsStore.form.lightModel} · ${settingsStore.form.heavyModel} · ${permission?.label || '写入前确认'}`;
});

const shouldShowTodoBoard = computed(() => {
  if (!chatStore.todoBoardVisible || chatStore.currentMode !== 'agent' || !chatStore.activeTodoBoard) {
    return false;
  }

  return Array.isArray(chatStore.activeTodoBoard.items) && chatStore.activeTodoBoard.items.length > 0;
});

const hasUserMessages = computed(() => {
  return chatStore.messages.some((message) => message.role === 'user');
});

const citationCacheKey = computed(() => {
  return chatStore.citations
    .map((item) => `${item.ref}:${item.paragraphIndex}`)
    .join('|');
});

const composerHint = computed(() => {
  const hints = {
    ask: '先读取证据，再给出回答。',
    plan: '先澄清目标，再生成计划。',
    agent: '按当前权限执行并验证改动。'
  };
  return hints[chatStore.currentMode] || hints.ask;
});

const isSubmitDisabled = computed(() => {
  return chatStore.isLoading || draft.value.trim().length === 0 || settingsStore.isLoading;
});

function renderMessage(message) {
  const messageId = message?.id || '';
  const content = message?.content || '';
  const citations = message?.role === 'assistant' ? citationCacheKey.value : '';
  const cached = renderCache.get(messageId);
  if (cached && cached.content === content && cached.citations === citations) {
    return cached.rendered;
  }

  const html = marked.parse(content);
  const rendered = !message || message.role !== 'assistant'
    ? html
    : html.replace(/\[(\d+)\]/g, (fullMatch, rawRef) => {
      const ref = Number(rawRef);
      const citation = chatStore.findCitation(ref);
      if (!citation) {
        return fullMatch;
      }

      const title = `段落 ${citation.paragraphIndex}`;
      return `<button class="citation-anchor" type="button" data-citation-ref="${ref}" title="${title}">[${ref}]</button>`;
    });

  renderCache.set(messageId, {
    content,
    citations,
    rendered
  });
  if (renderCache.size > 120) {
    renderCache.delete(renderCache.keys().next().value);
  }

  return rendered;
}

async function submitMessage() {
  if (isSubmitDisabled.value) {
    return;
  }

  await sendMessage(draft.value.trim(), chatStore.currentMode);
}

function handleComposerKeydown(event) {
  if (event.key !== 'Enter' || event.shiftKey || event.isComposing) {
    return;
  }

  event.preventDefault();
  submitMessage();
}

async function submitQuickAction(action) {
  if (!action || chatStore.isLoading) {
    return;
  }

  await navigateToView('chat');
  await sendMessage(action.content, action.manualMode || 'ask', action.permissionMode || '');
}

async function sendMessage(content, manualMode, permissionModeOverride = '') {
  const request = {
    content,
    manualMode,
    permissionMode: permissionModeOverride || settingsStore.form.permissionMode,
    requireConfirmationForScripts: requireConfirmationForPermission(
      permissionModeOverride || settingsStore.form.permissionMode
    ),
    contextWindowTokens: settingsStore.form.contextWindowTokens,
    contextSoftLimitRatio: settingsStore.form.contextSoftLimitRatio,
    contextHardLimitRatio: settingsStore.form.contextHardLimitRatio,
    contextEmergencyLimitRatio: settingsStore.form.contextEmergencyLimitRatio,
    contextTokenSafetyMargin: settingsStore.form.contextTokenSafetyMargin,
    customInstructions: settingsStore.form.customInstructions,
    selectedSkillNames: skillsStore.selectedSkillNames
  };

  chatStore.setMode(manualMode);
  chatStore.appendUserMessage(content);
  draft.value = '';
  syncComposerHeight();
  chatStore.startLoading();

  try {
    await hostBridge.sendMessage(request);
  } catch (error) {
    chatStore.finishLoading();
    chatStore.appendAssistantMessage(`请求发送失败：${error.message || '未知错误'}`);
  }
}

async function respondToPendingToolCall(confirmed, options = {}) {
  if (!chatStore.pendingConfirmation) {
    return;
  }

  chatStore.setPendingConfirmationSubmitting(true);
  try {
    await hostBridge.confirmToolCall(chatStore.pendingConfirmation.toolCallId, confirmed, options);
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

async function rememberPendingToolCall() {
  await respondToPendingToolCall(true, { remember: true });
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
    contextWindowTokens: settingsStore.form.contextWindowTokens,
    contextSoftLimitRatio: settingsStore.form.contextSoftLimitRatio,
    contextHardLimitRatio: settingsStore.form.contextHardLimitRatio,
    contextEmergencyLimitRatio: settingsStore.form.contextEmergencyLimitRatio,
    contextTokenSafetyMargin: settingsStore.form.contextTokenSafetyMargin,
    activePlan: plan,
    selectedSkillNames: skillsStore.selectedSkillNames
  });
}

function requireConfirmationForPermission(permissionMode) {
  return !['auto_safe_writes', 'full_auto'].includes(permissionMode);
}

function requestFrame(callback) {
  if (typeof window !== 'undefined' && typeof window.requestAnimationFrame === 'function') {
    return window.requestAnimationFrame(callback);
  }

  return setTimeout(callback, 16);
}

function cancelFrame(frameId) {
  if (!frameId || typeof window === 'undefined') {
    return;
  }

  if (typeof window.cancelAnimationFrame === 'function') {
    window.cancelAnimationFrame(frameId);
    return;
  }

  window.clearTimeout(frameId);
}

function isMessageListNearBottom() {
  const element = messageListRef.value;
  if (!element) {
    return true;
  }

  const distance = element.scrollHeight - element.scrollTop - element.clientHeight;
  return distance < 96;
}

function scheduleScrollToBottom(options = {}) {
  const force = options.force === true;
  const shouldFollow = force || isMessageListNearBottom();
  if (!shouldFollow) {
    return;
  }

  nextTick(() => {
    if (messageListRef.value) {
      messageListRef.value.scrollTop = messageListRef.value.scrollHeight;
    }
  });
}

function toggleNavigation() {
  isNavigationOpen.value = !isNavigationOpen.value;
}

function closeNavigation() {
  isNavigationOpen.value = false;
}

async function navigateToView(view) {
  if (!navigationItems.some((item) => item.id === view)) {
    return;
  }

  activeView.value = view;
  closeNavigation();
  taskHistoryStore.isPanelOpen = view === 'history';
  skillsStore.isPanelOpen = view === 'skills';
  if (view === 'settings') {
    settingsStore.openPanel();
  } else {
    settingsStore.closePanel();
  }

  if (view === 'history' && !taskHistoryStore.items.length) {
    await taskHistoryStore.loadRecentTasks();
  }

  if (view === 'skills' && !skillsStore.items.length) {
    await skillsStore.loadSkills();
  }

  if (view === 'chat') {
    scheduleScrollToBottom({ force: true });
    nextTick(syncComposerHeight);
  }
}

function syncComposerHeight() {
  nextTick(() => {
    const element = composerInputRef.value;
    if (!element) {
      return;
    }

    element.style.height = 'auto';
    element.style.height = `${Math.min(element.scrollHeight, 120)}px`;
  });
}

function flushPendingAssistantChunk() {
  if (!pendingAssistantChunk) {
    return;
  }

  const content = pendingAssistantChunk;
  pendingAssistantChunk = '';
  chatStore.appendAssistantChunk(content);
  scheduleScrollToBottom();
}

function scheduleAssistantChunk(content) {
  if (!content) {
    return;
  }

  pendingAssistantChunk += content;
  if (chunkFrameId) {
    return;
  }

  chunkFrameId = requestFrame(() => {
    chunkFrameId = 0;
    flushPendingAssistantChunk();
  });
}

function getCancellationMessage() {
  return chatStore.currentMode === 'agent'
    ? '已取消当前任务。已验证的改动会保留，未验证的当前步骤会回滚。'
    : '已停止当前回答。';
}

function buildResumePrompt(decision) {
  switch (decision) {
    case 'skip_current_todo':
      return '请跳过当前失败步骤，继续执行后续未完成的任务。';
    case 'recover_existing':
    default:
      return '请换一种方法继续尝试当前失败步骤，保持已完成进度，不要重复已经完成的步骤。';
  }
}

async function resumePausedTodoRun(decision) {
  const pause = chatStore.pendingTodoPause;
  if (!pause) {
    return;
  }

  if (decision === 'stop_task') {
    chatStore.setPendingTodoPauseSubmitting(true);
    try {
      await hostBridge.stopPausedTodoRun();
      chatStore.clearPendingTodoPause();
      chatStore.clearTodoBoard();
      chatStore.appendAssistantMessage('已停止当前任务。已验证的修改会保留，当前失败步骤已回退，暂停任务板已清理。');
    } catch (error) {
      chatStore.setPendingTodoPauseSubmitting(false);
      chatStore.appendAssistantMessage(`停止任务失败：${error.message || '未知错误'}`);
    }
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
      contextWindowTokens: settingsStore.form.contextWindowTokens,
      contextSoftLimitRatio: settingsStore.form.contextSoftLimitRatio,
      contextHardLimitRatio: settingsStore.form.contextHardLimitRatio,
      contextEmergencyLimitRatio: settingsStore.form.contextEmergencyLimitRatio,
      contextTokenSafetyMargin: settingsStore.form.contextTokenSafetyMargin,
      activePlan: chatStore.lastApprovedPlan,
      todoBoardDecision: decision,
      selectedSkillNames: skillsStore.selectedSkillNames
    });
  } catch (error) {
    chatStore.finishLoading();
    chatStore.appendAssistantMessage(`继续执行失败：${error.message || '未知错误'}`);
  }
}

function handleAgentEvent(event) {
  if (event.type === 'stream_chunk') {
    scheduleAssistantChunk(event.content || '');
    return;
  }

  flushPendingAssistantChunk();

  switch (event.type) {
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
        scheduleScrollToBottom({ force: true });
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
      if (taskHistoryStore.isPanelOpen) {
        taskHistoryStore.loadRecentTasks();
      }
      isCancelling.value = false;
      if (event.message) {
        chatStore.appendAssistantMessage(event.message);
        scheduleScrollToBottom({ force: true });
      }
      break;
    case 'cancelled':
      chatStore.setCitations(event.citations);
      chatStore.clearPendingRunWaits();
      chatStore.finishLoading();
      chatStore.finalizeTaskChanges();
      chatStore.appendAssistantMessage(getCancellationMessage());
      scheduleScrollToBottom({ force: true });
      if (taskHistoryStore.isPanelOpen) {
        taskHistoryStore.loadRecentTasks();
      }
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
        scheduleScrollToBottom({ force: true });
      }
      if (taskHistoryStore.isPanelOpen) {
        taskHistoryStore.loadRecentTasks();
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

onMounted(async () => {
  unsubscribeAgentEvent = hostBridge.onAgentEvent(handleAgentEvent);
  await settingsStore.loadSettings();
  await skillsStore.loadSkills();

  if (!chatStore.messages.length) {
    chatStore.appendAssistantMessage(
      '我可以帮你总结、审阅、改写或整理当前 Word 文档。直接描述目标即可，也可以从侧边菜单打开常用任务。'
    );
  }

  syncComposerHeight();

  if (hasUserMessages.value || chatStore.isLoading) {
    scheduleScrollToBottom({ force: true });
  }
});

onUnmounted(() => {
  if (typeof unsubscribeAgentEvent === 'function') {
    unsubscribeAgentEvent();
  }

  cancelFrame(chunkFrameId);
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
    if (!hasUserMessages.value && !chatStore.isLoading) {
      return;
    }

    scheduleScrollToBottom({ force: hasUserMessages.value && chatStore.messages.length <= 2 });
  }
);

watch(draft, syncComposerHeight);

watch(
  () => settingsStore.form.permissionMode,
  (permissionMode) => {
    settingsStore.form.requireConfirmationForScripts = requireConfirmationForPermission(permissionMode);
  }
);
</script>

<style scoped>
.chat-window {
  position: relative;
  display: grid;
  grid-template-rows: 48px minmax(0, 1fr);
  width: 100%;
  height: 100vh;
  min-height: 0;
  padding: 0;
  overflow: hidden;
  background: var(--sw-bg);
}

.app-bar {
  position: relative;
  z-index: 10;
  display: grid;
  grid-template-columns: 36px minmax(0, 1fr) auto;
  align-items: center;
  gap: 8px;
  min-width: 0;
  padding: 6px 10px;
  border-bottom: 1px solid var(--sw-border);
  background: rgba(255, 255, 255, 0.96);
}

.app-bar__title {
  display: flex;
  min-width: 0;
  align-items: baseline;
  gap: 7px;
  overflow: hidden;
}

.app-bar__title p,
.app-bar__title h1 {
  margin: 0;
  white-space: nowrap;
}

.app-bar__title p {
  color: var(--sw-text);
  font-size: 13px;
  font-weight: 700;
}

.app-bar__title h1 {
  overflow: hidden;
  color: var(--sw-text-muted);
  font-size: 11px;
  font-weight: 500;
  text-overflow: ellipsis;
}

.icon-button {
  display: inline-grid;
  width: 34px;
  height: 34px;
  flex: 0 0 34px;
  place-items: center;
  padding: 0;
  border: 1px solid transparent;
  border-radius: var(--sw-radius-sm);
  background: transparent;
  color: var(--sw-text-soft);
  font-size: 20px;
  line-height: 1;
  cursor: pointer;
}

.icon-button:hover {
  border-color: var(--sw-border);
  background: var(--sw-surface-muted);
  color: var(--sw-text);
}

.menu-icon {
  display: flex;
  width: 16px;
  flex-direction: column;
  gap: 3px;
}

.menu-icon i {
  display: block;
  width: 100%;
  height: 1.5px;
  border-radius: var(--sw-radius-pill);
  background: currentColor;
}

.mode-pill {
  max-width: 78px;
  padding: 4px 7px;
  overflow: hidden;
  border: 1px solid rgba(37, 99, 235, 0.18);
  border-radius: var(--sw-radius-pill);
  background: var(--sw-primary-soft);
  color: var(--sw-primary-strong);
  font-size: 10px;
  font-weight: 700;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.app-content,
.chat-view {
  min-width: 0;
  min-height: 0;
}

.app-content {
  overflow: hidden;
}

.chat-view {
  display: grid;
  grid-template-rows: minmax(0, 1fr) auto;
  height: 100%;
}

.message-list {
  display: flex;
  min-width: 0;
  min-height: 0;
  flex-direction: column;
  gap: 12px;
  padding: 14px 12px 18px;
  overflow-x: hidden;
  overflow-y: auto;
  overscroll-behavior: contain;
  scrollbar-gutter: stable;
}

.page-view {
  height: 100%;
  padding: 12px;
  overflow-x: hidden;
  overflow-y: auto;
}

.page-view :deep(.settings-panel),
.page-view :deep(.skill-panel),
.page-view :deep(.task-history-panel),
.page-view :deep(.quick-actions) {
  min-height: 100%;
  border: 0;
  border-radius: 0;
  background: transparent;
  box-shadow: none;
}

.message-card {
  width: 100%;
  min-width: 0;
  padding: 0;
  border: 0;
  border-radius: 0;
  background: transparent;
  box-shadow: none;
}

.message-card--user {
  width: auto;
  max-width: 88%;
  align-self: flex-end;
  padding: 9px 11px;
  border: 1px solid rgba(37, 99, 235, 0.12);
  border-radius: 12px 12px 4px 12px;
  background: #eaf1ff;
}

.message-card--assistant {
  align-self: stretch;
}

.message-meta {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  margin: 0 0 4px;
  color: var(--sw-text-muted);
  font-size: 10px;
  line-height: 1.3;
}

.message-card--user .message-meta {
  margin-bottom: 3px;
}

.message-meta span:last-child {
  flex: 0 0 auto;
  white-space: nowrap;
}

.message-body {
  min-width: 0;
  color: var(--sw-text);
  font-size: 13px;
  line-height: 1.62;
  overflow-wrap: anywhere;
}

.message-body :deep(p) {
  margin: 0 0 8px;
}

.message-body :deep(p:last-child),
.message-body :deep(ul:last-child),
.message-body :deep(ol:last-child),
.message-body :deep(pre:last-child) {
  margin-bottom: 0;
}

.message-body :deep(ul),
.message-body :deep(ol) {
  margin: 6px 0 8px;
  padding-left: 18px;
}

.message-body :deep(pre) {
  max-width: 100%;
  margin: 8px 0;
  padding: 9px;
  overflow: auto;
  border: 1px solid var(--sw-border);
  border-radius: var(--sw-radius-sm);
  background: #111827;
  color: #f9fafb;
  font-size: 11px;
}

.message-body :deep(code) {
  font-family: Consolas, "Courier New", monospace;
}

.message-body :deep(:not(pre) > code) {
  padding: 1px 4px;
  border-radius: 4px;
  background: var(--sw-surface-strong);
  font-size: 11px;
}

.message-body :deep(a),
.message-body :deep(.citation-anchor) {
  color: var(--sw-primary-strong);
}

.message-body :deep(.citation-anchor) {
  display: inline;
  padding: 0;
  border: 0;
  background: transparent;
  font-weight: 700;
  cursor: pointer;
}

.message-card--loading {
  min-height: 36px;
}

.composer {
  display: grid;
  flex: initial;
  gap: 7px;
  max-height: none;
  padding: 8px 10px max(8px, env(safe-area-inset-bottom));
  overflow: visible;
  border: 0;
  border-top: 1px solid var(--sw-border);
  border-radius: 0;
  background: rgba(255, 255, 255, 0.98);
  box-shadow: 0 -5px 18px rgba(17, 24, 39, 0.05);
}

.mode-selector {
  display: grid;
  grid-template-columns: repeat(3, minmax(0, 1fr));
  gap: 2px;
  padding: 2px;
  border: 1px solid var(--sw-border);
  border-radius: var(--sw-radius-sm);
  background: var(--sw-surface-muted);
}

.mode-option {
  min-width: 0;
  min-height: 28px;
  padding: 4px 6px;
  border: 0;
  border-radius: var(--sw-radius-xs);
  background: transparent;
  color: var(--sw-text-muted);
  font: inherit;
  font-size: 11px;
  font-weight: 600;
  line-height: 1;
  white-space: nowrap;
  cursor: pointer;
}

.mode-option--active {
  background: var(--sw-bg-elevated);
  color: var(--sw-text);
  box-shadow: 0 1px 4px rgba(17, 24, 39, 0.1);
}

.composer-main {
  display: flex;
  min-width: 0;
  align-items: flex-end;
  gap: 7px;
}

.composer-input {
  width: 100%;
  min-width: 0;
  min-height: 42px;
  max-height: 120px;
  padding: 10px 11px;
  overflow-y: auto;
  border: 1px solid var(--sw-border-strong);
  border-radius: var(--sw-radius-md);
  background: var(--sw-bg-elevated);
  color: var(--sw-text);
  font: inherit;
  font-size: 13px;
  line-height: 1.5;
  resize: none;
}

.composer-input:focus {
  border-color: rgba(37, 99, 235, 0.48);
  outline: none;
  box-shadow: var(--sw-focus);
}

.composer-input::placeholder {
  color: var(--sw-text-muted);
}

.composer-action {
  width: 50px;
  height: 42px;
  flex: 0 0 50px;
  padding: 0 8px;
  border-radius: var(--sw-radius-md);
  font: inherit;
  font-size: 12px;
  font-weight: 700;
  white-space: nowrap;
  cursor: pointer;
}

.composer-action--send {
  border: 0;
  background: var(--sw-primary);
  color: #ffffff;
}

.composer-action--send:hover:not(:disabled) {
  background: var(--sw-primary-strong);
}

.composer-action--stop {
  border: 1px solid rgba(190, 18, 60, 0.22);
  background: var(--sw-danger-soft);
  color: var(--sw-danger);
}

.composer-tools {
  position: relative;
  display: flex;
  min-width: 0;
  min-height: 20px;
  align-items: center;
  gap: 7px;
}

.composer-hint {
  min-width: 0;
  flex: 1;
  overflow: hidden;
  color: var(--sw-text-muted);
  font-size: 10px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.composer-counter {
  flex: 0 0 auto;
  color: var(--sw-text-muted);
  font-size: 9px;
  white-space: nowrap;
}

.skill-popover {
  position: relative;
  flex: 0 0 auto;
}

.skill-popover summary {
  display: inline-flex;
  align-items: center;
  gap: 3px;
  min-height: 22px;
  padding: 3px 6px;
  border-radius: var(--sw-radius-xs);
  color: var(--sw-text-soft);
  font-size: 10px;
  font-weight: 600;
  list-style: none;
  white-space: nowrap;
  cursor: pointer;
}

.skill-popover summary::-webkit-details-marker {
  display: none;
}

.skill-popover summary:hover {
  background: var(--sw-surface-muted);
}

.skill-selector__chips {
  position: absolute;
  bottom: calc(100% + 8px);
  left: 0;
  z-index: 25;
  display: flex;
  width: min(260px, calc(100vw - 20px));
  max-height: 160px;
  flex-wrap: wrap;
  gap: 6px;
  padding: 9px;
  overflow-y: auto;
  border: 1px solid var(--sw-border);
  border-radius: var(--sw-radius-md);
  background: var(--sw-bg-elevated);
  box-shadow: var(--sw-shadow-soft);
}

.skill-chip {
  max-width: 100%;
  min-height: 28px;
  padding: 5px 8px;
  overflow: hidden;
  border: 1px solid var(--sw-border);
  border-radius: var(--sw-radius-xs);
  background: var(--sw-surface-muted);
  color: var(--sw-text-soft);
  font-size: 10px;
  text-overflow: ellipsis;
  white-space: nowrap;
  cursor: pointer;
}

.skill-chip--active {
  border-color: rgba(37, 99, 235, 0.3);
  background: var(--sw-primary-soft);
  color: var(--sw-primary-strong);
}

.navigation-backdrop {
  position: fixed;
  inset: 0;
  z-index: 40;
  background: rgba(17, 24, 39, 0.36);
}

.navigation-drawer {
  position: fixed;
  inset: 0 auto 0 0;
  z-index: 50;
  display: grid;
  grid-template-rows: auto minmax(0, 1fr) auto;
  width: min(86vw, 268px);
  padding: 10px;
  overflow: hidden;
  border-right: 1px solid var(--sw-border);
  background: var(--sw-bg-elevated);
  box-shadow: 16px 0 36px rgba(17, 24, 39, 0.16);
  visibility: hidden;
  pointer-events: none;
  transform: translateX(-104%);
  transition: transform 180ms ease, visibility 0s linear 180ms;
}

.navigation-drawer--open {
  visibility: visible;
  pointer-events: auto;
  transform: translateX(0);
  transition: transform 180ms ease;
}

.navigation-drawer__header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  min-width: 0;
  padding: 2px 2px 12px 8px;
  border-bottom: 1px solid var(--sw-border);
}

.navigation-drawer__header p,
.navigation-drawer__header strong {
  display: block;
  margin: 0;
  white-space: nowrap;
}

.navigation-drawer__header p {
  color: var(--sw-text-muted);
  font-size: 10px;
}

.navigation-drawer__header strong {
  margin-top: 2px;
  color: var(--sw-text);
  font-size: 14px;
}

.navigation-list {
  display: flex;
  min-height: 0;
  flex-direction: column;
  gap: 4px;
  padding: 10px 0;
  overflow-y: auto;
}

.navigation-item {
  display: grid;
  grid-template-columns: 28px minmax(0, 1fr);
  align-items: center;
  gap: 9px;
  min-height: 40px;
  padding: 6px 9px;
  border: 0;
  border-radius: var(--sw-radius-sm);
  background: transparent;
  color: var(--sw-text-soft);
  font: inherit;
  font-size: 12px;
  font-weight: 600;
  text-align: left;
  white-space: nowrap;
  cursor: pointer;
}

.navigation-item:hover {
  background: var(--sw-surface-muted);
  color: var(--sw-text);
}

.navigation-item--active {
  background: var(--sw-primary-soft);
  color: var(--sw-primary-strong);
}

.navigation-item__icon {
  display: grid;
  width: 28px;
  height: 28px;
  place-items: center;
  border: 1px solid var(--sw-border);
  border-radius: var(--sw-radius-xs);
  background: var(--sw-bg-elevated);
  color: var(--sw-text-muted);
  font-size: 9px;
  font-weight: 800;
}

.navigation-item--active .navigation-item__icon {
  border-color: rgba(37, 99, 235, 0.2);
  color: var(--sw-primary-strong);
}

.navigation-drawer__footer {
  display: flex;
  min-width: 0;
  flex-direction: column;
  gap: 3px;
  padding: 10px 8px 4px;
  border-top: 1px solid var(--sw-border);
  color: var(--sw-text-muted);
  font-size: 9px;
  line-height: 1.4;
}

.navigation-drawer__footer span {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.question-panel,
.plan-panel {
  display: flex;
  flex-direction: column;
  gap: 9px;
  padding: 11px;
  border: 1px solid var(--sw-border);
  border-radius: var(--sw-radius-md);
  background: var(--sw-bg-elevated);
  box-shadow: none;
}

.question-panel__text,
.plan-panel__desc {
  margin: 0;
  color: var(--sw-text);
  font-size: 12px;
  line-height: 1.5;
}

.question-panel__options {
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.question-option-btn {
  min-height: 34px;
  padding: 7px 9px;
  border: 1px solid var(--sw-border);
  border-radius: var(--sw-radius-sm);
  background: var(--sw-surface-muted);
  color: var(--sw-text-soft);
  font: inherit;
  font-size: 11px;
  text-align: left;
  cursor: pointer;
}

.question-panel__custom {
  display: grid;
  grid-template-columns: minmax(0, 1fr) auto;
  gap: 6px;
}

.question-panel__custom input {
  min-width: 0;
  padding: 8px 9px;
  border: 1px solid var(--sw-border);
  border-radius: var(--sw-radius-sm);
  background: var(--sw-bg-elevated);
}

.send-button,
.ghost-button {
  min-height: 34px;
  padding: 7px 10px;
  border-radius: var(--sw-radius-sm);
  font: inherit;
  font-size: 11px;
  font-weight: 700;
  white-space: nowrap;
  cursor: pointer;
}

.send-button {
  border: 0;
  background: var(--sw-primary);
  color: #ffffff;
}

.ghost-button {
  border: 1px solid var(--sw-border);
  background: var(--sw-bg-elevated);
  color: var(--sw-text-soft);
}

.send-button--full,
.ghost-button--full {
  width: 100%;
}

.plan-panel__todo {
  margin: 0;
  padding-left: 18px;
  color: var(--sw-text-soft);
  font-size: 11px;
  line-height: 1.5;
}

.plan-panel__risks {
  display: flex;
  flex-wrap: wrap;
  gap: 5px;
}

.risk-note {
  padding: 3px 6px;
  border-radius: var(--sw-radius-pill);
  background: var(--sw-warning-soft);
  color: var(--sw-warning);
  font-size: 9px;
}

.typing-dot {
  display: inline-block;
  width: 5px;
  height: 5px;
  margin-right: 3px;
  border-radius: 50%;
  background: var(--sw-text-muted);
  animation: typing-pulse 1.2s infinite ease-in-out;
}

.typing-dot:nth-child(2) {
  animation-delay: 120ms;
}

.typing-dot:nth-child(3) {
  animation-delay: 240ms;
}

@keyframes typing-pulse {
  0%,
  100% {
    opacity: 0.28;
    transform: translateY(0);
  }

  50% {
    opacity: 1;
    transform: translateY(-2px);
  }
}

.sr-only {
  position: absolute;
  width: 1px;
  height: 1px;
  padding: 0;
  overflow: hidden;
  clip: rect(0, 0, 0, 0);
  white-space: nowrap;
  border: 0;
}

@media (max-height: 700px) {
  .chat-window {
    padding: 0;
  }

  .composer {
    max-height: none;
    padding-top: 6px;
    padding-bottom: 6px;
  }

  .message-list {
    padding-top: 10px;
  }
}
</style>
