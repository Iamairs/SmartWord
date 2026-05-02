using System;
using SmartWord.Core.Models;

namespace SmartWord.Application.Context
{
    /// <summary>
    /// 按模型上下文窗口比例计算压缩触发预算。
    /// </summary>
    public sealed class ContextBudgetPolicy
    {
        private const int DefaultContextWindowTokens = 256 * 1024;
        private const double DefaultSoftLimitRatio = 0.65;
        private const double DefaultHardLimitRatio = 0.85;
        private const double DefaultEmergencyLimitRatio = 0.95;
        private const double DefaultTokenSafetyMargin = 1.2;

        public ContextBudgetSnapshot Resolve(AgentRunOptions options)
        {
            var safeOptions = options ?? new AgentRunOptions();
            var contextWindowTokens = safeOptions.ContextWindowTokens > 0
                ? safeOptions.ContextWindowTokens
                : DefaultContextWindowTokens;
            var softRatio = NormalizeRatio(safeOptions.ContextSoftLimitRatio, DefaultSoftLimitRatio);
            var hardRatio = NormalizeRatio(safeOptions.ContextHardLimitRatio, DefaultHardLimitRatio);
            var emergencyRatio = NormalizeRatio(safeOptions.ContextEmergencyLimitRatio, DefaultEmergencyLimitRatio);
            if (hardRatio <= softRatio)
            {
                hardRatio = Math.Min(0.95, softRatio + 0.15);
            }

            if (emergencyRatio <= hardRatio)
            {
                emergencyRatio = Math.Min(0.99, hardRatio + 0.10);
            }

            return new ContextBudgetSnapshot
            {
                ContextWindowTokens = contextWindowTokens,
                SoftLimitTokens = Math.Max(1, (int)Math.Floor(contextWindowTokens * softRatio)),
                HardLimitTokens = Math.Max(1, (int)Math.Floor(contextWindowTokens * hardRatio)),
                EmergencyLimitTokens = Math.Max(1, (int)Math.Floor(contextWindowTokens * emergencyRatio)),
                TokenSafetyMargin = safeOptions.ContextTokenSafetyMargin > 0
                    ? safeOptions.ContextTokenSafetyMargin
                    : DefaultTokenSafetyMargin
            };
        }

        public int ApplySafetyMargin(int estimatedTokens, ContextBudgetSnapshot budget)
        {
            if (estimatedTokens <= 0)
            {
                return 0;
            }

            var margin = budget == null || budget.TokenSafetyMargin <= 0
                ? DefaultTokenSafetyMargin
                : budget.TokenSafetyMargin;
            return (int)Math.Ceiling(estimatedTokens * margin);
        }

        private static double NormalizeRatio(double value, double fallback)
        {
            return value > 0 && value < 1
                ? value
                : fallback;
        }
    }

    public sealed class ContextBudgetSnapshot
    {
        public int ContextWindowTokens { get; set; }

        public int SoftLimitTokens { get; set; }

        public int HardLimitTokens { get; set; }

        public int EmergencyLimitTokens { get; set; }

        public double TokenSafetyMargin { get; set; }
    }
}
