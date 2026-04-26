import { defineStore } from 'pinia';

function createMessage(role, content) {
  return {
    id: `${role}-${Date.now()}-${Math.random().toString(16).slice(2)}`,
    role,
    content,
    timestamp: new Date().toLocaleTimeString('zh-CN', {
      hour: '2-digit',
      minute: '2-digit'
    })
  };
}

function cloneParagraphs(affectedParagraphs) {
  return Array.isArray(affectedParagraphs) ? [...affectedParagraphs] : [];
}

function normalizeTodoBoardUpdateKind(kind) {
  return typeof kind === 'string' ? kind.trim().toLowerCase() : 'unknown';
}

function createTodoBoardNotice(updateKind, message, board) {
  const normalizedKind = normalizeTodoBoardUpdateKind(updateKind);
  const safeMessage = (message || '').trim();
  const checkpointSummary = (board?.lastTrustedCheckpointSummary || '').trim();

  switch (normalizedKind) {
    case 'rollback_restored':
      return {
        kind: normalizedKind,
        message: safeMessage || '当前写步骤已回退，任务板已恢复到最近可信检查点。',
        checkpointSummary
      };
    case 'reminder':
      return safeMessage
        ? {
            kind: normalizedKind,
            message: safeMessage,
            checkpointSummary
          }
        : null;
    default:
      return null;
  }
}

function createChangeRecord(change) {
  return {
    id: change.toolCallId || `${Date.now()}-${Math.random().toString(16).slice(2)}`,
    toolName: change.toolName || 'unknown_tool',
    operationDescription: change.operationDescription || '已执行写入。',
    affectedParagraphs: cloneParagraphs(change.affectedParagraphs),
    status: 'executed',
    statusMessage: change.message || ''
  };
}

function normalizeChangeStatus(item) {
  const statusMessage = item?.statusMessage || '';
  if (statusMessage.includes('已通过验证') || statusMessage.includes('确认改动生效')) {
    return 'verified';
  }

  return item?.status || 'executed';
}

function normalizeChangeKey(item) {
  return [
    item?.toolName || '',
    item?.operationDescription || ''
  ].join('\n').trim();
}

function compactCompletedChanges(changes) {
  const normalizedChanges = changes.map((item) => ({
    ...item,
    status: normalizeChangeStatus(item),
    affectedParagraphs: [...(item.affectedParagraphs || [])]
  }));
  const verifiedKeys = new Set(
    normalizedChanges
      .filter((item) => item.status === 'verified')
      .map((item) => normalizeChangeKey(item))
      .filter(Boolean)
  );

  return normalizedChanges.filter((item) => {
    if (item.status === 'verified') {
      return true;
    }

    const key = normalizeChangeKey(item);
    return !key || !verifiedKeys.has(key);
  });
}

export const useChatStore = defineStore('chat', {
  state: () => ({
    messages: [],
    isLoading: false,
    currentMode: 'ask',
    activeToolCalls: [],
    citations: [],
    pendingConfirmation: null,
    pendingQuestion: null,
    activePlan: null,
    lastApprovedPlan: null,
    activeTodoBoard: null,
    todoBoardNotice: null,
    pendingTodoRecovery: null,
    pendingTodoPause: null,
    currentTodoId: '',
    todoBoardVisible: false,
    currentTaskChanges: [],
    completedTaskChanges: []
  }),
  actions: {
    appendUserMessage(content) {
      this.messages.push(createMessage('user', content));
    },
    appendAssistantMessage(content) {
      if (!String(content || '').trim()) {
        return;
      }

      this.messages.push(createMessage('assistant', content));
    },
    appendAssistantChunk(content) {
      if (!content) {
        return;
      }

      const lastMessage = this.messages[this.messages.length - 1];

      if (!lastMessage || lastMessage.role !== 'assistant') {
        this.appendAssistantMessage(content);
        return;
      }

      lastMessage.content += content;
    },
    setMode(mode) {
      this.currentMode = mode || 'ask';
    },
    startToolCall(toolCallId, toolName, toolInput, metadata = {}) {
      const existing = this.activeToolCalls.find((item) => item.id === toolCallId);
      if (existing) {
        existing.name = toolName || existing.name;
        existing.input = toolInput || existing.input;
        existing.status = 'running';
        existing.operationDescription = metadata.operationDescription || existing.operationDescription || '';
        existing.requiresConfirmation =
          metadata.requiresConfirmation ?? existing.requiresConfirmation ?? false;
        return;
      }

      this.activeToolCalls.push({
        id: toolCallId || `${Date.now()}-${Math.random().toString(16).slice(2)}`,
        name: toolName || 'unknown_tool',
        input: toolInput || '',
        output: '',
        status: 'running',
        requiresConfirmation: metadata.requiresConfirmation === true,
        operationDescription: metadata.operationDescription || ''
      });

      if (metadata.requiresConfirmation === true) {
        this.pendingConfirmation = {
          toolCallId: toolCallId || '',
          toolName: toolName || 'unknown_tool',
          toolInput: toolInput || '',
          operationDescription: metadata.operationDescription || '',
          isSubmitting: false
        };
      }
    },
    completeToolCall(toolCallId, success, output, status = null) {
      const target = this.activeToolCalls.find((item) => item.id === toolCallId);
      if (!target) {
        this.activeToolCalls.push({
          id: toolCallId || `${Date.now()}-${Math.random().toString(16).slice(2)}`,
          name: 'unknown_tool',
          input: '',
          output: output || '',
          status: status || (success ? 'success' : 'failed')
        });
      } else {
        target.output = output || '';
        target.status = status || (success ? 'success' : 'failed');
      }

      if (this.pendingConfirmation && this.pendingConfirmation.toolCallId === toolCallId) {
        this.pendingConfirmation = null;
      }
    },
    setCitations(citationList) {
      this.citations = Array.isArray(citationList) ? citationList : [];
    },
    setPendingConfirmationSubmitting(isSubmitting) {
      if (!this.pendingConfirmation) {
        return;
      }

      this.pendingConfirmation = {
        ...this.pendingConfirmation,
        isSubmitting: isSubmitting === true
      };
    },
    clearPendingConfirmation() {
      this.pendingConfirmation = null;
    },
    upsertTaskChange(change, status) {
      const changeId = change.toolCallId || `${Date.now()}-${Math.random().toString(16).slice(2)}`;
      const existing = this.currentTaskChanges.find((item) => item.id === changeId);
      const nextRecord = existing || createChangeRecord({
        ...change,
        toolCallId: changeId
      });

      nextRecord.id = changeId;
      nextRecord.toolName = change.toolName || nextRecord.toolName || 'unknown_tool';
      nextRecord.operationDescription =
        change.operationDescription || nextRecord.operationDescription || '已执行写入。';
      nextRecord.affectedParagraphs = cloneParagraphs(change.affectedParagraphs ?? nextRecord.affectedParagraphs);
      nextRecord.status = status || nextRecord.status || 'executed';
      nextRecord.statusMessage = change.message || nextRecord.statusMessage || '';

      if (!existing) {
        this.currentTaskChanges.push(nextRecord);
      }
    },
    recordChangeExecuted(change) {
      this.upsertTaskChange(change, 'executed');
    },
    recordChangeApplied(change) {
      this.upsertTaskChange(change, 'verified');
    },
    recordChangeUnverified(change) {
      this.upsertTaskChange(change, 'unverified');
    },
    recordChangeVerificationFailed(change) {
      this.upsertTaskChange(change, 'verification_failed');
    },
    recordChangeRepairRequired(change) {
      this.upsertTaskChange(change, 'repair_required');
    },
    finalizeTaskChanges() {
      this.completedTaskChanges = compactCompletedChanges(this.currentTaskChanges);
      this.currentTaskChanges = [];
      this.pendingConfirmation = null;
      this.todoBoardNotice = null;
    },
    findCitation(ref) {
      return this.citations.find((item) => item.ref === ref) || null;
    },
    setPendingQuestion(question) {
      this.pendingQuestion = question;
    },
    clearPendingQuestion() {
      this.pendingQuestion = null;
    },
    setPlan(planJson) {
      try {
        const parsedPlan = typeof planJson === 'string' ? JSON.parse(planJson) : planJson;
        this.activePlan = parsedPlan;
        this.lastApprovedPlan = parsedPlan;
      } catch {
        this.activePlan = null;
        this.lastApprovedPlan = null;
      }
    },
    updatePlanProgress(planJson) {
      try {
        this.activePlan = typeof planJson === 'string' ? JSON.parse(planJson) : planJson;
      } catch { /* 忽略解析失败 */ }
    },
    setTodoBoard(boardJson, currentTodoId = '', updateKind = 'unknown', message = '') {
      try {
        this.activeTodoBoard = typeof boardJson === 'string' ? JSON.parse(boardJson) : boardJson;
        this.currentTodoId =
          currentTodoId ||
          this.activeTodoBoard?.items?.find((item) => {
            const status = typeof item.status === 'string' ? item.status.toLowerCase() : item.status;
            return status === 'inprogress' || status === 'in_progress' || status === 1;
          })?.id ||
          '';
        this.todoBoardNotice = createTodoBoardNotice(updateKind, message, this.activeTodoBoard);
        this.todoBoardVisible = true;
        this.pendingTodoRecovery = null;
        this.pendingTodoPause = null;
      } catch {
        this.activeTodoBoard = null;
        this.todoBoardNotice = null;
        this.currentTodoId = '';
        this.todoBoardVisible = false;
      }
    },
    setTodoRecovery(recovery) {
      let board = null;
      try {
        board = recovery?.boardJson
          ? typeof recovery.boardJson === 'string'
            ? JSON.parse(recovery.boardJson)
            : recovery.boardJson
          : null;
      } catch {
        board = null;
      }

      this.pendingTodoRecovery = {
        recoveryRequestId: recovery?.recoveryRequestId || '',
        recoveryReason: recovery?.recoveryReason || '',
        lastRunOutcome: recovery?.lastRunOutcome || '',
        lastErrorSummary: recovery?.lastErrorSummary || '',
        hasActivePlan: recovery?.hasActivePlan === true,
        canRecoverExisting: recovery?.canRecoverExisting !== false && Boolean(board),
        board,
        isSubmitting: false
      };
      this.activeTodoBoard = null;
      this.todoBoardNotice = null;
      this.currentTodoId = '';
      this.todoBoardVisible = false;
      this.pendingTodoPause = null;
    },
    setPendingTodoRecoverySubmitting(isSubmitting) {
      if (!this.pendingTodoRecovery) {
        return;
      }

      this.pendingTodoRecovery = {
        ...this.pendingTodoRecovery,
        isSubmitting: isSubmitting === true
      };
    },
    clearPendingTodoRecovery() {
      this.pendingTodoRecovery = null;
    },
    setTodoPause(pause) {
      let board = null;
      try {
        board = pause?.boardJson
          ? typeof pause.boardJson === 'string'
            ? JSON.parse(pause.boardJson)
            : pause.boardJson
          : null;
      } catch {
        board = null;
      }

      this.pendingTodoPause = {
        message: pause?.message || '',
        lastRunOutcome: pause?.lastRunOutcome || '',
        lastErrorSummary: pause?.lastErrorSummary || '',
        hasActivePlan: pause?.hasActivePlan === true,
        canRecoverExisting: pause?.canRecoverExisting !== false && Boolean(board),
        board,
        isSubmitting: false
      };
      this.pendingTodoRecovery = null;
      this.activeTodoBoard = null;
      this.todoBoardNotice = null;
      this.currentTodoId = '';
      this.todoBoardVisible = false;
    },
    setPendingTodoPauseSubmitting(isSubmitting) {
      if (!this.pendingTodoPause) {
        return;
      }

      this.pendingTodoPause = {
        ...this.pendingTodoPause,
        isSubmitting: isSubmitting === true
      };
    },
    clearPendingTodoPause() {
      this.pendingTodoPause = null;
    },
    clearPendingRunWaits() {
      this.pendingConfirmation = null;
      this.pendingQuestion = null;
      this.pendingTodoRecovery = null;
      this.pendingTodoPause = null;
    },
    resumeLoadingAfterTodoRecovery() {
      this.isLoading = true;
      this.pendingTodoRecovery = null;
    },
    clearTodoBoard() {
      this.activeTodoBoard = null;
      this.todoBoardNotice = null;
      this.currentTodoId = '';
      this.todoBoardVisible = false;
    },
    startLoading() {
      this.isLoading = true;
      this.activeToolCalls = [];
      this.citations = [];
      this.pendingConfirmation = null;
      this.pendingQuestion = null;
      this.pendingTodoRecovery = null;
      this.pendingTodoPause = null;
      this.currentTaskChanges = [];
      this.completedTaskChanges = [];
      this.activeTodoBoard = null;
      this.todoBoardNotice = null;
      this.currentTodoId = '';
      this.todoBoardVisible = false;
    },
    finishLoading() {
      this.isLoading = false;
    }
  }
});
