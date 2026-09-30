using System.Windows;

namespace 编辑器
{
    /// <summary>
    /// AI 审稿报告窗口：只读展示问题清单。
    /// 刻意不提供「一键应用修改」——审稿结论必须经作者逐条核对，
    /// 自动改稿等于把创作主权让给一个会幻觉的模型。
    /// </summary>
    public partial class ReviewResultWindow : HandyControl.Controls.Window
    {
        private readonly string _report;

        /// <param name="title">窗口标题主体（调用方决定前缀，如「审稿报告 · 第3章 xx」）。</param>
        /// <param name="report">报告正文（只读展示）。</param>
        public ReviewResultWindow(string title, string report)
        {
            InitializeComponent();
            _report = report;
            TitleText.Text = title;
            ReportBox.Text = report;
            MetaText.Text = $"生成时间 {DateTime.Now:HH:mm} · {report.Length} 字 · 只读，不写回正文";
        }

        private void Copy_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(_report);
                HandyControl.Controls.MessageBox.Success("报告已复制到剪贴板。", "复制");
            }
            catch (Exception ex)
            {
                HandyControl.Controls.MessageBox.Error("复制失败：" + ex.Message, "复制");
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
