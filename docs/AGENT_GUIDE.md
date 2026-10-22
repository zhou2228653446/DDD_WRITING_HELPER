# TdxClaw 写作助手 · Agent 手册

这份文档是写给 **agent**（接进本服务的 AI 客户端）看的：读一遍就知道该怎么做、按什么顺序做、
什么不能做。人类用户也可以拿它当说明书。

MCP 服务器在 `initialize` 时会返回下面 `BEGIN/END:instructions` 之间的内容作为 instructions；
全文则随时可以通过 **`guide` 工具**、或资源 **`tdx://guide`** 再读一次。

<!-- BEGIN:instructions -->
TdxClaw 写作助手 MCP 服务器：直接读写 .tdxproj 长篇写作项目（小说 / 学术论文 / 公文 / 通用写作），并与界面端共享同一套 AI 提示词方案、设定集、结构化人物卡、记忆与文献库。如果你不确定怎么做，先调用 guide 工具读完整手册（黄金规则 / 工具地图 / 任务剧本 / 长篇上下文治理 / 出错处置）。

接入后先做三步自检（缺一步就不要往下写）：
1. project_list 找书——不传目录会扫软件项目目录（通常就是用户的真实书库，不必问用户路径）；
2. project_open 打开目标书，再 project_status 拿进度与下一步建议；
3. ai_config_check 确认 AI 配置可用——它不通就说明 Key 没配 / 失效，先让用户去软件「AI 设置」修，别急着写。

推荐工作流：
1. 找书与打开：先用 project_list 查找 .tdxproj，调用 project_open(path) 打开，或用 project_create(name, directory/path, description, ...) 新建（支持直接传完整 .tdxproj 路径并顺带填初始设定）；打开后优先调 project_status 获取全书进度画像与下一步建议。
2. 读稿与检索：chapters_list 看目录，chapter_read 读单章，chapter_search 全书搜词查伏笔/矛盾，settings_get 读五项贯穿设定（含结构化人物卡汇总），settings_book_get 读 12 章设定集，characters_list / characters_stats 看人物卡与出场统计，memory_get 读 AI 写作记忆。
3. 写稿与管章：chapter_create(title, content, summary) 新建章，chapter_write(number, content, mode=replace/append) 写正文，chapter_rename 改标题，chapter_reorder 调序，chapter_delete 删章，chapter_summary_set(number, summary) 写章节梗概（每章写完建议顺手更新梗概，续写/审稿会自动注入全部前情梗概 + 紧邻上章末尾 800 字）。
4. 改设定与记忆：settings_set 支持单项 (field, text) 或批量同时传多个字段 (full_outline / chapter_outline / characters / background / writing_style / narrative_viewpoint / description)；settings_book_set(key, content, mode) 改设定集（12 章内置 key：world / power / geography / history / factions / customs / characters / relations / timeline / foreshadow / style / glossary）；character_upsert(name, role, age, gender, occupation, appearance, personality, background, abilities, relationships, notes, scope, status) 管理结构化人物卡；scope=core/arc/guest（常驻/本卷/客串）、status=active/inactive/retired/deceased（活跃/幕后/退场/阵亡），建卡时可顺手标；memory_set 沉淀作者长期偏好。
5. 调用本软件 AI（ai_write）：自动注入五项设定 + 结构化人物卡 + 设定集 + 章节梗概链 + 上章末尾 + 记忆 + 文献库。task 支持 continue（续写，默认 maxTokens=16384）/ polish（润色）/ expand（扩写，默认 16384）/ review（一致性审稿）/ name（起名）/ chat（自由问答，默认 16384）/ outline / chapter_outline / character / background / write_style / setting_book。默认 writeBack=false 只返回文本；显式传 writeBack=true 才落盘。报失败时先调 ai_config_check 诊断。
6. 安全与回滚：所有写操作均串行加锁、写前自动存快照（高频轻量设定/人物卡修改自动合并防刷屏）并检查文件冲突；改错可随时用 snapshot_list + snapshot_restore 回滚；完稿用 project_export(format, output/path) 导出 docx / pdf / txt。
7. 资源（tdx://）：打开项目后可通过 resources/list 与 resources/read 直接读取 tdx://chapter/{number}、tdx://settings/{key}、tdx://settings-book/{key}；tdx://guide 是本手册，任何状态下都能读。
<!-- END:instructions -->

---

## 0. 接入后先做这三步（自检）

这三步是"能不能自主干活"的分水岭，**顺序也别换**：

| 步骤 | 调用 | 通了说明 | 不通说明 |
|---|---|---|---|
| 1 找书 | `project_list`（可无参） | 书库位置可解析（默认就扫用户的项目目录，**不用问用户路径**） | 目录被移动过：请用户给一个目录，或显式传 `directory` |
| 2 开书 | `project_open(path)` → `project_status` | 会话拿到了这本书的全部读写权 | 路径写错 / 文件被占：读错误信息里的原文 |
| 3 查 AI | `ai_config_check` | 服务商 / 模型 / Key 都可用，可以调 `ai_write` 写文章了 | 让用户去软件「AI 设置」修 Key 或模型名——**这一步不通，后面所有生成都会失败** |

`ai_config_check` 的 `probe=true`（默认）会真的发一个最小请求探活。它永远不会输出 Key 本身。
第 3 步通过之后，你就具备完整能力了：读稿、检索、生成、写回、回滚、导出。按第 4 节的剧本走。

## 1. 这是什么

一个**长篇写作工具**的 MCP 服务。核心不是"再写一段文本"，而是**让几十万字还不崩结构**：

- 服务端替你把上下文管起来（先省再压），你不需要自己灌一堆设定；
- AI 失败**绝不写回正文**，写盘前自动存快照，写盘原子替换；
- 章节梗概成链、设定集当事实基准、人物卡结构化存储。

一本书 = 一个 `.tdxproj` 文件（JSON）。**它是唯一权威**，不要绕过本服务直接改它。

## 2. 黄金规则（九条，务必遵守）

1. **写之前先读**：改任何一章前，先 `chapter_read` 或 `chapter_search` 看过现文；不要凭记忆改稿。
2. **正文落盘要显式**：`ai_write` 默认只返回文本（`writeBack=false`）。**先把文本给用户看，用户认可再传 `writeBack=true`**——这是"AI 失败绝不写回正文"的约定，别默认落盘。
3. **覆盖是危险动作**：`chapter_write(mode=replace)`、`chapter_delete`、`snapshot_restore` 会改动已有内容；执行前先跟用户确认。删改前服务会自动存快照，但**没确认过就别做**。
4. **每写完一章，顺手写梗概**：`chapter_summary_set(number, summary)`。长篇靠梗概链保持跨章连贯，缺梗概是"后期崩结构"的第一原因。
5. **用户的长期偏好沉淀到记忆**：`memory_set`（`mode=append`）。用户说"别写死主角""对话别太长"这类纠正，不记下来下一轮就忘了。
6. **不要重复灌上下文**：`ai_write` 已自动带上设定/人物卡/设定集/梗概链/上章末尾/记忆/文献库。你在 instruction 里再抄一遍设定，等于双倍 token + 版本冲突。
7. **一个会话一本书**：`project_open` 之后所有读写都针对它；要换书就再 `project_open`。会话是**按调用方令牌**分的——每份 MCP 配置一个会话，所以两个 agent 可以各写各的书、互不切书；用同一份配置的两个客户端共用一个会话。
8. **长篇先看状态，再写正文**：项目启用了长篇结构时，续写前先 `state_board_get` + `volume_list`；写完关键剧情（升级、得宝、有人死亡）立刻 `state_board_update` / `character_scope_set`。**不更新状态 = 下一章 AI 还按旧境界写**。状态属于"改项目"，但它是低风险的事实记录，可以直接写；改正文仍然要先给用户看。
9. **不确定就先读手册**：`guide` 工具或 `tdx://guide`，含任务剧本与出错处置。

## 3. 工具地图（39 个，按用途分组）

**项目与界面（6）**
| 工具 | 用途 |
|---|---|
| `project_list` | 找 `.tdxproj`（不传目录就扫默认项目目录 / 当前目录 / 文档 / 桌面） |
| `project_open` | 打开某本书，之后所有操作都针对它 |
| `project_create` | 新建一本（自动补设定集 12 章骨架；同名文件存在时报错而不是覆盖） |
| `project_delete` | 删一整本书。★ `confirm` 必须原样回传文件名（`project_list` 里看到的那个）才真删——别凭记忆猜书名。不是硬删：整本书（含 .bak / 对话记忆 / 快照）移入书库旁的 `.trash/`，照 `_删除信息.txt` 可搬回；同名导出文件（.txt/.docx）不删。正打开着的书拒绝删 |
| `project_status` | 进度画像 + 「下一步该做什么」的建议；用户问"写到哪了"先用它 |
| `ui_typography` | 网页端正文排版：字体 / 字号 / 行距 / 字距。用户说「字太小」「行距太挤」「换成楷体」时用它，别让他自己去外观设置里翻。不传参数 = 查看当前值 + 可用字体预设；传 `preset="楷体"` / `fontSize=20` / `lineHeight=2.2` 等即改，`reset=true` 回默认。改完网页端会自动应用 |

**读（7）**
`chapters_list`（目录）/ `chapter_read`（单章，可 `maxChars` 截断）/ `chapter_search`（全书搜词，查伏笔与矛盾）/ `settings_get`（五项贯穿设定 + 视角 + 简介）/ `settings_book_get`（设定集清单或单章）/ `characters_list`（人物卡）/ `memory_get`（AI 写作记忆）

**写（11）**
`chapter_create` / `chapter_write`（replace\|append）/ `chapter_rename` / `chapter_reorder` / `chapter_delete` / `chapter_summary_set` / `settings_set`（单项或批量）/ `settings_book_set` / `character_upsert`（只改传入字段，不传的保持原样）/ `character_delete` / `memory_set`（append\|replace）

**回滚（2）** `snapshot_list` / `snapshot_restore`（恢复前又会自动存一份当前状态，所以这一步本身也可反悔）

**AI 与产出（4）**
`ai_write`（与界面点按钮完全同源：提示词方案 + 全部上下文 + 技能）/ `ai_config_check`（AI 报错先跑它；401=Key 失效或欠费，404=模型名不对；它永不输出 Key）/ `project_export`（docx / pdf / txt，论文版式传 `paperMode=true`）/ `guide`（本手册）

**统计（1）** `characters_stats`（出场章次 / 总次数 / 连续缺席几章；纯本地扫描，不花 token）

**长篇结构（8，项目未启用时返回明确提示，不是"工具不存在"）**

| 工具 | 用途 |
|---|---|
| `ultra_config` | `action=get` 看状态；`enable` 启用（需 `confirm="enable"`，可选 `autoVolumes=true` + `chaptersPerVolume`）；`update` 改开关或 `dynamicBudgetRatio`（0.08~0.20） |
| `volume_list` | 各卷的章节范围、字数、目标与回顾，以及多少章未分卷 |
| `volume_upsert` | 新建卷（不传 `volumeId`）或改卷（标题 / 目标 / 回顾）。卷回顾会代替"全卷正文"进续写上下文 |
| `volume_delete` | 删卷。`confirm` 必须原样回传卷名；★ **不删章节**，只把相关章转为「未分卷」 |
| `chapter_volume_set` | 批量挂卷：`numbers="1-40"` 或 `[1,2,3]`；不传 `volumeId` = 改为未分卷 |
| `state_board_get` | 主角当前境界 / 处境 / 目标 / 关键道具 / 生死状态。**续写前先看它** |
| `state_board_update` | 改状态（升级、换地图、得失道具）。只改传入字段 |
| `character_scope_set` | 批量设 `scope`（core/arc/guest）与 `status`（active/inactive/retired/deceased）。标成 deceased 后会进"严禁出场"名单 |

> 这三样（分卷 / 角色分级 / 状态面板）只在**启用了长篇结构**的项目里生效。
> 论文、公文、普通小说项目不要启用——它们的角色字段存的是"研究对象 / 事项要素"。
> 判断该不该启用：用户明确说"长篇""几百万字""分卷"，或 `project_status` 显示章数上百。

**资源（`resources/*`）**：`tdx://chapter/{n}`、`tdx://settings/{key}`、`tdx://settings-book/{key}`、`tdx://guide`。支持资源的客户端可以直接把整章挂进上下文，比"先 list 再 read"省一轮往返。

## 4. 五套剧本（照做即可）

**A. 从零立一本书**
`project_create(name, description)` → `ai_write(task=outline, writeBack=true)` 生成全文大纲
→ `ai_write(task=character, writeBack=true)` 生成人物 → 用 `character_upsert` 补齐人物卡
→ `ai_write(task=chapter_outline, writeBack=true)` 排章节大纲 → `chapter_create` 建章节
→ 逐章 `ai_write(task=continue, number=N, writeBack=true)` → 每章 `chapter_summary_set`。

**B. 给已有的书续写一章**
`project_status` 看进度 → `chapters_list` 找章 → `chapter_read` 读上章结尾 + `chapter_summary_set` 补前情
→ `ai_write(task=continue, number=N, instruction="这一章要发生什么…")`（**先不落盘**）
→ 把文本给用户确认 → `ai_write(..., writeBack=true)` 或对生成结果 `chapter_write(mode=append)`
→ `chapter_summary_set(N, "本章梗概")` → `project_export` 需要时导出。

**C. 一致性审稿**
`ai_write(task=review, number=N)` 拿到矛盾清单（它会把设定集放到待审正文旁边逐条核对）
→ 需要证据时用 `chapter_search(关键词)` 全书找 → 把清单给用户，逐项确认后再改。

**D. 设定改了，回填前文**
`settings_set(field=..., text=...)` 改设定 → `chapter_search` 找出所有受影响的位置
→ 逐处 `chapter_write` 修正（每处都先说清楚改什么）→ 重要改动前提醒用户可用 `snapshot_list` 回滚。

**E. 导出交稿**
`project_export(format=docx|pdf|txt, output="完整路径")`；论文模式加 `paperMode=true`（首页连排摘要 + 数字编号章节 + GB/T 7714 文献）。

## 5. 长篇上下文怎么管（为什么不要自己灌）

服务端已经按"装不下先省再压"的顺序处理：**当前章正文 + 紧邻上章末尾 800 字 + 全部前情梗概
+ 设定集 + 人物卡 + 记忆 + 文献库 + 提示词方案 + 技能**，超预算会先省后压。

- 你该做的是**保证这些上游数据是新的**：梗概写了没、设定改了没、人物卡补了没。
- 你不该做的是把正文/设定**重复塞进 instruction**——那会让 token 翻倍，而且你手上那份可能
  跟文件里的已经不同，模型会照着你给的旧版本写。
- 真要限定本次写法时，instruction 只写**这次的要求**："这一章从苏晚视角写，节奏慢一点"。

## 6. 出错怎么办

工具返回 `isError=true` 时，**先读文本里的提示**，它通常已经说明原因：

| 现象 | 原因与处置 |
|---|---|
| `还没有打开项目` | 先 `project_open`（或 `project_list` 找） |
| `没有第 N 章` | `chapters_list` 核对章号；注意 `chapter_reorder` 之后章号会整体重排 |
| `ai_write` 报 401 / 403 | Key 失效或欠费 → `ai_config_check` 确认；提示用户去「AI 设置」换 Key |
| `ai_write` 报 404 | 模型名不对 → `ai_config_check`，让用户改配置里的模型名 |
| `文件已被别处修改` / 冲突 | 界面端正在编辑同一本书：重新读一次（`project_status` / `chapter_read`）再重试，别硬写 |
| `ai_write` 超时/中断 | 生成过长：调小 `maxTokens`，或分两次（`expand` 续着扩写） |
| 「思考过程已耗尽本次输出上限」 | **用的是推理模型**（DeepSeek-R1 / v4 这类带思维链的）：它的思考过程也占 `max_tokens`，预算不够时就只思考不动笔。服务端会**自动加大预算重试一次**；仍失败就显式传更大的 `maxTokens`，或换普通模型做这批短输出任务（审稿 / 润色最容易撞上）。**这不是 Key 或网络问题** |
| 生成很久但一直 0 字 | 同上。推理模型在思考阶段正文长度为 0，属正常；界面会显示「正在思考… 已 N token」，N 在涨就没卡死 |

## 7. 明文禁区

- **不要直接编辑 `.tdxproj` 文件**（除非用户明确要求且你清楚后果）：绕过服务就没有快照、
  没有原子写、没有冲突检查，一次写坏就是一整本书。
- **不要在未经确认时删除章节 / 覆盖正文 / 恢复快照**——这些动作服务端都会自动存快照，
  但那是兜底，不是许可。
- **不要把 API Key 写进任何输出**（`ai_config_check` 本身已保证不回显 Key）。

## 8. 通过 HTTP 接入时（网页版服务）

stdio 之外的另一种接法：网页版把同一套工具挂在 `POST /mcp` 上。

- 同一台机器：`http://127.0.0.1:5280/mcp`，回环免登录，**连令牌都不用**。
- 局域网 / 外网：地址填公网域名（如 `http://ddd-lighthouse.ddns.net:5280/mcp`），
  并且**必须带登录令牌**（`Authorization: Bearer <令牌>`，不支持自定义请求头的客户端
  可用 `?access_token=<令牌>`）。令牌在网页「帮助 → 接入 Agent」里一键生成。
- 端点**只接受 POST**，`GET /mcp` 返回 405，没有 SSE 长连接。客户端里请选
  `http` / `streamable-http` 类型；坚持要 SSE 的客户端请改走 stdio。
- 令牌是服务进程内存态：**服务重启 / 重新发布后失效**，重新生成一次即可。
- 网页界面会实时跟随你的操作（对着同一本书），用户在旁边看得见你在改哪一章——
  因此**动作前说清楚要做什么**，比默默写盘体验好得多。
