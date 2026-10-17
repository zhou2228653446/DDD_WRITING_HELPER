using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;

namespace 编辑器.Services
{
    public class SnapshotEntry
    {
        public string Id { get; set; } = "";
        public string Description { get; set; } = "";
        public DateTime Timestamp { get; set; } = DateTime.Now;

        [System.Text.Json.Serialization.JsonIgnore]
        public string DisplayText => $"[{Timestamp:HH:mm:ss}] {Description}";
    }

    public class ProjectSnapshotManager
    {
        private readonly string _projectFilePath;
        private readonly string _snapshotsDir;
        private const int MaxSnapshots = 50;

        // 新快照一律 gzip：一份 150 万字项目的全量 JSON 约 1.6MB（去掉中文转义之后），
        // 50 份就是 80MB；gzip 之后单份约 0.5MB，50 份不到 25MB。
        // 旧快照（{id}.json 明文）继续能读，不做一次性迁移——迁移要重写整个目录，
        // 风险大于收益（快照存在的意义就是"别丢东西"）。
        private const string GzExt = ".json.gz";
        private const string PlainExt = ".json";

        private static JsonSerializerOptions Json => NovelProject.FileJson;

        private string GzPath(string id) => Path.Combine(_snapshotsDir, id + GzExt);
        private string PlainPath(string id) => Path.Combine(_snapshotsDir, id + PlainExt);

        /// <summary>某个 id 实际落在哪个文件上：新格式优先，找不到返回 null。</summary>
        private string? FindSnapshotFile(string id)
        {
            var gz = GzPath(id);
            if (File.Exists(gz)) return gz;
            var plain = PlainPath(id);
            return File.Exists(plain) ? plain : null;
        }

        public ProjectSnapshotManager(string projectFilePath)
        {
            _projectFilePath = projectFilePath;
            var full = Path.GetFullPath(projectFilePath);
            var projectDir = Path.GetDirectoryName(full)!;

            // ★ 快照按**书名**分子目录，不按目录混在一起。
            //   以前所有 .tdxproj 共用一个 .snapshots/ —— 一个目录只放一本书时
            //   看不出区别，但网页版所有书稿都躺在同一个 books/ 目录下，那样
            //   「快照」列表里会混进别的书的快照，点「恢复」就是把**另一本书**
            //   的内容盖进当前这本。这已经不是"看着乱"，是能毁稿子的。
            _snapshotsDir = Path.Combine(projectDir, ".snapshots", Path.GetFileName(full));
            MigrateLegacySnapshots(projectDir);
        }

        /// <summary>
        /// 把旧版「目录级 .snapshots/」里的快照搬进本项目自己的子目录。
        ///
        /// 只有这个目录里确实只有一本书时才搬：有多本书时无法判断旧快照属于谁，
        /// 搬错等于把别人的稿子当成本书的快照摆出来让人恢复 —— 比丢掉旧快照
        /// 危险得多。判断不了就原样留着（用户还能自己去 .snapshots/ 里翻）。
        /// </summary>
        private void MigrateLegacySnapshots(string projectDir)
        {
            var legacy = Path.Combine(projectDir, ".snapshots");
            if (Directory.Exists(_snapshotsDir)) return;
            if (!File.Exists(Path.Combine(legacy, "index.json"))) return;
            try
            {
                if (Directory.GetFiles(projectDir, "*.tdxproj").Length > 1) return;
                Directory.CreateDirectory(_snapshotsDir);
                // 旧目录里可能混着明文 .json 与（别的版本写过的）.json.gz，两种都搬。
                // index.json 也在这个循环里——它本来就是快照索引，一起搬。
                foreach (var f in Directory.GetFiles(legacy))
                {
                    var name = Path.GetFileName(f);
                    if (!name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                        && !name.EndsWith(".json.gz", StringComparison.OrdinalIgnoreCase)) continue;
                    var target = Path.Combine(_snapshotsDir, name);
                    if (File.Exists(target)) continue;
                    File.Move(f, target);
                }
            }
            catch { /* 搬不动就当没有历史快照，不影响以后新拍的 */ }
        }

        public List<SnapshotEntry> LoadIndex()
        {
            var indexPath = Path.Combine(_snapshotsDir, "index.json");
            if (!File.Exists(indexPath))
                return new List<SnapshotEntry>();

            try
            {
                var json = File.ReadAllText(indexPath);
                var data = JsonSerializer.Deserialize<IndexFile>(json, Json);
                return data?.Entries ?? new List<SnapshotEntry>();
            }
            catch
            {
                return new List<SnapshotEntry>();
            }
        }

        public void SaveSnapshot(NovelProject project, string description)
        {
            Directory.CreateDirectory(_snapshotsDir);

            var index = LoadIndex();
            var id = (index.Count + 1).ToString("D3");
            var entry = new SnapshotEntry
            {
                Id = id,
                Description = description,
                Timestamp = DateTime.Now
            };

            var snapshotFile = GzPath(id);
            using (var fs = File.Create(snapshotFile))
            using (var gz = new GZipStream(fs, CompressionLevel.Optimal))
                JsonSerializer.Serialize(gz, project, Json);

            index.Add(entry);

            while (index.Count > MaxSnapshots)
            {
                var oldest = index[0];
                var oldFile = FindSnapshotFile(oldest.Id);
                if (oldFile != null) File.Delete(oldFile);
                index.RemoveAt(0);
            }

            ReIndex(index);
            SaveIndex(index);
        }

        public NovelProject? LoadSnapshot(SnapshotEntry entry)
        {
            var snapshotFile = FindSnapshotFile(entry.Id);
            if (snapshotFile == null) return null;

            try
            {
                using var fs = File.OpenRead(snapshotFile);
                // 按扩展名判断而不是魔数：这里我们自己写、自己读，扩展名就够；
                // 旧文件是 {id}.json，新文件是 {id}.json.gz。
                if (snapshotFile.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
                {
                    using var gz = new GZipStream(fs, CompressionMode.Decompress);
                    return JsonSerializer.Deserialize<NovelProject>(gz, Json);
                }

                using var reader = new StreamReader(fs);
                return JsonSerializer.Deserialize<NovelProject>(reader.ReadToEnd(), Json);
            }
            catch
            {
                return null;
            }
        }

        public void Clear()
        {
            if (Directory.Exists(_snapshotsDir))
            {
                Directory.Delete(_snapshotsDir, true);
            }
        }

        public bool HasSnapshots => Directory.Exists(_snapshotsDir) && LoadIndex().Count > 0;

        private void ReIndex(List<SnapshotEntry> index)
        {
            for (int i = 0; i < index.Count; i++)
            {
                var newId = (i + 1).ToString("D3");
                if (index[i].Id != newId)
                {
                    // 沿用原文件的扩展名：老快照是 .json，新快照是 .json.gz，
                    // 改名时不能把老文件改成 .gz（内容没压缩，读的时候会解压失败）。
                    var oldFile = FindSnapshotFile(index[i].Id);
                    if (oldFile != null)
                    {
                        var ext = oldFile.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? GzExt : PlainExt;
                        File.Move(oldFile, Path.Combine(_snapshotsDir, newId + ext), true);
                    }
                    index[i].Id = newId;
                }
            }
        }

        private void SaveIndex(List<SnapshotEntry> entries)
        {
            var indexPath = Path.Combine(_snapshotsDir, "index.json");
            var data = new IndexFile { Entries = entries };
            File.WriteAllText(indexPath, JsonSerializer.Serialize(data, Json));
        }

        private class IndexFile
        {
            public List<SnapshotEntry> Entries { get; set; } = new();
        }
    }
}
