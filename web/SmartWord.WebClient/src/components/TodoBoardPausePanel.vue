<template>
  <section class="todo-pause-panel">
    <div class="todo-pause-panel__header">
      <div>
        <p class="todo-pause-panel__eyebrow">Paused</p>
        <h2>任务已暂停</h2>
      </div>
      <span class="todo-pause-panel__outcome">{{ outcomeText }}</span>
    </div>

    <p class="todo-pause-panel__reason">
      {{ pause?.message || '当前步骤已回退，已保留之前通过验证的成果，请选择继续方式。' }}
    </p>

    <p v-if="pause?.lastErrorSummary" class="todo-pause-panel__error">
      最近提示：{{ pause.lastErrorSummary }}
    </p>

    <div v-if="orderedItems.length" class="todo-pause-panel__snapshot">
      <p class="todo-pause-panel__snapshot-title">最近可信任务板快照</p>
      <ul class="todo-pause-panel__list">
        <li
          v-for="item in orderedItems"
          :key="item.id || item.order"
          class="todo-pause-panel__item"
        >
          <span class="todo-pause-panel__badge">{{ badgeText(item.status) }}</span>
          <span class="todo-pause-panel__content">{{ item.id }} {{ item.content }}</span>
        </li>
      </ul>
    </div>

    <div class="todo-pause-panel__actions">
      <button
        class="todo-pause-panel__button"
        type="button"
        :disabled="pause?.isSubmitting || !pause?.canRecoverExisting"
        @click="$emit('resume', 'recover_existing')"
      >
        继续执行当前任务
      </button>
      <button
        class="todo-pause-panel__button"
        type="button"
        :disabled="pause?.isSubmitting || !pause?.hasActivePlan"
        @click="$emit('resume', 'rebuild_from_active_plan')"
      >
        按当前计划重建
      </button>
      <button
        class="todo-pause-panel__button todo-pause-panel__button--danger"
        type="button"
        :disabled="pause?.isSubmitting"
        @click="$emit('resume', 'discard_and_create_empty')"
      >
        丢弃并新建空板
      </button>
    </div>
  </section>
</template>

<script setup>
import { computed } from 'vue';

const props = defineProps({
  pause: {
    type: Object,
    default: null
  }
});

defineEmits(['resume']);

const orderedItems = computed(() => {
  return Array.isArray(props.pause?.board?.items)
    ? [...props.pause.board.items].sort((left, right) => (left.order || 0) - (right.order || 0))
    : [];
});

const outcomeText = computed(() => {
  const raw = (props.pause?.lastRunOutcome || '').toLowerCase();
  switch (raw) {
    case 'rolledback':
      return '本轮结果：当前步已回退';
    case 'pausedbybudget':
      return '本轮结果：预算暂停';
    case 'cancelled':
      return '本轮结果：已取消';
    case 'failed':
      return '本轮结果：执行中断';
    default:
      return '本轮结果：已暂停';
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
.todo-pause-panel {
  margin-bottom: 16px;
  padding: 16px;
  border-radius: 18px;
  border: 1px solid rgba(30, 112, 91, 0.18);
  background: linear-gradient(180deg, rgba(239, 253, 249, 0.96) 0%, rgba(255, 255, 255, 0.94) 100%);
  box-shadow: 0 18px 36px rgba(30, 112, 91, 0.08);
}

.todo-pause-panel__header {
  display: flex;
  justify-content: space-between;
  align-items: flex-start;
  gap: 10px;
}

.todo-pause-panel__eyebrow {
  margin: 0 0 4px;
  font-size: 12px;
  letter-spacing: 0.08em;
  text-transform: uppercase;
  color: #1e705b;
}

.todo-pause-panel__header h2 {
  margin: 0;
  font-size: 18px;
  color: #135345;
}

.todo-pause-panel__outcome {
  padding: 6px 10px;
  border-radius: 999px;
  background: rgba(30, 112, 91, 0.1);
  color: #1b6654;
  font-size: 11px;
  line-height: 1.4;
}

.todo-pause-panel__reason,
.todo-pause-panel__error {
  margin: 12px 0 0;
  font-size: 13px;
  line-height: 1.6;
  color: #315d52;
}

.todo-pause-panel__error {
  color: #7f4c12;
}

.todo-pause-panel__snapshot {
  margin-top: 14px;
  padding: 12px;
  border-radius: 14px;
  background: rgba(255, 255, 255, 0.84);
  border: 1px solid rgba(30, 112, 91, 0.12);
}

.todo-pause-panel__snapshot-title {
  margin: 0 0 10px;
  font-size: 12px;
  color: #1b6654;
}

.todo-pause-panel__list {
  margin: 0;
  padding: 0;
  list-style: none;
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.todo-pause-panel__item {
  display: flex;
  gap: 10px;
  align-items: flex-start;
}

.todo-pause-panel__badge {
  flex: 0 0 auto;
  min-width: 52px;
  padding: 3px 8px;
  border-radius: 999px;
  background: rgba(24, 49, 79, 0.08);
  color: #244464;
  font-size: 11px;
  text-align: center;
}

.todo-pause-panel__content {
  font-size: 13px;
  line-height: 1.5;
  color: #18314f;
}

.todo-pause-panel__actions {
  margin-top: 14px;
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.todo-pause-panel__button {
  width: 100%;
  padding: 10px 12px;
  border: 1px solid rgba(27, 102, 84, 0.18);
  border-radius: 12px;
  background: rgba(255, 255, 255, 0.92);
  color: #135345;
  font: inherit;
  font-size: 13px;
  font-weight: 600;
  cursor: pointer;
}

.todo-pause-panel__button--danger {
  border-color: rgba(161, 63, 28, 0.2);
  color: #a13f1c;
}

.todo-pause-panel__button:disabled {
  cursor: not-allowed;
  opacity: 0.55;
}
</style>
