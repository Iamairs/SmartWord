<template>
  <section v-if="changes.length" class="changes-panel">
    <div class="changes-panel__header">
      <div>
        <p class="changes-panel__eyebrow">本次改动摘要</p>
        <h2>{{ summaryTitle }}</h2>
        <p v-if="summaryHint" class="changes-panel__summary">{{ summaryHint }}</p>
      </div>
    </div>

    <article v-for="change in changes" :key="change.id" class="changes-panel__item">
      <div class="changes-panel__item-head">
        <span class="changes-panel__tool">{{ change.toolName }}</span>
      </div>
      <div class="changes-panel__meta">
        <span class="changes-panel__status" :class="`changes-panel__status--${change.status || 'executed'}`">
          {{ getStatusLabel(change.status) }}
        </span>
        <span class="changes-panel__count">
          {{ change.affectedParagraphs.length ? `段落 ${change.affectedParagraphs.join(', ')}` : '未提供段落索引' }}
        </span>
      </div>
      <p class="changes-panel__description">{{ change.operationDescription }}</p>
      <p v-if="change.statusMessage" class="changes-panel__message">{{ change.statusMessage }}</p>
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
import { computed } from 'vue';

const props = defineProps({
  changes: {
    type: Array,
    default: () => []
  }
});

defineEmits(['navigate']);

const verifiedCount = computed(() => props.changes.filter((item) => item.status === 'verified').length);
const verificationFailedCount = computed(
  () => props.changes.filter((item) => item.status === 'verification_failed').length
);
const repairRequiredCount = computed(
  () => props.changes.filter((item) => item.status === 'repair_required').length
);
const unverifiedCount = computed(
  () =>
    props.changes.filter(
      (item) => item.status === 'unverified' || item.status === 'executed'
    ).length
);

const summaryTitle = computed(() => {
  if (!props.changes.length) {
    return '';
  }

  if (verifiedCount.value === props.changes.length) {
    return `已验证生效 ${props.changes.length} 项变更`;
  }

  if (repairRequiredCount.value > 0) {
    return `已提交 ${props.changes.length} 项改动尝试`;
  }

  return `已执行 ${props.changes.length} 项写入`;
});

const summaryHint = computed(() => {
  if (!props.changes.length) {
    return '';
  }

  if (verifiedCount.value === props.changes.length) {
    return '本次写入均已通过 verify_change 验证。';
  }

  const fragments = [];
  if (verifiedCount.value > 0) {
    fragments.push(`${verifiedCount.value} 项已验证生效`);
  }

  if (unverifiedCount.value > 0) {
    fragments.push(`${unverifiedCount.value} 项未完成验证`);
  }

  if (verificationFailedCount.value > 0) {
    fragments.push(`${verificationFailedCount.value} 项验证失败`);
  }

  if (repairRequiredCount.value > 0) {
    fragments.push(`${repairRequiredCount.value} 项待修复`);
  }

  return fragments.join('，');
});

function getStatusLabel(status) {
  switch (status) {
    case 'verified':
      return '已验证生效';
    case 'verification_failed':
      return '验证失败';
    case 'repair_required':
      return '执行失败，待修复';
    case 'unverified':
      return '已执行但未验证';
    default:
      return '已执行，待验证';
  }
}
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

.changes-panel__summary {
  margin: 6px 0 0;
  font-size: 12px;
  color: #60758f;
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
  gap: 10px;
  align-items: center;
}

.changes-panel__meta {
  margin-top: 6px;
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

.changes-panel__status {
  display: inline-flex;
  align-items: center;
  padding: 4px 8px;
  border-radius: 999px;
  font-size: 11px;
  font-weight: 700;
}

.changes-panel__status--executed,
.changes-panel__status--unverified {
  background: rgba(243, 191, 73, 0.18);
  color: #8b5a00;
}

.changes-panel__status--verified {
  background: rgba(45, 106, 79, 0.12);
  color: #2d6a4f;
}

.changes-panel__status--repair_required,
.changes-panel__status--verification_failed {
  background: rgba(177, 63, 47, 0.12);
  color: #b13f2f;
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

.changes-panel__message {
  margin: 6px 0 0;
  font-size: 11px;
  line-height: 1.6;
  color: #60758f;
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
