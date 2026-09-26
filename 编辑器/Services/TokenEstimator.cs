using System;
using System.Collections.Generic;

namespace 编辑器.Services
{
    /// <summary>
    /// 上下文体积估算。
    ///
    /// ★ 移植自 ZCode（github.com/zai-org/ZCode）：
    ///   apps/zcode-cli/packages/core/src/context/utils.ts 的 estimateTokens()
    ///   与 packages/shared/src/usage-stats.ts 的 ESTIMATED_TOKEN_CHAR_DIVISOR = 3。
    ///
    /// 为什么不能照搬"字符数 ÷ 4"这类英文经验值：中文一个汉字占的 token 明显多于一个
    /// 西文字母，对小说正文按英文口径估会**低估一倍以上** —— 于是一次次"看着还没满"，
    /// 一路撞到服务端 400 才知道超了。
    ///
    /// 口径（与 ZCode 一致）：汉字记 2 个「估算字符」，其余记 1 个，最后统一除以 3。
    /// 折算下来 ≈ 汉字 0.67 token/字、西文 0.33 token/字符。
    /// 刻意偏保守：宁可略微高估、提前压缩，也不要撞服务端报错。
    ///
    /// 注意这里**只是本地兜底**。真正的判据优先用服务端返回的真实 usage
    /// （见 <see cref="CompactPolicy"/>），估算仅在还没有真实数据时使用。
    /// </summary>
    public static class TokenEstimator
    {
        /// <summary>与 ZCode 的 ESTIMATED_TOKEN_CHAR_DIVISOR 一致。</summary>
        private const double CharDivisor = 3.0;

        /// <summary>估算一段文本的 token 数。null / 空串返回 0。</summary>
        public static int Estimate(string? text)
        {
            if (string.IsNullOrEmpty(text)) return 0;

            int wide = 0;
            foreach (var ch in text)
            {
                if (IsWideChar(ch)) wide++;
            }

            int narrow = text.Length - wide;
            return (int)Math.Ceiling((wide * 2.0 + narrow) / CharDivisor);
        }

        /// <summary>估算多段文本之和（不拼接，避免大文本重复分配）。</summary>
        public static int EstimateAll(IEnumerable<string?>? texts)
        {
            if (texts == null) return 0;

            int total = 0;
            foreach (var text in texts)
                total += Estimate(text);
            return total;
        }

        /// <summary>
        /// 是否按"宽字符"（≈2 个估算字符）计入。
        ///
        /// ZCode 只匹配 U+4E00–U+9FFF（基本汉字）。这里额外纳入了中文标点、全角字符
        /// 与扩展 A 区 —— 小说里的「，。！？」「《》」同样是整 token 级别，漏掉会持续低估。
        /// </summary>
        public static bool IsWideChar(char c)
        {
            // CJK 基本区（ZCode 原范围）
            if (c >= '\u4E00' && c <= '\u9FFF') return true;
            // CJK 扩展 A
            if (c >= '\u3400' && c <= '\u4DBF') return true;
            // CJK 符号与标点（、。「」《》等）
            if (c >= '\u3000' && c <= '\u303F') return true;
            // 中日韩部首补充
            if (c >= '\u2E80' && c <= '\u2EFF') return true;
            // 全角字符（，！？：；（）等）
            if (c >= '\uFF00' && c <= '\uFFEF') return true;
            // 日文假名（有时会出现在中文小说里）
            if (c >= '\u3040' && c <= '\u30FF') return true;
            return false;
        }
    }
}
