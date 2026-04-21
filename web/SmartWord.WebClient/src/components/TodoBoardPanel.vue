<template>
  <section class="todo-board-panel">
    <div class="todo-board-panel__header">
      <div>
        <p class="todo-board-panel__eyebrow">Todo Board</p>
        <h2>执行任务板</h2>
      </div>
      <p class="todo-board-panel__meta">
        {{ statsText }}
      </p>
    </div>

    <ul class="todo-board-panel__list">
      <li
        v-for="item in orderedItems"
        :key="item.id || item.order"
        class="todo-board-panel__item"
        :class="[`todo-board-panel__item--${normalizeStatus(item.status)}`, { 'todo-board-panel__item--active': isActive(item) }]"
      >
        <span class="todo-board-panel__badge">{{ badgeText(item.status) }}</span>
        <div class="todo-board-panel__content">
          <p class="todo-board-panel__title">{{ item.id }} {{ item.content }}</p>
          <p v-if="item.notes" class="todo-board-panel__notes">{{ item.notes }}</p>
        </div>
      </li>
    </ul>
  </section>
</template>

<script setup>
import { computed } from 'vue';

const props = defineProps({
  board: {
    type: Object,
    default: null
  },
  currentTodoId: {
    type: String,
    default: ''
  }
});

const orderedItems = computed(() => {
  return Array.isArray(props.board?.items)
    ? [...props.board.items].sort((left, right) => (left.order || 0) - (right.order || 0))
    : [];
});

const statsText = computed(() => {
  const items = orderedItems.value;
  const statusCount = {
    pending: 0,
    in_progress: 0,
    completed: 0,
    failed: 0,
    skipped: 0
  };

  items.forEach((item) => {
    const status = normalizeStatus(item.status);
    if (statusCount[status] !== undefined) {
      statusCount[status] += 1;
    }
  });

  return `共 ${items.length} 项 · 进行中 ${statusCount.in_progress} · 已完成 ${statusCount.completed} · 失败 ${statusCount.failed}`;
});

function normalizeStatus(status) {
  if (typeof status === 'string') {
    const normalized = status.toLowerCase();
    if (normalized === 'inprogress') {
      return 'in_progress';
    }

    return normalized;
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

function isActive(item) {
  return Boolean(props.currentTodoId) && item?.id === props.currentTodoId;
}
</script>

<style scoped>
.todo-board-panel {
  margin-bottom: 16px;
  padding: 16px;
  border-radius: 18px;
  border: 1px solid rgba(24, 49, 79, 0.08);
  background: rgba(255, 255, 255, 0.78);
  box-shadow: 0 14px 28px rgba(24, 49, 79, 0.08);
}

.todo-board-panel__header {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 12px;
  margin-bottom: 14px;
}

.todo-board-panel__eyebrow {
  margin: 0 0 4px;
  font-size: 12px;
  letter-spacing: 0.08em;
  text-transform: uppercase;
  color: #5f7a99;
}

.todo-board-panel__header h2 {
  margin: 0;
  font-size: 18px;
  color: #18314f;
}

.todo-board-panel__meta {
  margin: 0;
  font-size: 12px;
  color: #5f7a99;
}

.todo-board-panel__list {
  display: flex;
  flex-direction: column;
  gap: 10px;
  margin: 0;
  padding: 0;
  list-style: none;
}

.todo-board-panel__item {
  display: flex;
  gap: 12px;
  align-items: flex-start;
  padding: 12px 14px;
  border-radius: 14px;
  background: #f7f9fc;
  border: 1px solid transparent;
}

.todo-board-panel__item--active {
  border-color: rgba(30, 102, 195, 0.18);
  background: rgba(30, 102, 195, 0.08);
}

.todo-board-panel__item--completed {
  background: rgba(27, 94, 32, 0.08);
}

.todo-board-panel__item--failed {
  background: rgba(186, 26, 26, 0.08);
}

.todo-board-panel__item--skipped {
  background: rgba(96, 108, 122, 0.1);
}

.todo-board-panel__badge {
  flex: 0 0 auto;
  min-width: 58px;
  padding: 4px 8px;
  border-radius: 999px;
  font-size: 11px;
  line-height: 1.4;
  text-align: center;
  background: #e6edf5;
  color: #18314f;
}

.todo-board-panel__content {
  min-width: 0;
}

.todo-board-panel__title {
  margin: 0;
  font-size: 14px;
  line-height: 1.5;
  color: #18314f;
}

.todo-board-panel__notes {
  margin: 4px 0 0;
  font-size: 12px;
  line-height: 1.5;
  color: #5f7a99;
}
</style>
