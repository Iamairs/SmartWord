<template>
  <section class="skill-panel">
    <div class="skill-panel__header">
      <div>
        <p class="skill-panel__eyebrow">Skills</p>
        <h2>文档能力包</h2>
        <p>选择、创建和管理当前本机的 Word 文档工作流。</p>
      </div>
      <button class="ghost-button ghost-button--small" type="button" @click="skillsStore.togglePanel()">
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
            {{ skill.isBuiltIn ? '内置' : '用户' }} · {{ skill.enabled ? '已启用' : '已禁用' }}
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

    <div v-if="skillsStore.selectedSkill" class="skill-detail">
      <div class="skill-detail__header">
        <h3>{{ skillsStore.selectedSkill.displayName || skillsStore.selectedSkill.name }}</h3>
        <span>{{ skillsStore.selectedSkill.isBuiltIn ? '只读内置 Skill' : '可编辑用户 Skill' }}</span>
      </div>

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

const skillsStore = useSkillsStore();

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
.skill-detail {
  display: flex;
  flex-direction: column;
  gap: 8px;
  margin-top: 10px;
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
