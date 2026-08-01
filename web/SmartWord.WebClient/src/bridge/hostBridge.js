const SETTINGS_STORAGE_KEY = 'smartword-settings';
const SKILLS_STORAGE_KEY = 'smartword-skills';
const VALID_MODES = new Set(['ask', 'plan', 'agent']);

let pendingMockConfirmation = null;
let pendingMockTodoRecovery = null;

function hasWebView2Bridge() {
  return Boolean(
    window.chrome &&
      window.chrome.webview &&
      window.chrome.webview.hostObjects &&
      window.chrome.webview.hostObjects.bridge
  );
}

function createDefaultSettings() {
  return {
    baseUrl: 'https://api.openai.com/v1',
    apiKey: '',
    apiKeyDisplay: '',
    hasApiKey: false,
    baseUrlHeavy: '',
    apiKeyHeavy: '',
    apiKeyHeavyDisplay: '',
    hasApiKeyHeavy: false,
    baseUrlLight: '',
    apiKeyLight: '',
    apiKeyLightDisplay: '',
    hasApiKeyLight: false,
    lightModel: 'gpt-4.1-mini',
    heavyModel: 'gpt-4.1',
    permissionMode: 'confirm_writes',
    requireConfirmationForScripts: true,
    contextWindowTokens: 262144,
    contextSoftLimitRatio: 0.65,
    contextHardLimitRatio: 0.85,
    contextEmergencyLimitRatio: 0.95,
    contextTokenSafetyMargin: 1.2,
    customInstructions: ''
  };
}

function normalizePermissionMode(permissionMode, requireConfirmationForScripts = true) {
  const raw = String(permissionMode || '').trim().toLowerCase();
  if (['read_only', 'confirm_writes', 'auto_safe_writes', 'full_auto'].includes(raw)) {
    return raw;
  }

  return requireConfirmationForScripts === false ? 'auto_safe_writes' : 'confirm_writes';
}

function legacyConfirmationFromPermissionMode(permissionMode) {
  return !['auto_safe_writes', 'full_auto'].includes(permissionMode);
}

function normalizeSettings(settings) {
  const source = settings || {};
  const defaults = createDefaultSettings();
  const legacyConfirmation =
    source.requireConfirmationForScripts ??
    source.RequireConfirmationForScripts ??
    defaults.requireConfirmationForScripts;
  const permissionMode = normalizePermissionMode(
    source.permissionMode ?? source.PermissionMode,
    legacyConfirmation
  );
  const hasApiKey = Boolean(source.hasApiKey ?? source.HasApiKey);
  const hasApiKeyHeavy = Boolean(source.hasApiKeyHeavy ?? source.HasApiKeyHeavy);
  const hasApiKeyLight = Boolean(source.hasApiKeyLight ?? source.HasApiKeyLight);

  return {
    baseUrl: source.baseUrl ?? source.BaseUrl ?? source.apiBaseUrl ?? source.ApiBaseUrl ?? defaults.baseUrl,
    apiKey:
      source.apiKey ??
      source.ApiKey ??
      source.apiKeyDisplay ??
      source.ApiKeyDisplay ??
      (hasApiKey ? '********' : defaults.apiKey),
    apiKeyDisplay: source.apiKeyDisplay ?? source.ApiKeyDisplay ?? (hasApiKey ? '********' : ''),
    hasApiKey,
    baseUrlHeavy: source.baseUrlHeavy ?? source.BaseUrlHeavy ?? defaults.baseUrlHeavy,
    apiKeyHeavy:
      source.apiKeyHeavy ??
      source.ApiKeyHeavy ??
      source.apiKeyHeavyDisplay ??
      source.ApiKeyHeavyDisplay ??
      (hasApiKeyHeavy ? '********' : defaults.apiKeyHeavy),
    apiKeyHeavyDisplay: source.apiKeyHeavyDisplay ?? source.ApiKeyHeavyDisplay ?? (hasApiKeyHeavy ? '********' : ''),
    hasApiKeyHeavy,
    baseUrlLight: source.baseUrlLight ?? source.BaseUrlLight ?? defaults.baseUrlLight,
    apiKeyLight:
      source.apiKeyLight ??
      source.ApiKeyLight ??
      source.apiKeyLightDisplay ??
      source.ApiKeyLightDisplay ??
      (hasApiKeyLight ? '********' : defaults.apiKeyLight),
    apiKeyLightDisplay: source.apiKeyLightDisplay ?? source.ApiKeyLightDisplay ?? (hasApiKeyLight ? '********' : ''),
    hasApiKeyLight,
    lightModel: source.lightModel ?? source.LightModel ?? defaults.lightModel,
    heavyModel: source.heavyModel ?? source.HeavyModel ?? defaults.heavyModel,
    permissionMode,
    requireConfirmationForScripts: legacyConfirmationFromPermissionMode(permissionMode),
    contextWindowTokens: normalizePositiveInt(
      source.contextWindowTokens ?? source.ContextWindowTokens,
      defaults.contextWindowTokens
    ),
    contextSoftLimitRatio: normalizeRatio(
      source.contextSoftLimitRatio ?? source.ContextSoftLimitRatio,
      defaults.contextSoftLimitRatio
    ),
    contextHardLimitRatio: normalizeRatio(
      source.contextHardLimitRatio ?? source.ContextHardLimitRatio,
      defaults.contextHardLimitRatio
    ),
    contextEmergencyLimitRatio: normalizeRatio(
      source.contextEmergencyLimitRatio ?? source.ContextEmergencyLimitRatio,
      defaults.contextEmergencyLimitRatio
    ),
    contextTokenSafetyMargin: normalizePositiveNumber(
      source.contextTokenSafetyMargin ?? source.ContextTokenSafetyMargin,
      defaults.contextTokenSafetyMargin
    ),
    customInstructions:
      source.customInstructions ?? source.CustomInstructions ?? defaults.customInstructions
  };
}

function normalizePositiveInt(value, fallback) {
  const parsed = Number.parseInt(value, 10);
  return Number.isFinite(parsed) && parsed > 0 ? parsed : fallback;
}

function normalizePositiveNumber(value, fallback) {
  const parsed = Number(value);
  return Number.isFinite(parsed) && parsed > 0 ? parsed : fallback;
}

function normalizeRatio(value, fallback) {
  const parsed = Number(value);
  return Number.isFinite(parsed) && parsed > 0 && parsed < 1 ? parsed : fallback;
}

function createMockResponse(request, notify) {
  if (!VALID_MODES.has(request.manualMode)) {
    window.setTimeout(
      () =>
        notify({
          type: 'error',
          message: '请求缺少有效的运行模式，请先在前端选择“对话交流”“规划任务”或“自主执行”。'
        }),
      120
    );
    return;
  }

  notify({
    type: 'mode_detected',
    detectedMode: request.manualMode
  });

  if (request.manualMode === 'agent' && request.permissionMode === 'confirm_writes') {
    pendingMockConfirmation = {
      toolCallId: 'mock-tool-call-1',
      toolName: 'patch_range',
      toolInput: JSON.stringify(
        {
          operations: [
            {
              type: 'replace_text',
              paragraph_index: 2,
              text: '这是浏览器预览模式下的模拟写入。'
            }
          ]
        },
        null,
        2
      ),
      operationDescription: '浏览器预览模式下的模拟写入。'
    };

    window.setTimeout(
      () =>
        notify({
          type: 'tool_call_started',
          toolCallId: pendingMockConfirmation.toolCallId,
          toolName: pendingMockConfirmation.toolName,
          toolInput: pendingMockConfirmation.toolInput,
          requiresConfirmation: true,
          operationDescription: pendingMockConfirmation.operationDescription
        }),
      140
    );
    return;
  }

  window.setTimeout(
    () =>
      notify({
        type: 'stream_chunk',
        content: `已收到请求：${request.content}`
      }),
    180
  );
  window.setTimeout(
    () =>
      notify({
        type: 'task_completed',
        message: '当前为浏览器降级模式，设置与对话请求将走本地模拟。'
      }),
    360
  );
}

function createMockTaskRuns() {
  const now = new Date();
  return [
    {
      id: 'mock-completed-agent',
      startedAtUtc: new Date(now.getTime() - 1000 * 60 * 18).toISOString(),
      endedAtUtc: new Date(now.getTime() - 1000 * 60 * 15).toISOString(),
      userGoal: '统一合同正文标题层级，并修正第三章格式。',
      mode: 'agent',
      permissionMode: 'confirm_writes',
      model: 'gpt-4.1',
      status: 'Completed',
      summary: '已完成任务，记录 3 个工具调用，2 项文档改动，2 项已验证。',
      failureReason: '',
      toolCount: 3,
      changeCount: 2,
      verifiedChangeCount: 2
    },
    {
      id: 'mock-failed-tool',
      startedAtUtc: new Date(now.getTime() - 1000 * 60 * 80).toISOString(),
      endedAtUtc: new Date(now.getTime() - 1000 * 60 * 78).toISOString(),
      userGoal: '批量替换表格中的过期条款。',
      mode: 'agent',
      permissionMode: 'auto_safe_writes',
      model: 'gpt-4.1',
      status: 'Failed',
      summary: '连续多次工具调用失败，系统已触发熔断停止。',
      failureReason: '目标表格定位失败。',
      toolCount: 4,
      changeCount: 0,
      verifiedChangeCount: 0
    },
    {
      id: 'mock-paused-budget',
      startedAtUtc: new Date(now.getTime() - 1000 * 60 * 140).toISOString(),
      endedAtUtc: new Date(now.getTime() - 1000 * 60 * 120).toISOString(),
      userGoal: '审阅全文并逐段改写为正式商务表达。',
      mode: 'agent',
      permissionMode: 'confirm_writes',
      model: 'gpt-4.1',
      status: 'Paused',
      summary: '达到本轮执行预算，任务已暂停。',
      failureReason: '',
      toolCount: 18,
      changeCount: 7,
      verifiedChangeCount: 6
    }
  ];
}

function createMockTaskDetail(taskRunId) {
  const run = createMockTaskRuns().find((item) => item.id === taskRunId) || createMockTaskRuns()[0];
  return {
    success: true,
    run,
    tools: [
      {
        toolCallId: `${run.id}-tool-1`,
        toolName: 'probe_document',
        operationDescription: '读取文档结构。',
        rawInput: '{"include_stats":true}',
        output: '{"paragraph_count":86}',
        success: true,
        createdAtUtc: run.startedAtUtc
      },
      {
        toolCallId: `${run.id}-tool-2`,
        toolName: run.status === 'Failed' ? 'patch_range' : 'verify_script',
        operationDescription: run.status === 'Failed' ? '尝试定位并修改表格条款。' : '验证写入结果。',
        rawInput: '{}',
        output: run.status === 'Failed' ? '[ERROR] 目标表格定位失败。' : '{"all_passed":true}',
        success: run.status !== 'Failed',
        createdAtUtc: run.endedAtUtc || run.startedAtUtc
      }
    ],
    changes: run.changeCount
      ? [
          {
            toolCallId: `${run.id}-change-1`,
            toolName: 'patch_range',
            operationDescription: '修正第三章标题格式。',
            affectedParagraphs: [12, 13],
            status: 'verified',
            message: '已通过验证步骤确认改动生效。',
            createdAtUtc: run.endedAtUtc || run.startedAtUtc
          }
        ]
      : []
  };
}

function createDefaultMockSkills() {
  return [
    {
      name: 'document-finalizer',
      displayName: '文档终检',
      description: '交付前检查占位符、TODO、编号、标题层级、异常标点和术语一致性。',
      version: '1.0.0',
      enabled: true,
      isBuiltIn: true,
      trustLevel: 'built_in',
      scriptPolicy: 'prompt',
      source: 'built_in',
      contentSha256: 'mock-document-finalizer',
      activationTriggers: ['终检', '交付前', '占位符', 'TODO'],
      activationExcludedTriggers: [],
      updatedAtUtc: new Date().toISOString(),
      content: `---
name: document-finalizer
display_name: 文档终检
description: 交付前检查当前 Word 文档。
version: 1.0.0
enabled: true
---

# 文档终检

检查占位符、TODO、编号、标题层级和低风险格式问题。`,
      resources: [],
      scripts: [
        {
          skillName: 'document-finalizer',
          relativePath: 'scripts/final_check.py',
          runtime: 'python',
          sizeBytes: 128,
          sha256: 'mock-finalizer-script',
          isApproved: false
        }
      ]
    },
    {
      name: 'business-report-polish',
      displayName: '商务报告润色',
      description: '将汇报材料和商务报告改写为正式、结论前置、结构清晰的表达。',
      version: '1.0.0',
      enabled: true,
      isBuiltIn: true,
      trustLevel: 'built_in',
      scriptPolicy: 'prompt',
      source: 'built_in',
      contentSha256: 'mock-business-report-polish',
      activationTriggers: ['汇报', '商务报告', '润色', '正式表达'],
      activationExcludedTriggers: [],
      updatedAtUtc: new Date().toISOString(),
      content: `---
name: business-report-polish
display_name: 商务报告润色
description: 润色商务报告。
version: 1.0.0
enabled: true
---

# 商务报告润色

保持事实不变，优化结构、语气和表达密度。`,
      resources: []
    },
    {
      name: 'term-consistency-check',
      displayName: '术语一致性检查',
      description: '检查并统一产品名、主体名、部门名、缩写、中英文术语和同义表达。',
      version: '1.0.0',
      enabled: true,
      isBuiltIn: true,
      trustLevel: 'built_in',
      scriptPolicy: 'prompt',
      source: 'built_in',
      contentSha256: 'mock-term-consistency-check',
      activationTriggers: ['术语', '产品名', '统一表达', '缩写'],
      activationExcludedTriggers: [],
      updatedAtUtc: new Date().toISOString(),
      content: `---
name: term-consistency-check
display_name: 术语一致性检查
description: 检查术语一致性。
version: 1.0.0
enabled: true
---

# 术语一致性检查

先生成候选术语表，批量替换前必须让用户确认标准写法。`,
      resources: []
    }
  ];
}

function loadMockSkills() {
  const cached = window.localStorage.getItem(SKILLS_STORAGE_KEY);
  if (!cached) {
    const defaults = createDefaultMockSkills();
    window.localStorage.setItem(SKILLS_STORAGE_KEY, JSON.stringify(defaults));
    return defaults;
  }

  try {
    const parsed = JSON.parse(cached);
    const defaults = createDefaultMockSkills();
    const defaultByName = new Map(defaults.map((item) => [item.name, item]));
    const migrated = (Array.isArray(parsed) ? parsed : []).map((item) => {
      const builtInDefault = defaultByName.get(item.name) || {};
      return {
        trustLevel: item.isBuiltIn ? 'built_in' : 'user',
        scriptPolicy: 'prompt',
        source: item.isBuiltIn ? 'built_in' : 'local',
        contentSha256: `mock-${item.name || Date.now()}`,
        activationTriggers: [],
        activationExcludedTriggers: [],
        ...builtInDefault,
        ...item,
        activationTriggers: item.activationTriggers || builtInDefault.activationTriggers || [],
        activationExcludedTriggers:
          item.activationExcludedTriggers || builtInDefault.activationExcludedTriggers || []
      };
    });
    saveMockSkills(migrated);
    return migrated;
  } catch {
    return createDefaultMockSkills();
  }
}

function saveMockSkills(skills) {
  window.localStorage.setItem(SKILLS_STORAGE_KEY, JSON.stringify(skills || []));
}

function createSkillTemplate(name, displayName, description) {
  return `---
name: ${name}
display_name: ${displayName || name}
description: ${description || '面向当前 Word 文档的自定义处理流程。'}
version: 1.0.0
enabled: true
---

# ${displayName || name}

## 工作流

1. 判断当前 Word 文档是否符合此 Skill 的使用场景。
2. 读取相关段落、标题、表格或选区。
3. 输出问题清单或处理计划。
4. 需要修改文档时，必须遵守 SmartWord 的权限确认、Undo 和验证。

## 安全边界

- scripts/ 下的脚本只能通过 skill_run_script 执行。
- 不绕过 SmartWord 的写入确认和任务审计。
`;
}

const listeners = new Set();

function emitEvent(payload) {
  listeners.forEach((listener) => listener(payload));
}

if (window.chrome && window.chrome.webview) {
  window.chrome.webview.addEventListener('message', (event) => {
    emitEvent(event.data);
  });
}

async function callBridge(methodName, ...args) {
  const bridge = window.chrome.webview.hostObjects.bridge;
  const result = bridge[methodName](...args);
  return await result;
}

export const hostBridge = {
  get isAvailable() {
    return hasWebView2Bridge();
  },

  onAgentEvent(handler) {
    listeners.add(handler);
    return () => listeners.delete(handler);
  },

  async getSettings() {
    if (this.isAvailable) {
      const raw = await callBridge('GetSettingsJson');
      return normalizeSettings(JSON.parse(raw || '{}'));
    }

    const cached = window.localStorage.getItem(SETTINGS_STORAGE_KEY);
    return normalizeSettings(cached ? JSON.parse(cached) : {});
  },

  async saveSettings(settings) {
    const normalized = normalizeSettings(settings);

    if (this.isAvailable) {
      const raw = await callBridge('SaveSettingsJson', JSON.stringify(normalized));
      const result = JSON.parse(raw || '{}');
      if (result.success === false) {
        throw new Error(result.message || '设置保存失败');
      }

      return normalizeSettings(result.settings || normalized);
    }

    window.localStorage.setItem(SETTINGS_STORAGE_KEY, JSON.stringify(normalized));
    return normalized;
  },

  async testModelConnection(settings) {
    const normalized = normalizeSettings(settings);

    if (this.isAvailable) {
      const raw = await callBridge('TestModelConnection', JSON.stringify(normalized));
      return JSON.parse(raw || '{}');
    }

    const hasKey = Boolean(normalized.apiKey || normalized.hasApiKey);
    return {
      success: hasKey,
      serviceReachable: hasKey,
      supportsToolCalling: !String(normalized.heavyModel || '').toLowerCase().includes('speciale'),
      usedFallbackModel: false,
      message: hasKey
        ? '浏览器预览模式：已模拟连接测试通过。'
        : '浏览器预览模式：请先填写 API Key。',
      routes: [
        {
          mode: 'ask',
          selectedModel: normalized.lightModel,
          enableToolCalling: true,
          routingMessage: '浏览器预览模式模拟结果。'
        },
        {
          mode: 'plan',
          selectedModel: normalized.heavyModel,
          enableToolCalling: true,
          routingMessage: '浏览器预览模式模拟结果。'
        },
        {
          mode: 'agent',
          selectedModel: normalized.heavyModel,
          enableToolCalling: true,
          routingMessage: '浏览器预览模式模拟结果。'
        }
      ]
    };
  },

  async sendMessage(request) {
    if (!VALID_MODES.has(request?.manualMode)) {
      throw new Error('请求缺少有效的运行模式，请先选择“对话交流”“规划任务”或“自主执行”。');
    }

    if (this.isAvailable) {
      const payload = JSON.stringify(request);
      window.chrome.webview.hostObjects.bridge.SendMessageAsync(payload);
      return;
    }

    console.warn('WebView2 HostObject 不可用，已回退到本地模拟。');
    createMockResponse(request, emitEvent);
  },

  async cancelCurrentRun() {
    pendingMockConfirmation = null;
    pendingMockTodoRecovery = null;

    if (this.isAvailable) {
      await callBridge('CancelCurrentRun');
      return;
    }

    emitEvent({
      type: 'cancelled',
      message: '已取消当前任务。'
    });
  },

  async confirmToolCall(toolCallId, confirmed, options = {}) {
    if (this.isAvailable) {
      await callBridge(
        'ConfirmToolCallWithOptions',
        toolCallId,
        confirmed === true,
        options?.remember === true
      );
      return;
    }

    if (!pendingMockConfirmation || pendingMockConfirmation.toolCallId !== toolCallId) {
      return;
    }

    if (confirmed) {
      emitEvent({
        type: 'tool_call_completed',
        toolCallId,
        toolName: pendingMockConfirmation.toolName,
        toolOutput: '{"success":true}',
        toolSuccess: true,
        operationDescription: pendingMockConfirmation.operationDescription
      });
      emitEvent({
        type: 'change_executed',
        toolCallId,
        toolName: pendingMockConfirmation.toolName,
        affectedParagraphs: [2],
        operationDescription: pendingMockConfirmation.operationDescription,
        message: '写入已执行，等待验证步骤返回结果。'
      });
      emitEvent({
        type: 'change_applied',
        toolCallId,
        toolName: pendingMockConfirmation.toolName,
        affectedParagraphs: [2],
        operationDescription: pendingMockConfirmation.operationDescription,
        message: '已通过验证步骤确认改动生效。'
      });
      emitEvent({
        type: 'task_completed',
        message: '当前为浏览器降级模式，已完成模拟写入。'
      });
    } else {
      emitEvent({
        type: 'tool_call_skipped',
        toolCallId,
        toolName: pendingMockConfirmation.toolName,
        toolOutput: "[SKIPPED] Tool 'patch_range' was skipped by user.",
        toolSuccess: false,
        operationDescription: pendingMockConfirmation.operationDescription
      });
      emitEvent({
        type: 'task_completed',
        message: '当前为浏览器降级模式，模拟写入已被跳过。'
      });
    }

    pendingMockConfirmation = null;
  },

  async submitTodoBoardRecoveryDecision(recoveryRequestId, decision) {
    if (this.isAvailable) {
      await callBridge('SubmitTodoBoardRecoveryDecision', recoveryRequestId, decision || '');
      return;
    }

    if (!pendingMockTodoRecovery || pendingMockTodoRecovery.recoveryRequestId !== recoveryRequestId) {
      return;
    }

    emitEvent({
      type: 'todo_board_ready',
      boardJson: pendingMockTodoRecovery.boardJson || '{"items":[]}',
      currentTodoId: ''
    });
    emitEvent({
      type: 'task_completed',
      message: `当前为浏览器降级模式，已模拟提交恢复决策：${decision || 'recover_existing'}。`
    });
    pendingMockTodoRecovery = null;
  },

  async stopPausedTodoRun() {
    pendingMockTodoRecovery = null;

    if (this.isAvailable) {
      const raw = await callBridge('StopPausedTodoRun');
      const result = JSON.parse(raw || '{}');
      if (result.success === false) {
        throw new Error(result.message || '停止任务失败');
      }

      return result;
    }

    return {
      success: true,
      message: '当前为浏览器降级模式，已模拟停止暂停任务。'
    };
  },

  async getRecentTaskRuns(limit = 20) {
    if (this.isAvailable) {
      const raw = await callBridge('GetRecentTaskRunsJson', limit);
      const result = JSON.parse(raw || '{}');
      if (result.success === false) {
        throw new Error(result.message || '历史读取失败');
      }

      return Array.isArray(result.items) ? result.items : [];
    }

    return createMockTaskRuns().slice(0, Math.max(1, Math.min(50, limit || 20)));
  },

  async getTaskRunDetail(taskRunId) {
    if (this.isAvailable) {
      const raw = await callBridge('GetTaskRunDetailJson', taskRunId || '');
      const result = JSON.parse(raw || '{}');
      if (result.success === false) {
        throw new Error(result.message || '历史详情读取失败');
      }

      return result;
    }

    return createMockTaskDetail(taskRunId);
  },

  async getSkills() {
    if (this.isAvailable) {
      const raw = await callBridge('GetSkillsJson');
      const result = JSON.parse(raw || '{}');
      if (result.success === false) {
        throw new Error(result.message || 'Skill 列表读取失败');
      }

      return Array.isArray(result.items) ? result.items : [];
    }

    return loadMockSkills().map(({ content, resources, ...skill }) => skill);
  },

  async getSkillDetail(name) {
    if (this.isAvailable) {
      const raw = await callBridge('GetSkillDetailJson', name || '');
      const result = JSON.parse(raw || '{}');
      if (result.success === false) {
        throw new Error(result.message || 'Skill 详情读取失败');
      }

      return result;
    }

    const skill = loadMockSkills().find((item) => item.name === name);
    if (!skill) {
      throw new Error('未找到指定 Skill');
    }

    const { content, resources, scripts, ...summary } = skill;
    return {
      success: true,
      skill: summary,
      content,
      resources: resources || [],
      scripts: scripts || []
    };
  },

  async createSkill(request) {
    if (this.isAvailable) {
      const raw = await callBridge('CreateSkillJson', JSON.stringify(request || {}));
      const result = JSON.parse(raw || '{}');
      if (result.success === false) {
        throw new Error(result.message || 'Skill 创建失败');
      }

      return result;
    }

    const skills = loadMockSkills();
    const name = String(request?.name || '').trim().toLowerCase();
    if (!/^[a-z0-9][a-z0-9-]{0,63}$/.test(name)) {
      throw new Error('Skill 名称只能包含小写字母、数字和连字符');
    }
    if (skills.some((item) => item.name === name)) {
      throw new Error('同名 Skill 已存在');
    }

    const skill = {
      name,
      displayName: request?.displayName || name,
      description: request?.description || '面向当前 Word 文档的自定义处理流程。',
      version: '1.0.0',
      enabled: true,
      isBuiltIn: false,
      trustLevel: 'user',
      scriptPolicy: 'prompt',
      source: 'local',
      contentSha256: `mock-${Date.now()}`,
      activationTriggers: [],
      activationExcludedTriggers: [],
      updatedAtUtc: new Date().toISOString(),
      content: request?.content || createSkillTemplate(name, request?.displayName, request?.description),
      resources: [],
      scripts: []
    };
    skills.push(skill);
    saveMockSkills(skills);
    const { content, resources, ...summary } = skill;
    return { success: true, skill: summary, content, resources, scripts: skill.scripts || [] };
  },

  async saveSkill(name, content) {
    return this.saveSkillWithVersion(name, content, '');
  },

  async saveSkillWithVersion(name, content, expectedContentSha256) {
    if (this.isAvailable) {
      const raw = await callBridge(
        'SaveSkillWithVersionJson',
        name || '',
        content || '',
        expectedContentSha256 || ''
      );
      const result = JSON.parse(raw || '{}');
      if (result.success === false) {
        throw new Error(result.message || 'Skill 保存失败');
      }

      return result;
    }

    const skills = loadMockSkills();
    const skill = skills.find((item) => item.name === name);
    if (!skill || skill.isBuiltIn) {
      throw new Error('只能编辑用户 Skill');
    }

    skill.content = content || '';
    skill.contentSha256 = `mock-${Date.now()}`;
    skill.updatedAtUtc = new Date().toISOString();
    saveMockSkills(skills);
    const { resources, scripts, ...summaryWithContent } = skill;
    const { content: savedContent, ...summary } = summaryWithContent;
    return { success: true, skill: summary, content: savedContent, resources: resources || [], scripts: scripts || [] };
  },

  async setSkillScriptPolicy(name, policy) {
    const normalized = String(policy || '').toLowerCase() === 'prompt' ? 'prompt' : 'disabled';
    if (this.isAvailable) {
      const raw = await callBridge('SetSkillScriptPolicyJson', name || '', normalized);
      const result = JSON.parse(raw || '{}');
      if (result.success === false) {
        throw new Error(result.message || 'Skill 脚本策略设置失败');
      }
      return result;
    }

    const skills = loadMockSkills();
    const skill = skills.find((item) => item.name === name);
    if (skill) {
      skill.scriptPolicy = normalized;
      saveMockSkills(skills);
    }
    return { success: true, scriptPolicy: normalized };
  },

  async getSkillTelemetrySummary() {
    if (this.isAvailable) {
      const raw = await callBridge('GetSkillTelemetrySummaryJson');
      const result = JSON.parse(raw || '{}');
      if (result.success === false) {
        throw new Error(result.message || '本地观测读取失败');
      }
      return result.summary || {};
    }

    return {
      isEnabled: true,
      eventCount: 0,
      skillContextResolvedCount: 0,
      completedTaskCount: 0,
      failedTaskCount: 0,
      toolFailureCount: 0,
      topSkills: []
    };
  },

  async getSkillScriptApprovals() {
    if (this.isAvailable) {
      const raw = await callBridge('GetSkillScriptApprovalsJson');
      const result = JSON.parse(raw || '{}');
      if (result.success === false) {
        throw new Error(result.message || '脚本授权读取失败');
      }

      return Array.isArray(result.items) ? result.items : [];
    }

    return JSON.parse(window.localStorage.getItem('smartword-script-approvals') || '[]');
  },

  async revokeSkillScriptApproval(key) {
    if (this.isAvailable) {
      const raw = await callBridge('RevokeSkillScriptApprovalJson', JSON.stringify(key || {}));
      const result = JSON.parse(raw || '{}');
      if (result.success === false) {
        throw new Error(result.message || '脚本授权撤销失败');
      }

      return result;
    }

    const stableKey = JSON.stringify(key || {});
    const approvals = JSON.parse(window.localStorage.getItem('smartword-script-approvals') || '[]')
      .filter((item) => JSON.stringify(item.key || {}) !== stableKey);
    window.localStorage.setItem('smartword-script-approvals', JSON.stringify(approvals));
    return { success: true };
  },

  async deleteSkill(name) {
    if (this.isAvailable) {
      const raw = await callBridge('DeleteSkillJson', name || '');
      const result = JSON.parse(raw || '{}');
      if (result.success === false) {
        throw new Error(result.message || 'Skill 删除失败');
      }

      return result;
    }

    const skills = loadMockSkills();
    const skill = skills.find((item) => item.name === name);
    if (skill?.isBuiltIn) {
      throw new Error('内置 Skill 不允许删除');
    }
    saveMockSkills(skills.filter((item) => item.name !== name));
    return { success: true };
  },

  async setSkillEnabled(name, enabled) {
    if (this.isAvailable) {
      const raw = await callBridge('SetSkillEnabledJson', name || '', enabled === true);
      const result = JSON.parse(raw || '{}');
      if (result.success === false) {
        throw new Error(result.message || 'Skill 启停设置失败');
      }

      return result;
    }

    const skills = loadMockSkills();
    const skill = skills.find((item) => item.name === name);
    if (skill) {
      skill.enabled = enabled === true;
      skill.updatedAtUtc = new Date().toISOString();
      saveMockSkills(skills);
    }
    return { success: true };
  },

  async navigateToParagraph(paragraphIndex) {
    if (!this.isAvailable) {
      return;
    }

    await callBridge('NavigateToParagraph', paragraphIndex);
  },

  async submitQuestionAnswer(questionId, answer) {
    if (this.isAvailable) {
      await callBridge('SubmitQuestionAnswer', questionId, answer || '');
      return;
    }
    // 浏览器降级模式：直接触发模拟事件
    emitEvent({ type: 'question_answered', questionId, answer: answer || '' });
  }
};
