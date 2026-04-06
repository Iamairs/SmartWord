using System;
using System.Collections.Generic;
using System.Linq;
using SmartWord.Core.Enums;
using SmartWord.Core.Interfaces;
using SmartWord.Core.Models;

namespace SmartWord.Application.Tools
{
    /// <summary>
    /// 维护运行模式到工具定义的暴露关系。
    /// </summary>
    public sealed class ToolRegistry : IToolRegistry
    {
        private readonly Dictionary<string, ITool> _tools =
            new Dictionary<string, ITool>(StringComparer.OrdinalIgnoreCase);

        public void Register(ITool tool)
        {
            if (tool == null)
            {
                throw new ArgumentNullException(nameof(tool));
            }

            _tools[tool.Name] = tool;
        }

        public IReadOnlyList<ToolDefinition> GetToolDefinitions(AgentMode mode)
        {
            IEnumerable<ITool> visibleTools = _tools.Values;
            if (mode == AgentMode.Ask || mode == AgentMode.Plan)
            {
                visibleTools = visibleTools.Where(item => item.RequiredPermission == ToolPermission.ReadOnly);
            }

            return visibleTools
                .Select(tool => new ToolDefinition
                {
                    Name = tool.Name,
                    Description = tool.Description,
                    Parameters = tool.InputSchema
                })
                .ToList()
                .AsReadOnly();
        }

        public ITool GetTool(string toolName)
        {
            if (string.IsNullOrWhiteSpace(toolName))
            {
                return null;
            }

            _tools.TryGetValue(toolName, out var tool);
            return tool;
        }
    }
}
