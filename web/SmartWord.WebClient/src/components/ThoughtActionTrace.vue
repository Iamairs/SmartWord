<template>
  <section v-if="toolCalls.length" class="trace-panel">
    <article v-for="toolCall in toolCalls" :key="toolCall.id" class="trace-card">
      <button class="trace-header" type="button" @click="toggle(toolCall.id)">
        <span class="trace-status" :class="`trace-status--${toolCall.status}`"></span>
        <span class="trace-name">{{ toolCall.name }}</span>
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
</style>
