using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace 编辑器.Services
{
    /// <summary>
    /// 「技能」= 一套可命名的**创作手法指令**。它不改身份层、不动上下文层，
    /// 只针对一个或多个任务槽位（<see cref="AiPrompts.Keys"/>）替换任务说明，
    /// 可选覆盖输出契约。
    ///
    /// 与「方案（Preset）」的区别：方案换的是整套人设与语境（小说/论文/公文），
    /// 技能换的是**手法**（这一章怎么起头、这段对白怎么改）——两者正交，可叠加：
    /// 方案定"你是谁"，技能定"这一下怎么干"。
    ///
    /// 内置技能写死在代码里（随版本升级）；用户自定义技能存
    /// <c>{配置目录}/skills.json</c>（只存自定义，内置不落盘）。
    /// </summary>
    public class NovelSkill
    {
        /// <summary>稳定 id。内置技能用固定字符串，自定义技能用 GUID。</summary>
        public string Id { get; set; } = "";

        public string Name { get; set; } = "";

        /// <summary>一句话说明这个技能什么时候用、管什么。</summary>
        public string Description { get; set; } = "";

        /// <summary>内置技能不可编辑/删除（随版本升级）。</summary>
        public bool IsBuiltIn { get; set; }

        /// <summary>适用任务键（AiPrompts.Keys 的值，如 "Task.Continue"）。命中才替换。</summary>
        public List<string> AppliesTo { get; set; } = new();

        /// <summary>
        /// 适用**方案**（PromptPresets 的 Id，如 "novel"）。空列表 = 全部方案可用。
        /// 方案切换后，面板的技能下拉只列出适用当前方案的技能——论文方案下不该冒出「黄金三章」。
        /// </summary>
        public List<string> Presets { get; set; } = new();

        /// <summary>命中时替换原任务说明的文本。</summary>
        public string TaskPrompt { get; set; } = "";

        /// <summary>可选：命中时覆盖输出契约（"Task.Output.Creative"/"Task.Output.Structured" 或 null=沿用默认）。</summary>
        public string? OutputContract { get; set; }

        /// <summary>AI 面板选中该技能时显示的输入提示。</summary>
        public string InputHint { get; set; } = "";

        public NovelSkill Clone() => new()
        {
            Id = Id,
            Name = Name,
            Description = Description,
            IsBuiltIn = IsBuiltIn,
            AppliesTo = new List<string>(AppliesTo),
            Presets = new List<string>(Presets),
            TaskPrompt = TaskPrompt,
            OutputContract = OutputContract,
            InputHint = InputHint
        };
    }

    /// <summary>
    /// 技能的加载 / 落盘 / 导入导出 / 解析。规则：
    /// · 有效技能 = 内置（恒在、只读）+ 自定义（读 skills.json）
    /// · Save 只写自定义；AddOrUpdate/Delete 对内置一律拒绝
    /// · 解析：技能命中 AppliesTo 才替换任务说明 / 契约，否则回退原值
    /// </summary>
    public static class NovelSkillStore
    {
        public const string FileName = "skills.json";

        public static readonly IReadOnlyList<NovelSkill> BuiltIn = new List<NovelSkill>
        {
            new()
            {
                IsBuiltIn = true,
                Presets = new List<string> { PromptPresets.IdNovel },   // 创作手法只属于小说方案
                Id = "hook-opening", Name = "黄金三章",
                Description = "开头定生死：把第一章的钩子立起来，让读者想翻下一页。",
                AppliesTo = new List<string> { AiPrompts.Keys.Continue, AiPrompts.Keys.Outline, AiPrompts.Keys.ChapterOutline },
                TaskPrompt = """
                    【手法：黄金三章】
                    这一章是全书最贵的几页，按下面的章法来：

                    - 第一章（代入）：开场三句内让读者知道"这是谁、在哪、现在正发生什么不能回避的事"。
                      用一个具体的动作或细节立住主角，不铺设定、不写心理小作文。
                    - 第二章（冲突）：把主角推进一个他必须做出选择、且两个选项都会失去什么的位置。
                      冲突要能看见——两个人面对面、一件事被撕开，而不是内心纠结。
                    - 第三章（悬念）：在章节末尾埋一个"答案在别处"的钩子：一个反常的细节、
                      一句没说透的话、一个刚出现就消失的人。钩子必须指向后面的情节，不能是装饰。

                    每章都留一个"再往下看一点"的缺口；不要在第一页解释世界观。
                    """,
                InputHint = "告诉 AI 你现在写到第几章、主角是谁、想让哪个悬念立起来。"
            },
            new()
            {
                IsBuiltIn = true,
                Presets = new List<string> { PromptPresets.IdNovel },   // 创作手法只属于小说方案
                Id = "suspense", Name = "悬念钩子",
                Description = "让读者放不下：信息差、倒计时与读者比主角更早知道。",
                AppliesTo = new List<string> { AiPrompts.Keys.Continue, AiPrompts.Keys.Polish, AiPrompts.Keys.Chat },
                TaskPrompt = """
                    【手法：悬念钩子】
                    给这段内容制造张力，手段按优先级：

                    - 信息差：让读者知道一件主角不知道的事（或者反过来），并让这个差距即将造成后果。
                    - 倒计时：明确或暗示"某件事即将发生"——期限、脚步声、逼近的截止点。
                    - 反常细节：写一个看似无关、但反复出现的细节，读者会记住并等待它引爆。
                    - 留白：关键处用动作和反应代替解释，让读者自己拼出那个让他不安的答案。

                    不要为了悬念而隐瞒读者该知道的信息；每一处悬念都要在情节推进中兑现或升级。
                    """,
                InputHint = "把当前卡住的段落贴给 AI，或说清你想要的悬念类型。"
            },
            new()
            {
                IsBuiltIn = true,
                Presets = new List<string> { PromptPresets.IdNovel },   // 创作手法只属于小说方案
                Id = "dialogue", Name = "对白强化",
                Description = "对话不是交换信息，是交锋：潜台词、打断与反应。",
                AppliesTo = new List<string> { AiPrompts.Keys.Polish, AiPrompts.Keys.Continue },
                TaskPrompt = """
                    【手法：对白强化】
                    把这段对话改成真正在"过招"的对白：

                    - 每人一句话只说一件事，话里有话；重要的意思藏在没说出口的部分。
                    - 打断与抢话：角色情绪上来时打断对方，比写"他激动地说"有力十倍。
                    - 反应写动作不写标签：不要"他冷冷地说"，要"他低头把玩着手里的杯子"。
                    - 对话之间要有留白与动作节拍，不要一段话追着一段话。
                    - 每个角色的口癖、用词习惯、话多话少，与其身份和心情一致。

                    保留原对话传达的所有信息，只重写"怎么说"。
                    """,
                InputHint = "贴上一段对白，或说清是谁和谁在谈、双方各自想从对方那里得到什么。"
            },
            new()
            {
                IsBuiltIn = true,
                Presets = new List<string> { PromptPresets.IdNovel },   // 创作手法只属于小说方案
                Id = "scene", Name = "场景渲染",
                Description = "环境是角色情绪的放大器：用感官细节让场景活着。",
                AppliesTo = new List<string> { AiPrompts.Keys.Polish, AiPrompts.Keys.Continue },
                TaskPrompt = """
                    【手法：场景渲染】
                    让场景承载情绪，而不是背景板：

                    - 五感挑选：给每个场景挑 2~3 个"对的感官"——雨夜要声音与温度，旧屋要气味与光线。
                      不要五个感官全上，那是清单。
                    - 光线与视角：光源从哪来、主角先看到什么，决定场景的压迫感或松弛感。
                    - 环境与人物互文：让一个环境细节呼应角色此刻的心情（或与之对抗），
                      别让环境只是"他走进一间房"。
                    - 克制：长场景渲染只用于关键节点；叙事节奏快的地方，一笔带过。

                    不改变情节与动作，只增强氛围的质感。
                    """,
                InputHint = "贴出当前场景段落，说明氛围基调（压迫/松弛/诡异/温暖）。"
            },
            new()
            {
                IsBuiltIn = true,
                Presets = new List<string> { PromptPresets.IdNovel },   // 创作手法只属于小说方案
                Id = "ending", Name = "结尾升华",
                Description = "把一章或全书收在读者的心上：回响、代价与新的开始。",
                AppliesTo = new List<string> { AiPrompts.Keys.Polish, AiPrompts.Keys.Continue },
                TaskPrompt = """
                    【手法：结尾升华】
                    给这段收尾一个让人合上书还想一会儿的力度：

                    - 回响：结尾呼应开头的某个细节、某句话——读者会突然意识到那个细节一直有重量。
                    - 代价：让结局体现"得到什么、失去什么"，而不是大团圆式的一笔带过。
                    - 新的开始：章节结尾埋一个"世界还在运转"的信号，暗示后面还有事。
                    - 克制收束：最后一句用短句、用具体画面，不总结、不点题、不说教。

                    如果是全书结尾，还要让主角与开头的自己形成可感知的对照。
                    """,
                InputHint = "贴出要收尾的段落，说明这是章节结尾还是全书结尾。"
            },
        };

        // ==================================================================
        // 加载 / 落盘
        // ==================================================================

        /// <summary>读取全部技能（内置在前、自定义追加），文件损坏时只返回内置。</summary>
        public static List<NovelSkill> Load(string configDir)
        {
            // ⚠ clone 时强制内置标记：内置定义漏写 IsBuiltIn=true 也不允许被误当自定义改删
            var result = BuiltIn.Select(s =>
            {
                var c = s.Clone();
                c.IsBuiltIn = true;
                return c;
            }).ToList();

            try
            {
                var path = Path.Combine(configDir, FileName);
                if (!File.Exists(path)) return result;

                var json = File.ReadAllText(path);
                var customs = JsonSerializer.Deserialize<List<NovelSkill>>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (customs == null) return result;

                foreach (var s in customs)
                {
                    if (s == null || string.IsNullOrWhiteSpace(s.Name)) continue;
                    s.IsBuiltIn = false; // 落盘的都是自定义
                    result.Add(s);
                }
            }
            catch
            {
                // 文件损坏时静默回退到内置
            }

            return result;
        }

        /// <summary>只把自定义技能写回 skills.json；内置不落盘。</summary>
        public static void Save(string configDir, IEnumerable<NovelSkill> all)
        {
            var customs = all.Where(s => !s.IsBuiltIn).ToList();
            var path = Path.Combine(configDir, FileName);
            if (customs.Count == 0)
            {
                if (File.Exists(path)) File.Delete(path);
                return;
            }

            Directory.CreateDirectory(configDir);
            var json = JsonSerializer.Serialize(customs, new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            });
            File.WriteAllText(path, json);
        }

        // ==================================================================
        // 增删改（对内置一律拒绝）
        // ==================================================================

        /// <summary>新增或覆盖一个技能。内置 id 拒绝。返回是否成功。</summary>
        public static bool AddOrUpdate(List<NovelSkill> all, NovelSkill skill)
        {
            if (skill == null || string.IsNullOrWhiteSpace(skill.Id)) return false;
            var existing = all.FirstOrDefault(s => s.Id == skill.Id);
            if (existing is { IsBuiltIn: true }) return false;

            if (existing != null)
            {
                existing.Name = skill.Name;
                existing.Description = skill.Description;
                existing.AppliesTo = new List<string>(skill.AppliesTo);
                existing.Presets = new List<string>(skill.Presets);
                existing.TaskPrompt = skill.TaskPrompt;
                existing.OutputContract = skill.OutputContract;
                existing.InputHint = skill.InputHint;
            }
            else
            {
                all.Add(skill.Clone());
            }
            return true;
        }

        /// <summary>删除技能。内置拒绝。返回是否删除。</summary>
        public static bool Delete(List<NovelSkill> all, string id)
        {
            var existing = all.FirstOrDefault(s => s.Id == id);
            if (existing == null || existing.IsBuiltIn) return false;
            return all.Remove(existing);
        }

        // ==================================================================
        // 导入 / 导出
        // ==================================================================

        /// <summary>解析一个技能 JSON（单技能或数组）。内置 id 拒绝导入。返回可导入的技能列表。</summary>
        public static List<NovelSkill> ParseImport(string json)
        {
            var result = new List<NovelSkill>();
            try
            {
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                if (json.TrimStart().StartsWith("["))
                {
                    var list = JsonSerializer.Deserialize<List<NovelSkill>>(json, options);
                    if (list != null) result.AddRange(list);
                }
                else
                {
                    var one = JsonSerializer.Deserialize<NovelSkill>(json, options);
                    if (one != null) result.Add(one);
                }
            }
            catch
            {
                return result;
            }

            return result
                .Where(s => s != null && !string.IsNullOrWhiteSpace(s.Name) && !string.IsNullOrWhiteSpace(s.TaskPrompt))
                .Where(s => !BuiltIn.Any(b => b.Id == s.Id)) // 内置 id 不导入
                .Select(s => { s.IsBuiltIn = false; return s; })
                .ToList();
        }

        /// <summary>导出单个技能为 JSON 字符串（不含内置标记）。</summary>
        public static string ExportJson(NovelSkill skill)
        {
            var copy = skill.Clone();
            copy.IsBuiltIn = false;
            return JsonSerializer.Serialize(copy, new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            });
        }

        // ==================================================================
        // 解析（任务提示词 / 输出契约的命中替换）
        // ==================================================================

        /// <summary>
        /// 按方案过滤可用技能：<see cref="NovelSkill.Presets"/> 为空 = 全方案可用；
        /// 否则仅当列表包含当前方案 Id 才可用。
        /// </summary>
        public static List<NovelSkill> AvailableFor(IEnumerable<NovelSkill> skills, string? presetId) =>
            skills.Where(s => s.Presets.Count == 0
                              || (presetId != null && s.Presets.Contains(presetId)))
                  .ToList();

        /// <summary>技能是否命中某任务键。</summary>
        public static bool AppliesTo(NovelSkill? skill, string taskKey)
        {
            if (skill == null || string.IsNullOrWhiteSpace(taskKey)) return false;
            return skill.AppliesTo.Contains(taskKey)
                && !string.IsNullOrWhiteSpace(skill.TaskPrompt);
        }

        /// <summary>命中返回技能的任务说明，否则回退 fallback。</summary>
        public static string ResolveTaskPrompt(NovelSkill? skill, string taskKey, string fallback) =>
            AppliesTo(skill, taskKey) ? skill!.TaskPrompt : fallback;

        /// <summary>命中且技能声明了输出契约时返回它，否则回退 fallback。</summary>
        public static string? ResolveContract(NovelSkill? skill, string taskKey, string? fallback)
        {
            if (skill != null && skill.AppliesTo.Contains(taskKey)
                && !string.IsNullOrWhiteSpace(skill.OutputContract))
                return skill.OutputContract;
            return fallback;
        }
    }
}
