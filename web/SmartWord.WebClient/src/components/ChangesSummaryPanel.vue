<template>
  <section v-if="changes.length" class="changes-panel">
    <div class="changes-panel__header">
      <div>
        <p class="changes-panel__eyebrow">本次改动摘要</p>
        <h2>已完成 {{ changes.length }} 项变更</h2>
      </div>
    </div>

    <article v-for="change in changes" :key="change.id" class="changes-panel__item">
      <div class="changes-panel__item-head">
        <span class="changes-panel__tool">{{ change.toolName }}</span>
        <span class="changes-panel__count">
          {{ change.affectedParagraphs.length ? `段落 ${change.affectedParagraphs.join(', ')}` : '未提供段落索引' }}
        </span>
      </div>
      <p class="changes-panel__description">{{ change.operationDescription }}</p>
      <div v-if="change.affectedParagraphs.length" class="changes-panel__actions">
        <button
          v-for="paragraphIndex in change.affectedParagraphs"
          :key="`${change.id}-${paragraphIndex}`"
          class="changes-panel__jump"
          type="button"
          @click="$emit('navigate', paragraphIndex)"
        >
          跳转到段落 {{ paragraphIndex }}
        </button>
      </div>
    </article>
  </section>
</template>

<script setup>
defineProps({
  changes: {
    type: Array,
    default: () => []
  }
});

defineEmits(['navigate']);
</script>

<style scoped>
.changes-panel {
  padding: 14px;
  border-radius: 18px;
  background: rgba(255, 255, 255, 0.94);
  border: 1px solid rgba(38, 98, 75, 0.16);
  box-shadow: 0 16px 34px rgba(24, 49, 79, 0.08);
}

.changes-panel__eyebrow {
  margin: 0 0 4px;
  font-size: 11px;
  letter-spacing: 0.08em;
  text-transform: uppercase;
  color: #2d6a4f;
}

.changes-panel__header h2 {
  margin: 0;
  font-size: 16px;
  color: #224a3c;
}

.changes-panel__item {
  margin-top: 12px;
  padding-top: 12px;
  border-top: 1px solid rgba(89, 118, 161, 0.12);
}

.changes-panel__item:first-of-type {
  margin-top: 10px;
}

.changes-panel__item-head {
  display: flex;
  justify-content: space-between;
  gap: 10px;
  align-items: center;
}

.changes-panel__tool {
  font-size: 12px;
  font-weight: 700;
  color: #18314f;
}

.changes-panel__count {
  font-size: 11px;
  color: #60758f;
}

.changes-panel__description {
  margin: 8px 0 0;
  font-size: 12px;
  line-height: 1.6;
  color: #395372;
}

.changes-panel__actions {
  margin-top: 10px;
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
}

.changes-panel__jump {
  padding: 8px 10px;
  border-radius: 10px;
  border: 1px solid rgba(36, 68, 100, 0.18);
  background: #f4f7fb;
  color: #244464;
  font: inherit;
  font-size: 11px;
  cursor: pointer;
}
</style>
