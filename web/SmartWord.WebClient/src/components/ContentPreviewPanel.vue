<template>
  <section class="preview-panel">
    <div class="preview-panel__header">
      <div>
        <p class="preview-panel__eyebrow">待确认写操作</p>
        <h2>{{ confirmation.toolName }}</h2>
      </div>
      <span class="preview-panel__status">等待你的决定</span>
    </div>

    <p class="preview-panel__description">
      {{ confirmation.operationDescription || '即将执行写操作，请先确认。' }}
    </p>

    <details class="preview-panel__details">
      <summary>查看原始输入</summary>
      <pre>{{ confirmation.toolInput || '{}' }}</pre>
    </details>

    <div class="preview-panel__actions">
      <button
        class="preview-panel__button preview-panel__button--confirm"
        type="button"
        :disabled="confirmation.isSubmitting"
        @click="$emit('confirm')"
      >
        {{ confirmation.isSubmitting ? '提交中...' : '确认执行' }}
      </button>
      <button
        class="preview-panel__button preview-panel__button--skip"
        type="button"
        :disabled="confirmation.isSubmitting"
        @click="$emit('skip')"
      >
        跳过此步
      </button>
      <button
        class="preview-panel__button preview-panel__button--cancel"
        type="button"
        :disabled="confirmation.isSubmitting"
        @click="$emit('cancel')"
      >
        取消任务
      </button>
    </div>
  </section>
</template>

<script setup>
defineProps({
  confirmation: {
    type: Object,
    required: true
  }
});

defineEmits(['confirm', 'skip', 'cancel']);
</script>

<style scoped>
.preview-panel {
  padding: 14px;
  border-radius: 18px;
  background: linear-gradient(180deg, rgba(255, 247, 237, 0.98) 0%, rgba(255, 255, 255, 0.94) 100%);
  border: 1px solid rgba(217, 111, 50, 0.24);
  box-shadow: 0 18px 36px rgba(217, 111, 50, 0.12);
}

.preview-panel__header {
  display: flex;
  justify-content: space-between;
  gap: 10px;
  align-items: flex-start;
}

.preview-panel__eyebrow {
  margin: 0 0 4px;
  font-size: 11px;
  letter-spacing: 0.08em;
  text-transform: uppercase;
  color: #a64b18;
}

.preview-panel__header h2 {
  margin: 0;
  font-size: 16px;
  color: #7a3110;
}

.preview-panel__status {
  padding: 6px 10px;
  border-radius: 999px;
  background: rgba(217, 111, 50, 0.12);
  color: #a64b18;
  font-size: 11px;
  font-weight: 600;
}

.preview-panel__description {
  margin: 10px 0 0;
  font-size: 12px;
  line-height: 1.6;
  color: #6f4f38;
}

.preview-panel__details {
  margin-top: 10px;
}

.preview-panel__details summary {
  cursor: pointer;
  font-size: 12px;
  font-weight: 600;
  color: #7a3110;
}

.preview-panel__details pre {
  margin: 8px 0 0;
  max-height: 160px;
  overflow: auto;
  padding: 10px;
  border-radius: 12px;
  background: rgba(255, 255, 255, 0.78);
  font-size: 11px;
  line-height: 1.5;
  white-space: pre-wrap;
  word-break: break-word;
}

.preview-panel__actions {
  margin-top: 12px;
  display: grid;
  grid-template-columns: repeat(3, minmax(0, 1fr));
  gap: 8px;
}

.preview-panel__button {
  min-height: 38px;
  border-radius: 12px;
  border: none;
  font: inherit;
  font-size: 12px;
  font-weight: 600;
  cursor: pointer;
}

.preview-panel__button:disabled {
  cursor: not-allowed;
  opacity: 0.65;
}

.preview-panel__button--confirm {
  background: linear-gradient(135deg, #d96f32 0%, #ee8d32 100%);
  color: #ffffff;
}

.preview-panel__button--skip {
  background: rgba(255, 255, 255, 0.9);
  color: #6f4f38;
  border: 1px solid rgba(166, 75, 24, 0.16);
}

.preview-panel__button--cancel {
  background: #ffffff;
  color: #9f1239;
  border: 1px solid rgba(159, 18, 57, 0.18);
}
</style>
