using System.Windows;

namespace 编辑器
{
    /// <summary>
    /// 「支持作者」窗口：退出时弹出，请求给 GitHub 仓库点 Star。
    ///
    /// 只在退出确认流程之后出现；用户勾选「不再显示」后写进
    /// appearance.json（SuppressSupportPrompt），以后退出直接走。
    /// </summary>
    public partial class SupportWindow : HandyControl.Controls.Window
    {
        /// <summary>仓库地址（「关于」窗口也用它）。</summary>
        public const string RepoUrl = "https://github.com/zhou2228653446/DDD_WRITING_HELPER";

        /// <summary>关闭时是否需要保存「不再显示」。主窗口按需读取勾选状态。</summary>
        public bool SuppressRequested => SuppressCheck.IsChecked == true;

        public SupportWindow()
        {
            InitializeComponent();
        }

        private void OpenGithub_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = RepoUrl,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                HandyControl.Controls.MessageBox.Error(
                    "打开浏览器失败：" + ex.Message + "\n\n仓库地址已复制，可手动粘贴到浏览器。",
                    "打开失败");
            }
            // 无论浏览器开没开成，都把链接放剪贴板兜底
            TryCopyUrl();
            DialogResult = true;
        }

        private void Skip_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        private void CopyUrl_Click(object sender, RoutedEventArgs e)
        {
            TryCopyUrl();
            HandyControl.Controls.MessageBox.Success("仓库地址已复制到剪贴板。", "复制");
        }

        private void TryCopyUrl()
        {
            try { Clipboard.SetText(RepoUrl); } catch { /* 剪贴板被占用时不打断退出 */ }
        }
    }
}
