<template>
  <section class="preview-panel">
    <div class="preview-panel__header">
      <div>
        <p class="preview-panel__eyebrow">待确认写操作</p>
        <h2>{{ preview.title }}</h2>
      </div>
      <span class="preview-panel__status" :class="`preview-panel__status--${preview.riskLevel}`">
        {{ preview.riskLabel }}
      </span>
    </div>

    <p class="preview-panel__description">
      {{ confirmation.operationDescription || '即将执行写操作，请先确认。' }}
    </p>

    <div class="preview-panel__summary">
      <div>
        <span>影响范围</span>
        <strong>{{ preview.scopeLabel }}</strong>
      </div>
      <div>
        <span>验证方式</span>
        <strong>{{ preview.verifyLabel }}</strong>
      </div>
      <div>
        <span>撤销能力</span>
        <strong>当前步骤可回滚</strong>
      </div>
    </div>

    <div v-if="preview.operations.length" class="preview-panel__operations">
      <p>将执行的操作</p>
      <ol>
        <li v-for="(operation, index) in preview.operations" :key="index">
          {{ operation }}
        </li>
      </ol>
    </div>

    <details class="preview-panel__details">
      <summary>查看技术详情</summary>
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
import { computed } from 'vue';

const props = defineProps({
  confirmation: {
    type: Object,
    required: true
  }
});

defineEmits(['confirm', 'skip', 'cancel']);

const parsedInput = computed(() => {
  try {
    return JSON.parse(props.confirmation.toolInput || '{}');
  } catch {
    return {};
  }
});

const preview = computed(() => {
  const toolName = props.confirmation.toolName || 'unknown_tool';
  if (toolName === 'patch_range') {
    return buildPatchRangePreview(parsedInput.value);
  }

  if (toolName === 'execute_script') {
    return buildScriptPreview(parsedInput.value);
  }

  return {
    title: getToolDisplayName(toolName),
    riskLevel: 'medium',
    riskLabel: '中风险',
    scopeLabel: '待确认',
    verifyLabel: '系统将尝试验证',
    operations: []
  };
});

function buildPatchRangePreview(input) {
  const operations = Array.isArray(input.operations) ? input.operations : [];
  const affectedParagraphs = collectPatchParagraphs(operations);
  return {
    title: '标准文档补丁',
    riskLevel: operations.some((item) => item?.type === 'delete_paragraph' || item?.type === 'delete')
      ? 'high'
      : 'low',
    riskLabel: operations.some((item) => item?.type === 'delete_paragraph' || item?.type === 'delete')
      ? '较高风险'
      : '低风险',
    scopeLabel: affectedParagraphs.length
      ? `段落 ${affectedParagraphs.join(', ')}`
      : '由工具输入决定',
    verifyLabel: '系统自动验证',
    operations: operations.map(formatPatchOperation)
  };
}

function buildScriptPreview(input) {
  const affectedParagraphs = Array.isArray(input.affected_paragraphs) ? input.affected_paragraphs : [];
  return {
    title: '脚本类文档操作',
    riskLevel: 'high',
    riskLabel: '高风险',
    scopeLabel: affectedParagraphs.length
      ? `段落 ${affectedParagraphs.join(', ')}`
      : '脚本自行定位，需重点确认',
    verifyLabel: input.verify_code ? '脚本验证计划' : '缺少显式验证',
    operations: [
      input.description || '执行一段受控 C# 脚本修改 Word 文档。',
      input.verify_code ? '写入后执行只读验证脚本。' : '当前输入未提供可展示的验证脚本。'
    ]
  };
}

function collectPatchParagraphs(operations) {
  const values = new Set();
  operations.forEach((operation) => {
    const index = operation?.paragraph_index;
    if (Number.isInteger(index)) {
      values.add(index);
    }
  });
  return [...values].sort((a, b) => a - b);
}

function formatPatchOperation(operation) {
  const type = String(operation?.type || '').trim();
  const paragraphIndex = Number.isInteger(operation?.paragraph_index)
    ? operation.paragraph_index
    : '未知';
  const text = summarizeText(operation?.text || '');
  const style = operation?.style || '';

  switch (type) {
    case 'replace_text':
    case 'replace':
    case 'set_text':
      return `替换第 ${paragraphIndex} 段文本${text ? `为“${text}”` : ''}。`;
    case 'insert_paragraph_after':
    case 'insert_after':
      return `在第 ${paragraphIndex} 段后插入新段落${text ? `：“${text}”` : ''}。`;
    case 'set_paragraph_style':
    case 'set_style':
      return `将第 ${paragraphIndex} 段样式设置为 ${style || '指定样式'}。`;
    case 'delete_paragraph':
    case 'delete':
      return `删除第 ${paragraphIndex} 段。`;
    default:
      return `对第 ${paragraphIndex} 段执行 ${type || '未知'} 操作。`;
  }
}

function summarizeText(text) {
  const normalized = String(text || '').replace(/\s+/g, ' ').trim();
  return normalized.length > 36 ? `${normalized.slice(0, 36)}...` : normalized;
}

function getToolDisplayName(toolName) {
  const names = {
    patch_range: '标准文档补丁',
    execute_script: '脚本类文档操作',
    todo_write: '任务板更新'
  };
  return names[toolName] || toolName;
}
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

.preview-panel__status--low {
  background: rgba(45, 106, 79, 0.12);
  color: #2d6a4f;
}

.preview-panel__status--medium {
  background: rgba(243, 191, 73, 0.18);
  color: #8b5a00;
}

.preview-panel__status--high {
  background: rgba(180, 35, 24, 0.12);
  color: #b42318;
}

.preview-panel__description {
  margin: 10px 0 0;
  font-size: 12px;
  line-height: 1.6;
  color: #6f4f38;
}

.preview-panel__summary {
  margin-top: 10px;
  display: grid;
  grid-template-columns: 1fr;
  gap: 6px;
}

.preview-panel__summary div {
  display: flex;
  justify-content: space-between;
  gap: 10px;
  padding: 8px;
  border-radius: 10px;
  background: rgba(255, 255, 255, 0.72);
  font-size: 11px;
  color: #6f4f38;
}

.preview-panel__summary strong {
  color: #7a3110;
  text-align: right;
}

.preview-panel__operations {
  margin-top: 10px;
  padding: 10px;
  border-radius: 10px;
  background: rgba(255, 255, 255, 0.72);
}

.preview-panel__operations p {
  margin: 0 0 6px;
  font-size: 12px;
  font-weight: 700;
  color: #7a3110;
}

.preview-panel__operations ol {
  margin: 0;
  padding-left: 18px;
}

.preview-panel__operations li {
  font-size: 12px;
  line-height: 1.5;
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
