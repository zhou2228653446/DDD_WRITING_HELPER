using System.Linq;
using 编辑器.Services;

namespace 编辑器.Tests;

/// <summary>
/// 超长篇提示词方案。
/// 重点：**它是一套独立方案**，选了才生效；别的方案一个字都不该被带着改。
/// </summary>
public class UltraLongPresetTests
{
    private static readonly string[] AllKeys =
    {
        AiPrompts.Keys.Base,
        AiPrompts.Keys.CreativeOutput,
        AiPrompts.Keys.StructuredOutput,
        AiPrompts.Keys.Continue,
        AiPrompts.Keys.Polish,
        AiPrompts.Keys.Name,
        AiPrompts.Keys.Character,
        AiPrompts.Keys.Outline,
        AiPrompts.Keys.Background,
        AiPrompts.Keys.ChapterOutline,
        AiPrompts.Keys.WriteStyle,
        AiPrompts.Keys.Expand,
        AiPrompts.Keys.Chat,
        AiPrompts.Keys.SettingBook,
        AiPrompts.Keys.Review,
    };

    [Fact]
    public void 预设已注册()
    {
        var def = PromptPresets.Find(PromptPresets.IdUltraLongNovel);
        Assert.NotNull(def);
        Assert.Equal("超长篇网络小说", def!.Name);
    }

    [Fact]
    public void 十五个槽位全都有文本_不会回落到通用方案()
    {
        var def = PromptPresets.Find(PromptPresets.IdUltraLongNovel)!;
        foreach (var key in AllKeys)
        {
            Assert.True(def.Texts.TryGetValue(key, out var text) && !string.IsNullOrWhiteSpace(text),
                $"槽位 {key} 缺文本——它会静默回落到「通用写作」，语气就跑偏了");
        }
    }

    [Fact]
    public void 自己的一套上下文标签_不会串到别的方案()
    {
        var def = PromptPresets.Find(PromptPresets.IdUltraLongNovel)!;
        Assert.Equal("核心人物（常驻阵容）", def.Labels[PromptPresets.LabelKeys.Characters]);

        // 小说方案仍是"主要人物设定"，公文仍是"事项要素"
        Assert.Equal("主要人物设定",
            PromptPresets.Find(PromptPresets.IdNovel)!.Labels[PromptPresets.LabelKeys.Characters]);
        Assert.Equal("事项要素",
            PromptPresets.Find(PromptPresets.IdOfficial)!.Labels[PromptPresets.LabelKeys.Characters]);
    }

    [Fact]
    public void 其它四套方案的文本没有被带动()
    {
        // 小说的 Base 就是默认文本；论文的 Base 有自己的人格设定
        Assert.Equal(AiPrompts.Defaults.Base,
            PromptPresets.TextOf(PromptPresets.IdNovel, AiPrompts.Keys.Base));
        Assert.Contains("学术写作助手",
            PromptPresets.TextOf(PromptPresets.IdAcademic, AiPrompts.Keys.Base));

        // 论文的审稿仍是通用默认文本——没有被超长篇的红线污染
        Assert.Equal(AiPrompts.Defaults.Task.Review,
            PromptPresets.TextOf(PromptPresets.IdAcademic, AiPrompts.Keys.Review));
    }

    [Fact]
    public void 审稿红线只写在超长篇自己的任务说明里()
    {
        var ultra = PromptPresets.TextOf(PromptPresets.IdUltraLongNovel, AiPrompts.Keys.Review);
        Assert.Contains("战力与状态", ultra);
        Assert.Contains("角色阵容", ultra);

        // 小说方案的审稿不该出现这两条（它是通用一致性检查）
        var novel = PromptPresets.TextOf(PromptPresets.IdNovel, AiPrompts.Keys.Review);
        Assert.DoesNotContain("战力与状态", novel);
    }

    [Fact]
    public void 方案列表里多了一套_但其它四套顺序不变()
    {
        var ids = PromptPresets.All.Select(d => d.Id).ToList();
        Assert.Equal(5, ids.Count);
        Assert.Equal(new[]
        {
            PromptPresets.IdNovel,
            PromptPresets.IdAcademic,
            PromptPresets.IdOfficial,
            PromptPresets.IdGeneral,
            PromptPresets.IdUltraLongNovel,
        }, ids);
    }
}
