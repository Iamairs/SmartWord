<template>
  <section class="todo-recovery-panel">
    <div class="todo-recovery-panel__header">
      <div>
        <p class="todo-recovery-panel__eyebrow">Recovery Required</p>
        <h2>任务板需要恢复确认</h2>
      </div>
      <span class="todo-recovery-panel__outcome">{{ outcomeText }}</span>
    </div>

    <p class="todo-recovery-panel__reason">{{ recovery?.recoveryReason || '检测到上一轮任务板状态异常。' }}</p>

    <p v-if="recovery?.lastErrorSummary" class="todo-recovery-panel__error">
      最近错误：{{ recovery.lastErrorSummary }}
    </p>

    <div v-if="orderedItems.length" class="todo-recovery-panel__snapshot">
      <p class="todo-recovery-panel__snapshot-title">旧任务板快照</p>
      <ul class="todo-recovery-panel__list">
        <li
          v-for="item in orderedItems"
          :key="item.id || item.order"
          class="todo-recovery-panel__item"
        >
          <span class="todo-recovery-panel__badge">{{ badgeText(item.status) }}</span>
          <span class="todo-recovery-panel__content">{{ item.id }} {{ item.content }}</span>
        </li>
      </ul>
    </div>

    <div class="todo-recovery-panel__actions">
      <button
        class="todo-recovery-panel__button"
        type="button"
        :disabled="recovery?.isSubmitting || !recovery?.canRecoverExisting"
        @click="$emit('recover', 'recover_existing')"
      >
        恢复旧任务板
      </button>
      <button
        class="todo-recovery-panel__button"
        type="button"
        :disabled="recovery?.isSubmitting || !recovery?.hasActivePlan"
        @click="$emit('recover', 'rebuild_from_active_plan')"
      >
        按当前计划重建
      </button>
      <button
        class="todo-recovery-panel__button todo-recovery-panel__button--danger"
        type="button"
        :disabled="recovery?.isSubmitting"
        @click="$emit('recover', 'discard_and_create_empty')"
      >
        丢弃并新建空板
      </button>
    </div>
  </section>
</template>

<script setup>
import { computed } from 'vue';

const props = defineProps({
  recovery: {
    type: Object,
    default: null
  }
});

defineEmits(['recover']);

const orderedItems = computed(() => {
  return Array.isArray(props.recovery?.board?.items)
    ? [...props.recovery.board.items].sort((left, right) => (left.order || 0) - (right.order || 0))
    : [];
});

const outcomeText = computed(() => {
  const raw = (props.recovery?.lastRunOutcome || '').toLowerCase();
  switch (raw) {
    case 'cancelled':
      return '上次结果：已取消';
    case 'rolledback':
      return '上次结果：已回滚';
    case 'crashedlike':
      return '上次结果：疑似崩溃';
    case 'failed':
      return '上次结果：执行失败';
    default:
      return '上次结果：待确认';
  }
});

function normalizeStatus(status) {
  if (typeof status === 'string') {
    const normalized = status.toLowerCase();
    return normalized === 'inprogress' ? 'in_progress' : normalized;
  }

  switch (status) {
    case 1:
      return 'in_progress';
    case 2:
      return 'completed';
    case 3:
      return 'failed';
    case 4:
      return 'skipped';
    case 0:
    default:
      return 'pending';
  }
}

function badgeText(status) {
  switch (normalizeStatus(status)) {
    case 'in_progress':
      return '进行中';
    case 'completed':
      return '已完成';
    case 'failed':
      return '失败';
    case 'skipped':
      return '已跳过';
    case 'pending':
    default:
      return '待处理';
  }
}
</script>

<style scoped>
.todo-recovery-panel {
  margin-bottom: 16px;
  padding: 16px;
  border-radius: 18px;
  border: 1px solid rgba(191, 87, 0, 0.18);
  background: linear-gradient(180deg, rgba(255, 247, 237, 0.96) 0%, rgba(255, 255, 255, 0.94) 100%);
  box-shadow: 0 18px 36px rgba(191, 87, 0, 0.08);
}

.todo-recovery-panel__header {
  display: flex;
  justify-content: space-between;
  align-items: flex-start;
  gap: 10px;
}

.todo-recovery-panel__eyebrow {
  margin: 0 0 4px;
  font-size: 12px;
  letter-spacing: 0.08em;
  text-transform: uppercase;
  color: #9a5a14;
}

.todo-recovery-panel__header h2 {
  margin: 0;
  font-size: 18px;
  color: #7a3f00;
}

.todo-recovery-panel__outcome {
  padding: 6px 10px;
  border-radius: 999px;
  background: rgba(191, 87, 0, 0.1);
  color: #8c4900;
  font-size: 11px;
  line-height: 1.4;
}

.todo-recovery-panel__reason,
.todo-recovery-panel__error {
  margin: 12px 0 0;
  font-size: 13px;
  line-height: 1.6;
  color: #6d4b2d;
}

.todo-recovery-panel__error {
  color: #a13f1c;
}

.todo-recovery-panel__snapshot {
  margin-top: 14px;
  padding: 12px;
  border-radius: 14px;
  background: rgba(255, 255, 255, 0.84);
  border: 1px solid rgba(191, 87, 0, 0.12);
}

.todo-recovery-panel__snapshot-title {
  margin: 0 0 10px;
  font-size: 12px;
  color: #8c4900;
}

.todo-recovery-panel__list {
  margin: 0;
  padding: 0;
  list-style: none;
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.todo-recovery-panel__item {
  display: flex;
  gap: 10px;
  align-items: flex-start;
}

.todo-recovery-panel__badge {
  flex: 0 0 auto;
  min-width: 52px;
  padding: 3px 8px;
  border-radius: 999px;
  background: rgba(24, 49, 79, 0.08);
  color: #244464;
  font-size: 11px;
  text-align: center;
}

.todo-recovery-panel__content {
  font-size: 13px;
  line-height: 1.5;
  color: #18314f;
}

.todo-recovery-panel__actions {
  margin-top: 14px;
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.todo-recovery-panel__button {
  width: 100%;
  padding: 10px 12px;
  border: 1px solid rgba(140, 73, 0, 0.18);
  border-radius: 12px;
  background: rgba(255, 255, 255, 0.92);
  color: #7a3f00;
  font: inherit;
  font-size: 13px;
  font-weight: 600;
  cursor: pointer;
}

.todo-recovery-panel__button--danger {
  border-color: rgba(161, 63, 28, 0.2);
  color: #a13f1c;
}

.todo-recovery-panel__button:disabled {
  cursor: not-allowed;
  opacity: 0.55;
}
</style>
