import { defineStore } from 'pinia';
import { hostBridge } from '../bridge/hostBridge';

export const useSkillsStore = defineStore('skills', {
  state: () => ({
    isPanelOpen: false,
    isLoading: false,
    isSaving: false,
    items: [],
    selectedSkillNames: [],
    autoActiveSkillNames: [],
    suppressedSkillNames: [],
    recommendations: [],
    skillPromptTokens: 0,
    selectedSkill: null,
    selectedContent: '',
    resources: [],
    scripts: [],
    approvals: [],
    telemetrySummary: null,
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
      state.items.filter((item) => state.selectedSkillNames.includes(item.name)),
    activeSkillNames: (state) => {
      const names = [...state.selectedSkillNames];
      state.autoActiveSkillNames.forEach((name) => {
        if (!names.includes(name) && names.length < 3) names.push(name);
      });
      return names;
    },
    activeSkillTags() {
      return this.activeSkillNames.map((name) => {
        const skill = this.items.find((item) => item.name === name) || {};
        return {
          name,
          label: skill.displayName || name,
          source: this.selectedSkillNames.includes(name) ? 'manual' : 'auto'
        };
      });
    },
    recommendedItems(state) {
      return state.recommendations.filter(
        (item) => !this.activeSkillNames.includes(item.skillName) && !state.suppressedSkillNames.includes(item.skillName)
      );
    }
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
        this.autoActiveSkillNames = this.autoActiveSkillNames.filter((name) =>
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
      this.suppressedSkillNames = this.suppressedSkillNames.filter((itemName) => itemName !== name);
      this.errorMessage = '';
    },

    closeActiveSkill(name) {
      if (this.selectedSkillNames.includes(name)) {
        this.selectedSkillNames = this.selectedSkillNames.filter((itemName) => itemName !== name);
        return;
      }

      this.autoActiveSkillNames = this.autoActiveSkillNames.filter((itemName) => itemName !== name);
      if (!this.suppressedSkillNames.includes(name)) {
        this.suppressedSkillNames = [...this.suppressedSkillNames, name];
      }
    },

    updateLocalRecommendations(input, mode) {
      const text = String(input || '').trim().toLowerCase();
      if (!text) {
        this.autoActiveSkillNames = [];
        this.recommendations = [];
        this.skillPromptTokens = 0;
        return;
      }

      const modeIndex = { ask: 0, plan: 1, agent: 2 }[String(mode || '').toLowerCase()];
      const candidates = this.items
        .filter((skill) => skill.enabled)
        .map((skill) => {
          const supportedModes = Array.isArray(skill.supportedModes) ? skill.supportedModes : [];
          const modeSupported = !supportedModes.length || supportedModes.some((item) => {
            const normalized = String(item).toLowerCase();
            return normalized === String(mode || '').toLowerCase() || Number(item) === modeIndex;
          });
          if (!modeSupported) return null;

          const excluded = (skill.activationExcludedTriggers || [])
            .map((item) => String(item || '').trim().toLowerCase())
            .filter(Boolean)
            .find((trigger) => text.includes(trigger));
          if (excluded) return null;

          const trigger = (skill.activationTriggers || [])
            .map((item) => String(item || '').trim().toLowerCase())
            .filter(Boolean)
            .find((item) => text.includes(item));
          const displayName = String(skill.displayName || '').trim().toLowerCase();
          const skillName = String(skill.name || '').trim().toLowerCase();
          let score = 0;
          let reason = '';
          if (skillName && text.includes(`@${skillName}`)) {
            score = 1;
            reason = '输入中显式指定了 Skill';
          } else if (trigger) {
            score = 0.9;
            reason = `命中触发词“${trigger}”`;
          } else if ((displayName && text.includes(displayName)) || (skillName && text.includes(skillName))) {
            score = 0.82;
            reason = '输入中提到了 Skill 名称';
          }
          return score >= 0.45
            ? { skillName: skill.name, displayName: skill.displayName || skill.name, score, reason, autoActivated: score >= 0.8 }
            : null;
        })
        .filter(Boolean)
        .sort((left, right) => right.score - left.score);

      this.recommendations = candidates;
      const availableSlots = Math.max(0, 3 - this.selectedSkillNames.length);
      this.autoActiveSkillNames = candidates
        .filter((item) => item.autoActivated && (
          item.score >= 0.99 || !this.suppressedSkillNames.includes(item.skillName)
        ))
        .map((item) => item.skillName)
        .filter((name) => !this.selectedSkillNames.includes(name))
        .slice(0, availableSlots);
    },

    resetTaskRecommendations() {
      this.autoActiveSkillNames = [];
      this.recommendations = [];
      this.suppressedSkillNames = [];
      this.skillPromptTokens = 0;
    },

    applyRuntimeRecommendations(event) {
      const recommendations = Array.isArray(event?.skillRecommendations)
        ? event.skillRecommendations.map((item) => ({
          skillName: item.skillName || item.SkillName || '',
          displayName: item.displayName || item.DisplayName || item.skillName || item.SkillName || '',
          score: Number(item.score ?? item.Score ?? 0),
          reason: item.reason || item.Reason || '',
          autoActivated: Boolean(item.autoActivated ?? item.AutoActivated)
        })).filter((item) => item.skillName)
        : [];
      const runtimeActive = Array.isArray(event?.activeSkillNames) ? event.activeSkillNames : [];
      const explicitRuntimeNames = recommendations
        .filter((item) => item.score >= 0.99)
        .map((item) => item.skillName);
      this.recommendations = recommendations;
      this.autoActiveSkillNames = runtimeActive
        .filter((name) => !this.selectedSkillNames.includes(name) && (
          explicitRuntimeNames.includes(name) || !this.suppressedSkillNames.includes(name)
        ))
        .slice(0, Math.max(0, 3 - this.selectedSkillNames.length));
      this.skillPromptTokens = Number(event?.skillPromptTokens || 0);
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
        const detail = await hostBridge.saveSkillWithVersion(
          this.selectedSkill.name,
          this.selectedContent,
          this.selectedSkill.contentSha256 || ''
        );
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

    async setScriptPolicy(name, policy) {
      this.errorMessage = '';
      this.successMessage = '';
      try {
        await hostBridge.setSkillScriptPolicy(name, policy);
        await this.loadSkills();
        if (this.selectedSkill?.name === name) {
          await this.loadSkillDetail(name);
        }
        this.successMessage = policy === 'prompt' ? '脚本已启用，首次执行仍需授权。' : '脚本已禁用。';
      } catch (error) {
        this.errorMessage = error.message || 'Skill 脚本策略设置失败';
      }
    },

    async loadTelemetrySummary() {
      try {
        this.telemetrySummary = await hostBridge.getSkillTelemetrySummary();
      } catch (error) {
        this.telemetrySummary = null;
        this.errorMessage = error.message || '本地观测读取失败';
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
