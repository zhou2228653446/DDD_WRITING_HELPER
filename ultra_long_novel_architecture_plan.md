# 超长篇小说架构改造方案 v2

> **v2 修订说明**：v1 的诊断（角色全量注入、15 章窗口、缺分卷、状态不追踪、单文件膨胀）
> 已逐条对照代码核实，行号与行为全部属实。v2 保留这些判断，重写的是**改法**——
> 因为 v1 有三处会伤到别的文体、两处会丢数据、一处会废掉 MCP 的冲突检测。
> v1 原文留在 `ultra_long_novel_architecture_plan.v1.md`，被否掉的设计见附录 A。

---

## 0. 三条硬约束

这个仓库不是"小说软件"，是**多文体写作工具**：论文、公文、通用、小说共用同一批底层路径。

| 约束 | 含义 | 违反的后果 |
|---|---|---|
| **① 不能为小说牺牲别的文体** | 共享方法只许加"默认关闭"的参数，不许改语义 | 论文的「研究对象与关键要素」会被当角色裁掉 |
| **② 数据优先于结构** | 新增字段全局可空，行为由项目自己的能力开关决定 | 论文项目打开就多出一堆虚拟卷 |
| **③ 不拆单文件** | 正文留在 `.tdxproj` 里 | MCP 的写前冲突检测会失效，静默覆盖回来 |

第 ① 条不是空话，具体到这个仓库：

```
PromptPresets.cs:175   [LabelKeys.Characters] = "研究对象与关键要素"   ← 论文方案
PromptPresets.cs:351   [LabelKeys.Characters] = "事项要素"             ← 公文方案
NovelProject.cs:168    BuildEffectiveCharacterSettings()   ← 无参，所有文体共用
AiTools.cs:136         调用点，所有文体共用
```

同一个槽位，在小说里是"角色"，在论文里是"研究对象"，在公文里是"发文要素"。
**任何按"角色生命周期"过滤这个字段的改动，都会让论文和公文静默出错。**

---

## 1. 先量化，再动手

改造前先拿到基线，否则做完说不清收益。（下列为估算，实施第一步是测真值。）

| 指标 | 现状估算 | 怎么测 |
|---|---|---|
| 150 万字项目文件 | 约 10 MB | `(Get-Item x.tdxproj).Length` |
| 其中中文转义占比 | **实测 46%**（106 KB → 57 KB） | 搜 `\uXXXX` 出现次数 |
| 单次 `Save()` 耗时 | 300~900 ms | 临时 Stopwatch 包一层 |
| 50 个快照占用 | 约 500 MB | `.snapshots/` 目录大小 |
| 80 个角色卡的 token | 6K~12K | `TokenEstimator` 估算 |
| 15 章梗概 + 800 字尾巴 | 约 3K token | 同上 |

**已经确认的两个事实**（v1 没提，但直接决定方案选型）：

1. `NovelProject.Save()`（`:91`）和 `ProjectSnapshotManager`（`:25`）都用
   `new JsonSerializerOptions { WriteIndented = true }`，**没设 Encoder**。
   System.Text.Json 默认把非 ASCII 转成 `\uXXXX`——一个汉字占 6 字节。
   加一行 `Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping` 就能让文件**缩到约 1/5**。
2. `Save()` 写盘**只有一次**：写 tmp → 旧文件改名 .bak → tmp 改名就位。
   后两步是改目录项，不搬数据。v1 说"写盘两次"不准确，但这不改变"文件太大"的结论。

---

## 2. 设计原则

1. **能力挂在项目上，不挂在文体上。** 不用 `if (方案 == 小说)`。
   一个百万字的论文分"编"也是合理的，别把能力和文体焊死。
2. **默认关闭。** 新参数一律 `= false`，新对象一律 `= null`。
   所有现有调用点不改一行，行为一字不变。
3. **先复用，再新建。** 出场统计（`CharacterAppearanceService`）、摘要生成
   （`ContextSummarizer`）、事实基准（设定集 7 个事实章）都已经有了。
4. **可恢复。** 任何新结构都要能回答：旧快照恢复时它在哪？旧版本打开时它去哪了？

---

## 3. 能力容器（核心改动）

```csharp
// 编辑器/Models/NovelProject.cs
public UltraLongConfig? UltraLong { get; set; }   // null = 从未启用

public sealed class UltraLongConfig
{
    public int SchemaVersion { get; set; } = 1;

    /// <summary>分卷结构。false 时章节树仍是扁平两层。</summary>
    public bool EnableVolumes { get; set; } = true;

    /// <summary>角色生命周期（Core/Arc/Guest + 生死状态）参与上下文筛选。</summary>
    public bool EnableCharacterLifecycle { get; set; } = true;

    /// <summary>主角状态面板参与注入。</summary>
    public bool EnableStateBoard { get; set; } = true;

    public List<Volume> Volumes { get; set; } = new();

    /// <summary>视角主角的 CharacterId。状态面板只为这些人渲染。</summary>
    public List<string> ProtagonistIds { get; set; } = new();

    /// <summary>动态素材占总窗口的比例上限（0.08~0.20），默认 12%。</summary>
    public double DynamicBudgetRatio { get; set; } = 0.12;
}
```

为什么是"容器里放开关"，而不是"顶层加字段"：

- 论文 / 公文 / 普通小说项目的 JSON 里 `ultraLong` 是 `null`，**不会多出任何键**；
- 启用时一次性把需要的结构建出来，没用的能力可以单独关；
- 所有行为收口成一句 `if (project.UltraLong is { ... })`，不用在十几个调用点判断文体。

**启用方式只有两条**：新建项目时勾选，或 agent 调 `ultra_enable`（需 `confirm`）。
**绝不自动启用**——v1 里"旧项目自动按每 50 章建卷"必须删掉。

---

## 4. 阶段 0：存储瘦身（0.5 天，与文体完全无关）

先做这个，因为它零风险、当天见效，而且能直接砍掉 v1 的整个"阶段三"。

### 4.1 序列化不再转义中文

```csharp
// NovelProject.Save() 与 ProjectSnapshotManager 共用一份 options
internal static readonly JsonSerializerOptions FileJson = new()
{
    WriteIndented = true,
    PropertyNameCaseInsensitive = true,
    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
};
```

- 收益（实测，不是估的）：一个 106 KB 的真实项目去掉转义后是 **57 KB（54%）**；
  150 万字项目约 10 MB → **约 5.4 MB**；序列化耗时同比例下降。
  50 份快照从约 500 MB 降到约 270 MB（再叠 4.2 的 gzip 才到 110 MB）。
- 安全性：`UnsafeRelaxedJsonEscaping` 只是不转义 `<` `>` `&` 与中文。
  `.tdxproj` 只写本地文件、由 Web 端以 `application/json` 返回，不拼进 HTML，
  没有 XSS 面。**这一点要写进代码注释**，否则下一个人看到 "Unsafe" 会想改回去。
- 兼容性：旧文件（含 `\uXXXX`）照常能读，`System.Text.Json` 两种形式都认。

### 4.2 快照压缩

```csharp
// ProjectSnapshotManager：{id}.json → {id}.json.gz
using var gz = new GZipStream(File.Create(path), CompressionLevel.Optimal);
JsonSerializer.Serialize(gz, project, FileJson);
```

- **实测合计 4.3 倍**（同一份 106 KB 项目：Encoder 后 57 KB，gzip 后 24.5 KB）。
  150 万字的一份快照约 10 MB → 5.4 MB → **约 2.3 MB**，50 份约 115 MB。
- 索引仍用明文 JSON（`index.json` 只有几十条，需要人眼可读）。
- 兼容：读取时按魔数判断（`1F 8B`），老快照 `.json` 继续能读，不做一次性迁移——
  迁移会在第一次打开时重写全部快照，风险大于收益。

### 4.3 不做的事

**不拆正文文件**（v1 阶段三）。理由见附录 A 第 1 条，这是本方案最重要的一条否决。

验收：同一项目改前/改后各存一次，文件大小、耗时、内容往返一致（含引号、反斜杠、emoji、
`<script>` 这类字符）。

---

## 5. 阶段 1：数据模型（1.5 天）

### 5.1 Volume（分卷）

```csharp
public sealed class Volume
{
    public string VolumeId { get; set; } = Guid.NewGuid().ToString();
    public int Number { get; set; } = 1;
    public string Title { get; set; } = "第一卷";
    public string Goal { get; set; } = "";      // 本卷要达成的核心目标（进 AI 上下文）
    public string Summary { get; set; } = "";   // 本卷回顾（卷末生成，进 AI 上下文）
}
```

**唯一真相：`Chapter.VolumeId`。** `Volume` 里不存 `ChapterNumbers`——
两份清单必然漂移，而漂移时没人知道该信哪份。卷的章节范围一律现算：

```csharp
public IEnumerable<Chapter> ChaptersOfVolume(string volumeId)
    => Chapters.Where(c => c.VolumeId == volumeId).OrderBy(c => c.ChapterNumber);
```

**章号仍然是 1..N 连续**，`RenumberChapters()` 的语义不变。
分卷只是给章节贴一个分组标签，不改变任何定位方式——`/api/chapter/{n}`、
`chapter_read`、`tdx://chapter/3`、快照索引全部照旧。这一条必须在文档里写死，
否则实施者很容易把章号改成"卷内编号"，那会一次性打断 MCP、Web 与所有快照。

**删卷不删章**：`volume_delete` 只把相关章节的 `VolumeId` 清空，章节回到"未分卷"。

### 5.2 角色分级

```csharp
public enum CharacterScope { Core, Arc, Guest }
public enum CharacterStatus { Active, Inactive, Retired, Deceased }

// Character 追加
public CharacterScope Scope { get; set; } = CharacterScope.Arc;
public CharacterStatus Status { get; set; } = CharacterStatus.Active;
```

- 默认 `Arc` 而不是 `Core`：v1 写"旧角色默认 Core"，那等于一个都没筛掉，改造白做；
  也不能默认 `Guest`，否则正文里写了的人会从上下文里消失。
- `Guest` 的语义是"不进全局上下文、但保留记录"——**不是"随章销毁"**。
  v1 说"随章销毁"，那样审稿会把正文里合法出现的龙套判成"查无此人"。

### 5.3 主角状态：跟着人物走，不另开一张表

v1 的 `ProtagonistState` 是独立类，只能描述一个人。改成扩 `Character`：

```csharp
// Character 追加（仅当 UltraLong 启用且该角色在 ProtagonistIds 里才注入）
public string CurrentRealm { get; set; } = "";      // 境界/职级/阶段（论文里可以是"研究进展"）
public string CurrentSituation { get; set; } = "";  // 一句话当前处境
public string CurrentGoal { get; set; } = "";       // 当前目标（进上下文，压住跑偏）
public List<string> KeyItems { get; set; } = new(); // 关键道具/法宝/数据资产
```

好处：双主角、群像、多视角天然支持；状态和人物卡同源，不会出现
"人物卡说金丹、状态簿说筑基"。代价是 `Character` 多了 4 个字段——论文项目里这些字段
是空字符串，不序列化开关、不注入、不显示。

### 5.4 迁移与降级

- 旧项目：`UltraLong == null`，**什么都不建**。用户点"启用长篇结构"后：
  1. 询问"是否按现有章节自动切卷"（默认否）；
  2. 若选是，按每 50 章建一个卷，卷名"第一卷…"，可改；
  3. 所有角色 `Scope` 保持默认 `Arc`，`Status` 保持 `Active`。
- 降级：`SchemaVersion` 只写不验，旧版读不懂就会在下次保存时抹掉。
  **缓解措施**：启用长篇结构时强制打一次快照（`SaveSnapshot("启用长篇结构前")`），
  并在 UI 与 README 写明"回退旧版本前先导出"。

---

## 6. 阶段 2：上下文组装（2.5 天，最需要小心）

### 6.1 分层与缓存断点

这是 v1 最严重的技术错误：它把「主角当前状态」放进了 System 稳定前缀。
状态簿的核心特征就是**每章都在变**，它一变，整个 system 前缀的 prompt cache 全部失效，
长文场景下输入成本反而翻倍。

正确分层（`BuildSections` 的分段机制不变，只调整内容归属）：

```
┌─ System（缓存断点之前，跨请求稳定）──────────────────────────┐
│  · 预设 Base + 输出契约                                    │
│  · 全文大纲 / 章节大纲（作者维护，低频变化）                │
│  · 设定集（已确认事实）                                     │
│  · Core 角色的**静态**资料（外貌/性格/能力，不含当前状态）  │
│  · 文风 / 叙事视角 / AI 记忆                                │
├─ User（每章都变，不缓存）─────────────────────────────────┤
│  · 主角当前状态（境界/处境/目标/关键道具）    ← v1 放错了   │
│  · 前序卷摘要（近 3 卷详写，更早合成一段）                  │
│  · 本卷目标 + 本章登场角色（2~4 人）                        │
│  · 最近 5~8 章梗概                                         │
│  · 上一章末尾 800 字                                       │
└────────────────────────────────────────────────────────────┘
```

判据只有一条：**这一块会不会在两章之间变。会变的一律进 user。**

### 6.2 收口到一个新类

现在上下文由 `AiTools.cs`（MCP）与 `MainWindow.BuildSystemPrompt`（桌面）
各拼一遍，Web 端再拼第三遍。再加分卷/状态簿，三处必然漂移。先收口：

```csharp
// 编辑器/Services/ContextAssembler.cs（新建，三端共用）
public static class ContextAssembler
{
    /// <summary>稳定前缀。ultra 为 null 时逐字节走老路。</summary>
    public static string BuildStable(NovelProject p, string? memory, string stableFallback);

    /// <summary>动态素材。ultra 为 null 或未启用时返回空串。</summary>
    public static string BuildDynamic(NovelProject p, int chapterNumber, int windowTokens);

    /// <summary>角色筛选。filter=false 时逐字返回 BuildEffectiveCharacterSettings()。</summary>
    public static string BuildCharacterContext(NovelProject p, bool filter, int chapterNumber);
}
```

`BuildCharacterContext` 的兜底极其重要：

```csharp
if (!filter || p.UltraLong is not { EnableCharacterLifecycle: true })
    return p.BuildEffectiveCharacterSettings();   // 论文/公文的路径，一个字节都不变
```

### 6.3 角色筛选规则

```csharp
var chapter = p.Chapters.FirstOrDefault(c => c.ChapterNumber == chapterNumber);
var chapterIds = chapter?.CharacterIds ?? new();

// 必选：Core 且未死亡
var core = p.Characters.Where(c => c.Scope == Core && c.Status != Deceased);

// 本章：显式勾选 ∪ 最近 3 章出现过（Arc）
var recent = p.Chapters.Where(c => c.ChapterNumber < chapterNumber)
                       .TakeLast(3)
                       .SelectMany(c => c.CharacterIds)
                       .ToHashSet();
var arc = p.Characters.Where(c => c.Scope == Arc
                               && c.Status == Active
                               && (chapterIds.Contains(c.CharacterId)
                                   || recent.Contains(c.CharacterId)));

// 反向名单：只取最近 20 章内死亡的，不列全书死亡名单
var dead = p.Chapters.Where(c => c.ChapterNumber >= chapterNumber - 20)
                     .SelectMany(c => c.CharacterIds)
                     .Join(p.Characters.Where(c => c.Status == Deceased),
                           id => id, c => c.CharacterId, (_, c) => c.Name)
                     .Distinct();
```

两个细节：

- **`Chapter.CharacterIds` 现在全仓库零引用**，不能指望作者手工勾。首次启用时用
  `CharacterAppearanceService.Analyze()` 扫全书自动回填（它已经算得出"每个角色出现在
  哪些章"），之后由 `chapter_write` 顺手维护。作者不维护也能用。
- 死亡名单只列最近 20 章的：300 章下来死上百人，全列出来又是一块几千 token 的负担。
  更早的死亡由设定集「主要人物档案」承担。

### 6.4 预算按池分配，不写死字数

v1 给的是"卷总结 150 字 / 本卷 300 字 / 近期 500 字 / 上章 800 字"。
固定字数在短章和高潮章都不对。改成：

```
动态预算 = min(窗口 × DynamicBudgetRatio, 12K token)      // 默认 12%
  卷摘要   25%   近 3 卷详写，更早的合并成一句
  本章角色 30%   每人 80~150 字，按剩余预算截断
  近期梗概 30%   按章号倒序装满为止
  上章尾巴 15%   最多 800 字（这是语气衔接，多了没用）
```

`DynamicBudgetRatio` 放进 `UltraLongConfig`，用户可以在设置里调；默认 12% 已经能把
原来的 12K 角色卡压到 3K 左右。

---

## 7. 阶段 3：提示词预设（1 天）

新增 `ultra_long_novel`，**只影响选了这个方案的项目**：

- 13 个槽位必须全部补齐（`PromptPresets` 的键是固定的，缺哪个都会退回通用方案）：
  `Base` / `Task.Continue` / `Task.Polish` / `Task.Expand` / `Task.Review` /
  `Task.Name` / `Task.Chat` / `Task.Outline` / `Task.ChapterOutline` /
  `Task.Character` / `Task.Background` / `Task.WriteStyle` / `Task.SettingBook`。
  语义对不上的（如"起名"）就写成该领域最近的任务，这是本仓库已有的做法。
- 自己的 `Labels`（不能复用 `NovelLabels`，否则没选的方案也会受影响——
  Labels 是按方案取的，各写各的）。
- **不碰** 其它四套方案的任何文本。
- 前端下拉自动出现：`AiPrompts` 遍历 `PromptPresets.All` 生成列表，
  Web `/api/prompts` 同源。**不用改界面**（v1 说"前端暴露选项"，其实不需要）。
- **MCP 要加 `preset` 参数**：`ai_write` 现在只能用"当前生效方案"，
  agent 想按任务切一套就得改全局（会顺带改掉界面）。加一个可选参数，
  不传就是当前方案，语义不变。

预设文本要点（v1 的四段方向是对的，保留）：
人设锁死 / 状态优先 / 短句节奏 / 章尾钩子；审稿三条红线只写在**这个预设的
`Task.Review` 里**，不进共享的 review 逻辑（论文审稿要查的是引用与数据）。

---

## 8. 阶段 4：MCP 工具（1 天）

| 工具 | 说明 |
|---|---|
| `ultra_config` | 查询或启用长篇结构（`action=get\|enable`，enable 需 `confirm`） |
| `volume_list` | 卷列表 + 每卷章节范围 + 字数 |
| `volume_upsert` | 建卷 / 改卷（标题、目标、卷回顾） |
| `volume_delete` | 删卷（只解绑章节，不删章） |
| `chapter_volume_set` | 把章节挂到某卷（支持批量章号） |
| `state_board_get` | 读主角的 CurrentRealm / Situation / Goal / KeyItems |
| `state_board_update` | 改主角状态（升级、换地图、得失关键道具） |
| `character_scope_set` | 批量设 Scope / Status（比逐个 `character_upsert` 轻） |

工具数 31 → **39**。两条约束：

- 项目未启用时返回**明确错误**（"该项目未启用长篇结构，先调 `ultra_enable`"），
  不是"工具不存在"，否则 agent 会以为客户端版本旧。
- 工具描述第一句写"仅当项目启用长篇结构时可用"，避免 agent 在论文项目里误调。

---

## 9. 阶段 5：界面与导出（2 天）

| 位置 | 改动 | 未启用时的表现 |
|---|---|---|
| 桌面章节树 | 已是 `TreeView`（项目 → 章），有卷时插一层"卷"节点 | 完全不显示，仍是两层 |
| Web 章节列表 | 有卷时按卷分组，卷标题可折叠 | 平铺 |
| 卷管理 | 桌面右键菜单 + Web「更多」；改名 / 设目标 / 写回顾 / 调整归属 | 菜单项隐藏 |
| 新建项目 | 勾选"长篇增强结构（分卷 / 角色分级 / 状态面板）" | 不勾 = 老行为 |
| Word / PDF 导出 | 目录按卷分级（卷一、卷二…），有卷才分级 | 原来的两级目录 |
| AI 设置 | `DynamicBudgetRatio` 滑块（8%~20%） | 显示默认值，禁用 |

导出这条 v1 没写，但它是用户能直接感知的收益：几百万字的稿子导出成 Word，
没有卷级目录基本没法看。

---

## 10. 数据安全（必做项，不是可选项）

### 10.1 快照恢复会丢分卷

`ProjectSnapshotManager.LoadSnapshot()` 是整本反序列化后直接替换当前项目。
一份"启用长篇结构之前"的快照里没有 `ultraLong`，恢复后 `UltraLong` 变 `null`，
**分卷、状态、角色分级一起消失，且不报错**。

修法：新增合并式恢复，只在两端有差异时介入：

```csharp
public static NovelProject RestoreMerged(NovelProject snapshot, NovelProject current)
{
    // 快照没有结构、当前有 → 把当前的结构带过去，正文/设定仍来自快照
    if (snapshot.UltraLong is null && current.UltraLong is not null)
    {
        snapshot.UltraLong = current.UltraLong;
        foreach (var c in snapshot.Chapters)          // 章号连续，按章号映射
            c.VolumeId = current.Chapters
                .FirstOrDefault(x => x.ChapterNumber == c.ChapterNumber)?.VolumeId ?? "";
    }
    return snapshot;
}
```

恢复前弹一次确认，说明"会恢复正文与设定，保留分卷结构"。

### 10.2 其它三条

| 场景 | 处理 |
|---|---|
| 并发写（网页 + MCP） | 维持单文件 + `Ticks:Length` 指纹，**不拆文件**；结构变更也走同一次 `Save()`，自动被覆盖保护 |
| 删角色 | `character_delete` 同步清理 `CharacterIds` 里的失效 id，渲染时忽略找不到的 id |
| 删卷 | 只清 `Chapter.VolumeId`，不删章节、不重排章号 |
| 降级 | 启用时强制快照；文档写明回退前导出 |

---

## 11. 测试计划

单测（现有 171 项全部要保持绿）：

| 新增 | 断言 |
|---|---|
| 存储往返 | 含中文/引号/反斜杠/emoji/`<script>` 的项目，序列化→反序列化后逐字段相等 |
| 体积回归 | 构造 100 章项目，文件大小 < 原实现的一半（钉住 Encoder 不被改回去） |
| 快照压缩 | 写入读回一致；老的 `.json` 快照仍可读 |
| **多文体守护** | 论文项目（`Characters` 有内容、`UltraLong == null`）调用 `BuildCharacterContext(filter:true)`，输出与 `BuildEffectiveCharacterSettings()` **逐字节相等** |
| 卷边界 | 移动章节跨卷后章号仍 1..N 连续；删卷不删章 |
| 状态注入位置 | `BuildDynamic` 的输出**不出现在** `BuildStable` 里（防止有人又把状态簿塞回 System） |
| 恢复合并 | 旧快照恢复后 `Volumes` 仍在，章节归属不变 |
| MCP | 未启用项目调 `volume_list` 返回明确错误；启用后增删改查闭环 |

MCP 自检 76 → 约 88 项。

**回归一条硬指标**：同一个论文项目、同一套方案，改造前后的 system 提示词
**字节级一致**（导出到文件做 diff）。这条不过，说明已经污染了别的文体。

---

## 12. 路线图

| 阶段 | 内容 | 预估 | 风险 | 可独立上线 |
|---|---|---|---|---|
| 0 | Encoder + 快照 gzip | 0.5 天 | 低 | ✅ 立刻可发 |
| 1 | `UltraLongConfig` 容器 + 迁移 | 1 天 | 低 | ✅ |
| 2 | Volume / 角色分级 / 状态字段 | 1.5 天 | 中 | ✅ 有 UI 才有用 |
| 3 | `ContextAssembler` + 预算池 | 2.5 天 | **高** | 要跟 2 一起 |
| 4 | `ultra_long_novel` 预设 | 1 天 | 低 | ✅ |
| 5 | MCP 工具 + `preset` 参数 | 1 天 | 低 | ✅ |
| 6 | 界面 / 导出分卷 | 2 天 | 中 | ✅ |
| 7 | 测试与回归 | 1 天 | 低 | —— |

合计约 **10.5 人日**（估算，不含调试）。阶段 0 建议先单独做掉：当天就能把
主文件缩到 54%、快照缩到 23%，而且不碰任何行为。

**验收标准**：
1. 150 万字样本：`Save()` 耗时减半，文件 < 6 MB，50 份快照 < 130 MB；
2. 论文 / 公文项目的 system 提示词与改造前逐字节一致；
3. 角色卡占用从 6~12K token 降到 3K 以内；
4. MCP 自检与 171 项单测全绿，且新增守护测试覆盖上面 8 条。

---

## 附录 A：v1 被否掉的设计

| # | v1 的做法 | 为什么不行 |
|---|---|---|
| 1 | 阶段三把正文拆成 `_content/vol_1.dat` | MCP 冲突检测只认单个 `.tdxproj` 的 `Ticks:Length`（`NovelTools.cs:173`）。正文挪走后主文件长度不变，**网页改了稿、MCP 看不出来**，静默覆盖回来。而且 Encoder + 快照 gzip 实测已有 4.3 倍收益（单文件 5.4 MB），不必拿数据安全换这点空间 |
| 2 | 主角状态簿放进 System 稳定前缀 | 状态每章都变，会让缓存断点前的整段前缀失效。项目自己在 `AiTools.cs:155` 写过这个道理 |
| 3 | 旧角色默认 `Core`、旧项目每 50 章自动建卷 | 无条件对所有项目执行，论文项目打开就多出一堆虚拟卷和"核心角色"。能力必须显式启用 |
| 4 | `ProtagonistState` 独立类 | 只能描述一个主角；且与设定集「力量体系/主要人物档案」形成双真相。改成状态跟着 `Character` 走 |
| 5 | `Volume.ChapterNumbers` + `Chapter.VolumeId` 双写 | 两份清单必然漂移，只保留 `Chapter.VolumeId` |
| 6 | 死亡名单全量注入 | 300 章下来上百人，反向名单本身又变成几千 token |
| 7 | 固定字数（150/300/500/800） | 短章浪费、高潮章不够；改成预算池按比例分 |
| 8 | "角色卡占 System 绝大部分空间" | 12K / 200K 是 6%，真正的问题是注意力稀释，不是装不下 |

---

## 附录 B：明确不改的东西

- **章号 1..N 连续**，`RenumberChapters()` 语义不变，`/api/chapter/{n}`、`chapter_read`、`tdx://` 资源全部照旧。
- **单文件存储**，不拆块；冲突检测机制不动。
- **其它四套预设**（小说/论文/公文/通用）一字不改。
- **`BuildEffectiveCharacterSettings()` 无参重载保留**，新逻辑走新增参数。
- **`review` 的共享逻辑不动**，小说红线写进预设文本。
- **桌面端视觉**不动（分卷只是章节树多一层，不改配色/材质/布局）。

---

## 附录 C：代码位置索引

| 位置 | 现有行为 | 本方案怎么动 |
|---|---|---|
| `编辑器/Models/NovelProject.cs:91` | `Save()` 全量序列化，无 Encoder | 加 `Encoder`，options 提到静态字段 |
| `:168` | `BuildEffectiveCharacterSettings()` 无参全量 | 保留，新增 `BuildCharacterContext(filter, n)` 包一层 |
| `:230` | `BuildPriorChapterBrief(n, tail, 15, 800)` | 保留；超长篇走新的预算池版本 |
| `:343` | `Chapter.CharacterIds` 零引用 | 用 `CharacterAppearanceService` 自动回填，`chapter_write` 顺手维护 |
| `编辑器/Services/ProjectSnapshotManager.cs:23` | `MaxSnapshots = 50` | 不动，改存储格式为 gzip |
| `:117` | `LoadSnapshot` 整本替换 | 加 `RestoreMerged`，保住结构 |
| `编辑器.Mcp/AiTools.cs:136` | 所有文体共用角色注入 | 按 `UltraLong` 开关分流 |
| `:180` | 续写/审稿共用 15 章前情 | 超长篇替换为卷摘要 + 近期窗口 |
| `编辑器/Services/PromptPresets.cs:78` | `All` 遍历生成方案列表 | 追加一个 `Definition` 即可，前端自动出现 |
| `编辑器/Services/CharacterAppearanceService.cs:34` | 已有出场统计 | 直接复用作自动回填与"最近出现"判断 |
| `编辑器.Mcp/McpServer.cs:617` | `ai_write` 的 inputSchema 没有 `preset` 参数 | 加可选参数 |
| `编辑器.Web/Program.cs:1734` | `/api/prompts/active` 存在 | 不动，新方案自动可切 |
