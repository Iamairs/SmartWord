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
    citations: []
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
    startToolCall(toolCallId, toolName, toolInput) {
      const existing = this.activeToolCalls.find((item) => item.id === toolCallId);
      if (existing) {
        existing.name = toolName || existing.name;
        existing.input = toolInput || existing.input;
        existing.status = 'running';
        return;
      }

      this.activeToolCalls.push({
        id: toolCallId || `${Date.now()}-${Math.random().toString(16).slice(2)}`,
        name: toolName || 'unknown_tool',
        input: toolInput || '',
        output: '',
        status: 'running'
      });
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
        return;
      }

      target.output = output || '';
      target.status = status || (success ? 'success' : 'failed');
    },
    setCitations(citationList) {
      this.citations = Array.isArray(citationList) ? citationList : [];
    },
    findCitation(ref) {
      return this.citations.find((item) => item.ref === ref) || null;
    },
    startLoading() {
      this.isLoading = true;
      this.activeToolCalls = [];
      this.citations = [];
    },
    finishLoading() {
      this.isLoading = false;
    }
  }
});
