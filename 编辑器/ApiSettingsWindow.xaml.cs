using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using 编辑器.Services;

namespace 编辑器
{
    public partial class ApiSettingsWindow : HandyControl.Controls.Window
    {
        private readonly ApiProfileManager _profileManager;
        private readonly SystemPromptStore? _promptStore;
        private bool _isSwitchingProfile; // 防止切换时触发重复加载
        private bool _isSwitchingModel;   // 防止程序改写模型下拉时回填到输入框

        // ---- 系统提示词编辑页状态 ----
        private readonly List<PromptItem> _promptItems = new();
        private PromptItem? _currentPromptItem;
        private bool _isSwitchingPrompt;    // 防止条目选中联动递归
        private bool _isSwitchingPreset;    // 防止方案下拉联动递归
        private string _editingPresetId = AiPrompts.DefaultPresetId;   // 正在编辑哪个方案
        private PromptStoreSnapshot? _initialPromptSnapshot;           // 取消时整批回滚用

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };

        public ApiSettingsWindow(Window owner, ApiProfileManager profileManager,
            SystemPromptStore? promptStore = null)
        {
            InitializeComponent();
            Owner = owner;
            _profileManager = profileManager;
            _promptStore = promptStore;

            // 服务商预设：地址 / 协议 / 认证头 / 常用模型 / 申请入口都已内置，
            // 用户只需要选一个服务商 + 粘一个 Key。
            ProviderComboBox.ItemsSource = ApiProviders.All
                .OrderBy(p => Array.IndexOf(ApiProviders.GroupOrder, p.Group))
                .ToList();

            AuthComboBox.ItemsSource = new List<AuthChoice>
            {
                new("", "自动（推荐）"),
                new("Bearer", ApiProviders.AuthLabel(ApiAuth.Bearer)),
                new("XApiKey", ApiProviders.AuthLabel(ApiAuth.XApiKey)),
                new("ApiKeyHeader", ApiProviders.AuthLabel(ApiAuth.ApiKeyHeader)),
            };
            AuthComboBox.SelectedIndex = 0;

            RefreshProfileList();
            LoadProfileToUI(_profileManager.ActiveProfile);
            InitializePromptEditor();
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

            // 选中对应的服务商（认 Id，也认旧配置里的显示名）
            ProviderComboBox.SelectedItem = ApiProviders.Find(config.Provider) ?? ApiProviders.Custom;

            ApiUrlTextBox.Text = config.ApiUrl;
            ModelTextBox.Text = config.Model;
            ApiKeyPasswordBox.Password = config.ApiKey;

            RefreshAuthChoices(config.AuthOverride);
            RefreshProviderDependent();

            _isSwitchingProfile = false;
        }

        private ApiConfig CurrentFormConfig => new()
        {
            Provider = (ProviderComboBox.SelectedItem as ProviderPreset)?.Id ?? ApiProviders.IdOpenAi,
            ApiUrl = ApiUrlTextBox.Text.Trim(),
            // ⚠ 不要 ToLowerInvariant：硅基流动的 deepseek-ai/DeepSeek-V3、
            //   MiniMax 的 MiniMax-Text-01 都区分大小写，降成小写会直接 404。
            Model = ModelTextBox.Text.Trim(),
            ApiKey = ApiKeyPasswordBox.Password,
            AuthOverride = (AuthComboBox.SelectedItem as AuthChoice)?.Value ?? "",
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

        // ---- 服务商联动 ----

        private void ProviderComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_isSwitchingProfile) return;
            if (ProviderComboBox.SelectedItem is not ProviderPreset preset) return;

            if (!preset.IsCustom)
            {
                // 选中预设时把地址与默认模型一次性带出来，用户不用查文档
                ApiUrlTextBox.Text = preset.Endpoint;
                ModelTextBox.Text = preset.DefaultModel;
            }

            // 换服务商 → 认证方式回到「自动」，避免上一家的手工选择残留
            RefreshAuthChoices(explicitValue: null);
            RefreshProviderDependent();
        }

        private void ModelPresetComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_isSwitchingModel) return;
            if (ModelPresetComboBox.SelectedItem is string model && !string.IsNullOrWhiteSpace(model))
            {
                ModelTextBox.Text = model;
                ModelTextBox.CaretIndex = ModelTextBox.Text.Length;
            }
        }

        private void AuthComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            RefreshProviderHint();
        }

        private void ApiUrlTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            // 地址是协议判定的依据（出现 /messages 即按 Anthropic 处理），改了要跟着刷新提示
            if (_isSwitchingProfile) return;
            RefreshProviderHint();
        }

        private void OpenProviderConsole_Click(object sender, RoutedEventArgs e)
        {
            var url = (ProviderComboBox.SelectedItem as ProviderPreset)?.ConsoleUrl;
            if (string.IsNullOrWhiteSpace(url)) return;

            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"无法打开链接：\n{url}\n{ex.Message}", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        /// <summary>认证方式下拉复位/指定。explicitValue 为 null 表示回到「自动」。</summary>
        private void RefreshAuthChoices(string? explicitValue)
        {
            var want = explicitValue ?? "";
            var target = AuthComboBox.Items.OfType<AuthChoice>()
                .FirstOrDefault(c => string.Equals(c.Value, want, StringComparison.OrdinalIgnoreCase));
            AuthComboBox.SelectedItem = target ?? AuthComboBox.Items.OfType<AuthChoice>().First();
        }

        /// <summary>服务商变了要连带刷新的东西：常用模型下拉 + 参数速览。</summary>
        private void RefreshProviderDependent()
        {
            RefreshModelSuggestions();
            RefreshProviderHint();
        }

        private void RefreshModelSuggestions()
        {
            var preset = ProviderComboBox.SelectedItem as ProviderPreset;
            var models = preset?.Models ?? (IReadOnlyList<string>)Array.Empty<string>();

            _isSwitchingModel = true;
            ModelPresetComboBox.ItemsSource = models.ToList();
            ModelPresetComboBox.SelectedIndex = -1;
            ModelPresetComboBox.IsEnabled = models.Count > 0;
            ModelPresetComboBox.ToolTip = models.Count > 0
                ? "点一个填进左边的模型名称；也可以直接手写任意模型名（以服务商文档为准）。"
                : "该服务商没有内置常用型号，请直接填写模型名称。";
            _isSwitchingModel = false;
        }

        /// <summary>
        /// 刷新「协议 / 认证 / 注意事项 / 申请入口」这一栏。
        /// 用的是**当前表单实际会怎么发请求**，而不是预设的声明 ——
        /// 用户可能改过地址、也可能手工指定过认证方式，提示必须跟得上。
        /// </summary>
        private void RefreshProviderHint()
        {
            if (ProviderComboBox.SelectedItem is not ProviderPreset preset)
            {
                ProviderHintText.Text = "";
                ProviderKeyBtn.IsEnabled = false;
                return;
            }

            var probe = new ApiConfig
            {
                Provider = preset.Id,
                ApiUrl = ApiUrlTextBox.Text.Trim(),
                AuthOverride = (AuthComboBox.SelectedItem as AuthChoice)?.Value ?? "",
            };

            var lines = new List<string>
            {
                $"{preset.Group} · 协议：{ApiProviders.WireLabel(ApiProviders.ResolveWire(probe))}",
                $"认证：{ApiProviders.AuthLabel(ApiProviders.ResolveAuth(probe))}"
                + (string.IsNullOrWhiteSpace(probe.AuthOverride) ? "（自动）" : "（手动指定）"),
            };
            if (!string.IsNullOrWhiteSpace(preset.Note)) lines.Add(preset.Note);

            // 地址与所选服务商声明的协议矛盾时显式提醒，否则会静默打不通
            var warning = ApiProviders.WireWarning(probe);
            if (warning != null) lines.Add(warning);

            ProviderHintText.Text = string.Join("\n", lines);
            ProviderHintText.Foreground = warning == null
                ? (System.Windows.Media.Brush)FindResource("Brush.TextMuted")
                : (System.Windows.Media.Brush)FindResource("Brush.Danger");
            ProviderKeyBtn.IsEnabled = !string.IsNullOrWhiteSpace(preset.ConsoleUrl);
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
                client.Timeout = TimeSpan.FromSeconds(15);

                // 认证头与协议必需的头走与真实调用**同一份**逻辑，
                // 否则会出现「测试连接成功、真正调用却 401」的假阳性。
                var authLabel = ApiProviders.ApplyHeaders(client.DefaultRequestHeaders, config);
                ApiProviders.ApplyExtraHeaders(client.DefaultRequestHeaders, config);

                string json;
                if (ApiProviders.ResolveWire(config) == ApiWire.AnthropicMessages)
                {
                    var anthropicRequest = new
                    {
                        model = config.Model,
                        max_tokens = 10,
                        messages = new[] { new { role = "user", content = "Hello" } }
                    };
                    json = JsonSerializer.Serialize(anthropicRequest);
                }
                else
                {
                    var openAiRequest = new
                    {
                        model = config.Model,
                        messages = new[] { new { role = "user", content = "Hello" } },
                        max_tokens = 10
                    };
                    json = JsonSerializer.Serialize(openAiRequest);
                }

                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var response = await client.PostAsync(config.ApiUrl, content);

                if (response.IsSuccessStatusCode)
                {
                    var preset = ProviderComboBox.SelectedItem as ProviderPreset;
                    MessageBox.Show(
                        "连接成功！API 配置可用。\n\n"
                        + $"服务商：{preset?.Name ?? "自定义"}\n"
                        + $"协议：  {ApiProviders.WireLabel(ApiProviders.ResolveWire(config))}\n"
                        + $"认证：  {authLabel}\n"
                        + $"模型：  {config.Model}",
                        "成功", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    var err = await response.Content.ReadAsStringAsync();
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

        // ==================================================================
        // 系统提示词编辑
        //
        // 交互约定：
        //   · 「当前方案」下拉切换的是**真正生效**的方案（保存后 AI 立即按新方案工作）；
        //   · 编辑框改的是**当前方案**下那一条覆写，不同方案的改动彼此独立、互不影响；
        //   · 切换条目 / 切换方案 / 点确定 / 点保存 都会把编辑框内容收进内存（不落盘）；
        //   · 点「保存提示词」或「确定」才真正写 system_prompts.json；
        //   · 点「取消」整批回滚（含方案的新建 / 重命名 / 删除与生效方案），不留副作用；
        //   · 文本与内置默认实质相同时会被当作"恢复默认"，覆写记录随之删除。
        //
        // 内置方案同样可以改：改动记在该方案自己的覆写下，「恢复本项默认」可退回出厂文本。
        // 想既保住出厂文本、又要一个自己的版本，用「另存为副本」——副本记着它是从哪个
        // 内置方案派生的，所以没动过的条目仍会跟随内置文本的后续改进。
        //
        // ⚠ 这里所有对内置文本的读取都走 DefaultText()，**不经过 AiPrompts.Store**：
        //   设置页要在方案之间来回切，取的是"被编辑的那个方案"的默认值，
        //   而 AiPrompts 上那套属性永远只看"当前生效方案"。
        // ==================================================================

        private void InitializePromptEditor()
        {
            if (_promptStore == null)
            {
                // 没拿到存储（理论上不会发生）——禁用该页而不是让用户改了个假配置
                PromptTabItem.IsEnabled = false;
                PromptEditorTextBox.IsEnabled = false;
                ResetPromptBtn.IsEnabled = false;
                PromptStatusText.Text = "未接入提示词存储";
                return;
            }

            _initialPromptSnapshot = _promptStore.Capture();

            foreach (var meta in AiPrompts.Entries)
                _promptItems.Add(new PromptItem(meta));

            PromptListBox.ItemsSource = _promptItems;

            // 先定好"在编辑哪个方案"，再选条目——条目的默认文本取决于方案
            _editingPresetId = _promptStore.ActivePresetId;
            RefreshPresetList();
            if (_promptItems.Count > 0) PromptListBox.SelectedIndex = 0;

            UpdatePromptStatus();
        }

        /// <summary>取某方案下某条目的**内置**默认文本（不含覆写）。</summary>
        private string DefaultText(string presetId, string key) =>
            PromptPresets.TextOf(_promptStore!.ResolveBasePreset(presetId), key);

        /// <summary>取某方案下某条目的生效文本（覆写优先）。</summary>
        private string EffectiveText(string presetId, string key) =>
            _promptStore!.Get(presetId, key, DefaultText(presetId, key));

        // ---- 方案栏 ----

        private void RefreshPresetList()
        {
            if (_promptStore == null) return;

            _isSwitchingPreset = true;

            // 方案清单必须来自**本窗口手上的那个 store**，不能读全局 AiPrompts.Store：
            // 全局那份是"当前生效"的引用，与设置页正在编辑的可能是两个实例。
            var options = AiPrompts.PresetOptions(_promptStore)
                .Select(o => new PresetChoice(o.Id, o.Name, o.IsBuiltIn, o.Description))
                .ToList();
            PresetComboBox.ItemsSource = options;

            var current = options.FirstOrDefault(o => o.Id == _editingPresetId) ?? options[0];

            // 下拉的选中项 = 当前生效方案。为 null 时才需要重新赋（避免无谓的重入）
            if (!ReferenceEquals(PresetComboBox.SelectedItem, current))
                PresetComboBox.SelectedItem = current;

            PresetDescText.Text = current.Description;
            RenamePresetBtn.IsEnabled = !current.IsBuiltIn;
            DeletePresetBtn.IsEnabled = !current.IsBuiltIn;

            _isSwitchingPreset = false;
        }

        private void PresetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isSwitchingPreset || _promptStore == null) return;
            if (PresetComboBox.SelectedItem is not PresetChoice option) return;
            if (string.Equals(option.Id, _editingPresetId, StringComparison.Ordinal)) return;

            // 离开旧方案之前，先把编辑框里的内容收进**旧方案**（此刻 _editingPresetId 还没变）
            StashCurrentPromptEdit();

            _editingPresetId = option.Id;
            // 下拉就是"当前使用方案"，切换立即生效；点「取消」会整批回滚
            _promptStore.ActivePresetId = option.Id;

            PresetDescText.Text = option.Description;
            RenamePresetBtn.IsEnabled = !option.IsBuiltIn;
            DeletePresetBtn.IsEnabled = !option.IsBuiltIn;

            ReloadCurrentEntry();
            UpdatePromptStatus();
        }

        private void DuplicatePreset_Click(object sender, RoutedEventArgs e)
        {
            if (_promptStore == null) return;

            StashCurrentPromptEdit();

            var sourceId = _editingPresetId;
            var baseId = _promptStore.ResolveBasePreset(sourceId);
            var sourceName = _promptStore.PresetName(sourceId) ?? "方案";
            var baseName = PromptPresets.Find(baseId)?.Name ?? "通用写作";

            var name = PromptForName("另存为副本",
                $"新方案的名称（将基于内置方案「{baseName}」）：", sourceName + " 副本");
            if (string.IsNullOrWhiteSpace(name)) return;

            var newId = DuplicatePresetCore(sourceId, name, out var error);
            if (newId == null)
            {
                MessageBox.Show(error, "无法创建副本", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            SwitchToPreset(newId);   // 复制出来就是为了改它，顺手切过去
        }

        /// <summary>
        /// 「另存为副本」的核心逻辑（不含弹窗，便于回归测试直接调用）。
        /// 把源方案的**生效文本**作为种子写进副本；与依据方案相同的那几条会被
        /// <see cref="SystemPromptStore.Set"/> 自动过滤掉，所以副本里只留下真正
        /// "继承自源方案"的差异条目 —— 没动过的部分仍会跟随内置文本的后续改进。
        /// </summary>
        /// <returns>新方案 Id；失败返回 null 并填 <paramref name="error"/>。</returns>
        private string? DuplicatePresetCore(string sourceId, string name, out string error)
        {
            error = "";
            if (_promptStore == null) { error = "未接入提示词存储。"; return null; }

            name = (name ?? "").Trim();
            if (name.Length == 0) { error = "方案名称不能为空。"; return null; }

            if (_promptStore.IsNameTaken(name))
            {
                error = $"已经有一个叫「{name}」的方案了，换个名字。";
                return null;
            }

            var baseId = _promptStore.ResolveBasePreset(sourceId);
            var newId = _promptStore.CreateCustom(baseId, name);

            foreach (var meta in AiPrompts.Entries)
                _promptStore.Set(newId, meta.Key, EffectiveText(sourceId, meta.Key),
                    PromptPresets.TextOf(baseId, meta.Key));

            return newId;
        }

        private void RenamePreset_Click(object sender, RoutedEventArgs e)
        {
            if (_promptStore == null || !_promptStore.IsCustom(_editingPresetId)) return;

            var current = _promptStore.CustomName(_editingPresetId) ?? "";
            var name = PromptForName("重命名方案", "新的方案名称：", current);
            if (string.IsNullOrWhiteSpace(name)) return;

            if (!RenamePresetCore(_editingPresetId, name, out var error))
            {
                MessageBox.Show(error, "无法重命名", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            RefreshPresetList();
            UpdatePromptStatus();
        }

        /// <summary>重命名的核心逻辑（不含弹窗）。只能重命名自定义方案。</summary>
        private bool RenamePresetCore(string presetId, string name, out string error)
        {
            error = "";
            if (_promptStore == null) { error = "未接入提示词存储。"; return false; }
            if (!_promptStore.IsCustom(presetId)) { error = "内置方案不能重命名，请用「另存为副本」。"; return false; }

            name = (name ?? "").Trim();
            if (name.Length == 0) { error = "方案名称不能为空。"; return false; }

            if (_promptStore.IsNameTaken(name, presetId))
            {
                error = $"已经有一个叫「{name}」的方案了，换个名字。";
                return false;
            }

            return _promptStore.RenameCustom(presetId, name);
        }

        private void DeletePreset_Click(object sender, RoutedEventArgs e)
        {
            if (_promptStore == null || !_promptStore.IsCustom(_editingPresetId)) return;

            var name = _promptStore.CustomName(_editingPresetId) ?? "该方案";
            if (MessageBox.Show($"确定删除自定义方案「{name}」吗？\n"
                                + "它的全部自定义内容会一并删除，且无法恢复。\n"
                                + "（还要点「保存提示词」或「确定」才会写入文件）",
                    "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            DeletePresetCore(_editingPresetId);
        }

        /// <summary>
        /// 删除自定义方案的核心逻辑（不含弹窗）。
        /// 删的若是当前生效方案，<see cref="SystemPromptStore.DeleteCustom"/> 已把它切回
        /// 依据的内置方案 —— 否则会留下一个指向不存在方案的 ActivePresetId。
        /// </summary>
        private bool DeletePresetCore(string presetId)
        {
            if (_promptStore == null || !_promptStore.IsCustom(presetId)) return false;

            _promptStore.DeleteCustom(presetId);

            // 编辑框里可能还留着被删方案的文本。先把引用清掉再重载，
            // 否则切到别的方案时那次自动 Stash 会把废弃文本写进新方案。
            _currentPromptItem = null;
            _editingPresetId = _promptStore.ActivePresetId;

            RefreshPresetList();
            PromptListBox.SelectedIndex = -1;
            if (_promptItems.Count > 0) PromptListBox.SelectedIndex = 0;

            UpdatePromptStatus();
            return true;
        }

        /// <summary>把界面切到指定方案（下拉、编辑目标、条目标记一起走）。</summary>
        private void SwitchToPreset(string presetId)
        {
            if (_promptStore == null) return;

            _editingPresetId = presetId;
            _promptStore.ActivePresetId = presetId;

            RefreshPresetList();
            ReloadCurrentEntry();
            UpdatePromptStatus();
        }

        // ---- 条目 ----

        private void PromptListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isSwitchingPrompt) return;

            // 离开上一条之前先把编辑框里的内容收进内存，避免"切一下就丢改动"
            StashCurrentPromptEdit();

            _currentPromptItem = PromptListBox.SelectedItem as PromptItem;
            ReloadCurrentEntry();
            UpdatePromptStatus();
        }

        /// <summary>按当前方案 + 当前条目刷新右侧编辑区。</summary>
        private void ReloadCurrentEntry()
        {
            if (_promptStore == null || _currentPromptItem == null) return;

            var entry = _currentPromptItem.Entry;

            _isSwitchingPrompt = true;
            PromptTitleText.Text = $"{entry.Group} · {entry.Title}";
            PromptDescText.Text = entry.Description;
            PromptEditorTextBox.Text = EffectiveText(_editingPresetId, entry.Key);
            ResetPromptBtn.IsEnabled = _promptStore.IsOverridden(_editingPresetId, entry.Key);
            _isSwitchingPrompt = false;

            RefreshEntryMarks();
        }

        /// <summary>刷新左侧列表上的"已自定义"标记（换方案后必须整体刷新）。</summary>
        private void RefreshEntryMarks()
        {
            if (_promptStore == null) return;
            foreach (var item in _promptItems)
                item.IsOverridden = _promptStore.IsOverridden(_editingPresetId, item.Entry.Key);
        }

        /// <summary>把编辑框当前内容写入内存（不落盘），并刷新列表上的"已自定义"标记。</summary>
        private void StashCurrentPromptEdit()
        {
            if (_promptStore == null || _currentPromptItem == null) return;

            var entry = _currentPromptItem.Entry;
            _promptStore.Set(_editingPresetId, entry.Key, PromptEditorTextBox.Text,
                DefaultText(_editingPresetId, entry.Key));

            _currentPromptItem.IsOverridden = _promptStore.IsOverridden(_editingPresetId, entry.Key);
            ResetPromptBtn.IsEnabled = _currentPromptItem.IsOverridden;
        }

        private void ResetPrompt_Click(object sender, RoutedEventArgs e)
        {
            if (_promptStore == null || _currentPromptItem == null) return;

            var entry = _currentPromptItem.Entry;
            _promptStore.Reset(_editingPresetId, entry.Key);
            _currentPromptItem.IsOverridden = false;

            _isSwitchingPrompt = true;
            PromptEditorTextBox.Text = DefaultText(_editingPresetId, entry.Key);
            ResetPromptBtn.IsEnabled = false;
            _isSwitchingPrompt = false;

            UpdatePromptStatus();
        }

        /// <summary>只复位**当前方案**的那些覆写，别的方案不受影响。</summary>
        private void ResetAllPrompts_Click(object sender, RoutedEventArgs e)
        {
            if (_promptStore == null) return;

            var presetName = _promptStore.PresetName(_editingPresetId) ?? "当前方案";
            var n = _promptStore.OverrideCountOf(_editingPresetId);
            if (n == 0)
            {
                PromptStatusText.Text = $"「{presetName}」没有任何自定义，已全部是内置默认";
                return;
            }

            if (MessageBox.Show($"确定要把「{presetName}」的 {n} 条自定义全部恢复成内置默认吗？\n"
                                + "其它方案的改动不受影响。\n"
                                + "（还要点「保存提示词」或「确定」才会写入文件）",
                    "确认恢复默认", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            _promptStore.ResetAll(_editingPresetId);
            foreach (var item in _promptItems) item.IsOverridden = false;

            _isSwitchingPrompt = true;
            if (_currentPromptItem != null)
                PromptEditorTextBox.Text = DefaultText(_editingPresetId, _currentPromptItem.Entry.Key);
            ResetPromptBtn.IsEnabled = false;
            _isSwitchingPrompt = false;

            UpdatePromptStatus();
        }

        private void SavePrompts_Click(object sender, RoutedEventArgs e)
        {
            if (!TrySavePrompts(out var message)) return;

            UpdatePromptStatus();
            MessageBox.Show(message, "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        /// <summary>
        /// 把提示词落盘。返回 false 表示保存失败（调用方应中止关闭对话框）。
        /// </summary>
        private bool TrySavePrompts(out string message)
        {
            if (_promptStore == null) { message = ""; return true; }

            StashCurrentPromptEdit();

            try
            {
                _promptStore.Save();
            }
            catch (Exception ex)
            {
                message = "";
                MessageBox.Show($"提示词保存失败：\n{ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            var n = _promptStore.OverrideCount;
            var active = _promptStore.PresetName(_promptStore.ActivePresetId) ?? "默认";
            var customCount = _promptStore.CustomPresets.Count;

            message = $"已保存：当前使用「{active}」方案。"
                      + (n == 0 ? "该方案全部条目为内置默认" : $"所有方案合计 {n} 条自定义")
                      + (customCount > 0 ? $"，另有 {customCount} 个自定义方案" : "")
                      + $"，已写入\n{_promptStore.FilePath}\n\n下次调用 AI 即时生效。";
            return true;
        }

        private void UpdatePromptStatus()
        {
            if (_promptStore == null) return;

            var total = AiPrompts.Entries.Count;
            var n = _promptStore.OverrideCountOf(_editingPresetId);
            var name = _promptStore.PresetName(_editingPresetId) ?? "未知方案";
            var kind = _promptStore.IsCustom(_editingPresetId) ? "自定义" : "内置";
            var usingNow = string.Equals(_promptStore.ActivePresetId, _editingPresetId, StringComparison.Ordinal)
                ? "使用中" : "未使用";

            PromptStatusText.Text = n == 0
                ? $"当前方案：{name}（{kind} · {usingNow}） · {total} 条全部为内置默认"
                : $"当前方案：{name}（{kind} · {usingNow}） · {n} / {total} 条已自定义";
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

            // 提示词编辑也要落盘（失败则不关闭，让用户能看到错误）
            if (!TrySavePrompts(out _)) return;

            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            // 丢弃本轮对提示词的所有编辑（含方案的新建 / 重命名 / 删除与切换），回到打开窗口时的状态
            if (_promptStore != null && _initialPromptSnapshot != null)
                _promptStore.Restore(_initialPromptSnapshot);

            DialogResult = false;
            Close();
        }

        // ---- 辅助 ----

        private string? PromptForName(string title, string message, string defaultValue)
        {
            var dialog = new InputDialog(title, message, defaultValue) { Owner = this };
            return dialog.ShowDialog() == true ? dialog.InputText : null;
        }

        /// <summary>认证方式下拉的一项。<see cref="Value"/> 为空表示「自动，按服务商预设决定」。</summary>
        private class AuthChoice
        {
            public string Value { get; }
            private string Label { get; }

            public AuthChoice(string value, string label)
            {
                Value = value;
                Label = label;
            }

            public override string ToString() => Label;
        }
    }

    /// <summary>
    /// 设置页左侧列表里的一条提示词。除了承载条目元数据，还负责在"已自定义"
    /// 状态变化时通知界面刷新标记（编辑 → 恢复默认会让标记消失）。
    /// </summary>
    public sealed class PromptItem : INotifyPropertyChanged
    {
        public AiPrompts.EntryMeta Entry { get; }

        private bool _isOverridden;

        /// <summary>
        /// 该条目在**当前编辑的方案**下是否已被自定义（即覆写文件里有记录）。
        /// 切换方案时要整体重算——同一条目在 A 方案改过、在 B 方案没改，是常态。
        /// </summary>
        public bool IsOverridden
        {
            get => _isOverridden;
            set
            {
                if (_isOverridden == value) return;
                _isOverridden = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsOverridden)));
            }
        }

        public PromptItem(AiPrompts.EntryMeta entry)
        {
            Entry = entry;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>
    /// 方案下拉里的一项。用 <see cref="ToString"/> 出显示文本而不是 ItemTemplate：
    /// HandyControl 的 ComboBox 选中态展示与下拉项展示走的是两套模板，
    /// 用 ToString 能保证两处一致，也少一层模板风险。
    /// </summary>
    public sealed class PresetChoice
    {
        public string Id { get; }
        public string Name { get; }
        public bool IsBuiltIn { get; }
        public string Description { get; }

        public PresetChoice(string id, string name, bool isBuiltIn, string description)
        {
            Id = id;
            Name = name;
            IsBuiltIn = isBuiltIn;
            Description = description;
        }

        public override string ToString() => IsBuiltIn ? $"{Name}（内置）" : $"{Name}（自定义）";
    }
}
