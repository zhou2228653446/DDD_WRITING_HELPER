using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace 编辑器.Services
{
    /// <summary>某一家服务商上次拉到的模型清单。</summary>
    public sealed class ModelCacheEntry
    {
        /// <summary>拉取时刻（<c>yyyy-MM-dd HH:mm</c>，给界面显示用）。</summary>
        public string FetchedAt { get; set; } = "";

        /// <summary>
        /// 写入序号，**淘汰顺序只看它**。
        /// 不用 <see cref="FetchedAt"/> 排序：它只精确到分钟，同一分钟内写入的多家会变成
        /// "相同键值"，排序退化成依赖字典的枚举顺序 —— 而 `Dictionary` 在发生过删除之后
        /// 会复用空出来的槽位，枚举顺序不再等于插入顺序，于是淘汰谁变得不确定
        /// （实测会把刚写进去的那家淘汰掉）。
        /// </summary>
        public long Seq { get; set; }

        public List<string> Models { get; set; } = new();
    }

    /// <summary>
    /// 拉到的模型清单落盘缓存（<c>{配置目录}/model_cache.json</c>）。
    ///
    /// 为什么不写进 <c>api_profiles.json</c>：那个文件在设置页有「JSON 编辑」页，是给用户手改的；
    /// 往里塞几百个模型名会把真正需要看的 API 配置淹掉。
    ///
    /// 键是「服务商 Id @ 主机名」而不是配置方案名 —— 换个方案名不该让缓存失效，
    /// 同一个 Key 配两份方案也不该拉两次。
    /// </summary>
    public sealed class ModelCacheStore
    {
        /// <summary>单个服务商最多缓存多少条（OpenRouter 有 300+，没必要全留）。</summary>
        private const int MaxPerEntry = 400;

        /// <summary>最多保留多少家（超了淘汰最旧的）。</summary>
        private const int MaxEntries = 40;

        private readonly Dictionary<string, ModelCacheEntry> _map = new(StringComparer.Ordinal);
        private long _seq;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            // 中文不转义，文件是给人看的
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        public string FilePath { get; }

        public ModelCacheStore(string configDir)
        {
            FilePath = Path.Combine(configDir, "model_cache.json");
            Load();
        }

        public int Count => _map.Count;

        /// <summary>
        /// 缓存键：服务商 + 主机（含端口）。
        /// 带上端口是必要的：本机自建中转常常是 127.0.0.1:3000 / :8000 两台，
        /// 只按主机名算两个服务会共用一份清单。
        /// </summary>
        public static string KeyOf(string? providerId, string? apiUrl)
        {
            var provider = string.IsNullOrWhiteSpace(providerId) ? "custom" : providerId.Trim();

            var site = "";
            try
            {
                if (!string.IsNullOrWhiteSpace(apiUrl) && Uri.TryCreate(apiUrl.Trim(), UriKind.Absolute, out var uri))
                    site = uri.Authority;   // Authority 在端口是协议默认值时自动省略
            }
            catch { /* 地址不合法就当没有主机名 */ }

            return site.Length > 0 ? provider + "@" + site : provider;
        }

        /// <summary>取缓存的模型名（没有则返回 null）。</summary>
        public IReadOnlyList<string>? Get(string key)
        {
            if (!_map.TryGetValue(key, out var entry) || entry.Models.Count == 0) return null;
            return entry.Models;
        }

        /// <summary>缓存的拉取时刻，给界面显示"什么时候拉的"。</summary>
        public string? FetchedAt(string key) =>
            _map.TryGetValue(key, out var entry) ? entry.FetchedAt : null;

        /// <summary>写入并立即落盘。落盘失败不抛异常 —— 缓存丢了不该打断用户操作。</summary>
        public void Set(string key, IEnumerable<string> models)
        {
            var list = models
                .Where(m => !string.IsNullOrWhiteSpace(m))
                .Select(m => m.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaxPerEntry)
                .ToList();

            if (list.Count == 0) return;

            _map[key] = new ModelCacheEntry
            {
                FetchedAt = Timestamp(),
                Seq = ++_seq,
                Models = list,
            };
            Evict();
            Save();
        }

        public bool Remove(string key)
        {
            if (!_map.Remove(key)) return false;
            Save();
            return true;
        }

        private void Evict()
        {
            if (_map.Count <= MaxEntries) return;

            foreach (var key in _map
                         .OrderBy(kv => kv.Value.Seq)
                         .Take(_map.Count - MaxEntries)
                         .Select(kv => kv.Key)
                         .ToList())
                _map.Remove(key);
        }

        public static string Timestamp() => DateTime.Now.ToString("yyyy-MM-dd HH:mm");

        private void Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return;

                var json = File.ReadAllText(FilePath);
                if (string.IsNullOrWhiteSpace(json)) return;

                var parsed = JsonSerializer.Deserialize<Dictionary<string, ModelCacheEntry>>(json);
                if (parsed == null) return;

                foreach (var kv in parsed)
                {
                    if (string.IsNullOrWhiteSpace(kv.Key) || kv.Value?.Models == null) continue;
                    _map[kv.Key] = kv.Value;
                    if (kv.Value.Seq > _seq) _seq = kv.Value.Seq;
                }
            }
            catch
            {
                // 文件损坏就当没有缓存 —— 顶多重新拉一次，不能让设置页打不开
                _map.Clear();
            }
        }

        private void Save()
        {
            try
            {
                var dir = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                File.WriteAllText(FilePath, JsonSerializer.Serialize(_map, JsonOptions));
            }
            catch
            {
                // 忽略：缓存写不进去不影响主流程
            }
        }
    }
}
