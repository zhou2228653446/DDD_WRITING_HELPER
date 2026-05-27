using System;
using System.ComponentModel;
using System.Windows;

namespace 编辑器
{
    public partial class FloatingAiWindow : Window
    {
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
