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
    public partial class AppearanceSettingsWindow : HandyControl.Controls.Window
    {
        private readonly AppearanceManager _manager;
        private readonly AppearanceConfig _originalConfig;
        private readonly Action<AppearanceConfig>? _onPreview;
        private readonly bool _initializing;
        private bool _suppressPreview;
        private bool _confirmed;

        private ColorScheme? _selectedScheme;
        private Material? _selectedMaterial;

        public AppearanceConfig Result { get; private set; } = null!;

        public AppearanceSettingsWindow(
            Window owner,
            AppearanceManager manager,
            AppearanceConfig current,
            Action<AppearanceConfig>? onPreview = null)
        {
            _initializing = true;
            InitializeComponent();
            Owner = owner;
            _manager = manager;
            _onPreview = onPreview;
            _originalConfig = new AppearanceConfig
            {
                PresetName = current.PresetName,
                MaterialName = current.MaterialName,
                MaterialIntensity = current.MaterialIntensity,
                BackgroundImagePath = current.BackgroundImagePath
            };
            Result = _originalConfig;

            // 配色列表
            PresetListBox.ItemsSource = AppearanceManager.ColorSchemes;
            _selectedScheme = AppearanceManager.ColorSchemes
                .FirstOrDefault(p => p.Name == current.PresetName);
            PresetListBox.SelectedItem = _selectedScheme ?? AppearanceManager.ColorSchemes[0];

            // 材质列表：预览块要用"当前配色"渲染，所以先定配色再建材质项
            var materialName = string.IsNullOrWhiteSpace(current.MaterialName)
                ? "paper"
                : current.MaterialName;
            RefreshMaterialItems();
            _selectedMaterial = AppearanceManager.ResolveMaterial(materialName);
            MaterialListBox.SelectedItem = MaterialListBox.Items
                .OfType<MaterialOption>()
                .FirstOrDefault(o => o.Material.Name == _selectedMaterial.Name)
                ?? MaterialListBox.Items[0];

            var intensity = current.MaterialIntensity <= 0 ? 1.0 : current.MaterialIntensity;
            IntensitySlider.Value = Math.Clamp(intensity, 0, 2);
            RefreshIntensityText();

            // 背景图片
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

            _initializing = false;
        }

        /// <summary>材质预览项：把材质 + 当前配色底色合成出一小块预览画刷。</summary>
        private sealed class MaterialOption
        {
            public Material Material { get; init; } = null!;
            public string DisplayName => Material.DisplayName;
            public string Description => Material.Description;
            public Brush Preview { get; init; } = Brushes.Transparent;
        }

        private AppearanceConfig BuildCurrentConfig()
        {
            var scheme = _selectedScheme ?? AppearanceManager.ColorSchemes[0];
            var material = _selectedMaterial ?? AppearanceManager.BuiltInMaterials[0];

            return new AppearanceConfig
            {
                PresetName = scheme.Name,
                MaterialName = material.Name,
                MaterialIntensity = Math.Round(IntensitySlider.Value, 2),
                BackgroundImagePath = EnableBgCheckBox.IsChecked == true && !string.IsNullOrWhiteSpace(BgPathTextBox.Text)
                    ? BgPathTextBox.Text
                    : null
            };
        }

        private void NotifyPreview()
        {
            if (_initializing || _suppressPreview) return;
            _onPreview?.Invoke(BuildCurrentConfig());
        }

        private void RefreshMaterialItems()
        {
            // 预览要"所见即所得"：用当前配色的面板底色 + 该配色的深浅色分档来渲染
            var scheme = _selectedScheme ?? AppearanceManager.ColorSchemes[0];
            MaterialListBox.ItemsSource = AppearanceManager.BuiltInMaterials
                .Select(m => new MaterialOption
                {
                    Material = m,
                    Preview = ThemeTokens.PreviewSurface(m, scheme)
                })
                .ToList();
        }

        private void PresetListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            _selectedScheme = PresetListBox.SelectedItem as ColorScheme;

            // 换配色后材质预览要重新渲染（预览块是用配色底色画的）
            var keep = MaterialListBox.SelectedIndex;
            _suppressPreview = true;
            try
            {
                RefreshMaterialItems();
                if (keep >= 0 && keep < MaterialListBox.Items.Count)
                    MaterialListBox.SelectedIndex = keep;
            }
            finally
            {
                _suppressPreview = false;
            }

            NotifyPreview();
        }

        private void MaterialListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (MaterialListBox.SelectedItem is MaterialOption opt)
            {
                _selectedMaterial = opt.Material;
                NotifyPreview();
            }
        }

        private void IntensitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            RefreshIntensityText();
            NotifyPreview();
        }

        private void RefreshIntensityText()
        {
            if (IntensityText == null) return;
            var v = IntensitySlider.Value;
            IntensityText.Text = v <= 0 ? "关" : v.ToString("0.0");
        }

        private void EnableBg_Changed(object sender, RoutedEventArgs e)
        {
            var enabled = EnableBgCheckBox.IsChecked == true;
            if (BrowseBgBtn != null)
                BrowseBgBtn.IsEnabled = enabled;
            if (!enabled)
            {
                if (BgPathTextBox != null) BgPathTextBox.Text = "";
                if (RemoveBgBtn != null) RemoveBgBtn.IsEnabled = false;
            }
            NotifyPreview();
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
                NotifyPreview();
            }
        }

        private void RemoveBg_Click(object sender, RoutedEventArgs e)
        {
            BgPathTextBox.Text = "";
            RemoveBgBtn.IsEnabled = false;
            NotifyPreview();
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            _confirmed = true;
            Result = BuildCurrentConfig();
            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            _confirmed = false;
            _onPreview?.Invoke(_originalConfig);
            DialogResult = false;
            Close();
        }

        protected override void OnClosed(EventArgs e)
        {
            // 无论是点击「取消」、按 Esc 还是点右上角 X 关闭，只要未点「确定」就还原修改前的外观
            if (!_confirmed)
            {
                _onPreview?.Invoke(_originalConfig);
            }
            base.OnClosed(e);
        }
    }
}
