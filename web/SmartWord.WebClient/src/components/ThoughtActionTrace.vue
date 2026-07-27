<template>
  <section v-if="toolCalls.length" class="trace-panel">
    <button class="trace-summary" type="button" :aria-expanded="isExpanded" @click="togglePanel">
      <span class="trace-summary-label">{{ hasRunningCall ? '处理中' : '已处理' }}</span>
      <span class="trace-summary-duration">{{ formatDuration(totalDuration) }}</span>
      <span class="trace-summary-chevron" aria-hidden="true">{{ isExpanded ? '⌃' : '⌄' }}</span>
    </button>

    <div v-if="isExpanded" class="trace-list">
      <article v-for="toolCall in toolCalls" :key="toolCall.id" class="trace-item">
        <div class="trace-item-marker" aria-hidden="true">
          <span class="trace-status" :class="'trace-status--' + toolCall.status"></span>
        </div>
        <div class="trace-item-content">
          <button class="trace-header" type="button" :aria-expanded="expandedIds.has(toolCall.id)" @click="toggle(toolCall.id)">
            <span class="trace-action-icon" aria-hidden="true">{{ getToolIcon(toolCall.name) }}</span>
            <span class="trace-name">{{ getToolDisplayName(toolCall.name) }}</span>
            <span class="trace-technical">{{ toolCall.name }}</span>
            <span class="trace-duration">{{ formatDuration(getToolDuration(toolCall)) }}</span>
            <span class="trace-toggle">{{ expandedIds.has(toolCall.id) ? '收起' : '详情' }}</span>
          </button>
          <p v-if="toolCall.operationDescription" class="trace-description">{{ toolCall.operationDescription }}</p>
          <div v-if="expandedIds.has(toolCall.id)" class="trace-body">
            <p class="trace-label">输入</p>
            <pre class="trace-block">{{ toolCall.input || '{}' }}</pre>
            <p class="trace-label">输出</p>
            <pre class="trace-block">{{ toolCall.output || '等待结果...' }}</pre>
          </div>
        </div>
      </article>
    </div>
  </section>
</template>

<script setup>
import { computed, onBeforeUnmount, onMounted, ref } from 'vue';

const props = defineProps({
  toolCalls: {
    type: Array,
    default: () => []
  }
});

const isExpanded = ref(false);
const currentTime = ref(Date.now());
const expandedIds = ref(new Set());
let timerId = null;

const hasRunningCall = computed(() => props.toolCalls.some((toolCall) => toolCall.status === 'running'));
const totalDuration = computed(() => {
  const starts = props.toolCalls.map((toolCall) => toolCall.startedAt).filter(Number.isFinite);
  if (!starts.length) return 0;
  const latestEnd = hasRunningCall.value
    ? currentTime.value
    : Math.max(...props.toolCalls.map((toolCall) => toolCall.endedAt || toolCall.startedAt || 0));
  return Math.max(latestEnd - Math.min(...starts), 0);
});

onMounted(() => {
  timerId = window.setInterval(() => {
    if (hasRunningCall.value) currentTime.value = Date.now();
  }, 1000);
});

onBeforeUnmount(() => {
  if (timerId) window.clearInterval(timerId);
});

function togglePanel() {
  isExpanded.value = !isExpanded.value;
}

function toggle(toolCallId) {
  const next = new Set(expandedIds.value);
  if (next.has(toolCallId)) next.delete(toolCallId);
  else next.add(toolCallId);
  expandedIds.value = next;
}

function getToolDuration(toolCall) {
  if (!Number.isFinite(toolCall.startedAt)) return 0;
  const end = Number.isFinite(toolCall.endedAt) ? toolCall.endedAt : currentTime.value;
  return Math.max(end - toolCall.startedAt, 0);
}

function formatDuration(durationMs) {
  const totalSeconds = Math.max(Math.floor(durationMs / 1000), 0);
  const seconds = totalSeconds % 60;
  const minutes = Math.floor(totalSeconds / 60);
  return minutes > 0 ? minutes + '分 ' + seconds + '秒' : seconds + '秒';
}

function getToolIcon(toolName) {
  const icons = { probe_document: '⌕', read_section: '≡', grep_document: '⌕', get_selection_context: '⌖', read_table: '▦', read_annotations: '▤', read_script: '⌘', patch_range: '✎', execute_script: '▶', todo_read: '☷', todo_write: '✓', ask_user_question: '?' };
  return icons[toolName] || '•';
}

function getToolDisplayName(toolName) {
  const names = { probe_document: '读取文档概况', read_section: '读取文档片段', grep_document: '搜索文档内容', get_selection_context: '读取当前选区', read_table: '读取表格', read_annotations: '读取批注', read_script: '诊断文档结构', patch_range: '执行标准补丁', execute_script: '执行脚本操作', todo_read: '读取任务板', todo_write: '更新任务板', ask_user_question: '询问用户' };
  return names[toolName] || toolName || '工具调用';
}
</script>

<style scoped>
.trace-panel { display: flex; flex-direction: column; gap: 4px; }
.trace-summary { width: 100%; display: flex; align-items: center; gap: 6px; min-width: 0; padding: 8px 2px; border: 0; border-bottom: 1px solid var(--sw-border); background: transparent; color: var(--sw-text-muted); font: inherit; text-align: left; cursor: pointer; }
.trace-summary-label { color: var(--sw-text-soft); font-size: 12px; font-weight: 600; }
.trace-summary-duration { font-size: 12px; }
.trace-summary-chevron { margin-left: auto; font-size: 16px; line-height: 1; }
.trace-list { display: flex; flex-direction: column; max-height: min(42vh, 360px); overflow-y: auto; padding: 4px 2px 4px 0; scrollbar-width: thin; }
.trace-item { display: flex; min-width: 0; }
.trace-item-marker { position: relative; width: 18px; flex: 0 0 18px; display: flex; justify-content: center; }
.trace-item-marker::after { content: ''; position: absolute; top: 16px; bottom: 0; width: 1px; background: var(--sw-border); }
.trace-item:last-child .trace-item-marker::after { display: none; }
.trace-status { position: relative; z-index: 1; width: 7px; height: 7px; margin-top: 10px; border-radius: 50%; background: var(--sw-text-muted); }
.trace-status--running { background: var(--sw-accent); box-shadow: 0 0 0 3px var(--sw-accent-soft); }
.trace-status--success { background: var(--sw-success); }
.trace-status--failed, .trace-status--denied { background: var(--sw-danger); }
.trace-item-content { min-width: 0; flex: 1; border-bottom: 1px solid var(--sw-border); }
.trace-item:last-child .trace-item-content { border-bottom: 0; }
.trace-header { width: 100%; min-width: 0; display: flex; align-items: center; gap: 6px; padding: 8px 0; border: 0; background: transparent; color: var(--sw-text-soft); font: inherit; text-align: left; cursor: pointer; }
.trace-action-icon { flex: 0 0 16px; color: var(--sw-text-muted); font-size: 13px; text-align: center; }
.trace-name { min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; font-size: 12px; font-weight: 600; }
.trace-technical { max-width: 78px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; color: var(--sw-text-muted); font-size: 10px; }
.trace-duration, .trace-toggle { flex: 0 0 auto; color: var(--sw-text-muted); font-size: 10px; }
.trace-toggle { margin-left: auto; }
.trace-description { margin: -2px 0 7px 22px; overflow: hidden; color: var(--sw-text-muted); font-size: 10px; line-height: 1.45; text-overflow: ellipsis; white-space: nowrap; }
.trace-body { padding: 0 0 10px 22px; }
.trace-label { margin: 7px 0 3px; color: var(--sw-text-muted); font-size: 10px; }
.trace-block { max-height: 120px; margin: 0; overflow: auto; padding: 7px; border-radius: var(--sw-radius-xs); background: var(--sw-surface-muted); color: var(--sw-text-soft); font-size: 10px; line-height: 1.45; white-space: pre-wrap; word-break: break-word; }
</style>
