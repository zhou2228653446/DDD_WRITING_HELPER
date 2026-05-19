using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace 编辑器
{
    public class NovelProject
    {
        public string ProjectName { get; set; } = "新建小说";
        public string Author { get; set; } = "作者";
        public string Description { get; set; } = "";
        public DateTime CreatedDate { get; set; } = DateTime.Now;
        public DateTime ModifiedDate { get; set; } = DateTime.Now;
        public string FilePath { get; set; } = "";

        public WorldSetting? WorldSetting { get; set; }
        public List<Character> Characters { get; set; } = new List<Character>();
        public List<Chapter> Chapters { get; set; } = new List<Chapter>();

        // AI 创作上下文
        public string FullOutline { get; set; } = "";           // 全文大纲
        public string ChapterOutline { get; set; } = "";         // 章节大纲
        public string CharacterSettings { get; set; } = "";     // 主要人物设定
        public string BackgroundSettings { get; set; } = "";    // 主要背景设定
        public string WritingStyle { get; set; } = "";          // 文风设定

        public void Save()
        {
            if (string.IsNullOrEmpty(FilePath))
                throw new InvalidOperationException("项目路径未设置");

            ModifiedDate = DateTime.Now;
            var options = new JsonSerializerOptions { WriteIndented = true };
            string json = JsonSerializer.Serialize(this, options);
            File.WriteAllText(FilePath, json);
        }

        public static NovelProject Load(string path)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException("项目文件不存在", path);

            string json = File.ReadAllText(path);
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var project = JsonSerializer.Deserialize<NovelProject>(json, options)
                ?? throw new InvalidOperationException("项目文件格式错误");
            return project;
        }
    }

    public class WorldSetting
    {
        public string WorldName { get; set; } = "";
        public string TimePeriod { get; set; } = "";
        public string Location { get; set; } = "";
        public string Background { get; set; } = "";
        public string MagicSystem { get; set; } = "";
        public string TechnologyLevel { get; set; } = "";
    }

    public class Character
    {
        public string CharacterId { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = "";
        public int Age { get; set; }
        public string Gender { get; set; } = "";
        public string Occupation { get; set; } = "";
        public string Appearance { get; set; } = "";
        public string Personality { get; set; } = "";
        public string Background { get; set; } = "";
        public string Role { get; set; } = ""; // 主角、配角、反派等
    }

    public class Chapter : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        private void Notify([CallerMemberName] string propertyName = "") =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        public string ChapterId { get; set; } = Guid.NewGuid().ToString();

        private string _title = "新建章节";
        public string Title
        {
            get => _title;
            set { if (_title != value) { _title = value; Notify(); } }
        }

        public int ChapterNumber { get; set; } = 1;

        private string _content = "";
        public string Content
        {
            get => _content;
            set { if (_content != value) { _content = value; Notify(); } }
        }

        public string Summary { get; set; } = "";
        public int WordCount => Content.Length;
        public DateTime CreatedDate { get; set; } = DateTime.Now;
        public DateTime ModifiedDate { get; set; } = DateTime.Now;
        public DateTime LastModified { get; set; } = DateTime.Now;
        public List<string> CharacterIds { get; set; } = new List<string>();
        public string SceneSetting { get; set; } = "";
    }
}