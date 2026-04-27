<template>
  <section class="task-history-panel">
    <div class="task-history-panel__header">
      <div>
        <h2>历史</h2>
        <p>当前文档最近任务</p>
      </div>
      <button class="ghost-button ghost-button--small" type="button" :disabled="store.isLoading" @click="store.loadRecentTasks()">
        刷新
      </button>
    </div>

    <p v-if="store.errorMessage" class="history-message history-message--error">{{ store.errorMessage }}</p>
    <p v-else-if="store.isLoading" class="history-message">正在读取历史...</p>
    <p v-else-if="!store.items.length" class="history-message">当前文档还没有任务历史。</p>

    <div v-else class="task-history-list">
      <button
        v-for="item in store.items"
        :key="item.id"
        class="task-history-item"
        type="button"
        :class="{ 'task-history-item--active': selectedRunId === item.id }"
        @click="selectTask(item.id)"
      >
        <span class="task-history-item__top">
          <span>{{ formatTime(item.startedAtUtc) }}</span>
          <span :class="['status-pill', `status-pill--${normalizeStatus(item.status)}`]">
            {{ statusLabel(item.status) }}
          </span>
        </span>
        <strong>{{ truncate(item.userGoal, 58) }}</strong>
        <span class="task-history-item__meta">
          {{ modeLabel(item.mode) }} · {{ permissionLabel(item.permissionMode) }}
        </span>
        <span class="task-history-item__counts">
          工具 {{ item.toolCount || 0 }} · 改动 {{ item.changeCount || 0 }} · 验证 {{ item.verifiedChangeCount || 0 }}
        </span>
        <span v-if="item.failureReason || item.summary" class="task-history-item__summary">
          {{ truncate(item.failureReason || item.summary, 70) }}
        </span>
      </button>
    </div>

    <div v-if="store.selectedTask" class="task-history-detail">
      <div class="task-history-detail__header">
        <h3>任务详情</h3>
        <button class="ghost-button ghost-button--small" type="button" @click="store.clearSelectedTask()">收起</button>
      </div>
      <p v-if="store.isLoadingDetail" class="history-message">正在读取详情...</p>
      <template v-else>
        <dl class="detail-grid">
          <div>
            <dt>目标</dt>
            <dd>{{ detailRun.userGoal }}</dd>
          </div>
          <div>
            <dt>模式</dt>
            <dd>{{ modeLabel(detailRun.mode) }} · {{ statusLabel(detailRun.status) }}</dd>
          </div>
          <div>
            <dt>权限</dt>
            <dd>{{ permissionLabel(detailRun.permissionMode) }}</dd>
          </div>
          <div>
            <dt>模型</dt>
            <dd>{{ detailRun.model || '未记录' }}</dd>
          </div>
          <div v-if="detailRun.summary || detailRun.failureReason || detailRun.cancelReason">
            <dt>结果</dt>
            <dd>{{ detailRun.failureReason || detailRun.cancelReason || detailRun.summary }}</dd>
          </div>
        </dl>

        <div v-if="detailTools.length" class="detail-section">
          <h4>工具调用</h4>
          <div v-for="tool in detailTools" :key="`${tool.toolCallId}-${tool.createdAtUtc}`" class="detail-row">
            <span>{{ tool.toolName }}</span>
            <strong>{{ tool.success ? '成功' : '失败' }}</strong>
            <p>{{ tool.operationDescription || truncate(tool.output, 80) }}</p>
          </div>
        </div>

        <div v-if="detailChanges.length" class="detail-section">
          <h4>文档改动</h4>
          <div v-for="change in detailChanges" :key="`${change.toolCallId}-${change.status}-${change.createdAtUtc}`" class="detail-row">
            <span>{{ changeStatusLabel(change.status) }}</span>
            <strong>{{ change.toolName || '文档操作' }}</strong>
            <p>{{ change.operationDescription || change.message }}</p>
            <div v-if="change.affectedParagraphs?.length" class="paragraph-links">
              <button
                v-for="paragraph in change.affectedParagraphs"
                :key="paragraph"
                class="paragraph-link"
                type="button"
                @click="$emit('navigate', paragraph)"
              >
                段落 {{ paragraph }}
              </button>
            </div>
          </div>
        </div>
      </template>
    </div>
  </section>
</template>

<script setup>
import { computed } from 'vue';
import { useTaskHistoryStore } from '../stores/taskHistory';

defineEmits(['navigate']);

const store = useTaskHistoryStore();

const selectedRunId = computed(() => store.selectedTask?.run?.id || '');
const detailRun = computed(() => store.selectedTask?.run || {});
const detailTools = computed(() => store.selectedTask?.tools || []);
const detailChanges = computed(() => store.selectedTask?.changes || []);

async function selectTask(taskRunId) {
  if (selectedRunId.value === taskRunId) {
    store.clearSelectedTask();
    return;
  }

  await store.loadTaskDetail(taskRunId);
}

function normalizeStatus(status) {
  return String(status || '').trim().toLowerCase();
}

function statusLabel(status) {
  const labels = {
    completed: '已完成',
    failed: '失败',
    cancelled: '已取消',
    canceled: '已取消',
    paused: '已暂停',
    running: '运行中'
  };

  return labels[normalizeStatus(status)] || '未知';
}

function modeLabel(mode) {
  const labels = {
    ask: '对话交流',
    plan: '规划任务',
    agent: '自主执行'
  };

  return labels[String(mode || '').toLowerCase()] || '未知模式';
}

function permissionLabel(permissionMode) {
  const labels = {
    read_only: '只读模式',
    confirm_writes: '写入前确认',
    auto_safe_writes: '自动安全写入',
    full_auto: '全自动执行'
  };

  return labels[String(permissionMode || '').toLowerCase()] || '未记录权限';
}

function changeStatusLabel(status) {
  const labels = {
    executed: '已执行',
    verified: '已验证',
    unverified: '未验证',
    verification_failed: '验证失败',
    repair_required: '待修复'
  };

  return labels[String(status || '').toLowerCase()] || '已记录';
}

function formatTime(value) {
  if (!value) {
    return '未记录时间';
  }

  const date = new Date(value);
  if (Number.isNaN(date.getTime())) {
    return '未记录时间';
  }

  return date.toLocaleString('zh-CN', {
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit'
  });
}

function truncate(text, maxLength) {
  const value = String(text || '').trim();
  if (value.length <= maxLength) {
    return value;
  }

  return `${value.slice(0, maxLength)}...`;
}
</script>

<style scoped>
.task-history-panel {
  padding: 12px;
  border-radius: 8px;
  background: rgba(255, 255, 255, 0.94);
  border: 1px solid rgba(89, 118, 161, 0.18);
  box-shadow: 0 12px 28px rgba(24, 49, 79, 0.08);
}

.task-history-panel__header,
.task-history-detail__header,
.task-history-item__top {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 8px;
}

.task-history-panel h2,
.task-history-panel h3,
.task-history-panel h4,
.task-history-panel p {
  margin: 0;
}

.task-history-panel h2 {
  font-size: 16px;
}

.task-history-panel h3 {
  font-size: 14px;
}

.task-history-panel h4 {
  font-size: 12px;
  color: #395372;
}

.task-history-panel__header p,
.history-message,
.task-history-item__meta,
.task-history-item__counts,
.task-history-item__summary {
  font-size: 11px;
  line-height: 1.45;
  color: #60758f;
}

.history-message {
  margin-top: 10px;
}

.history-message--error {
  color: #b42318;
}

.task-history-list,
.task-history-detail,
.detail-section {
  margin-top: 10px;
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.task-history-item {
  width: 100%;
  display: flex;
  flex-direction: column;
  gap: 5px;
  padding: 10px;
  border: 1px solid rgba(89, 118, 161, 0.16);
  border-radius: 8px;
  background: #f7f9fc;
  color: #18314f;
  font: inherit;
  text-align: left;
  cursor: pointer;
}

.task-history-item--active {
  border-color: rgba(24, 49, 79, 0.48);
  background: #eef4fb;
}

.task-history-item strong {
  font-size: 12px;
  line-height: 1.45;
}

.status-pill {
  flex: 0 0 auto;
  padding: 3px 6px;
  border-radius: 999px;
  font-size: 10px;
  font-weight: 700;
  background: #e6eef7;
  color: #244464;
}

.status-pill--completed {
  background: #dff3e8;
  color: #24613f;
}

.status-pill--failed {
  background: #fde8e5;
  color: #9b2c1d;
}

.status-pill--cancelled,
.status-pill--canceled,
.status-pill--paused {
  background: #fff0d8;
  color: #8a4b12;
}

.detail-grid {
  margin: 10px 0 0;
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.detail-grid div,
.detail-row {
  padding: 8px;
  border-radius: 8px;
  background: #f7f9fc;
}

.detail-grid dt {
  margin-bottom: 3px;
  font-size: 10px;
  font-weight: 700;
  color: #60758f;
}

.detail-grid dd {
  margin: 0;
  font-size: 12px;
  line-height: 1.45;
  color: #18314f;
}

.detail-row {
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.detail-row span,
.detail-row strong {
  font-size: 11px;
}

.detail-row p {
  font-size: 11px;
  line-height: 1.45;
  color: #60758f;
}

.paragraph-links {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
}

.paragraph-link {
  border: none;
  border-radius: 999px;
  padding: 4px 7px;
  background: #18314f;
  color: #ffffff;
  font-size: 10px;
  cursor: pointer;
}
</style>
