using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using 编辑器.Services;

namespace 编辑器
{
    public partial class ApiSettingsWindow : Window
    {
        private readonly ApiProfileManager _profileManager;
        private bool _isSwitchingProfile; // 防止切换时触发重复加载

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };

        public ApiSettingsWindow(Window owner, ApiProfileManager profileManager)
        {
            InitializeComponent();
            Owner = owner;
            _profileManager = profileManager;

            ProviderComboBox.ItemsSource = KnownProviders.All
                .Select(p => new ProviderOption(p.Name, p.DefaultUrl, p.DefaultModel))
                .ToList();

            RefreshProfileList();
            LoadProfileToUI(_profileManager.ActiveProfile);
        }

        // ---- Profile 管理 ----

        private void RefreshProfileList()
        {
            _isSwitchingProfile = true;
            var current = ProfileComboBox.SelectedItem as string;
            ProfileComboBox.ItemsSource = _profileManager.GetProfileNames();
            if (current != null && _profileManager.GetProfileNames().Contains(current))
                ProfileComboBox.SelectedItem = current;
            else
                ProfileComboBox.SelectedItem = _profileManager.ActiveProfileName;

            DeleteProfileBtn.IsEnabled = _profileManager.Profiles.Count > 1;
            _isSwitchingProfile = false;
        }

        private void LoadProfileToProfileName(string profileName)
        {
            if (profileName == null || !_profileManager.Profiles.TryGetValue(profileName, out var config))
                return;

            _isSwitchingProfile = true;
            ProfileComboBox.SelectedItem = profileName;
            _isSwitchingProfile = false;

            LoadProfileToUI(config);
        }

        private void LoadProfileToUI(ApiConfig? config)
        {
            if (config == null) return;

            _isSwitchingProfile = true;

            // 选中对应的 Provider
            var match = ProviderComboBox.Items.OfType<ProviderOption>()
                .FirstOrDefault(p => p.Name == config.Provider);
            ProviderComboBox.SelectedItem = match ?? ProviderComboBox.Items.OfType<ProviderOption>()
                .First(p => p.Name == KnownProviders.Custom);

            ApiUrlTextBox.Text = config.ApiUrl;
            ModelTextBox.Text = config.Model;
            ApiKeyPasswordBox.Password = config.ApiKey;

            _isSwitchingProfile = false;
        }

        private ApiConfig CurrentFormConfig => new()
        {
            Provider = (ProviderComboBox.SelectedItem as ProviderOption)?.Name ?? KnownProviders.OpenAI,
            ApiUrl = ApiUrlTextBox.Text.Trim(),
            Model = ModelTextBox.Text.Trim().ToLowerInvariant(),
            ApiKey = ApiKeyPasswordBox.Password,
        };

        private void ProfileComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_isSwitchingProfile) return;
            if (ProfileComboBox.SelectedItem is string name && _profileManager.Profiles.TryGetValue(name, out var config))
            {
                LoadProfileToUI(config);
            }
        }

        private void NewProfile_Click(object sender, RoutedEventArgs e)
        {
            var name = PromptForName("新建配置方案", "请输入方案名称：", "");
            if (string.IsNullOrWhiteSpace(name)) return;

            if (_profileManager.GetProfileNames().Contains(name))
            {
                MessageBox.Show("该名称已存在", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var baseProfile = ProfileComboBox.SelectedItem as string;
            if (baseProfile != null && _profileManager.Profiles.TryGetValue(baseProfile, out var baseConfig))
            {
                _profileManager.AddOrUpdate(name, new ApiConfig
                {
                    Provider = baseConfig.Provider,
                    ApiUrl = baseConfig.ApiUrl,
                    Model = baseConfig.Model,
                    ApiKey = baseConfig.ApiKey
                });
            }
            else
            {
                _profileManager.AddOrUpdate(name, CurrentFormConfig);
            }

            RefreshProfileList();
            ProfileComboBox.SelectedItem = name;
        }

        private void SaveProfile_Click(object sender, RoutedEventArgs e)
        {
            var currentName = ProfileComboBox.SelectedItem as string;
            if (string.IsNullOrEmpty(currentName))
            {
                MessageBox.Show("请先选择要保存的配置方案", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _profileManager.AddOrUpdate(currentName, CurrentFormConfig);
            MessageBox.Show($"配置方案「{currentName}」已保存", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void DeleteProfile_Click(object sender, RoutedEventArgs e)
        {
            var currentName = ProfileComboBox.SelectedItem as string;
            if (string.IsNullOrEmpty(currentName)) return;

            if (_profileManager.Profiles.Count <= 1)
            {
                MessageBox.Show("至少保留一个配置方案", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var result = MessageBox.Show($"确定删除配置方案「{currentName}」吗？", "确认删除",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;

            _profileManager.Delete(currentName);
            RefreshProfileList();
        }

        // ---- Provider 选择联动 ----

        private void ProviderComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_isSwitchingProfile) return;
            if (ProviderComboBox.SelectedItem is ProviderOption option && option.Name != KnownProviders.Custom)
            {
                if (string.IsNullOrWhiteSpace(ApiUrlTextBox.Text) || !IsUrlModified())
                    ApiUrlTextBox.Text = option.DefaultUrl;
                if (string.IsNullOrWhiteSpace(ModelTextBox.Text))
                    ModelTextBox.Text = option.DefaultModel;
            }
        }

        private bool IsUrlModified()
        {
            // 如果当前 URL 匹配某个已知提供商的默认值，认为是"未修改"
            return !KnownProviders.All.Any(p => p.DefaultUrl == ApiUrlTextBox.Text);
        }

        // ---- 测试连接 ----

        private async void TestConnection_Click(object sender, RoutedEventArgs e)
        {
            var config = CurrentFormConfig;

            if (string.IsNullOrWhiteSpace(config.ApiKey))
            {
                MessageBox.Show("请输入 API Key", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            TestButton.IsEnabled = false;
            TestButton.Content = "连接中...";

            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.Add("Authorization", $"Bearer {config.ApiKey}");
                client.Timeout = TimeSpan.FromSeconds(15);

                var request = new
                {
                    model = config.Model,
                    messages = new[] { new { role = "user", content = "Hello" } },
                    max_tokens = 10
                };
                var json = JsonSerializer.Serialize(request);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await client.PostAsync(config.ApiUrl, content);

                if (response.IsSuccessStatusCode)
                {
                    MessageBox.Show("连接成功！API 配置可用。", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    var err = await response.Content.ReadAsStringAsync();
                    // 401/403 等认证错误也提示，但说明是配置问题
                    MessageBox.Show($"API 返回错误 ({(int)response.StatusCode}):\n{err}", "连接失败",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"无法连接到 API:\n{ex.Message}", "连接失败",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                TestButton.IsEnabled = true;
                TestButton.Content = "测试连接";
            }
        }

        // ---- JSON 编辑 ----

        private void ModeTabControl_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (e.Source != ModeTabControl) return;

            // 切换到 JSON 标签时加载当前配置
            if (ModeTabControl.SelectedItem == JsonTabItem)
            {
                LoadJsonEditor();
            }
        }

        private void LoadJsonEditor()
        {
            // 先把表单内容同步到内存，确保当前编辑未丢失
            var currentName = ProfileComboBox.SelectedItem as string;
            ApiConfig? config = null;
            if (currentName != null)
            {
                _profileManager.AddOrUpdate(currentName, CurrentFormConfig);
                config = _profileManager.ActiveProfile;
            }

            // 只展示当前方案的 JSON，与其他方案分离
            if (config != null)
            {
                JsonEditorTextBox.Text = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
            }
            else
            {
                JsonEditorTextBox.Text = "{}";
            }

            JsonValidationText.Text = "已加载";
            JsonValidationText.Foreground = System.Windows.Media.Brushes.Gray;
        }

        private void FormatJson_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                using var doc = JsonDocument.Parse(JsonEditorTextBox.Text);
                JsonEditorTextBox.Text = JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
                JsonValidationText.Text = "格式化完成";
                JsonValidationText.Foreground = System.Windows.Media.Brushes.Green;
            }
            catch (JsonException ex)
            {
                JsonValidationText.Text = $"JSON 格式错误: {ex.Message}";
                JsonValidationText.Foreground = System.Windows.Media.Brushes.Red;
            }
        }

        private void SaveJson_Click(object sender, RoutedEventArgs e)
        {
            var currentName = ProfileComboBox.SelectedItem as string;
            if (string.IsNullOrEmpty(currentName))
            {
                MessageBox.Show("请先选择一个配置方案", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // 解析并验证 JSON 为 ApiConfig 格式
            ApiConfig parsedConfig;
            try
            {
                parsedConfig = JsonSerializer.Deserialize<ApiConfig>(JsonEditorTextBox.Text, _jsonOptions)
                    ?? throw new InvalidOperationException("JSON 解析结果为空");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"JSON 格式错误，请修正后重试:\n{ex.Message}", "保存失败",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                JsonValidationText.Text = "JSON 格式错误";
                JsonValidationText.Foreground = System.Windows.Media.Brushes.Red;
                return;
            }

            // 校验必要字段
            if (string.IsNullOrWhiteSpace(parsedConfig.ApiKey))
            {
                if (MessageBox.Show("API Key 为空，确定要保存吗？", "提示",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.No)
                    return;
            }

            try
            {
                // 更新当前方案，写入文件
                _profileManager.AddOrUpdate(currentName, parsedConfig);

                // 刷新 UI
                RefreshProfileList();
                LoadProfileToProfileName(currentName);

                JsonValidationText.Text = "已保存并刷新";
                JsonValidationText.Foreground = System.Windows.Media.Brushes.Green;

                MessageBox.Show($"配置方案「{currentName}」已保存", "成功", MessageBoxButton.OK, MessageBoxImage.Information);

                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"保存失败:\n{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ---- 确定 / 取消 ----

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            var config = CurrentFormConfig;

            if (string.IsNullOrWhiteSpace(config.ApiKey))
            {
                MessageBox.Show("请输入 API Key", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (string.IsNullOrWhiteSpace(config.ApiUrl))
            {
                MessageBox.Show("请输入 API 地址", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (string.IsNullOrWhiteSpace(config.Model))
            {
                var result = MessageBox.Show("模型名称为空，确定继续吗？", "提示",
                    MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result == MessageBoxResult.No)
                    return;
            }

            // 保存当前修改到活动配置
            var currentName = ProfileComboBox.SelectedItem as string;
            if (currentName != null)
            {
                _profileManager.AddOrUpdate(currentName, config);
                _profileManager.SetActive(currentName);
            }

            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        // ---- 辅助 ----

        private string? PromptForName(string title, string message, string defaultValue)
        {
            var dialog = new InputDialog(title, message, defaultValue) { Owner = this };
            return dialog.ShowDialog() == true ? dialog.InputText : null;
        }

        private class ProviderOption
        {
            public string Name { get; }
            public string DefaultUrl { get; }
            public string DefaultModel { get; }

            public ProviderOption(string name, string defaultUrl, string defaultModel)
            {
                Name = name;
                DefaultUrl = defaultUrl;
                DefaultModel = defaultModel;
            }

            public override string ToString() => Name;
        }
    }
}
