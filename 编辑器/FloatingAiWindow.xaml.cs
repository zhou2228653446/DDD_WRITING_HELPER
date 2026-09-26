using System;
using System.ComponentModel;
using System.Windows;

namespace 编辑器
{
    public partial class FloatingAiWindow : HandyControl.Controls.Window
    {
        /// <summary>
        /// 窗口正在关闭时触发（标题栏 X / Alt+F4 / 被主窗口连带关闭）。
        /// 【契约】处理方此刻正处在 Closing 流程中，只能把内容搬走并复位状态，
        /// 严禁再调用 Show / Close / ShowDialog / 修改 Visibility——那会抛
        /// InvalidOperationException（"在窗口关闭期间……"）。
        /// 需要主动关窗的路径请由 MainWindow 侧解绑本事件后再 Close()。
        /// </summary>
        public event Action? ClosingRequested;

        public FloatingAiWindow()
        {
            InitializeComponent();
        }

        public void SetContent(AiPanelControl panel)
        {
            HostGrid.Children.Clear();
            HostGrid.Children.Add(panel);
        }

        public void ClearContent()
        {
            HostGrid.Children.Clear();
        }

        private void Window_Closing(object? sender, CancelEventArgs e)
        {
            // 关闭窗口 = 合并回主窗口
            ClosingRequested?.Invoke();
        }
    }
}
