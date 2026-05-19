using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using 编辑器.Services;

namespace 编辑器
{
    public partial class AppearanceSettingsWindow : Window
    {
        private readonly AppearanceManager _manager;
        private ThemePreset? _selectedPreset;

        public AppearanceConfig Result { get; private set; } = null!;

        public AppearanceSettingsWindow(Window owner, AppearanceManager manager, AppearanceConfig current)
        {
            InitializeComponent();
            Owner = owner;
            _manager = manager;

            // 加载预设列表
            PresetListBox.ItemsSource = AppearanceManager.BuiltInPresets;
            var match = AppearanceManager.BuiltInPresets.FirstOrDefault(p => p.Name == current.PresetName);
            if (match != null)
                PresetListBox.SelectedItem = match;
            else
                PresetListBox.SelectedIndex = 0;

            // 加载背景图片配置
            if (!string.IsNullOrEmpty(current.BackgroundImagePath) && File.Exists(current.BackgroundImagePath))
            {
                EnableBgCheckBox.IsChecked = true;
                BgPathTextBox.Text = current.BackgroundImagePath;
                RemoveBgBtn.IsEnabled = true;
            }
            else
            {
                EnableBgCheckBox.IsChecked = false;
                BgPathTextBox.Text = "";
                RemoveBgBtn.IsEnabled = false;
                BrowseBgBtn.IsEnabled = false;
            }
        }

        private void PresetListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            _selectedPreset = PresetListBox.SelectedItem as ThemePreset;
        }

        private void EnableBg_Changed(object sender, RoutedEventArgs e)
        {
            var enabled = EnableBgCheckBox.IsChecked == true;
            BrowseBgBtn.IsEnabled = enabled;
            if (!enabled)
            {
                BgPathTextBox.Text = "";
                RemoveBgBtn.IsEnabled = false;
            }
        }

        private void BrowseBg_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "图片文件 (*.jpg;*.jpeg;*.png;*.bmp)|*.jpg;*.jpeg;*.png;*.bmp",
                Title = "选择背景图片"
            };

            if (dialog.ShowDialog() == true)
            {
                BgPathTextBox.Text = dialog.FileName;
                RemoveBgBtn.IsEnabled = true;
            }
        }

        private void RemoveBg_Click(object sender, RoutedEventArgs e)
        {
            BgPathTextBox.Text = "";
            RemoveBgBtn.IsEnabled = false;
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            var preset = _selectedPreset ?? AppearanceManager.BuiltInPresets[0];

            Result = new AppearanceConfig
            {
                PresetName = preset.Name,
                BackgroundImagePath = EnableBgCheckBox.IsChecked == true && !string.IsNullOrWhiteSpace(BgPathTextBox.Text)
                    ? BgPathTextBox.Text
                    : null
            };

            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
