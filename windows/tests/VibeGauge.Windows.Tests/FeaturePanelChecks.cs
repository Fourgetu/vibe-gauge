using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VibeGauge.Core;
using VibeGauge.Windows.Services;
using VibeGauge.Windows.ViewModels;
using Xunit;

namespace VibeGauge.Windows.Tests;

internal static class FeaturePanelChecks
{
    internal static void Verify()
    {
        var root = Path.Combine(Path.GetTempPath(), "vibegauge-settings-ui-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root, Path.Combine(root, "local"));
        using var proxy = new ProxyManager(paths);
        using var vm = new DashboardViewModel(new DashboardCoordinator(paths, proxy), new StartupRegistrar(), proxy, new ProxySettings());
        vm.Dispose();
        var panel = new FeatureSettingsPanel(vm);
        var scroll = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var shell = new DockPanel { Margin = new Thickness(14) };
        var languages = new LanguageSelector { DataContext = vm, HorizontalAlignment = HorizontalAlignment.Right };
        var footer = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom); shell.Children.Add(footer);
        DockPanel.SetDock(languages, Dock.Right); footer.Children.Add(languages);
        var quit = new TextBlock { Text = "退出 VibeGauge", VerticalAlignment = VerticalAlignment.Center, FontSize = 14 };
        quit.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush"); footer.Children.Add(quit);
        shell.Children.Add(scroll);
        var window = new Window { Content = shell, Width = 420, Height = 820, Left = -20000, Top = -20000, ShowInTaskbar = false, ShowActivated = false };
        window.SetResourceReference(Control.BackgroundProperty, "WindowBrush");
        try
        {
            window.Show(); Layout(window);
            Assert.True(panel.IsOverview); Assert.Empty(Descendants<Expander>(panel));
            Assert.Equal(9, Descendants<CheckBox>(panel).Count());
            Assert.Contains("检查软件更新", Text(panel)); Assert.DoesNotContain("Windows 版本更新", Text(panel));
            Assert.False(File.Exists(Path.Combine(paths.LocalDataRoot, "features.json")));
            foreach (var language in new[] { "zh", "en" })
            foreach (var light in new[] { false, true })
            foreach (var width in new[] { 420, 520 })
            {
                UiLocalization.SetLanguage(language); window.Width = width;
                ((ThemePalette)System.Windows.Application.Current.FindResource("ThemePalette")).IsLight = light;
                scroll.ScrollToTop(); Layout(window);
                Assert.True(scroll.ExtentWidth <= scroll.ViewportWidth + 1);
                var toggles = Descendants<CheckBox>(panel).ToArray();
                var rightEdges = toggles.Select(x => x.TranslatePoint(new Point(x.ActualWidth, 0), panel).X).ToArray();
                Assert.InRange(rightEdges.Max() - rightEdges.Min(), 0, 1);
                foreach (var toggle in toggles)
                {
                    Assert.InRange(toggle.ActualWidth, 40, 48);
                    var row = (Grid)toggle.Parent; var labels = row.Children.OfType<StackPanel>().Single();
                    Assert.True(labels.TranslatePoint(new Point(labels.ActualWidth, 0), row).X + 10 <= toggle.TranslatePoint(new Point(), row).X);
                    Assert.True(labels.ActualWidth >= 240);
                    Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(toggle)));
                }
                foreach (var navigation in Descendants<Button>(panel))
                {
                    var row = Assert.IsType<Grid>(navigation.Content);
                    Assert.InRange(navigation.ActualWidth - row.ActualWidth, 0, 1);
                }
                if (language == "en") Assert.DoesNotMatch(@"[\u4e00-\u9fff]", Text(panel));
                if (width == 420) Capture(window, $"settings-{language}-{(light ? "light" : "dark")}.png");
            }
            UiLocalization.SetLanguage("en");
            Assert.Equal("12 calls today", UiLocalization.Text("今日 12 次调用"));
            Assert.Equal(@"C:\用户\工作目录", UiLocalization.Text(@"C:\用户\工作目录"));
            Assert.Equal("我的自定义站点", UiLocalization.Text("我的自定义站点"));
            UiLocalization.SetLanguage("zh"); window.Width = 420; Layout(window);
            var text = new TextBlock(); var source = new ValueSource();
            text.SetBinding(TextBlock.TextProperty, new Binding(nameof(ValueSource.Value)) { Source = source });
            ((StackPanel)panel.Content).Children.Add(text); Layout(window);
            UiLocalization.SetLanguage("en"); Layout(window); Assert.Equal("3 calls today", text.Text);
            source.Value = "今日 8 次调用"; Layout(window); Assert.Equal("8 calls today", text.Text);
            Assert.True(BindingOperations.IsDataBound(text, TextBlock.TextProperty));
            UiLocalization.SetLanguage("zh"); Layout(window); Assert.Equal("今日 8 次调用", text.Text);
            ((StackPanel)panel.Content).Children.Remove(text);
            var egress = Descendants<CheckBox>(panel).Single(x => x.Content as string == "出口变化通知");
            egress.IsChecked = true; egress.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.True(FeaturePreferences.Load(paths).EgressNotifications);
            Assert.False(FeaturePreferences.Load(paths).NetworkDiagnostics);
            Layout(window); Capture(window, "settings-switch-enabled.png");
            scroll.ScrollToBottom(); Layout(window); Capture(window, "settings-details.png");
            foreach (var route in new[] { "通知与阈值", "网络诊断", "API 成本与套餐", "磁盘占用与维护", "软件版本", "官方 CLI", "代理连接" })
            {
                Descendants<Button>(panel).Single(x => AutomationProperties.GetName(x) == route).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Layout(window); Assert.False(panel.IsOverview); Assert.Contains("‹ 返回设置", Text(panel));
                if (route == "通知与阈值")
                {
                    Assert.Equal(4, Descendants<CheckBox>(panel).Count());
                    Assert.Contains("额度已用阈值 %（50–100）", Text(panel));
                }
                if (route == "软件版本")
                {
                    Assert.Contains("VibeGauge 软件更新", Text(panel));
                    Assert.Contains("不检查 Windows 系统更新", Text(panel));
                    Assert.Contains(Descendants<Button>(panel), x => x.Content as string == "立即检查更新");
                    Capture(window, "settings-app-update.png");
                }
                if (route == "API 成本与套餐")
                {
                    var updatePrices = Descendants<Button>(panel).Single(x => x.Name == "OpenRouterPriceUpdate");
                    Assert.True(updatePrices.IsEnabled);
                    Assert.Equal("使用 / 更新 OpenRouter 价格", updatePrices.Content);
                    Assert.Contains("无需密钥", Text(panel));
                    Capture(window, "settings-openrouter-zh.png");
                    UiLocalization.SetLanguage("en"); Layout(window);
                    Assert.Equal("Use / update OpenRouter prices", updatePrices.Content);
                    Capture(window, "settings-openrouter-en.png");
                    UiLocalization.SetLanguage("zh"); Layout(window);
                }
                if (route == "网络诊断")
                {
                    var secret = Descendants<PasswordBox>(panel).Single(x => x.Name == "ClashSecret");
                    var endpoint = Descendants<TextBox>(panel).Single(x => x.Name == "ClashEndpoint");
                    var store = new ClashControllerCredentials(paths, () => null);
                    var save = Descendants<Button>(panel).Single(x => x.Name == "ClashSave");
                    secret.Password = "ui-controller-secret-fixture";
                    Assert.DoesNotContain(secret.Password, Text(panel));
                    save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Layout(window);
                    Assert.Empty(secret.Password); Assert.Equal("ui-controller-secret-fixture", store.ReadSaved(endpoint.Text));
                    Assert.DoesNotContain("ui-controller-secret-fixture", File.ReadAllText(Path.Combine(paths.LocalDataRoot, "features.json")));
                    save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Equal("ui-controller-secret-fixture", store.ReadSaved(endpoint.Text));
                    foreach (var language in new[] { "zh", "en" })
                    foreach (var light in new[] { false, true })
                    {
                        UiLocalization.SetLanguage(language);
                        ((ThemePalette)System.Windows.Application.Current.FindResource("ThemePalette")).IsLight = light;
                        Layout(window);
                        Assert.True(scroll.ExtentWidth <= scroll.ViewportWidth + 1);
                        if (language == "en") Assert.DoesNotMatch(@"[\u4e00-\u9fff]", Text(panel));
                        Capture(window, $"controller-{language}-{(light ? "light" : "dark")}.png");
                    }
                    UiLocalization.SetLanguage("zh");
                    var lan = Descendants<CheckBox>(panel).Single(x => x.Name == "ClashLan");
                    lan.IsChecked = true; lan.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    endpoint.Text = "http://192.168.10.1:9090";
                    var sourceIp = Descendants<TextBox>(panel).Single(x => x.Name == "ClashSourceIp");
                    sourceIp.Text = "192.168.10.8";
                    secret.Password = "router-ui-fixture";
                    save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Layout(window);
                    var routerStore = new ClashControllerCredentials(paths, () => null, allowLan: true);
                    Assert.True(FeaturePreferences.Load(paths).LanClashController);
                    Assert.Equal("192.168.10.8", FeaturePreferences.Load(paths).ClashSourceIp);
                    Assert.Equal("router-ui-fixture", routerStore.ReadSaved(endpoint.Text));
                    Assert.Equal("ui-controller-secret-fixture", store.ReadSaved("http://127.0.0.1:9090"));
                    foreach (var language in new[] { "zh", "en" })
                    foreach (var light in new[] { false, true })
                    {
                        UiLocalization.SetLanguage(language);
                        ((ThemePalette)System.Windows.Application.Current.FindResource("ThemePalette")).IsLight = light;
                        Layout(window);
                        Assert.True(scroll.ExtentWidth <= scroll.ViewportWidth + 1);
                        if (language == "en") Assert.DoesNotMatch(@"[\u4e00-\u9fff]", Text(panel));
                        Capture(window, $"router-{language}-{(light ? "light" : "dark")}.png");
                    }
                    UiLocalization.SetLanguage("zh");
                    Descendants<Button>(panel).Single(x => x.Name == "ClashRemove").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Null(routerStore.ReadSaved(endpoint.Text));
                    endpoint.Text = "http://127.0.0.1:9090";
                    lan.IsChecked = false; lan.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Descendants<Button>(panel).Single(x => x.Name == "ClashRemove").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Null(store.ReadSaved(endpoint.Text));
                }
                Descendants<Button>(panel).Single(x => x.Content as string == "‹ 返回设置").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Layout(window); Assert.True(panel.IsOverview);
            }
            Assert.True(Descendants<CheckBox>(panel).Single(x => x.Content as string == "出口变化通知").IsChecked);
            var english = Descendants<RadioButton>(languages).Single(x => x.Tag as string == "en");
            english.IsChecked = true; english.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Layout(window);
            Assert.Equal("en", FeaturePreferences.Load(paths).Language); Assert.True(UiLocalization.IsEnglish);
            Assert.Contains("Check for app updates", Text(panel));
            VerifyMainWindow(vm);
        }
        finally { UiLocalization.SetLanguage("zh"); window.Close(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private static void VerifyMainWindow(DashboardViewModel vm)
    {
        var main = new MainWindow(vm, autoHide: false) { Width = 420, Height = 860, Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            vm.SelectTab(2); main.Show(); Layout(main);
            var settings = (SettingsPanel)main.FindName("SystemSettings");
            var scroll = Descendants<ScrollViewer>(main).Single(x => x.IsVisible && x.Content is StackPanel);
            foreach (var language in new[] { "zh", "en" })
            foreach (var light in new[] { false, true })
            {
                var footer = Descendants<LanguageSelector>(main).Single();
                var selected = Descendants<RadioButton>(footer).Single(x => x.Tag as string == language);
                selected.IsChecked = true; selected.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                main.SetThemeForCapture(light); Layout(main);
                scroll.ScrollToVerticalOffset(settings.TranslatePoint(new Point(), (UIElement)scroll.Content).Y);
                Layout(main);
                Assert.True(scroll.ExtentWidth <= scroll.ViewportWidth + 1);
                Assert.Equal(9, Descendants<CheckBox>(settings).Count());
                Assert.True(footer.IsVisible);
                foreach (var button in Descendants<ButtonBase>((FrameworkElement)main.FindName("CaptionArea")).Where(x => x.IsVisible))
                {
                    var right = button.TranslatePoint(new Point(button.ActualWidth, 0), main).X;
                    Assert.InRange(right, 1, main.ActualWidth - 5);
                }
                var quit = Descendants<Button>(main).Single(x => x.Content as string == UiLocalization.Text("退出 VibeGauge"));
                Assert.True(quit.TranslatePoint(new Point(quit.ActualWidth, 0), main).X <= footer.TranslatePoint(new Point(), main).X);
                Capture(main, $"main-settings-{language}-{(light ? "light" : "dark")}.png");
            }
        }
        finally
        {
            typeof(MainWindow).GetField("allowClose", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(main, true);
            main.Close();
        }
    }
    private sealed class ValueSource : INotifyPropertyChanged
    {
        private string value = "今日 3 次调用";
        public string Value { get => value; set { this.value = value; PropertyChanged?.Invoke(this, new(nameof(Value))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
    private static string Text(DependencyObject root) => string.Join("\n", Descendants<TextBlock>(root).Select(x => x.Text));
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); if (child is T target) yield return target;
            foreach (var item in Descendants<T>(child)) yield return item;
        }
    }
    private static void Capture(FrameworkElement element, string name)
    {
        var output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, name.StartsWith("router-") || name.StartsWith("controller-")
            ? "../../../../../artifacts/router-controller-20260930/ui" : "../../../../../artifacts/settings-redesign-20260930/ui"));
        Directory.CreateDirectory(output);
        var target = element is Window window ? (FrameworkElement)window.Content : element;
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            var bounds = new Rect(0, 0, target.ActualWidth, target.ActualHeight);
            context.DrawRectangle((Brush)System.Windows.Application.Current.FindResource("WindowBrush"), null, bounds);
            context.DrawRectangle(new VisualBrush(target), null, bounds);
        }
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(target.ActualWidth), (int)Math.Ceiling(target.ActualHeight), 96, 96, PixelFormats.Pbgra32); bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(output, name)); encoder.Save(file);
    }
    private static void Layout(Window window) { window.UpdateLayout(); window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); }
}
