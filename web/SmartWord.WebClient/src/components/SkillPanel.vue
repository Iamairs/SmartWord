<template>
  <section class="skill-panel">
    <div class="skill-panel__header">
      <div>
        <p class="skill-panel__eyebrow">Skills</p>
        <h2>文档能力包</h2>
        <p>选择、创建和管理当前本机的 Word 文档工作流。</p>
      </div>
      <button
        v-if="showCloseButton"
        class="ghost-button ghost-button--small"
        type="button"
        @click="$emit('close')"
      >
        收起
      </button>
    </div>

    <p v-if="skillsStore.errorMessage" class="skill-message skill-message--error">
      {{ skillsStore.errorMessage }}
    </p>
    <p v-if="skillsStore.successMessage" class="skill-message skill-message--success">
      {{ skillsStore.successMessage }}
    </p>

    <div class="skill-panel__toolbar">
      <button class="ghost-button ghost-button--small" type="button" @click="skillsStore.loadSkills()">
        刷新
      </button>
      <span>{{ skillsStore.items.length }} 个 Skill</span>
    </div>

    <div class="skill-import">
      <div class="skill-import__header">
        <h3>导入 Skill</h3>
        <span>外部脚本默认禁用</span>
      </div>

      <div class="skill-import__modes" role="tablist" aria-label="Skill 导入来源">
        <button
          type="button"
          role="tab"
          :aria-selected="skillsStore.importMode === 'network'"
          :class="{ 'is-active': skillsStore.importMode === 'network' }"
          @click="skillsStore.importMode = 'network'"
        >
          网络
        </button>
        <button
          type="button"
          role="tab"
          :aria-selected="skillsStore.importMode === 'folder'"
          :class="{ 'is-active': skillsStore.importMode === 'folder' }"
          @click="skillsStore.importMode = 'folder'"
        >
          文件夹
        </button>
      </div>

      <div v-if="skillsStore.importMode === 'network'" class="skill-import__source">
        <input
          v-model.trim="skillsStore.importUrl"
          type="url"
          autocomplete="off"
          placeholder="https://github.com/owner/repo"
        />
        <button
          class="ghost-button ghost-button--small"
          type="button"
          :disabled="skillsStore.isImporting || !skillsStore.importUrl"
          @click="skillsStore.previewNetworkImport()"
        >
          预览
        </button>
      </div>

      <div v-else class="skill-import__source skill-import__source--folders">
        <div class="skill-import__folder-actions">
          <button
            class="ghost-button ghost-button--small"
            type="button"
            :disabled="skillsStore.isImporting"
            @click="skillsStore.addImportFolder()"
          >
            添加文件夹
          </button>
          <button
            class="ghost-button ghost-button--small"
            type="button"
            :disabled="skillsStore.isImporting || !skillsStore.importFolders.length"
            @click="skillsStore.previewFolderImport()"
          >
            批量预览
          </button>
        </div>
        <div v-if="skillsStore.importFolders.length" class="skill-import__folders">
          <div v-for="folder in skillsStore.importFolders" :key="folder">
            <span :title="folder">{{ folder }}</span>
            <button type="button" title="移除此文件夹" @click="skillsStore.removeImportFolder(folder)">×</button>
          </div>
        </div>
      </div>

      <div v-if="skillsStore.importPreview" class="skill-import__preview">
        <div
          v-for="item in skillsStore.importPreview.items"
          :key="item.itemId"
          class="skill-import__item"
          :class="{ 'skill-import__item--invalid': !item.canInstall }"
        >
          <div class="skill-import__item-title">
            <strong>{{ item.displayName || item.name || '无法识别' }}</strong>
            <span>{{ item.canInstall ? '可安装' : '不可安装' }}</span>
          </div>
          <p class="skill-import__source-label" :title="item.source">{{ item.source }}</p>
          <p v-if="item.name" class="skill-import__meta">
            {{ item.name }}<template v-if="item.version"> · v{{ item.version }}</template>
            · {{ item.fileCount }} 文件 · {{ formatBytes(item.totalBytes) }}
          </p>
          <p v-if="item.name" class="skill-import__meta">
            资源 {{ item.resourceCount }} · 脚本 {{ item.scriptCount }} · {{ formatHash(item.contentSha256) }}
          </p>
          <p v-for="warning in item.warnings" :key="warning" class="skill-import__warning">{{ warning }}</p>
          <p v-for="error in item.errors" :key="error" class="skill-import__error">{{ error }}</p>
        </div>

        <label v-if="installablePreviewItems.length" class="skill-import__confirm">
          <input v-model="skillsStore.importRiskConfirmed" type="checkbox" />
          <span>我已核对来源，并了解安装不代表脚本可信。</span>
        </label>
        <div class="skill-import__preview-actions">
          <button
            class="send-button"
            type="button"
            :disabled="skillsStore.isImporting || !installablePreviewItems.length || !skillsStore.importRiskConfirmed"
            @click="skillsStore.installImportPreview()"
          >
            {{ skillsStore.isImporting ? '处理中...' : `安装 ${installablePreviewItems.length} 个` }}
          </button>
          <button
            class="ghost-button"
            type="button"
            :disabled="skillsStore.isImporting"
            @click="skillsStore.cancelImportPreview()"
          >
            取消
          </button>
        </div>
      </div>
    </div>

    <div class="skill-list">
      <article
        v-for="skill in skillsStore.items"
        :key="skill.name"
        class="skill-card"
        :class="{ 'skill-card--active': skillsStore.selectedSkill?.name === skill.name }"
      >
        <button class="skill-card__main" type="button" @click="skillsStore.loadSkillDetail(skill.name)">
          <span class="skill-card__name">{{ skill.displayName || skill.name }}</span>
          <span class="skill-card__meta">
            {{ trustLabel(skill) }} · {{ skill.enabled ? '已启用' : '已禁用' }}
            <template v-if="skill.scriptPolicy === 'disabled'"> · 脚本禁用</template>
          </span>
          <span class="skill-card__desc">{{ skill.description }}</span>
        </button>
        <div class="skill-card__actions">
          <button
            class="ghost-button ghost-button--tiny"
            type="button"
            :disabled="!skill.enabled"
            @click="skillsStore.toggleSelectedSkill(skill.name)"
          >
            {{ skillsStore.selectedSkillNames.includes(skill.name) ? '取消选择' : '选择' }}
          </button>
          <button
            class="ghost-button ghost-button--tiny"
            type="button"
            @click="skillsStore.setEnabled(skill.name, !skill.enabled)"
          >
            {{ skill.enabled ? '禁用' : '启用' }}
          </button>
        </div>
      </article>
    </div>

    <form class="skill-create" @submit.prevent="skillsStore.createSkill()">
      <h3>创建 Skill</h3>
      <input v-model.trim="skillsStore.createForm.name" type="text" placeholder="skill-name" />
      <input v-model.trim="skillsStore.createForm.displayName" type="text" placeholder="显示名称" />
      <textarea v-model.trim="skillsStore.createForm.description" rows="3" placeholder="描述使用场景"></textarea>
      <button class="send-button send-button--full" type="submit" :disabled="skillsStore.isSaving">
        {{ skillsStore.isSaving ? '处理中...' : '创建' }}
      </button>
    </form>

    <div class="skill-telemetry">
      <div class="skill-telemetry__header">
        <h3>本地观测</h3>
        <button class="ghost-button ghost-button--tiny" type="button" @click="skillsStore.loadTelemetrySummary()">刷新</button>
      </div>
      <template v-if="skillsStore.telemetrySummary">
        <p>仅保存在本机，不上传文档正文。</p>
        <div class="skill-telemetry__stats">
          <span>Skill 解析 {{ skillsStore.telemetrySummary.skillContextResolvedCount || 0 }}</span>
          <span>完成 {{ skillsStore.telemetrySummary.completedTaskCount || 0 }}</span>
          <span>失败 {{ skillsStore.telemetrySummary.failedTaskCount || 0 }}</span>
          <span>工具失败 {{ skillsStore.telemetrySummary.toolFailureCount || 0 }}</span>
        </div>
      </template>
      <p v-else>本地观测已启用，尚无可汇总记录。</p>
    </div>

    <div v-if="skillsStore.selectedSkill" class="skill-detail">
      <div class="skill-detail__header">
        <h3>{{ skillsStore.selectedSkill.displayName || skillsStore.selectedSkill.name }}</h3>
        <span>{{ trustLabel(skillsStore.selectedSkill) }} · {{ skillsStore.selectedSkill.isBuiltIn ? '只读' : '可编辑' }}</span>
      </div>

      <p v-if="skillsStore.selectedSkill.trustLevel === 'external'" class="skill-security-note">
        外部 Skill 的脚本默认禁用。Markdown 工作流仍可查看和使用。
      </p>

      <textarea
        v-model="skillsStore.selectedContent"
        class="skill-editor"
        rows="12"
        :readonly="skillsStore.selectedSkill.isBuiltIn"
      ></textarea>

      <div v-if="skillsStore.resources.length" class="skill-resources">
        <p>资源</p>
        <span v-for="resource in skillsStore.resources" :key="resource.relativePath">
          {{ resource.kind }} / {{ resource.relativePath }}
        </span>
      </div>

      <div v-if="skillsStore.scripts.length" class="skill-resources">
        <p>scripts/</p>
        <span v-for="script in skillsStore.scripts" :key="script.relativePath">
          {{ script.runtime }} / {{ script.relativePath }} / {{ formatHash(script.sha256) }}
          · {{ skillsStore.isScriptApproved(script) ? '已授权' : '未授权' }}
        </span>
      </div>

      <div v-if="skillsStore.scripts.length" class="skill-script-policy">
        <div>
          <strong>脚本执行</strong>
          <p>使用独立进程隔离，但不等同于完整的操作系统沙箱；首次执行仍需授权。</p>
        </div>
        <button
          class="ghost-button ghost-button--small"
          type="button"
          :disabled="skillsStore.isSaving"
          @click="skillsStore.setScriptPolicy(
            skillsStore.selectedSkill.name,
            skillsStore.selectedSkill.scriptPolicy === 'disabled' ? 'prompt' : 'disabled'
          )"
        >
          {{ skillsStore.selectedSkill.scriptPolicy === 'disabled' ? '启用脚本' : '禁用脚本' }}
        </button>
      </div>

      <div v-if="matchingApprovals.length" class="skill-resources">
        <p>已授权脚本</p>
        <span v-for="record in matchingApprovals" :key="approvalKey(record)">
          {{ approvalLabel(record) }}
          <button class="ghost-button ghost-button--tiny" type="button" @click="skillsStore.revokeScriptApproval(record)">
            撤销
          </button>
        </span>
      </div>

      <div class="skill-detail__actions">
        <button
          v-if="!skillsStore.selectedSkill.isBuiltIn"
          class="send-button"
          type="button"
          :disabled="skillsStore.isSaving"
          @click="skillsStore.saveSelectedSkill()"
        >
          保存
        </button>
        <button
          v-if="!skillsStore.selectedSkill.isBuiltIn"
          class="ghost-button"
          type="button"
          :disabled="skillsStore.isSaving"
          @click="skillsStore.deleteSelectedSkill()"
        >
          删除
        </button>
      </div>
    </div>
  </section>
</template>

<script setup>
import { computed, onMounted } from 'vue';
import { useSkillsStore } from '../stores/skills';

defineProps({
  showCloseButton: {
    type: Boolean,
    default: true
  }
});

defineEmits(['close']);

const skillsStore = useSkillsStore();

const installablePreviewItems = computed(() => {
  const items = Array.isArray(skillsStore.importPreview?.items) ? skillsStore.importPreview.items : [];
  return items.filter((item) => item.canInstall);
});

const matchingApprovals = computed(() => {
  const skillName = skillsStore.selectedSkill?.name || '';
  return skillsStore.approvals.filter((record) => {
    const key = record.key || record.Key || {};
    return String(key.skillName || key.SkillName || '').toLowerCase() === skillName.toLowerCase();
  });
});

function formatHash(hash) {
  const value = String(hash || '');
  return value.length > 14 ? `${value.slice(0, 10)}...` : value;
}

function formatBytes(bytes) {
  const value = Number(bytes || 0);
  if (value < 1024) return `${value} B`;
  if (value < 1024 * 1024) return `${(value / 1024).toFixed(1)} KB`;
  return `${(value / (1024 * 1024)).toFixed(1)} MB`;
}

function trustLabel(skill) {
  const trustLevel = String(skill?.trustLevel || '').toLowerCase();
  if (trustLevel === 'external') return '外部';
  if (trustLevel === 'built_in' || skill?.isBuiltIn) return '内置';
  return '本地';
}

function approvalKey(record) {
  const key = record.key || record.Key || {};
  return [
    key.skillName || key.SkillName,
    key.relativeScriptPath || key.RelativeScriptPath,
    key.scriptHash || key.ScriptHash,
    key.runtime || key.Runtime,
    key.permissionSet || key.PermissionSet
  ].join('|');
}

function approvalLabel(record) {
  const key = record.key || record.Key || {};
  return `${key.runtime || key.Runtime} / ${key.relativeScriptPath || key.RelativeScriptPath} / ${formatHash(
    key.scriptHash || key.ScriptHash
  )}`;
}

onMounted(() => {
  if (!skillsStore.items.length) {
    skillsStore.loadSkills();
  }
  skillsStore.loadTelemetrySummary();
});
</script>

<style scoped>
.skill-panel {
  padding: 14px;
  border-radius: 18px;
  background: rgba(255, 255, 255, 0.94);
  border: 1px solid rgba(89, 118, 161, 0.16);
  box-shadow: 0 18px 40px rgba(24, 49, 79, 0.08);
}

.skill-panel__header,
.skill-panel__toolbar,
.skill-detail__header,
.skill-detail__actions,
.skill-card__actions {
  display: flex;
  justify-content: space-between;
  gap: 8px;
}

.skill-panel__header {
  align-items: flex-start;
  margin-bottom: 12px;
}

.skill-panel__eyebrow,
.skill-panel__header p,
.skill-panel__toolbar,
.skill-card__meta,
.skill-card__desc,
.skill-detail__header span,
.skill-resources {
  margin: 0;
  font-size: 11px;
  line-height: 1.45;
  color: #60758f;
}

.skill-panel h2,
.skill-create h3,
.skill-detail h3 {
  margin: 0;
  font-size: 16px;
}

.skill-panel__eyebrow {
  text-transform: uppercase;
  letter-spacing: 0.08em;
  color: #6f86a3;
}

.skill-message {
  margin: 0 0 8px;
  font-size: 11px;
  line-height: 1.5;
}

.skill-message--error {
  color: #b42318;
}

.skill-message--success {
  color: #2d6a4f;
}

.skill-list,
.skill-create,
.skill-detail,
.skill-telemetry,
.skill-import {
  display: flex;
  flex-direction: column;
  gap: 8px;
  margin-top: 10px;
}

.skill-import {
  padding: 10px 0;
  border-top: 1px solid rgba(89, 118, 161, 0.16);
  border-bottom: 1px solid rgba(89, 118, 161, 0.16);
}

.skill-import__header,
.skill-import__item-title,
.skill-import__folder-actions,
.skill-import__preview-actions {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
}

.skill-import__header h3 {
  margin: 0;
  font-size: 13px;
}

.skill-import__header span,
.skill-import__meta,
.skill-import__source-label,
.skill-import__warning,
.skill-import__error {
  margin: 0;
  font-size: 10px;
  line-height: 1.45;
}

.skill-import__header span,
.skill-import__meta,
.skill-import__source-label {
  color: #60758f;
}

.skill-import__modes {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  padding: 2px;
  border-radius: 6px;
  background: #edf2f7;
}

.skill-import__modes button {
  min-width: 0;
  padding: 6px 8px;
  border: 0;
  border-radius: 4px;
  color: #60758f;
  background: transparent;
  cursor: pointer;
}

.skill-import__modes button.is-active {
  color: #244464;
  background: #ffffff;
  box-shadow: 0 1px 2px rgba(24, 49, 79, 0.12);
}

.skill-import__source {
  display: grid;
  grid-template-columns: minmax(0, 1fr) auto;
  gap: 6px;
}

.skill-import__source input {
  min-width: 0;
  width: 100%;
  padding: 8px;
  border: 1px solid rgba(89, 118, 161, 0.26);
  border-radius: 6px;
  font: inherit;
}

.skill-import__source--folders,
.skill-import__preview,
.skill-import__folders {
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.skill-import__folders > div {
  display: grid;
  grid-template-columns: minmax(0, 1fr) 24px;
  align-items: center;
  gap: 4px;
  font-size: 10px;
  color: #60758f;
}

.skill-import__folders span,
.skill-import__source-label {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.skill-import__folders button {
  width: 24px;
  height: 24px;
  padding: 0;
  border: 0;
  color: #60758f;
  background: transparent;
  cursor: pointer;
}

.skill-import__item {
  padding-top: 7px;
  border-top: 1px solid rgba(89, 118, 161, 0.16);
}

.skill-import__item-title strong {
  min-width: 0;
  overflow-wrap: anywhere;
  font-size: 11px;
}

.skill-import__item-title span {
  flex: 0 0 auto;
  font-size: 10px;
  color: #2d6a4f;
}

.skill-import__item--invalid .skill-import__item-title span,
.skill-import__error {
  color: #b42318;
}

.skill-import__warning {
  color: #92400e;
}

.skill-import__confirm {
  display: grid;
  grid-template-columns: 16px minmax(0, 1fr);
  align-items: start;
  gap: 6px;
  font-size: 10px;
  line-height: 1.45;
  color: #445b73;
}

.skill-import__confirm input {
  width: 14px;
  height: 14px;
  margin: 1px 0 0;
}

.skill-telemetry {
  padding-top: 10px;
  border-top: 1px solid rgba(89, 118, 161, 0.16);
}

.skill-telemetry__header,
.skill-script-policy {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 8px;
}

.skill-telemetry h3,
.skill-telemetry p,
.skill-script-policy p,
.skill-security-note {
  margin: 0;
  font-size: 11px;
  line-height: 1.45;
}

.skill-telemetry__stats {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 4px 8px;
  margin-top: 6px;
  color: #60758f;
  font-size: 10px;
}

.skill-security-note {
  padding: 7px 8px;
  border-left: 3px solid #b45309;
  background: #fff7ed;
  color: #92400e;
}

.skill-script-policy {
  padding: 8px 0;
  border-top: 1px solid rgba(89, 118, 161, 0.16);
  border-bottom: 1px solid rgba(89, 118, 161, 0.16);
}

.skill-script-policy strong {
  font-size: 11px;
}

.skill-script-policy p {
  color: #60758f;
}

.skill-card {
  padding: 10px;
  border: 1px solid rgba(89, 118, 161, 0.16);
  border-radius: 12px;
  background: #f7f9fc;
}

.skill-card--active {
  border-color: rgba(40, 81, 125, 0.48);
}

.skill-card__main {
  width: 100%;
  border: none;
  padding: 0;
  background: transparent;
  text-align: left;
  color: inherit;
  cursor: pointer;
}

.skill-card__name {
  display: block;
  font-size: 13px;
  font-weight: 700;
  color: #244464;
}

.skill-card__desc {
  display: block;
  margin-top: 4px;
  word-break: break-word;
}

.skill-card__actions {
  margin-top: 8px;
}

.ghost-button--tiny {
  padding: 5px 7px;
  font-size: 11px;
}

.skill-create input,
.skill-create textarea,
.skill-editor {
  width: 100%;
  padding: 9px 10px;
  border: 1px solid rgba(89, 118, 161, 0.26);
  border-radius: 12px;
  font: inherit;
  color: inherit;
  background: #ffffff;
}

.skill-create textarea,
.skill-editor {
  resize: vertical;
}

.skill-editor {
  min-height: 220px;
  font-family: Consolas, "Courier New", monospace;
  font-size: 11px;
  line-height: 1.5;
}

.skill-resources {
  display: flex;
  flex-direction: column;
  gap: 4px;
  padding: 8px;
  border-radius: 10px;
  background: #f7f9fc;
}
</style>
