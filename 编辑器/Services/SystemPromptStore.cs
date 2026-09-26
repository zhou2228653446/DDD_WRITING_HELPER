using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace 编辑器.Services
{
    /// <summary>一个自定义方案（用户「另存为副本」得到的）。</summary>
    public sealed class CustomPresetData
    {
        /// <summary>稳定 Id，形如 custom-3f9a1c07。</summary>
        public string Id { get; set; } = "";

        /// <summary>用户起的名字。</summary>
        public string Name { get; set; } = "";

        /// <summary>
        /// 依据的内置方案 Id。<b>这是"跟随内置改进"的关键</b>：自定义方案里没被改过的
        /// 条目，仍然按这个内置方案的当前文本解析，所以内置提示词将来改好了，
        /// 用户没动过的那几条会自动跟上。
        /// </summary>
        public string BasedOn { get; set; } = PromptPresets.IdNovel;

        /// <summary>该方案下与依据方案不同的条目（键 → 文本）。</summary>
        public Dictionary<string, string> Overrides { get; set; } = new(StringComparer.Ordinal);

        public CustomPresetData Clone() => new()
        {
            Id = Id,
            Name = Name,
            BasedOn = BasedOn,
            Overrides = new Dictionary<string, string>(Overrides, StringComparer.Ordinal)
        };
    }

    /// <summary>
    /// 整个存储的内存快照。设置页打开时先 <see cref="SystemPromptStore.Capture"/>，
    /// 用户点「取消」时再 <see cref="SystemPromptStore.Restore"/> —— 这样对话框里
    /// 对方案结构（新建 / 重命名 / 删除）和文本的所有编辑都能整批丢弃，不留副作用。
    /// </summary>
    public sealed class PromptStoreSnapshot
    {
        public string ActivePresetId { get; init; } = PromptPresets.IdNovel;
        public Dictionary<string, Dictionary<string, string>> Overrides { get; init; } = new(StringComparer.Ordinal);
        public List<CustomPresetData> CustomPresets { get; init; } = new();
    }

    /// <summary>
    /// 系统提示词的**方案 + 用户覆写**层。
    ///
    /// 存储位置：{配置目录}/system_prompts.json（默认 %AppData%\TdxClaw\）。
    ///
    /// ── 结构（Version 2）──
    ///   · <see cref="ActivePresetId"/>：当前生效的方案
    ///   · Presets：{ 方案Id → { 键 → 覆写文本 } }，内置方案与自定义方案共用这张表
    ///   · CustomPresets：自定义方案的元数据（名字 + 依据哪个内置方案）
    ///
    /// ── 为什么只存差异 ──
    /// 每个方案只保存「与它依据的内置方案不同」的条目，不把 13 段提示词全量拷一份。
    /// 好处有两个：
    ///   1. 内置提示词将来改进时，用户没动过的条目会自动跟着更新（全量拷贝会把旧文本
    ///      永久钉死，等于每次升级都丢失）；
    ///   2. 用户想看看自己改过什么，打开文件一目了然。
    ///
    /// ── 向后兼容 ──
    /// Version 1 的文件只有一个扁平的 Overrides（那时只有一套小说提示词），
    /// 读到时自动迁移为"小说创作方案的覆写"，老用户的自定义一条不丢。
    /// </summary>
    public sealed class SystemPromptStore
    {
        public const string FileName = "system_prompts.json";
        private const int CurrentVersion = 2;

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            // 不转义中文——这个文件是打算让人直接打开看的
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            // 不写 null 字段：v1 的 Overrides 在 v2 里恒为 null，
            // 留着会在文件里出现一行 "Overrides": null，让人以为还有旧字段在生效
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        private sealed class Payload
        {
            public int Version { get; set; } = CurrentVersion;
            public string ActivePresetId { get; set; } = PromptPresets.IdNovel;

            /// <summary>方案 Id → （键 → 覆写文本）。</summary>
            public Dictionary<string, Dictionary<string, string>> Presets { get; set; } = new();

            public List<CustomPresetData> CustomPresets { get; set; } = new();

            /// <summary>
            /// Version 1 的旧字段：那时只有一套小说提示词，覆写是扁平的。
            /// 只用于读取迁移，写回时不再产生。
            /// </summary>
            public Dictionary<string, string>? Overrides { get; set; }
        }

        private readonly string _filePath;
        private readonly Dictionary<string, Dictionary<string, string>> _overrides = new(StringComparer.Ordinal);
        private readonly List<CustomPresetData> _customPresets = new();

        /// <summary>覆写文件路径（{配置目录}/system_prompts.json）。</summary>
        public string FilePath => _filePath;

        /// <summary>当前生效的方案 Id。设置页切换方案就是改它。</summary>
        public string ActivePresetId { get; set; } = PromptPresets.IdNovel;

        /// <summary>全部方案下被自定义的条目总数。0 表示全部使用内置默认。</summary>
        public int OverrideCount => _overrides.Values.Sum(d => d.Count);

        /// <summary>自定义方案（顺序即创建顺序）。</summary>
        public IReadOnlyList<CustomPresetData> CustomPresets => _customPresets;

        public SystemPromptStore(string configDir)
        {
            _filePath = Path.Combine(configDir, FileName);
        }

        // ------------------------------------------------------------------
        // 读写
        // ------------------------------------------------------------------

        /// <summary>
        /// 从磁盘加载。文件不存在 / 损坏时静默退化为"全部使用内置默认"——
        /// 提示词配置坏掉不该导致软件起不来。
        /// </summary>
        public void Load()
        {
            _overrides.Clear();
            _customPresets.Clear();
            ActivePresetId = PromptPresets.IdNovel;

            if (!File.Exists(_filePath)) return;

            try
            {
                var json = File.ReadAllText(_filePath);
                if (string.IsNullOrWhiteSpace(json)) return;

                var payload = JsonSerializer.Deserialize<Payload>(json, _jsonOptions);
                if (payload == null) return;

                ActivePresetId = payload.ActivePresetId ?? PromptPresets.IdNovel;

                foreach (var (presetId, map) in payload.Presets ?? new())
                {
                    if (string.IsNullOrWhiteSpace(presetId) || map == null) continue;
                    foreach (var (key, value) in map)
                    {
                        if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
                            MapFor(presetId)[key] = value;
                    }
                }

                // Version 1 迁移：扁平覆写等价于小说创作方案的覆写
                if (payload.Overrides != null)
                {
                    foreach (var (key, value) in payload.Overrides)
                    {
                        if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
                            MapFor(PromptPresets.IdNovel)[key] = value;
                    }
                }

                foreach (var custom in payload.CustomPresets ?? new())
                {
                    if (custom == null || string.IsNullOrWhiteSpace(custom.Id)) continue;
                    if (_customPresets.Any(c => c.Id == custom.Id)) continue;   // 去重，坏文件不该让 UI 崩

                    var normalized = custom.Clone();
                    normalized.Overrides = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var (key, value) in custom.Overrides ?? new())
                        if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
                            normalized.Overrides[key] = value;

                    if (PromptPresets.Find(normalized.BasedOn) == null)
                        normalized.BasedOn = PromptPresets.IdGeneral;   // 依据方案不存在 → 落到通用写作

                    if (string.IsNullOrWhiteSpace(normalized.Name))
                        normalized.Name = "未命名方案";

                    _customPresets.Add(normalized);
                }

                // 生效方案必须真实存在，否则回到出厂默认
                if (!Exists(ActivePresetId)) ActivePresetId = PromptPresets.IdNovel;
            }
            catch (Exception ex)
            {
                // 记下来但不弹窗、不中断启动
                System.Diagnostics.Debug.WriteLine($"[SystemPromptStore] 读取失败，改用内置默认：{ex.Message}");
                _overrides.Clear();
                _customPresets.Clear();
                ActivePresetId = PromptPresets.IdNovel;
            }
        }

        /// <summary>写回磁盘。失败会抛出，由调用方决定怎么提示用户。</summary>
        public void Save()
        {
            var presets = _overrides
                .Where(kv => kv.Value.Count > 0)
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .ToDictionary(
                    kv => kv.Key,
                    kv => kv.Value.OrderBy(o => o.Key, StringComparer.Ordinal)
                                  .ToDictionary(o => o.Key, o => o.Value));

            var payload = new Payload
            {
                Version = CurrentVersion,
                ActivePresetId = ActivePresetId,
                Presets = presets,
                CustomPresets = _customPresets.Select(c => c.Clone()).ToList(),
                Overrides = null   // 不再写旧字段
            };

            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            File.WriteAllText(_filePath, JsonSerializer.Serialize(payload, _jsonOptions));
        }

        // ------------------------------------------------------------------
        // 方案查询
        // ------------------------------------------------------------------

        private Dictionary<string, string> MapFor(string presetId)
        {
            if (!_overrides.TryGetValue(presetId, out var map))
            {
                map = new Dictionary<string, string>(StringComparer.Ordinal);
                _overrides[presetId] = map;
            }
            return map;
        }

        /// <summary>方案是否存在（内置或自定义）。</summary>
        public bool Exists(string? presetId) =>
            PromptPresets.Find(presetId) != null || IsCustom(presetId);

        public bool IsCustom(string? presetId) =>
            !string.IsNullOrEmpty(presetId) && _customPresets.Any(c => c.Id == presetId);

        /// <summary>自定义方案的名字；内置方案返回 null。</summary>
        public string? CustomName(string? presetId) =>
            _customPresets.FirstOrDefault(c => c.Id == presetId)?.Name;

        /// <summary>方案显示名。未知方案返回 null。</summary>
        public string? PresetName(string? presetId)
        {
            var builtin = PromptPresets.Find(presetId);
            if (builtin != null) return builtin.Name;
            return CustomName(presetId);
        }

        /// <summary>
        /// 把任意方案 Id 解析成它依据的**内置**方案 Id。
        /// 内置方案返回自身；自定义方案返回它的 BasedOn；未知返回小说创作。
        /// </summary>
        public string ResolveBasePreset(string? presetId)
        {
            if (PromptPresets.Find(presetId) is { } builtin) return builtin.Id;

            var custom = _customPresets.FirstOrDefault(c => c.Id == presetId);
            if (custom != null && PromptPresets.Find(custom.BasedOn) is { } b) return b.Id;

            return PromptPresets.IdNovel;
        }

        // ------------------------------------------------------------------
        // 文本查询
        // ------------------------------------------------------------------

        /// <summary>取某方案下某条目的生效值：有覆写用覆写，否则用该方案的内置默认。</summary>
        public string Get(string presetId, string key, string fallback) =>
            _overrides.TryGetValue(presetId, out var map)
            && map.TryGetValue(key, out var custom)
            && !string.IsNullOrWhiteSpace(custom)
                ? custom
                : fallback;

        /// <summary>当前生效方案下某条目的生效值。</summary>
        public string GetActive(string key, string fallback) => Get(ActivePresetId, key, fallback);

        public bool IsOverridden(string presetId, string key) =>
            _overrides.TryGetValue(presetId, out var map)
            && map.TryGetValue(key, out var custom)
            && !string.IsNullOrWhiteSpace(custom);

        /// <summary>某方案下被自定义的条目数。</summary>
        public int OverrideCountOf(string presetId) =>
            _overrides.TryGetValue(presetId, out var map) ? map.Count : 0;

        /// <summary>
        /// 只取覆写值；未自定义返回 null。
        /// （自定义方案导出时要用它区分"跟着依据方案走"和"这条被改过"。）
        /// </summary>
        public string? GetOverride(string presetId, string key) =>
            _overrides.TryGetValue(presetId, out var map) && map.TryGetValue(key, out var custom)
                ? custom
                : null;

        /// <summary>批量取出某方案的覆写条目（键 → 文本）。</summary>
        public Dictionary<string, string> SnapshotOverrides(string presetId) =>
            _overrides.TryGetValue(presetId, out var map)
                ? new Dictionary<string, string>(map, StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal);

        // ------------------------------------------------------------------
        // 文本修改
        // ------------------------------------------------------------------

        /// <summary>
        /// 写入一条覆写。文本为空、或与内置默认实质相同时，视为"恢复默认"，删除该条覆写
        /// （避免用户在编辑框里点了半天又把内容改回原样，文件里却留一条无意义的记录）。
        /// </summary>
        /// <param name="presetId">作用于哪个方案</param>
        /// <param name="key">条目键</param>
        /// <param name="value">编辑框里的当前文本</param>
        /// <param name="fallback">该条目在**这个方案**下的内置默认值</param>
        public void Set(string presetId, string key, string? value, string fallback)
        {
            if (string.IsNullOrWhiteSpace(value) || SameAs(value, fallback))
            {
                if (_overrides.TryGetValue(presetId, out var map)) map.Remove(key);
            }
            else
            {
                MapFor(presetId)[key] = value.Replace("\r\n", "\n");
            }
        }

        /// <summary>恢复单条默认。返回是否确实删掉了一条覆写。</summary>
        public bool Reset(string presetId, string key) =>
            _overrides.TryGetValue(presetId, out var map) && map.Remove(key);

        /// <summary>把某个方案全部恢复默认（内存层；要落盘还需调用 <see cref="Save"/>）。</summary>
        public void ResetAll(string presetId) => _overrides.Remove(presetId);

        // ------------------------------------------------------------------
        // 方案管理
        // ------------------------------------------------------------------

        /// <summary>新建一个自定义方案，返回新方案的 Id。</summary>
        /// <param name="basedOnId">依据的内置方案</param>
        /// <param name="name">显示名</param>
        public string CreateCustom(string basedOnId, string name)
        {
            if (PromptPresets.Find(basedOnId) == null) basedOnId = PromptPresets.IdGeneral;

            string id;
            do { id = "custom-" + Guid.NewGuid().ToString("N")[..8]; }
            while (Exists(id));

            _customPresets.Add(new CustomPresetData
            {
                Id = id,
                Name = string.IsNullOrWhiteSpace(name) ? "未命名方案" : name.Trim(),
                BasedOn = basedOnId
            });
            return id;
        }

        public bool RenameCustom(string presetId, string name)
        {
            var custom = _customPresets.FirstOrDefault(c => c.Id == presetId);
            if (custom == null || string.IsNullOrWhiteSpace(name)) return false;

            custom.Name = name.Trim();
            return true;
        }

        /// <summary>
        /// 删除自定义方案。若删的正是当前生效方案，自动切回它依据的内置方案
        /// ——否则会留下一个指向不存在方案的 ActivePresetId。
        /// </summary>
        public bool DeleteCustom(string presetId)
        {
            var custom = _customPresets.FirstOrDefault(c => c.Id == presetId);
            if (custom == null) return false;

            var basedOn = ResolveBasePreset(presetId);
            _customPresets.Remove(custom);
            _overrides.Remove(presetId);

            if (string.Equals(ActivePresetId, presetId, StringComparison.Ordinal))
                ActivePresetId = basedOn;

            return true;
        }

        /// <summary>名字是否已被别的方案占用（内置名也占位，避免重名分不清）。</summary>
        public bool IsNameTaken(string name, string? exceptId = null)
        {
            name = name.Trim();
            foreach (var d in PromptPresets.All)
                if (string.Equals(d.Name, name, StringComparison.Ordinal)) return true;

            foreach (var c in _customPresets)
                if (!string.Equals(c.Id, exceptId, StringComparison.Ordinal)
                    && string.Equals(c.Name, name, StringComparison.Ordinal))
                    return true;

            return false;
        }

        // ------------------------------------------------------------------
        // 整批快照 / 回滚（设置页「取消」用）
        // ------------------------------------------------------------------

        public PromptStoreSnapshot Capture()
        {
            var overrides = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
            foreach (var (presetId, map) in _overrides)
                overrides[presetId] = new Dictionary<string, string>(map, StringComparer.Ordinal);

            return new PromptStoreSnapshot
            {
                ActivePresetId = ActivePresetId,
                Overrides = overrides,
                CustomPresets = _customPresets.Select(c => c.Clone()).ToList()
            };
        }

        public void Restore(PromptStoreSnapshot snapshot)
        {
            _overrides.Clear();
            foreach (var (presetId, map) in snapshot.Overrides)
                _overrides[presetId] = new Dictionary<string, string>(map, StringComparer.Ordinal);

            _customPresets.Clear();
            foreach (var c in snapshot.CustomPresets) _customPresets.Add(c.Clone());

            ActivePresetId = snapshot.ActivePresetId;
        }

        // ------------------------------------------------------------------
        // 比较
        // ------------------------------------------------------------------

        /// <summary>
        /// 比较两段文本是否"实质相同"：忽略换行符差异与首尾空白。
        /// 编辑器里全选重贴一遍，不该被当成一次自定义。
        /// </summary>
        public static bool SameAs(string a, string b) => Normalize(a) == Normalize(b);

        private static string Normalize(string s) =>
            s.Replace("\r\n", "\n").Replace("\r", "\n").Trim();
    }
}
