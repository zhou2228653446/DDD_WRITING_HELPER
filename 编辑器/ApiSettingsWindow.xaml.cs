using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using 编辑器.Services;

namespace 编辑器
{
    public partial class ApiSettingsWindow : HandyControl.Controls.Window
    {
        private readonly ApiProfileManager _profileManager;
        private readonly SystemPromptStore? _promptStore;
        private bool _isSwitchingProfile; // 防止切换时触发重复加载
        private bool _isSwitchingModel;   // 防止程序改写模型下拉时回填到输入框

        // ---- 模型清单（真实可用型号靠拉取，内置表只作离线兜底） ----
        private readonly ModelCacheStore _modelCache;
        private readonly Dictionary<string, IReadOnlyList<ModelInfo>> _liveModels = new(StringComparer.Ordinal);
        private IReadOnlyList<ModelInfo> _allModels = Array.Empty<ModelInfo>();
        private ModelSource _modelSource = ModelSource.None;
        private string _fetchedKey = "";       // 当前展示的清单属于哪个「服务商@主机」
        private bool _showAllModels;           // 是否把非对话模型也摊开
        private bool _isFetchingModels;

        private enum ModelSource { None, BuiltIn, Cached, Live }

        // ---- 系统提示词编辑页状态 ----
        private readonly List<PromptItem> _promptItems = new();
        private PromptItem? _currentPromptItem;
        private bool _isSwitchingPrompt;    // 防止条目选中联动递归
        private bool _isSwitchingPreset;    // 防止方案下拉联动递归
        private string _editingPresetId = AiPrompts.DefaultPresetId;   // 正在编辑哪个方案
        private PromptStoreSnapshot? _initialPromptSnapshot;           // 取消时整批回滚用

        // ---- 技能编辑页状态 ----
        private readonly string _skillDir;          // 初始化后由构造函数赋值
        private List<NovelSkill> _skills = new();
        private NovelSkill? _currentSkill;
        private bool _loadingSkillForm;             // 载入表单时禁止 CheckBox/Combo 联动
        private readonly Dictionary<string, bool> _taskChecks = new(StringComparer.Ordinal); // 键 → 勾选

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

            _modelCache = new ModelCacheStore(ResolveConfigDir(promptStore));
            _skillDir = ResolveConfigDir(promptStore);

            RefreshProfileList();
            LoadProfileToUI(_profileManager.ActiveProfile);
            InitializePromptEditor();
            InitializeSkillEditor();
        }

        /// <summary>配置目录。优先跟提示词存储同一个目录，拿不到才回落到默认位置。</summary>
        private static string ResolveConfigDir(SystemPromptStore? store)
        {
            var dir = store == null ? null : Path.GetDirectoryName(store.FilePath);
            if (!string.IsNullOrWhiteSpace(dir)) return dir!;

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TdxClaw");
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
            if (ModelPresetComboBox.SelectedItem is ModelInfo model && model.Id.Length > 0)
            {
                ModelTextBox.Text = model.Id;
                ModelTextBox.CaretIndex = ModelTextBox.Text.Length;
            }
        }

        private void ShowAllModels_Click(object sender, RoutedEventArgs e)
        {
            _showAllModels = !_showAllModels;
            RenderModelList();
        }

        private void ModelTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isSwitchingModel) return;
            UpdateModelStatus();   // 手改模型名时立刻校验它是否在清单里
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

            // 主机变了 → 缓存键变了，型号下拉要跟着换成那一家的清单，
            // 否则会显示上一家的型号
            if (CurrentModelCacheKey() != _fetchedKey) RefreshModelSuggestions();
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

        // ---- 模型清单 ----
        //
        // 型号名的**唯一权威来源是服务商自己**：带日期版本号的名称（claude-sonnet-4-5-20250929
        // 这类）改动比程序发版快，内置表迟早会变成一串用不了的死字符串。
        // 所以下拉的优先级是：本次拉到的 > 上次拉到并缓存的 > 内置参考表。
        // 界面会明说当前这批是哪来的，用户不会误以为内置表就是全部。

        /// <summary>缓存键：服务商 + 主机。地址换了主机就重新拉，免得把中转的清单套到官网上。</summary>
        private string CurrentModelCacheKey() =>
            ModelCacheStore.KeyOf(
                (ProviderComboBox.SelectedItem as ProviderPreset)?.Id,
                ApiUrlTextBox.Text.Trim());

        private static ModelInfo ToModel(string id) => new()
        {
            Id = id,
            IsChatLike = ModelCatalog.IsChatLike(id),
        };

        private void RefreshModelSuggestions()
        {
            var preset = ProviderComboBox.SelectedItem as ProviderPreset;
            var key = CurrentModelCacheKey();

            _fetchedKey = key;
            _showAllModels = false;

            IReadOnlyList<ModelInfo> models;
            if (_liveModels.TryGetValue(key, out var live))
            {
                models = live;
                _modelSource = ModelSource.Live;
            }
            else
            {
                var cached = _modelCache.Get(key);
                if (cached != null && cached.Count > 0)
                {
                    models = cached.Select(ToModel).ToList();
                    _modelSource = ModelSource.Cached;
                }
                else if (preset != null && preset.Models.Count > 0)
                {
                    models = preset.Models.Select(ToModel).ToList();
                    _modelSource = ModelSource.BuiltIn;
                }
                else
                {
                    models = Array.Empty<ModelInfo>();
                    _modelSource = ModelSource.None;
                }
            }

            ApplyModelList(models);
        }

        private void ApplyModelList(IReadOnlyList<ModelInfo> models)
        {
            _allModels = models;
            RenderModelList();
            UpdateModelStatus();
        }

        /// <summary>把清单灌进下拉。对话模型排前面，非对话模型默认折叠（可点「显示其余 n 个」摊开）。</summary>
        private void RenderModelList()
        {
            var ordered = ModelCatalog.SortForDisplay(_allModels);
            var shown = _showAllModels
                ? ordered
                : ordered.Where(m => m.IsChatLike).ToList();

            _isSwitchingModel = true;
            ModelPresetComboBox.ItemsSource = shown.ToList();
            ModelPresetComboBox.SelectedIndex = -1;
            _isSwitchingModel = false;

            ModelPresetComboBox.IsEnabled = _allModels.Count > 0;
            ModelPresetComboBox.ToolTip = _modelSource switch
            {
                ModelSource.Live => $"刚从服务商拉到的真实清单（{_allModels.Count} 个）。点一个填进左边的模型名称。",
                ModelSource.Cached => $"上次拉取并缓存的真实清单（{_allModels.Count} 个，{_modelCache.FetchedAt(CurrentModelCacheKey())}）。"
                                      + "想确认是否有新模型，点「拉取列表」。",
                ModelSource.BuiltIn => "这是内置的参考型号，**可能已经过期**。建议点「拉取列表」向服务商要真实清单。",
                _ => "该服务商没有内置型号。点「拉取列表」获取，或按官方文档手填模型名称。",
            };

            var hidden = ModelCatalog.NonChatCount(_allModels);
            ShowAllModelsBtn.Visibility = hidden > 0 ? Visibility.Visible : Visibility.Collapsed;
            ShowAllModelsBtn.Content = _showAllModels ? "只看对话模型" : $"显示其余 {hidden} 个";
        }

        /// <summary>刷新型号那一行右侧的状态说明。传 error 则显示为错误（红色、可悬停看全文）。</summary>
        private void UpdateModelStatus(string? error = null)
        {
            if (!string.IsNullOrWhiteSpace(error))
            {
                ModelStatusText.Text = error.Replace("\n", " ");
                ModelStatusText.Foreground = (System.Windows.Media.Brush)FindResource("Brush.Danger");
                ModelStatusText.ToolTip = error;
                return;
            }

            ModelStatusText.Foreground = (System.Windows.Media.Brush)FindResource("Brush.TextMuted");
            ModelStatusText.ToolTip = null;

            var chat = _allModels.Count(m => m.IsChatLike);
            var hidden = _allModels.Count - chat;
            var extra = hidden > 0 ? $"（另有 {hidden} 个非对话模型）" : "";

            ModelStatusText.Text = _modelSource switch
            {
                ModelSource.Live => $"已拉取 {chat} 个对话模型{extra}",
                ModelSource.Cached => $"缓存 {chat} 个对话模型{extra}"
                                      + $" · 拉取于 {_modelCache.FetchedAt(CurrentModelCacheKey())}",
                ModelSource.BuiltIn => "内置参考型号，可能已过期 —— 建议点「拉取列表」",
                _ => "尚未拉取模型清单",
            };

            // 拿服务商的真实清单校一下手上这个模型名 —— 这是"名字不对导致 400/404"最直接的拦截点
            if (CurrentModelLooksUnknown())
            {
                ModelStatusText.Text += " · ⚠ 手上这个名字不在清单里";
                ModelStatusText.Foreground = (System.Windows.Media.Brush)FindResource("Brush.Danger");
            }
        }

        /// <summary>
        /// 输入框里的模型名是否不在已知清单里。
        /// 只有清单来自服务商（拉取 / 缓存）时才判断 —— 内置表自己就可能过期，拿它去质疑用户没意义。
        /// </summary>
        private bool CurrentModelLooksUnknown()
        {
            if (_modelSource != ModelSource.Live && _modelSource != ModelSource.Cached) return false;

            var typed = ModelTextBox.Text.Trim();
            if (typed.Length == 0) return false;

            return !_allModels.Any(m => string.Equals(m.Id, typed, StringComparison.OrdinalIgnoreCase));
        }

        private async void FetchModels_Click(object sender, RoutedEventArgs e) =>
            await FetchModelsAsync(interactive: true);

        /// <summary>
        /// 拉取真实模型清单。
        /// <paramref name="interactive"/> 为 false 时不出弹窗（「测试连接」成功后会顺手拉一次）。
        /// </summary>
        private async Task<bool> FetchModelsAsync(bool interactive)
        {
            if (_isFetchingModels) return false;

            var preset = ProviderComboBox.SelectedItem as ProviderPreset;
            var config = CurrentFormConfig;
            var key = ModelCacheStore.KeyOf(preset?.Id, config.ApiUrl);

            if (string.IsNullOrWhiteSpace(config.ApiKey) && !ModelCatalog.IsLocalHost(config.ApiUrl))
            {
                UpdateModelStatus("请先填 API Key —— 拉取模型列表同样要鉴权。");
                if (interactive)
                    MessageBox.Show(
                        "请先填写 API Key。\n\n拉取模型列表和对话端点一样需要鉴权，所以必须先有 Key。",
                        "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }

            _isFetchingModels = true;
            FetchModelsBtn.IsEnabled = false;
            FetchModelsBtn.Content = "拉取中";
            UpdateModelStatus("正在向服务商查询…");

            try
            {
                var result = await ModelCatalog.FetchAsync(config);

                if (result.Ok)
                {
                    // 结果按**它自己的**键写回；界面只在还停在同一家时才刷新，
                    // 避免用户拉到一半换了服务商，A 家的清单显示到 B 家下面
                    _modelCache.Set(key, result.Models.Select(m => m.Id));
                    _liveModels[key] = result.Models;

                    if (key == CurrentModelCacheKey())
                    {
                        _fetchedKey = key;
                        _modelSource = ModelSource.Live;
                        ApplyModelList(result.Models);
                    }
                }
                else if (key == CurrentModelCacheKey())
                {
                    UpdateModelStatus(result.Error);
                }

                if (!interactive) return result.Ok;

                if (result.Ok)
                {
                    var hidden = ModelCatalog.NonChatCount(result.Models);
                    MessageBox.Show(
                        $"拉到 {result.Models.Count} 个可用模型。"
                        + (hidden > 0 ? $"（其中 {hidden} 个不是对话模型，已收起）" : "")
                        + $"\n\n接口：{result.Url}"
                        + "\n\n从「可用型号」下拉里挑一个，或直接手写模型名。",
                        "拉取成功", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show(result.Error, "拉取失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                }

                return result.Ok;
            }
            finally
            {
                _isFetchingModels = false;
                FetchModelsBtn.IsEnabled = true;
                FetchModelsBtn.Content = "拉取列表";
            }
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

        private async void TestConnection_Click(object sender, RoutedEventArgs e) =>
            await RunConnectionTestAsync(interactive: true);

        /// <summary>
        /// 测试连接的实现。
        /// <paramref name="interactive"/> 为 false 时不出弹窗 —— 弹窗会阻塞线程，
        /// 想让它能被无头回归调用，「要不要说话」必须从逻辑里摘出来。
        /// </summary>
        private async Task<bool> RunConnectionTestAsync(bool interactive)
        {
            var config = CurrentFormConfig;

            if (string.IsNullOrWhiteSpace(config.ApiKey))
            {
                if (interactive)
                    MessageBox.Show("请输入 API Key", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
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

                    // 连接既然通了，顺手把真实模型清单也拉回来 ——
                    // 内置型号表可能已经过期，省得用户再去点一次「拉取列表」
                    var fetched = await FetchModelsAsync(interactive: false);

                    if (interactive)
                        MessageBox.Show(
                            "连接成功！API 配置可用。\n\n"
                            + $"服务商：{preset?.Name ?? "自定义"}\n"
                            + $"协议：  {ApiProviders.WireLabel(ApiProviders.ResolveWire(config))}\n"
                            + $"认证：  {authLabel}\n"
                            + $"模型：  {config.Model}\n"
                            + (fetched ? $"型号：  已拉到 {_allModels.Count} 个可用型号，见右侧下拉"
                                       : "型号：  未能自动拉取清单（可点「拉取列表」重试）"),
                            "成功", MessageBoxButton.OK, MessageBoxImage.Information);

                    return true;
                }
                else
                {
                    var err = await response.Content.ReadAsStringAsync();

                    // 最常撞的一种失败：模型名不对。此时把「去拉真实清单」这条出路直接指出来
                    var hint = LooksLikeBadModel(err, response.StatusCode)
                        ? "\n\n—— 这多半是模型名称不对。点「拉取列表」可以取到该服务商当前真实可用的模型名。"
                        : "";

                    // 失败原因同时落到型号那一行：弹窗关掉就没了，行内提示能留着对照着改
                    UpdateModelStatus($"连接失败（{(int)response.StatusCode}）{FirstLine(err)}");

                    if (interactive)
                        MessageBox.Show($"API 返回错误 ({(int)response.StatusCode}):\n{err}{hint}", "连接失败",
                            MessageBoxButton.OK, MessageBoxImage.Warning);

                    return false;
                }
            }
            catch (Exception ex)
            {
                UpdateModelStatus("连接失败：" + FirstLine(ex.Message));

                if (interactive)
                    MessageBox.Show($"无法连接到 API:\n{ex.Message}", "连接失败",
                        MessageBoxButton.OK, MessageBoxImage.Error);

                return false;
            }
            finally
            {
                TestButton.IsEnabled = true;
                TestButton.Content = "测试连接";
            }
        }

        /// <summary>压成一行并把长度截断（用于不用换行的提示位）。</summary>
        private static string FirstLine(string? text, int max = 160)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            var s = text.Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length <= max ? s : s[..max] + "…";
        }

        /// <summary>响应内容像是在说「这个模型不存在」——用来把用户直接引到「拉取列表」。</summary>
        private static bool LooksLikeBadModel(string? body, System.Net.HttpStatusCode status)
        {
            if (string.IsNullOrWhiteSpace(body)) return status == System.Net.HttpStatusCode.NotFound;

            var s = body.ToLowerInvariant();
            if (!s.Contains("model") && !s.Contains("模型")) return false;

            return s.Contains("not found") || s.Contains("not exist") || s.Contains("does not exist")
                || s.Contains("invalid model") || s.Contains("no such model") || s.Contains("unknown model")
                || s.Contains("模型不存在") || s.Contains("无此模型") || s.Contains("模型名称");
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

        // ==================================================================
        // 技能编辑页
        // ==================================================================

        private static readonly (string Key, string Display)[] SkillTaskOptions =
        {
            (AiPrompts.Keys.Continue, "续写"),
            (AiPrompts.Keys.Polish, "润色"),
            (AiPrompts.Keys.Name, "人名生成"),
            (AiPrompts.Keys.Chat, "万能聊天"),
            (AiPrompts.Keys.Outline, "全文大纲"),
            (AiPrompts.Keys.Character, "人物设定"),
            (AiPrompts.Keys.Background, "背景设定"),
            (AiPrompts.Keys.ChapterOutline, "章节大纲"),
            (AiPrompts.Keys.WriteStyle, "文风设定"),
            (AiPrompts.Keys.SettingBook, "设定集章节"),
        };

        private sealed class ContractChoice
        {
            public string Key { get; }
            public string Display { get; }
            public ContractChoice(string key, string display) { Key = key; Display = display; }
        }

        private void InitializeSkillEditor()
        {
            SkillContractCombo.ItemsSource = new List<ContractChoice>
            {
                new("", "沿用默认（按功能）"),
                new(AiPrompts.Keys.CreativeOutput, "创作类（可入稿正文）"),
                new(AiPrompts.Keys.StructuredOutput, "结构化（分条组织）"),
            };
            SkillContractCombo.SelectedIndex = 0;

            TaskCheckBoxList.ItemsSource = SkillTaskOptions.Select(o =>
                new { o.Key, o.Display }).ToList();

            // 适用方案勾选框：内置 4 方案 + 用户自定义方案，全部列出
            PresetCheckBoxList.ItemsSource = AiPrompts.PresetOptions(_promptStore).ToList();

            _skills = NovelSkillStore.Load(_skillDir);
            RefreshSkillList();
        }

        private void RefreshSkillList()
        {
            _currentSkill = null;
            _loadingSkillForm = true;
            try
            {
                SkillListBox.ItemsSource = _skills;
                if (_skills.Count > 0) SkillListBox.SelectedIndex = 0;
                else SetSkillFormEnabled(false);
            }
            finally { _loadingSkillForm = false; }
        }

        private void SetSkillFormEnabled(bool enabled)
        {
            SkillNameBox.IsEnabled = enabled;
            SkillDescBox.IsEnabled = enabled;
            SkillPromptBox.IsEnabled = enabled;
            SkillHintBox.IsEnabled = enabled;
            SkillContractCombo.IsEnabled = enabled;
            SaveSkillBtn.IsEnabled = enabled;
            DeleteSkillBtn.IsEnabled = enabled;
            ExportSkillBtn.IsEnabled = enabled;
            SkillBuiltInTag.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        }

        private void SkillListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loadingSkillForm) return;
            if (SkillListBox.SelectedItem is not NovelSkill skill) { _currentSkill = null; return; }
            _currentSkill = skill;
            _loadingSkillForm = true;
            try
            {
                SkillNameBox.Text = skill.Name;
                SkillDescBox.Text = skill.Description;
                SkillPromptBox.Text = skill.TaskPrompt;
                SkillHintBox.Text = skill.InputHint;
                SkillBuiltInTag.Visibility = skill.IsBuiltIn ? Visibility.Visible : Visibility.Collapsed;
                SkillContractCombo.SelectedItem = SkillContractCombo.Items
                    .Cast<ContractChoice>().FirstOrDefault(c => c.Key == (skill.OutputContract ?? ""))
                    ?? SkillContractCombo.Items[0];

                _taskChecks.Clear();
                foreach (var k in skill.AppliesTo) _taskChecks[k] = true;
                SetTaskCheckBoxes();

                _presetChecks.Clear();
                foreach (var p in skill.Presets) _presetChecks[p] = true;
                SetPresetCheckBoxes();

                SetSkillFormEnabled(!skill.IsBuiltIn);
                SkillNameBox.IsReadOnly = skill.IsBuiltIn;
                SkillDescBox.IsReadOnly = skill.IsBuiltIn;
                SkillPromptBox.IsReadOnly = skill.IsBuiltIn;
                SkillHintBox.IsReadOnly = skill.IsBuiltIn;
            }
            finally { _loadingSkillForm = false; }
        }

        /// <summary>按 _taskChecks 同步任务勾选框（容器未生成时跳过）。</summary>
        private void SetTaskCheckBoxes()
        {
            TaskCheckBoxList.UpdateLayout();
            foreach (var o in TaskCheckBoxList.Items)
            {
                var cp = TaskCheckBoxList.ItemContainerGenerator.ContainerFromItem(o) as ContentPresenter;
                var cb = cp?.ContentTemplate?.FindName("TaskCheck", cp) as CheckBox;
                if (cb == null) continue;
                var key = (string)o.GetType().GetProperty("Key")!.GetValue(o)!;
                cb.Tag = key;
                cb.IsChecked = _taskChecks.ContainsKey(key);
                cb.IsEnabled = _currentSkill is { IsBuiltIn: false };
            }
        }

        private void TaskCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_loadingSkillForm || sender is not CheckBox cb || cb.Tag == null) return;
            _taskChecks[(string)cb.Tag] = cb.IsChecked == true;
        }

        private readonly Dictionary<string, bool> _presetChecks = new(StringComparer.Ordinal); // 方案 Id → 勾选

        /// <summary>按 _presetChecks 同步方案勾选框（容器未生成时跳过）。</summary>
        private void SetPresetCheckBoxes()
        {
            PresetCheckBoxList.UpdateLayout();
            foreach (var o in PresetCheckBoxList.Items)
            {
                var cp = PresetCheckBoxList.ItemContainerGenerator.ContainerFromItem(o) as ContentPresenter;
                var cb = cp?.ContentTemplate?.FindName("PresetCheck", cp) as CheckBox;
                if (cb == null) continue;
                var id = (string)o.GetType().GetProperty("Id")!.GetValue(o)!;
                cb.Tag = id;
                cb.IsChecked = _presetChecks.ContainsKey(id);
                cb.IsEnabled = _currentSkill is { IsBuiltIn: false };
            }
        }

        private void PresetCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_loadingSkillForm || sender is not CheckBox cb || cb.Tag == null) return;
            _presetChecks[(string)cb.Tag] = cb.IsChecked == true;
        }

        private void SkillContractCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // 选中态在保存时直接读 Combo，无需联动处理
        }

        private void NewSkill_Click(object sender, RoutedEventArgs e)
        {
            _currentSkill = new NovelSkill { Id = Guid.NewGuid().ToString("N") };
            SkillListBox.SelectedItem = null;
            _loadingSkillForm = true;
            try
            {
                SkillNameBox.Text = "";
                SkillDescBox.Text = "";
                SkillPromptBox.Text = "";
                SkillHintBox.Text = "";
                SkillBuiltInTag.Visibility = Visibility.Collapsed;
                SkillContractCombo.SelectedIndex = 0;
                _taskChecks.Clear();
                _presetChecks.Clear();
                SetTaskCheckBoxes();
                SetPresetCheckBoxes();
                SetSkillFormEnabled(true);
                SkillNameBox.IsReadOnly = false;
                SkillNameBox.Focus();
            }
            finally { _loadingSkillForm = false; }
        }

        private void SaveSkill_Click(object sender, RoutedEventArgs e)
        {
            var name = SkillNameBox.Text.Trim();
            var prompt = SkillPromptBox.Text.Trim();
            if (name.Length == 0 || prompt.Length == 0)
            {
                MessageBox.Show("技能名称与任务提示词都是必填的。", "无法保存",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (_currentSkill is { IsBuiltIn: true }) return; // 内置不可改

            var skill = _currentSkill ?? new NovelSkill { Id = Guid.NewGuid().ToString("N") };
            skill.Name = name;
            skill.Description = SkillDescBox.Text.Trim();
            skill.TaskPrompt = prompt;
            skill.InputHint = SkillHintBox.Text.Trim();
            skill.OutputContract = (SkillContractCombo.SelectedItem as ContractChoice)?.Key is { Length: > 0 } k
                ? k : null;
            skill.AppliesTo = _taskChecks.Where(kv => kv.Value).Select(kv => kv.Key).ToList();
            skill.Presets = _presetChecks.Where(kv => kv.Value).Select(kv => kv.Key).ToList();

            if (NovelSkillStore.AddOrUpdate(_skills, skill))
            {
                NovelSkillStore.Save(_skillDir, _skills);
                RefreshSkillList();
            }
        }

        private void DeleteSkill_Click(object sender, RoutedEventArgs e)
        {
            if (_currentSkill is not { IsBuiltIn: false } skill) return;
            if (MessageBox.Show($"删除技能「{skill.Name}」？", "删除确认",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            if (NovelSkillStore.Delete(_skills, skill.Id))
            {
                NovelSkillStore.Save(_skillDir, _skills);
                RefreshSkillList();
            }
        }

        private void ExportSkill_Click(object sender, RoutedEventArgs e)
        {
            if (_currentSkill == null) return;
            var dialog = new SaveFileDialog
            {
                Filter = "JSON 文件 (*.json)|*.json",
                FileName = $"{Sanitize(_currentSkill.Name)}.skill.json"
            };
            if (dialog.ShowDialog() != true) return;

            File.WriteAllText(dialog.FileName, NovelSkillStore.ExportJson(_currentSkill), new UTF8Encoding(true));
        }

        private void ImportSkill_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog { Filter = "JSON 文件 (*.json)|*.json" };
            if (dialog.ShowDialog() != true) return;

            var imported = NovelSkillStore.ParseImport(File.ReadAllText(dialog.FileName));
            if (imported.Count == 0)
            {
                MessageBox.Show("没有可导入的技能：文件为空、格式不对，或与内置技能重名。", "导入失败",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            foreach (var s in imported) NovelSkillStore.AddOrUpdate(_skills, s);
            NovelSkillStore.Save(_skillDir, _skills);
            RefreshSkillList();
            MessageBox.Show($"已导入 {imported.Count} 个技能。", "导入完成",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private static string Sanitize(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
            return cleaned.Length == 0 ? "skill" : cleaned;
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
