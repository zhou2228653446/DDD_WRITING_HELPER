using System;
using System.Collections.Generic;
using System.IO;
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

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

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
                foreach (var f in Directory.GetFiles(legacy, "*.json"))
                    File.Move(f, Path.Combine(_snapshotsDir, Path.GetFileName(f)));
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
                var data = JsonSerializer.Deserialize<IndexFile>(json, _jsonOptions);
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

            var snapshotFile = Path.Combine(_snapshotsDir, $"{id}.json");
            var projectJson = JsonSerializer.Serialize(project, _jsonOptions);
            File.WriteAllText(snapshotFile, projectJson);

            index.Add(entry);

            while (index.Count > MaxSnapshots)
            {
                var oldest = index[0];
                var oldFile = Path.Combine(_snapshotsDir, $"{oldest.Id}.json");
                if (File.Exists(oldFile)) File.Delete(oldFile);
                index.RemoveAt(0);
            }

            ReIndex(index);
            SaveIndex(index);
        }

        public NovelProject? LoadSnapshot(SnapshotEntry entry)
        {
            var snapshotFile = Path.Combine(_snapshotsDir, $"{entry.Id}.json");
            if (!File.Exists(snapshotFile)) return null;

            try
            {
                var json = File.ReadAllText(snapshotFile);
                return JsonSerializer.Deserialize<NovelProject>(json, _jsonOptions);
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
                    var oldFile = Path.Combine(_snapshotsDir, $"{index[i].Id}.json");
                    var newFile = Path.Combine(_snapshotsDir, $"{newId}.json");
                    if (File.Exists(oldFile))
                        File.Move(oldFile, newFile);
                    index[i].Id = newId;
                }
            }
        }

        private void SaveIndex(List<SnapshotEntry> entries)
        {
            var indexPath = Path.Combine(_snapshotsDir, "index.json");
            var data = new IndexFile { Entries = entries };
            File.WriteAllText(indexPath, JsonSerializer.Serialize(data, _jsonOptions));
        }

        private class IndexFile
        {
            public List<SnapshotEntry> Entries { get; set; } = new();
        }
    }
}
