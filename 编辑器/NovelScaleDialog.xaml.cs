using System.Windows;
using System.Windows.Controls;

namespace 编辑器
{
    public partial class NovelScaleDialog : HandyControl.Controls.Window
    {
        /// <summary>选中的规模描述，null 表示跳过</summary>
        public string? ScaleDescription { get; private set; }

        public NovelScaleDialog()
        {
            InitializeComponent();
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            if (ScaleListBox.SelectedItem is ListBoxItem item)
            {
                var tag = item.Tag?.ToString();
                ScaleDescription = tag switch
                {
                    "short" => "短篇小说（1~5万字，5~15章，单线叙事）",
                    "medium" => "中篇小说（5~20万字，15~40章，双线叙事）",
                    "long" => "长篇小说（20~50万字，40~100章，多线叙事）",
                    "extralong" => "超长篇小说（50万字以上，100章以上，宏大世界观）",
                    _ => null
                };
            }
            DialogResult = true;
        }

        private void Skip_Click(object sender, RoutedEventArgs e)
        {
            ScaleDescription = null;
            DialogResult = false;
        }

        private void ScaleListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // 选中即高亮，无需额外处理
        }
    }
}
