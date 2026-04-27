import { defineStore } from 'pinia';
import { hostBridge } from '../bridge/hostBridge';

export const useTaskHistoryStore = defineStore('taskHistory', {
  state: () => ({
    isPanelOpen: false,
    isLoading: false,
    isLoadingDetail: false,
    items: [],
    selectedTask: null,
    errorMessage: '',
    limit: 20
  }),
  actions: {
    async togglePanel() {
      this.isPanelOpen = !this.isPanelOpen;
      if (this.isPanelOpen && !this.items.length) {
        await this.loadRecentTasks();
      }
    },
    async loadRecentTasks() {
      this.isLoading = true;
      this.errorMessage = '';
      try {
        this.items = await hostBridge.getRecentTaskRuns(this.limit);
      } catch (error) {
        this.errorMessage = error?.message || '历史读取失败。';
      } finally {
        this.isLoading = false;
      }
    },
    async loadTaskDetail(taskRunId) {
      if (!taskRunId) {
        return;
      }

      this.isLoadingDetail = true;
      this.errorMessage = '';
      try {
        this.selectedTask = await hostBridge.getTaskRunDetail(taskRunId);
      } catch (error) {
        this.errorMessage = error?.message || '历史详情读取失败。';
      } finally {
        this.isLoadingDetail = false;
      }
    },
    clearSelectedTask() {
      this.selectedTask = null;
    }
  }
});
