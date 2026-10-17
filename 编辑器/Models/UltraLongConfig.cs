using System;
using System.Collections.Generic;

namespace 编辑器
{
    /// <summary>
    /// 超长篇增强结构（分卷 / 角色分级 / 主角状态面板）。
    ///
    /// ★ <see cref="NovelProject.UltraLong"/> 为 <c>null</c> 表示**从未启用**——这是
    ///   论文、公文、通用以及普通小说项目的常态。所有新行为都必须先判断它是否为 null，
    ///   为 null 时走原来的路径，一个字节都不变。
    ///
    /// ★ 为什么能力挂在**项目**上而不是提示词方案上：一个百万字的论文分"编"也合理，
    ///   而"用哪套提示词"是另一件事。两者解耦后，"论文 + 分卷"这种组合才成立。
    ///
    /// ★ 为什么不直接把这些字段摊在 <see cref="NovelProject"/> 顶层：摊在顶层的话，
    ///   每加一个功能，所有文体的文件都会多出一堆键；装进一个可空容器，未启用时
    ///   连序列化都不会发生（见 NovelProject.UltraLong 上的 WhenWritingNull）。
    /// </summary>
    public sealed class UltraLongConfig
    {
        /// <summary>结构版本。给"未来某次改字段"留的识别位；旧版程序读不懂会丢，见方案 5.4。</summary>
        public int SchemaVersion { get; set; } = 1;

        /// <summary>分卷结构。关掉时章节树仍是扁平两层，卷数据保留不删。</summary>
        public bool EnableVolumes { get; set; } = true;

        /// <summary>角色生命周期（Core/Arc/Guest + 生死状态）参与上下文筛选。</summary>
        public bool EnableCharacterLifecycle { get; set; } = true;

        /// <summary>主角状态面板（境界 / 处境 / 目标 / 关键道具）参与注入。</summary>
        public bool EnableStateBoard { get; set; } = true;

        public List<Volume> Volumes { get; set; } = new();

        /// <summary>视角主角的 CharacterId。状态面板只为这些人渲染；空表示用 Role=="主角" 兜底。</summary>
        public List<string> ProtagonistIds { get; set; } = new();

        /// <summary>动态素材（卷摘要 / 本章角色 / 近期梗概 / 上章尾巴）占总窗口的比例上限。</summary>
        public double DynamicBudgetRatio { get; set; } = 0.12;

        /// <summary>
        /// 夹取后的预算比例。配置文件是可以手改的，也可能被旧版写成 0 或 NaN，
        /// 直接拿去乘窗口会把上下文算成负数。
        /// </summary>
        public double EffectiveBudgetRatio =>
            double.IsNaN(DynamicBudgetRatio) || DynamicBudgetRatio <= 0
                ? 0.12
                : Math.Clamp(DynamicBudgetRatio, 0.08, 0.20);
    }

    /// <summary>
    /// 角色的生命周期层级。
    ///
    /// ★ 默认值必须放在 0 上（<c>Arc = 0</c>），这样 <c>WhenWritingDefault</c> 才能
    ///   把它省略掉。写成 <c>{ Core, Arc, Guest }</c> 会让 Arc = 1，于是"默认值"
    ///   反而被写进每个角色的 JSON —— 论文/公文项目也会跟着多一行。
    /// </summary>
    public enum CharacterScope
    {
        /// <summary>分卷/副本关键人物。默认值：不占全局上下文，本卷或本章才注入。</summary>
        Arc = 0,

        /// <summary>全局常驻（主角、随身金手指、核心伴侣、贯穿反派）。每轮都注入。</summary>
        Core = 1,

        /// <summary>单章客串。保留记录（审稿要认得出正文里写的人），但不进全局上下文。</summary>
        Guest = 2,
    }

    /// <summary>角色当前状态。顺序同样要让默认值落在 0 上。</summary>
    public enum CharacterStatus
    {
        /// <summary>活跃登场中（默认）。</summary>
        Active = 0,

        /// <summary>暂未出场 / 幕后。</summary>
        Inactive = 1,

        /// <summary>已退场（飞升、离队、下线）。</summary>
        Retired = 2,

        /// <summary>已死亡。上下文里会进"绝不可出场"名单。</summary>
        Deceased = 3,
    }

    /// <summary>
    /// 一个剧情卷。
    ///
    /// ★ 卷与章节的关系**只存在 <see cref="Chapter.VolumeId"/> 上**，这里刻意不存
    ///   `ChapterNumbers`：两份清单必然漂移，而漂移时没人知道该信哪份。
    ///   卷的范围一律现算（见 <see cref="NovelProject.ChaptersOfVolume"/>）。
    /// </summary>
    public sealed class Volume
    {
        public string VolumeId { get; set; } = Guid.NewGuid().ToString();

        /// <summary>显示序号（第一卷 = 1）。允许重复，排序只按它在列表里的位置。</summary>
        public int Number { get; set; } = 1;

        public string Title { get; set; } = "第一卷";

        /// <summary>本卷要达成的核心目标。进 AI 上下文，压住"写着写着跑题"。</summary>
        public string Goal { get; set; } = "";

        /// <summary>本卷回顾。卷末由作者或 AI 写；进上下文，替代"全卷正文"。</summary>
        public string Summary { get; set; } = "";
    }
}
