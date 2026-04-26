<template>
  <section class="quick-actions">
    <div class="quick-actions__header">
      <h2>常用任务</h2>
      <span>点击后直接发起</span>
    </div>

    <div class="quick-actions__group" v-for="group in actionGroups" :key="group.title">
      <p>{{ group.title }}</p>
      <div class="quick-actions__grid">
        <button
          v-for="action in group.actions"
          :key="action.id"
          type="button"
          class="quick-action"
          @click="$emit('select', action)"
        >
          <span>{{ action.label }}</span>
          <small>{{ action.hint }}</small>
        </button>
      </div>
    </div>
  </section>
</template>

<script setup>
defineEmits(['select']);

const selectionGuard = '请优先读取当前选区；如果当前没有选区，请先说明无法安全限定范围，不要改全文。';

const actionGroups = [
  {
    title: '问文档',
    actions: [
      {
        id: 'summarize-document',
        label: '总结全文',
        hint: '只读',
        manualMode: 'ask',
        content: '请总结当前 Word 文档的核心内容、结构和关键结论，并给出引用来源。'
      },
      {
        id: 'summarize-section',
        label: '总结当前章节',
        hint: '只读',
        manualMode: 'ask',
        content: '请根据光标位置总结当前章节的主要内容，并给出引用来源。'
      },
      {
        id: 'explain-selection',
        label: '解释选区',
        hint: '选区',
        manualMode: 'ask',
        content: `${selectionGuard}\n\n请解释当前选区的含义、上下文和需要注意的表述。`
      }
    ]
  },
  {
    title: '改文字',
    actions: [
      {
        id: 'polish-selection',
        label: '润色选区',
        hint: '需确认',
        manualMode: 'agent',
        permissionMode: 'confirm_writes',
        content: `${selectionGuard}\n\n请润色当前选区，使表达更清晰、正式、自然。写入前按权限设置等待确认。`
      },
      {
        id: 'compress-selection',
        label: '压缩选区',
        hint: '需确认',
        manualMode: 'agent',
        permissionMode: 'confirm_writes',
        content: `${selectionGuard}\n\n请在保留关键信息的前提下压缩当前选区，使其更简洁。写入前按权限设置等待确认。`
      },
      {
        id: 'formal-selection',
        label: '正式表达',
        hint: '需确认',
        manualMode: 'agent',
        permissionMode: 'confirm_writes',
        content: `${selectionGuard}\n\n请把当前选区改成更正式、适合办公文档的表达。写入前按权限设置等待确认。`
      }
    ]
  },
  {
    title: '审文档',
    actions: [
      {
        id: 'proofread-document',
        label: '检查病句',
        hint: '先审阅',
        manualMode: 'ask',
        content: '请检查当前文档中明显的错别字、病句和不自然表达，按位置列出问题和修改建议，不要直接修改文档。'
      },
      {
        id: 'document-health',
        label: '文档体检',
        hint: '只读',
        manualMode: 'ask',
        content: '请对当前文档做一次只读体检，重点检查结构、标题层级、批注、表格、重复内容和格式不一致线索，并按问题位置列出。'
      },
      {
        id: 'review-annotations',
        label: '处理批注',
        hint: '先规划',
        manualMode: 'plan',
        content: '请先规划如何处理当前文档中的批注：汇总批注、按章节分类、判断哪些可自动处理、哪些需要人工确认。暂时不要修改文档。'
      }
    ]
  },
  {
    title: '整格式',
    actions: [
      {
        id: 'format-plan',
        label: '统一格式',
        hint: '先规划',
        manualMode: 'plan',
        content: '请先规划如何统一当前文档格式，包括标题层级、正文样式、行距、段前段后、字体字号和表格基础格式。先输出计划，暂时不要修改文档。'
      }
    ]
  }
];
</script>

<style scoped>
.quick-actions {
  padding: 12px;
  border-radius: 12px;
  background: rgba(255, 255, 255, 0.92);
  border: 1px solid rgba(89, 118, 161, 0.16);
  box-shadow: 0 10px 24px rgba(24, 49, 79, 0.06);
}

.quick-actions__header {
  display: flex;
  justify-content: space-between;
  gap: 8px;
  align-items: baseline;
}

.quick-actions__header h2 {
  margin: 0;
  font-size: 14px;
  color: #18314f;
}

.quick-actions__header span,
.quick-actions__group p,
.quick-action small {
  font-size: 11px;
  color: #60758f;
}

.quick-actions__group {
  margin-top: 10px;
}

.quick-actions__group p {
  margin: 0 0 6px;
  font-weight: 700;
}

.quick-actions__grid {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 6px;
}

.quick-action {
  min-height: 48px;
  padding: 8px;
  border: 1px solid rgba(89, 118, 161, 0.2);
  border-radius: 10px;
  background: #f7f9fc;
  color: #244464;
  font: inherit;
  text-align: left;
  cursor: pointer;
}

.quick-action span,
.quick-action small {
  display: block;
}

.quick-action span {
  font-size: 12px;
  font-weight: 700;
}
</style>
