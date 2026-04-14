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

export const useChatStore = defineStore('chat', {
  state: () => ({
    messages: [],
    isLoading: false,
    currentMode: 'ask',
    activeToolCalls: [],
    citations: [],
    pendingConfirmation: null,
    currentTaskChanges: [],
    completedTaskChanges: []
  }),
  actions: {
    appendUserMessage(content) {
      this.messages.push(createMessage('user', content));
    },
    appendAssistantMessage(content) {
      this.messages.push(createMessage('assistant', content));
    },
    appendAssistantChunk(content) {
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
    recordChangeApplied(change) {
      this.currentTaskChanges.push({
        id: change.toolCallId || `${Date.now()}-${Math.random().toString(16).slice(2)}`,
        toolName: change.toolName || 'unknown_tool',
        operationDescription: change.operationDescription || '已执行改动。',
        affectedParagraphs: Array.isArray(change.affectedParagraphs)
          ? [...change.affectedParagraphs]
          : []
      });
    },
    finalizeTaskChanges() {
      this.completedTaskChanges = this.currentTaskChanges.map((item) => ({
        ...item,
        affectedParagraphs: [...item.affectedParagraphs]
      }));
      this.currentTaskChanges = [];
      this.pendingConfirmation = null;
    },
    discardCurrentTaskChanges() {
      this.currentTaskChanges = [];
      this.completedTaskChanges = [];
      this.pendingConfirmation = null;
    },
    findCitation(ref) {
      return this.citations.find((item) => item.ref === ref) || null;
    },
    startLoading() {
      this.isLoading = true;
      this.activeToolCalls = [];
      this.citations = [];
      this.pendingConfirmation = null;
      this.currentTaskChanges = [];
      this.completedTaskChanges = [];
    },
    finishLoading() {
      this.isLoading = false;
    }
  }
});
