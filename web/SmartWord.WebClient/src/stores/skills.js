import { defineStore } from 'pinia';
import { hostBridge } from '../bridge/hostBridge';

export const useSkillsStore = defineStore('skills', {
  state: () => ({
    isPanelOpen: false,
    isLoading: false,
    isSaving: false,
    items: [],
    selectedSkillNames: [],
    selectedSkill: null,
    selectedContent: '',
    resources: [],
    scripts: [],
    approvals: [],
    errorMessage: '',
    successMessage: '',
    createForm: {
      name: '',
      displayName: '',
      description: ''
    }
  }),

  getters: {
    enabledItems: (state) => state.items.filter((item) => item.enabled),
    selectedItems: (state) =>
      state.items.filter((item) => state.selectedSkillNames.includes(item.name))
  },

  actions: {
    togglePanel() {
      this.isPanelOpen = !this.isPanelOpen;
      if (this.isPanelOpen && !this.items.length) {
        this.loadSkills();
      }
    },

    async loadSkills() {
      this.isLoading = true;
      this.errorMessage = '';
      try {
        this.items = await hostBridge.getSkills();
        this.selectedSkillNames = this.selectedSkillNames.filter((name) =>
          this.items.some((item) => item.name === name && item.enabled)
        );
      } catch (error) {
        this.errorMessage = error.message || 'Skill 读取失败';
      } finally {
        this.isLoading = false;
      }
    },

    async loadSkillDetail(name) {
      if (!name) return;
      this.isLoading = true;
      this.errorMessage = '';
      try {
        const detail = await hostBridge.getSkillDetail(name);
        this.selectedSkill = detail.skill;
        this.selectedContent = detail.content || '';
        this.resources = Array.isArray(detail.resources) ? detail.resources : [];
        this.scripts = Array.isArray(detail.scripts) ? detail.scripts : [];
        this.approvals = await hostBridge.getSkillScriptApprovals();
      } catch (error) {
        this.errorMessage = error.message || 'Skill 详情读取失败';
      } finally {
        this.isLoading = false;
      }
    },

    toggleSelectedSkill(name) {
      const item = this.items.find((skill) => skill.name === name);
      if (!item || !item.enabled) return;
      if (this.selectedSkillNames.includes(name)) {
        this.selectedSkillNames = this.selectedSkillNames.filter((itemName) => itemName !== name);
        return;
      }
      if (this.selectedSkillNames.length >= 3) {
        this.errorMessage = '单次任务最多选择 3 个 Skill。';
        return;
      }
      this.selectedSkillNames = [...this.selectedSkillNames, name];
      this.errorMessage = '';
    },

    async createSkill() {
      this.isSaving = true;
      this.errorMessage = '';
      this.successMessage = '';
      try {
        const detail = await hostBridge.createSkill(this.createForm);
        this.selectedSkill = detail.skill;
        this.selectedContent = detail.content || '';
        this.resources = Array.isArray(detail.resources) ? detail.resources : [];
        this.scripts = Array.isArray(detail.scripts) ? detail.scripts : [];
        this.createForm = { name: '', displayName: '', description: '' };
        await this.loadSkills();
        this.successMessage = 'Skill 已创建。';
      } catch (error) {
        this.errorMessage = error.message || 'Skill 创建失败';
      } finally {
        this.isSaving = false;
      }
    },

    async saveSelectedSkill() {
      if (!this.selectedSkill || this.selectedSkill.isBuiltIn) return;
      this.isSaving = true;
      this.errorMessage = '';
      this.successMessage = '';
      try {
        const detail = await hostBridge.saveSkill(this.selectedSkill.name, this.selectedContent);
        this.selectedSkill = detail.skill;
        this.selectedContent = detail.content || '';
        this.resources = Array.isArray(detail.resources) ? detail.resources : [];
        this.scripts = Array.isArray(detail.scripts) ? detail.scripts : [];
        await this.loadSkills();
        this.successMessage = 'Skill 已保存。';
      } catch (error) {
        this.errorMessage = error.message || 'Skill 保存失败';
      } finally {
        this.isSaving = false;
      }
    },

    async deleteSelectedSkill() {
      if (!this.selectedSkill || this.selectedSkill.isBuiltIn) return;
      this.isSaving = true;
      this.errorMessage = '';
      this.successMessage = '';
      const name = this.selectedSkill.name;
      try {
        await hostBridge.deleteSkill(name);
        this.selectedSkillNames = this.selectedSkillNames.filter((item) => item !== name);
        this.selectedSkill = null;
        this.selectedContent = '';
        this.resources = [];
        this.scripts = [];
        await this.loadSkills();
        this.successMessage = 'Skill 已删除。';
      } catch (error) {
        this.errorMessage = error.message || 'Skill 删除失败';
      } finally {
        this.isSaving = false;
      }
    },

    async setEnabled(name, enabled) {
      this.errorMessage = '';
      try {
        await hostBridge.setSkillEnabled(name, enabled);
        await this.loadSkills();
        if (!enabled) {
          this.selectedSkillNames = this.selectedSkillNames.filter((item) => item !== name);
        }
      } catch (error) {
        this.errorMessage = error.message || 'Skill 启停设置失败';
      }
    },

    async revokeScriptApproval(record) {
      this.errorMessage = '';
      this.successMessage = '';
      try {
        await hostBridge.revokeSkillScriptApproval(record?.key || {});
        this.approvals = await hostBridge.getSkillScriptApprovals();
        this.successMessage = '脚本授权已撤销。';
      } catch (error) {
        this.errorMessage = error.message || '脚本授权撤销失败';
      }
    },

    isScriptApproved(script) {
      return this.approvals.some((record) => {
        const key = record.key || record.Key || {};
        return (
          String(key.skillName || key.SkillName || '').toLowerCase() === String(script.skillName || '').toLowerCase() &&
          String(key.relativeScriptPath || key.RelativeScriptPath || '').toLowerCase() ===
            String(script.relativePath || '').toLowerCase() &&
          String(key.scriptHash || key.ScriptHash || '').toLowerCase() === String(script.sha256 || '').toLowerCase() &&
          String(key.runtime || key.Runtime || '').toLowerCase() === String(script.runtime || '').toLowerCase()
        );
      });
    }
  }
});
