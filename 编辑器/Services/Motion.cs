using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace 编辑器.Services
{
    /// <summary>
    /// 全局动效注册中心。
    ///
    /// 为什么用 EventManager.RegisterClassHandler 而不是在 XAML 里写隐式样式：
    ///   App.xaml 把 Themes/Tokens.xaml 合并排在 HandyControl 之后（优先级更高），
    ///   于是 Tokens.xaml 里任何 <c>&lt;Style TargetType="ComboBox"&gt;</c> 这类无 key 的隐式样式
    ///   都会**整体替换**掉 HandyControl 的控件外观，把皮肤打回裸模板。
    ///   类级事件钩子只在事件发生时补一段动画，不碰模板、不碰样式，天然免疫这个问题，
    ///   而且一处注册即对全部窗口（含后续 new 出来的对话框）生效。
    ///
    /// 两种弹出语义（别混用）：
    ///   · PlayUnfold —— 卷轴式：面板沿**顶边**向下铺开。菜单、下拉、树子级都用这个。
    ///     面板本来就在触发控件正下方，若再用"从下往上滑"的位移，等于从更下方飞上来，
    ///     方向反直觉、观感毛躁。
    ///   · PlayDropIn —— 落入式：整体带位移滑入。只给通知条这类"从窗口顶部降下"的元素。
    ///
    /// 所有动画只作用于 Opacity 与 RenderTransform —— 不触发布局重算，
    /// 编辑区滚动、输入法候选框、长文本重排都不受影响。
    /// </summary>
    public static class Motion
    {
        private static bool _registered;

        /// <summary>
        /// 诊断用：记录各类动效实际播放次数。
        /// 动效失效是"静默"的（不报错、不缺功能，只是少了点手感），
        /// 有计数器才能在回归时一眼看出"这一类动画从来没跑过"。
        /// </summary>
        public static readonly System.Collections.Generic.Dictionary<string, int> PlayCount = new();

        private static void Count(string key)
            => PlayCount[key] = PlayCount.TryGetValue(key, out var v) ? v + 1 : 1;

        // ---------- 节奏（与 Themes/Tokens.xaml 的动效 token 保持一致） ----------
        // 微交互要快（按下去必须立刻有反馈），弹出/展开要慢一档
        // —— 弹出是"内容登场"，太快会显得"啪"地弹出来，反而突兀。
        private static readonly Duration DurFast = new(TimeSpan.FromMilliseconds(140));  // 微交互（未在下方直接使用，与 token 对齐）
        private static readonly Duration DurFade = new(TimeSpan.FromMilliseconds(240));  // 淡入
        private static readonly Duration DurPop = new(TimeSpan.FromMilliseconds(320));   // 弹出 / 展开
        private static readonly Duration DurOut = new(TimeSpan.FromMilliseconds(200));   // 淡出

        /// <summary>
        /// 卷轴展开的起始压缩比例。
        /// 太小（0.3 那种）会把面板里的文字压扁得难看，太大又看不出"展开"这个动作。
        /// 0.72 ≈ 从七成高度铺开，变形不易察觉而"落下"的过程清晰可感。
        /// </summary>
        private const double UnfoldFrom = 0.72;

        private static readonly CubicEase EaseOut = CreateEase();

        private static CubicEase CreateEase()
        {
            var e = new CubicEase { EasingMode = EasingMode.EaseOut };
            e.Freeze();
            return e;
        }

        /// <summary>在 App.OnStartup 里调用一次即可。</summary>
        public static void Register()
        {
            if (_registered) return;
            _registered = true;

            // 窗口登场：淡入 + 内容轻微自上方落位
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
                new RoutedEventHandler(OnWindowLoaded));

            // 右键菜单：卷轴展开
            EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent,
                new RoutedEventHandler(OnContextMenuOpened));

            // 菜单栏的子菜单（文件 / 编辑 / ……）：卷轴展开
            EventManager.RegisterClassHandler(typeof(MenuItem), MenuItem.SubmenuOpenedEvent,
                new RoutedEventHandler(OnSubmenuOpened));

            // 下拉框（API 方案切换、润色预设、字号选择……）
            // ⚠ ComboBox.DropDownOpenedEvent 是 internal，无法直接做类级注册；
            //   而 RegisterClassHandler(typeof(ComboBox), LoadedEvent, …) 实测不生效
            //   （Loaded 的类处理器只在 Window 这类根元素上被触发）。
            //   所以改走"窗口加载时遍历可视化树逐个挂接"——窗口的 Loaded 钩子是可靠的。
            //   （见 OnWindowLoaded → HookComboBoxes）

            // 章节树展开：子级卷轴展开
            EventManager.RegisterClassHandler(typeof(TreeViewItem), TreeViewItem.ExpandedEvent,
                new RoutedEventHandler(OnTreeItemExpanded));

            // 标签页切换：新页内容淡入
            EventManager.RegisterClassHandler(typeof(TabControl), Selector.SelectionChangedEvent,
                new RoutedEventHandler(OnTabSelectionChanged));
        }

        // ==================== 事件处理 ====================

        private static void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is not Window win) return;
            Count("window");

            // 窗口可视化树已经生成，把窗口里所有下拉框统一挂上事件
            Guard(() => HookComboBoxes(win));

            Guard(() =>
            {
                // 整窗淡入。Windows 8+ 下 WPF 走分层窗口实现，无需 AllowsTransparency。
                win.BeginAnimation(UIElement.OpacityProperty, Fade(0, 1, DurFade));

                // 内容自上方轻落 12px。用负 offset —— 是"降下来"而不是"从下往上顶"，
                // 只在内容没有自带 RenderTransform 时叠加。
                if (win.Content is FrameworkElement content)
                {
                    var t = EnsureTransform(content, () => new TranslateTransform());
                    if (t != null)
                        PlayOnTransform(t, TranslateTransform.YProperty, -12, 0);
                }
            });
        }

        private static void OnContextMenuOpened(object sender, RoutedEventArgs e)
        {
            if (sender is not ContextMenu cm) return;
            Count("contextmenu");
            PlayUnfold(cm);
        }

        private static void OnSubmenuOpened(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem mi) return;
            // 子菜单的实际内容在 MenuItem 的 Popup 里
            if (FindDescendant<Popup>(mi)?.Child is FrameworkElement child)
            {
                Count("submenu");
                PlayUnfold(child);
            }
        }

        /// <summary>遍历可视化树，给所有 ComboBox 挂上下拉动画（先解绑再挂，可重复调用）。</summary>
        private static void HookComboBoxes(DependencyObject root)
        {
            if (root is ComboBox combo)
            {
                combo.DropDownOpened -= OnDropDownOpened;
                combo.DropDownOpened += OnDropDownOpened;
            }

            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
                HookComboBoxes(VisualTreeHelper.GetChild(root, i));
        }

        private static void OnDropDownOpened(object? sender, EventArgs e)
        {
            if (sender is not ComboBox combo) return;
            // ⚠ 实测：DropDownOpened 触发时 Popup 的 IsOpen 仍是 false（WPF 先抛事件、后开面板），
            // 面板此刻还没进入可视化树，直接挂动画会被丢弃。
            // 推迟到布局完成后再动画 —— 那时面板已经落位。
            combo.Dispatcher.BeginInvoke(new Action(() => AnimateDropDown(combo)), DispatcherPriority.Loaded);
        }

        private static void AnimateDropDown(ComboBox combo)
        {
            if (FindDescendant<Popup>(combo)?.Child is FrameworkElement child)
            {
                Count("dropdown");
                PlayUnfold(child);
            }
        }

        private static void OnTreeItemExpanded(object sender, RoutedEventArgs e)
        {
            if (sender is not TreeViewItem item) return;
            // TreeViewItem.Card 模板里的子级宿主叫 ItemsHost
            if (item.Template?.FindName("ItemsHost", item) is FrameworkElement host)
            {
                Count("tree");
                PlayUnfold(host);
            }
        }

        private static void OnTabSelectionChanged(object sender, RoutedEventArgs e)
        {
            if (sender is not TabControl tabs) return;
            // 切页后新生成的子级里可能有下拉框，补挂一次
            Guard(() => HookComboBoxes(tabs));

            // TabControl 模板约定：承载当前页的 ContentPresenter 其 ContentSource 为 SelectedContent
            var host = FindDescendant<ContentPresenter>(tabs, cp => cp.ContentSource as string == "SelectedContent");
            if (host != null)
            {
                Count("tab");
                // 纯淡入：切页是"内容替换"，叠位移动画会和页内滚动位置打架
                PlayFadeIn(host);
            }
        }

        // ==================== 动画工厂 ====================

        /// <summary>
        /// 卷轴式展开：面板沿**顶边**向下铺开（ScaleY 由小放大），同时淡入。
        ///
        /// 为什么是 ScaleY 而不是 TranslateY：
        ///   下拉面板就贴在触发控件正下方。用"从下往上滑"的位移，等于让面板从更下方
        ///   飞上来——方向与"面板向下展开"的心理模型相反，观感上像被弹了一下。
        ///   ScaleY 锚在顶边，视觉上是"从上往下掀开"，与菜单/下拉的预期一致。
        ///
        /// RenderTransformOrigin 默认就是 (0,0)（元素左上角），这里显式写出来表明是刻意为之。
        /// 面板宽度不受影响（ScaleX 恒为 1），且纯渲染变换不触发布局重算。
        /// ScaleY 的基值是 1、与动画终值相同，所以 FillBehavior.Stop 收尾后既不跳变也不残留。
        /// </summary>
        public static void PlayUnfold(FrameworkElement el, double from = UnfoldFrom)
        {
            Guard(() =>
            {
                el.RenderTransformOrigin = new Point(0, 0);

                var sc = EnsureTransform(el, () => new ScaleTransform(1, 1));
                if (sc == null) return;

                el.BeginAnimation(UIElement.OpacityProperty, Fade(0, 1, DurFade));
                PlayOnTransform(sc, ScaleTransform.ScaleYProperty, from, 1);
            });
        }

        /// <summary>
        /// 纯淡入。用于标签页切换这类"内容替换"场景。
        /// </summary>
        public static void PlayFadeIn(FrameworkElement el)
            => Guard(() => el.BeginAnimation(UIElement.OpacityProperty, Fade(0, 1, DurFade)));

        /// <summary>
        /// 淡入 + 整体滑入（offset 传负数 = 从上方落下）。
        /// 只用于通知条这种"从窗口顶部降下"的元素；菜单/下拉请用 <see cref="PlayUnfold"/>。
        /// </summary>
        public static void PlayDropIn(FrameworkElement el, double offset = -10)
        {
            Guard(() =>
            {
                el.BeginAnimation(UIElement.OpacityProperty, Fade(0, 1, DurFade));
                var t = EnsureTransform(el, () => new TranslateTransform());
                if (t != null)
                    PlayOnTransform(t, TranslateTransform.YProperty, offset, 0);
            });
        }

        /// <summary>淡出，动画结束后回调（用于「淡出后再真正隐藏」）。</summary>
        public static void PlayFadeOut(FrameworkElement el, Action? onDone = null)
        {
            Guard(() =>
            {
                var anim = new DoubleAnimation(1, 0, DurOut)
                {
                    EasingFunction = EaseOut,
                    FillBehavior = FillBehavior.HoldEnd
                };
                anim.Completed += (_, _) =>
                {
                    // 先摘掉动画（属性回到基值 1），再执行隐藏，避免淡出结束后闪回
                    el.BeginAnimation(UIElement.OpacityProperty, null);
                    onDone?.Invoke();
                };
                el.BeginAnimation(UIElement.OpacityProperty, anim);
            });
        }

        private static DoubleAnimation Fade(double from, double to, Duration d) => new(from, to, d)
        {
            EasingFunction = EaseOut,
            FillBehavior = FillBehavior.Stop
        };

        /// <summary>数值补间。用于 Opacity / ScaleY / TranslateY 这类 double 属性。</summary>
        private static DoubleAnimation Tween(double from, double to, Duration d) => new(from, to, d)
        {
            EasingFunction = EaseOut,
            FillBehavior = FillBehavior.Stop
        };

        /// <summary>
        /// 在变换（Freezable）的某个属性上播放补间，并在自然结束后摘除时钟。
        ///
        /// ⚠ 为什么要显式摘：FillBehavior.Stop 只保证"值回到基值"，对 Freezable 上的属性
        ///   它**不会**把时钟一并移除（实测 ScaleY 已回 1，HasAnimatedProperties 却仍为 true）。
        ///   时钟留着不影响外观，但会让"这个元素是否还在动画中"的判断长期失真，
        ///   也会在被复用/复测时留下噪声，所以收尾时主动摘掉。
        ///
        /// 摘除前先比对当前值：只有属性确实停在终值（= 没有更新的动画接管）时才摘，
        /// 避免快速连续触发时旧动画的回调把刚开始的新动画误摘掉。
        /// </summary>
        private static void PlayOnTransform<T>(T transform, DependencyProperty prop,
            double from, double to) where T : Animatable
        {
            var anim = Tween(from, to, DurPop);
            anim.Completed += (_, _) => Guard(() =>
            {
                if (Math.Abs((double)transform.GetValue(prop) - to) < 1e-9)
                    transform.BeginAnimation(prop, null);
            });
            transform.BeginAnimation(prop, anim);
        }

        /// <summary>
        /// 确保元素挂着一个 T 类型的变换并返回它；无法安全添加时返回 null（宁可不动它）。
        ///
        /// ⚠ 坑：FrameworkElement.RenderTransform 的**默认值是 Transform.Identity，不是 null**，
        /// 只判 null 会永远进不了"创建"分支，动画会静默失效。必须显式判等 Identity。
        /// 元素已挂着别的单一变换（旋转/位移）时，包一层 TransformGroup 把两者都保留，
        /// 绝不覆盖别人写好的 RenderTransform。
        /// </summary>
        private static T? EnsureTransform<T>(FrameworkElement el, Func<T> create) where T : Transform
        {
            var rt = el.RenderTransform;

            if (rt is T hit) return hit;

            if (rt is TransformGroup group)
            {
                foreach (var child in group.Children)
                    if (child is T inGroup) return inGroup;
                // 组里没有就追加一个，原有变换照旧生效
                var appended = create();
                group.Children.Add(appended);
                return appended;
            }

            var fresh = create();

            if (rt == null || ReferenceEquals(rt, Transform.Identity))
            {
                el.RenderTransform = fresh;
                return fresh;
            }

            // 已有别的单一变换：包成组后追加，两个都保留
            var wrapped = new TransformGroup();
            wrapped.Children.Add(rt);
            wrapped.Children.Add(fresh);
            el.RenderTransform = wrapped;
            return fresh;
        }

        // ==================== 工具 ====================

        private static T? FindDescendant<T>(DependencyObject? root, Func<T, bool>? match = null)
            where T : DependencyObject
        {
            if (root == null) return null;
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T hit && (match == null || match(hit))) return hit;
                var found = FindDescendant(child, match);
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>动画失败绝不能影响功能：任何异常都吞掉，界面照常可用（只是少了点动效）。</summary>
        private static void Guard(Action action)
        {
            try { action(); } catch { }
        }
    }
}
