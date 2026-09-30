using System.Windows;

namespace 编辑器
{
    /// <summary>
    /// 退出确认窗口：点关闭/退出时弹出。
    ///
    /// 一个窗口搞定三件事：
    ///   ① 确认是否退出（取消可反悔）
    ///   ② 有未保存项目时提供「保存并退出」
    ///   ③ 常驻展示 GitHub 仓库与作者主页（Star 请求不再单独弹窗）
    ///
    /// DialogResult 约定：null/true = 允许关闭，false = 用户取消退出。
    /// </summary>
    public partial class ExitConfirmWindow : HandyControl.Controls.Window
    {
        /// <summary>仓库地址（「关于」窗口也用它）。</summary>
        public const string RepoUrl = "https://github.com/zhou2228653446/DDD_WRITING_HELPER";

        /// <summary>作者主页。留空则界面上隐藏「主页」行。</summary>
        public const string HomeUrl = "";

        /// <summary>用户是否选择了「保存并退出」。</summary>
        public bool SaveRequested { get; private set; }

        public ExitConfirmWindow(string? projectName = null)
        {
            InitializeComponent();

            RepoText.Text = RepoUrl;
            if (string.IsNullOrWhiteSpace(HomeUrl))
            {
                HomeLabel.Visibility = Visibility.Collapsed;
                HomeText.Visibility = Visibility.Collapsed;
                OpenHomeBtn.Visibility = Visibility.Collapsed;
            }
            else
            {
                HomeText.Text = HomeUrl;
            }

            // 无项目（或未改动场景由调用方决定）：隐藏保存行与「保存并退出」
            if (string.IsNullOrWhiteSpace(projectName))
            {
                SavePanel.Visibility = Visibility.Collapsed;
                SaveExitBtn.Visibility = Visibility.Collapsed;
            }
            else
            {
                SaveQuestionText.Text = $"是否保存项目「{projectName}」的更改？";
            }
        }

        // ---- 退出决定 ----

        private void SaveExit_Click(object sender, RoutedEventArgs e)
        {
            SaveRequested = true;
            DialogResult = true;
        }

        private void ExitOnly_Click(object sender, RoutedEventArgs e) => DialogResult = true;

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        // ---- 链接操作 ----

        private void OpenGithub_Click(object sender, RoutedEventArgs e) => OpenUrl(RepoUrl);

        private void CopyRepo_Click(object sender, RoutedEventArgs e) => Copy(RepoUrl);
        private void CopyHome_Click(object sender, RoutedEventArgs e) => Copy(HomeUrl);

        private static void OpenUrl(string url)
        {
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
                Copy(url);   // 打不开就把链接放剪贴板兜底
                HandyControl.Controls.MessageBox.Error(
                    "打开浏览器失败：" + ex.Message + "\n\n链接已复制，可手动粘贴到浏览器。", "打开失败");
            }
        }

        private static void Copy(string text)
        {
            try { Clipboard.SetText(text); } catch { /* 剪贴板被占用时不打断 */ }
        }
    }
}
