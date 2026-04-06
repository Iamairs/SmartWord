using System;
using System.Collections.Generic;
using System.IO;
using SmartWord.Core.Enums;

namespace SmartWord.Application.PromptBuilder
{
    /// <summary>
    /// 按运行模式读取并拼装系统提示词。
    /// </summary>
    public class SystemPromptBuilder
    {
        private readonly string _promptsDirectory;

        public SystemPromptBuilder(string promptsDirectory)
        {
            _promptsDirectory = promptsDirectory ?? string.Empty;
        }

        public string Build(AgentMode mode)
        {
            if (string.IsNullOrWhiteSpace(_promptsDirectory) || !Directory.Exists(_promptsDirectory))
            {
                return string.Empty;
            }

            var promptParts = new List<string>
            {
                ReadPrompt("SYSTEM.md"),
                ReadPrompt(GetModePromptFileName(mode))
            };

            return string.Join(
                Environment.NewLine + Environment.NewLine,
                promptParts.FindAll(part => !string.IsNullOrWhiteSpace(part)));
        }

        private string ReadPrompt(string fileName)
        {
            var promptPath = Path.Combine(_promptsDirectory, fileName);
            return File.Exists(promptPath) ? File.ReadAllText(promptPath) : string.Empty;
        }

        private string GetModePromptFileName(AgentMode mode)
        {
            switch (mode)
            {
                case AgentMode.Ask:
                    return "ASK.md";
                case AgentMode.Plan:
                    return "PLAN.md";
                case AgentMode.Agent:
                default:
                    return "AGENT.md";
            }
        }
    }
}
