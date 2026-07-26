<template>
  <section v-if="toolCalls.length" class="trace-panel">
    <article v-for="toolCall in toolCalls" :key="toolCall.id" class="trace-card">
      <button class="trace-header" type="button" @click="toggle(toolCall.id)">
        <span class="trace-status" :class="`trace-status--${toolCall.status}`"></span>
        <span class="trace-name">{{ getToolDisplayName(toolCall.name) }}</span>
        <span class="trace-technical">{{ toolCall.name }}</span>
        <span class="trace-toggle">{{ expandedIds.has(toolCall.id) ? '收起' : '展开' }}</span>
      </button>

      <div v-if="expandedIds.has(toolCall.id)" class="trace-body">
        <p class="trace-label">输入</p>
        <pre class="trace-block">{{ toolCall.input || '{}' }}</pre>
        <p class="trace-label">输出</p>
        <pre class="trace-block">{{ toolCall.output || '等待结果...' }}</pre>
      </div>
    </article>
  </section>
</template>

<script setup>
import { ref } from 'vue';

defineProps({
  toolCalls: {
    type: Array,
    default: () => []
  }
});

const expandedIds = ref(new Set());

function toggle(toolCallId) {
  const next = new Set(expandedIds.value);
  if (next.has(toolCallId)) {
    next.delete(toolCallId);
  } else {
    next.add(toolCallId);
  }

  expandedIds.value = next;
}

function getToolDisplayName(toolName) {
  const names = {
    probe_document: '读取文档概况',
    read_section: '读取文档片段',
    grep_document: '搜索文档内容',
    get_selection_context: '读取当前选区',
    read_table: '读取表格',
    read_annotations: '读取批注',
    read_script: '诊断文档结构',
    patch_range: '执行标准补丁',
    execute_script: '执行脚本操作',
    todo_read: '读取任务板',
    todo_write: '更新任务板',
    ask_user_question: '询问用户'
  };
  return names[toolName] || toolName || '工具调用';
}
</script>

<style scoped>
.trace-panel {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.trace-card {
  border: 1px solid rgba(89, 118, 161, 0.16);
  border-radius: 14px;
  background: rgba(255, 255, 255, 0.92);
  overflow: hidden;
}

.trace-header {
  width: 100%;
  border: none;
  background: transparent;
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 10px 12px;
  font: inherit;
  color: #244464;
  cursor: pointer;
}

.trace-status {
  width: 8px;
  height: 8px;
  border-radius: 50%;
  flex: 0 0 auto;
}

.trace-status--running {
  background: #ee8d32;
}

.trace-status--success {
  background: #2d6a4f;
}

.trace-status--failed,
.trace-status--denied {
  background: #b42318;
}

.trace-status--skipped {
  background: #60758f;
}

.trace-name {
  flex: 1;
  font-size: 12px;
  font-weight: 600;
  text-align: left;
}

.trace-technical {
  max-width: 86px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-size: 10px;
  color: #8a9ab0;
}

.trace-toggle {
  font-size: 11px;
  color: #60758f;
}

.trace-body {
  padding: 0 12px 12px;
}

.trace-label {
  margin: 8px 0 4px;
  font-size: 11px;
  color: #60758f;
}

.trace-block {
  margin: 0;
  max-height: 140px;
  overflow: auto;
  padding: 8px;
  border-radius: 10px;
  background: #f4f7fb;
  font-size: 11px;
  line-height: 1.5;
  white-space: pre-wrap;
  word-break: break-word;
}

.trace-card {
  border-color: var(--sw-border);
  border-radius: var(--sw-radius-sm);
  background: var(--sw-surface);
  contain: content;
}

.trace-header {
  padding: 9px 10px;
  color: var(--sw-text-soft);
}

.trace-status--running {
  background: var(--sw-accent);
  box-shadow: 0 0 0 3px var(--sw-accent-soft);
}

.trace-status--success {
  background: var(--sw-success);
  box-shadow: 0 0 0 3px var(--sw-success-soft);
}

.trace-status--failed,
.trace-status--denied {
  background: var(--sw-danger);
  box-shadow: 0 0 0 3px var(--sw-danger-soft);
}

.trace-status--skipped {
  background: var(--sw-text-muted);
}

.trace-technical,
.trace-toggle,
.trace-label {
  color: var(--sw-text-muted);
}

.trace-block {
  border-radius: var(--sw-radius-xs);
  background: var(--sw-surface-muted);
  color: var(--sw-text-soft);
}
</style>
