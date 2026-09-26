using System.Windows;
using 编辑器.Services;

namespace 编辑器
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            // 全局动效注册（窗口登场 / 菜单 · 下拉弹出 / 树展开 / 标签切换）。
            // 放在 base 之前：StartupUri 指定的主窗口在 base.OnStartup 之后才创建。
            Motion.Register();
            base.OnStartup(e);
        }
    }
}
