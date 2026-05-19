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
            var projectDir = Path.GetDirectoryName(Path.GetFullPath(projectFilePath))!;
            _snapshotsDir = Path.Combine(projectDir, ".snapshots");
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
