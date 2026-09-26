using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using 编辑器.Services;

namespace 编辑器
{
    /// <summary>
    /// 主窗口侧的上下文预算胶水层。真正的机制在
    /// <see cref="Services.ChatContextCompactor"/> 里 —— 那边不依赖任何窗口，
    /// 因此可以离线跑断言（见 .workbuddy/compact）。
    ///
    /// 本文件只负责三件事：给压缩器接上"当前模型名 / 服务 / 状态栏"，
    /// 把摘要块拼进 systemPrompt，以及刷新面板上的记忆提示。
    /// </summary>
    public partial class MainWindow
    {
        /// <summary>聊天上下文的预算与压缩。跟着 <c>_chatSession</c> 一起随项目重建。</summary>
        private ChatContextCompactor? _compactor;

        private string CurrentModelName() => _profileManager?.ActiveProfile?.Model ?? "";

        /// <summary>
        /// 建好压缩器并接上依赖。必须在 <c>_chatSession</c> 就位之后调用
        ///（两者都挂在"当前项目"上，换项目时要一起换）。
        /// </summary>
        private void AttachCompactor()
        {
            if (_chatSession == null)
            {
                _compactor = null;
                return;
            }

            _compactor = new ChatContextCompactor(
                _chatSession,
                CurrentModelName,
                new CompactPolicyConfig
                {
                    MaxOutputTokens = ChatContextCompactor.DefaultMaxOutputTokens
                })
            {
                OnStatus = UpdateStatus
            };
        }

        /// <summary>
        /// 发请求前过一遍上下文预算（可能触发压缩）。返回最终要发送的历史。
        ///
        /// 摘要一旦生成就写进了 <c>_chatSession</c>，所以调用方必须**在这之后**
        /// 才调 <see cref="AppendChatSummaryBlock"/> 拼 systemPrompt —— 否则用的是旧摘要。
        /// </summary>
        private async Task<IReadOnlyList<ChatMessage>> EnsureChatContextBudgetAsync(
            string systemPrompt, string userPrompt, CancellationToken ct)
        {
            if (_compactor == null || _apiService == null)
                return _chatSession?.BuildHistory() ?? Array.Empty<ChatMessage>();

            var outcome = await _compactor.EnsureBudgetAsync(
                systemPrompt,
                userPrompt,
                // 取消令牌在 options 里（压缩器构造 options 时已写入 ct），
                // 所以这里用三参重载；摘要调用不带对话历史。
                (prompt, system, options, _) => _apiService.CompleteTextAsync(prompt, system, options),
                ct);

            switch (outcome.Outcome)
            {
                case CompactOutcome.Summarized:
                    ShowNotification(outcome.Message);
                    break;

                case CompactOutcome.Failed:
                    ShowNotification(outcome.Message);
                    break;
            }

            return outcome.History;
        }

        /// <summary>
        /// 把对话摘要拼到 systemPrompt 末尾。没有摘要时原样返回。
        ///
        /// 摘要放 system 而不是 messages：Anthropic 要求 messages 以 user 开头且
        /// **同角色不能连续**，而摘要天然是一条 user 消息，插在保留轮次（首条也是 user）
        /// 前面会直接 400。放进 system 既绕开这个坑，语义上也更顺 —— system 里本来就是背景设定。
        /// </summary>
        private string AppendChatSummaryBlock(string baseSystemPrompt)
        {
            var block = _chatSession?.BuildSummaryBlock();
            if (string.IsNullOrWhiteSpace(block)) return baseSystemPrompt;
            return baseSystemPrompt + "\n\n" + block;
        }

        /// <summary>
        /// 刷新面板上的「聊天记忆 / 上下文占用」提示。
        ///
        /// 显示的是**对话记忆**这一部分（摘要 + 历史轮次），不是整个请求的规模 ——
        /// 完整规模看状态栏那行由服务端报回的真实 Token 数。
        /// </summary>
        private void RefreshChatMemoryInfo()
        {
            if (_chatSession == null)
            {
                _aiPanel.SetChatMemoryInfo(0, 0, 0, 0);
                return;
            }

            var config = new CompactPolicyConfig
            {
                MaxOutputTokens = ChatContextCompactor.DefaultMaxOutputTokens
            };
            int window = CompactPolicy.ResolveContextWindow(config, CurrentModelName());
            int used = _chatSession.EstimatedTokens + TokenEstimator.Estimate(_chatSession.BuildSummaryBlock());

            _aiPanel.SetChatMemoryInfo(_chatSession.RoundCount, _chatSession.SummarizedRounds, used, window);
        }
    }
}
