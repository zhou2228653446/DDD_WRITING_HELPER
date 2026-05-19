using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace 编辑器.Services
{
    public class ApiProfileManager
    {
        private readonly string _filePath;
        private Dictionary<string, ApiConfig> _profiles = new();
        private string _activeProfileName = "";

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        public IReadOnlyDictionary<string, ApiConfig> Profiles => _profiles;
        public string ActiveProfileName => _activeProfileName;
        public ApiConfig? ActiveProfile => _activeProfileName != null && _profiles.TryGetValue(_activeProfileName, out var p) ? p : null;

        public event Action? ProfilesChanged;
        public event Action? ActiveProfileChanged;

        public ApiProfileManager(string filePath)
        {
            _filePath = filePath;
        }

        public void Load()
        {
            try
            {
                if (File.Exists(_filePath))
                {
                    var json = File.ReadAllText(_filePath);
                    var data = JsonSerializer.Deserialize<ProfileStore>(json, _jsonOptions);
                    if (data != null)
                    {
                        _profiles = data.Profiles ?? new Dictionary<string, ApiConfig>();
                        _activeProfileName = data.ActiveProfile ?? "";
                    }
                }
            }
            catch
            {
                _profiles = new Dictionary<string, ApiConfig>();
                _activeProfileName = "";
            }

            // 确保至少有一个默认配置
            if (_profiles.Count == 0)
            {
                _profiles["默认配置"] = new ApiConfig();
                _activeProfileName = "默认配置";
            }

            // 如果保存的活动配置不存在，选第一个
            if (!_profiles.ContainsKey(_activeProfileName))
            {
                _activeProfileName = _profiles.Keys.First();
            }
        }

        public void Save()
        {
            try
            {
                var dir = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                var data = new ProfileStore
                {
                    ActiveProfile = _activeProfileName,
                    Profiles = _profiles
                };
                File.WriteAllText(_filePath, JsonSerializer.Serialize(data, _jsonOptions));
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"保存配置方案失败: {ex.Message}", "错误");
            }
        }

        public void AddOrUpdate(string name, ApiConfig config)
        {
            _profiles[name] = config;
            Save();
            ProfilesChanged?.Invoke();
        }

        public bool Delete(string name)
        {
            if (!_profiles.ContainsKey(name) || _profiles.Count <= 1)
                return false;

            _profiles.Remove(name);

            if (_activeProfileName == name)
                _activeProfileName = _profiles.Keys.First();

            Save();
            ProfilesChanged?.Invoke();
            return true;
        }

        public bool Rename(string oldName, string newName)
        {
            if (!_profiles.ContainsKey(oldName) || _profiles.ContainsKey(newName))
                return false;

            var config = _profiles[oldName];
            _profiles.Remove(oldName);
            _profiles[newName] = config;

            if (_activeProfileName == oldName)
                _activeProfileName = newName;

            Save();
            ProfilesChanged?.Invoke();
            return true;
        }

        public void SetActive(string name)
        {
            if (_profiles.ContainsKey(name) && _activeProfileName != name)
            {
                _activeProfileName = name;
                Save();
                ActiveProfileChanged?.Invoke();
            }
        }

        public void Duplicate(string sourceName, string newName)
        {
            if (_profiles.TryGetValue(sourceName, out var config) && !_profiles.ContainsKey(newName))
            {
                _profiles[newName] = new ApiConfig
                {
                    Provider = config.Provider,
                    ApiUrl = config.ApiUrl,
                    Model = config.Model,
                    ApiKey = config.ApiKey
                };
                Save();
                ProfilesChanged?.Invoke();
            }
        }

        public string GetJsonPath() => _filePath;

        public void Reload()
        {
            Load();
            ProfilesChanged?.Invoke();
            ActiveProfileChanged?.Invoke();
        }

        public List<string> GetProfileNames()
        {
            return _profiles.Keys.ToList();
        }

        private class ProfileStore
        {
            public string ActiveProfile { get; set; } = "";
            public Dictionary<string, ApiConfig> Profiles { get; set; } = new();
        }
    }
}
