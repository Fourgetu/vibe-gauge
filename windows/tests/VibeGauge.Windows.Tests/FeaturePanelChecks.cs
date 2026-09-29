using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
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
        var root = Path.Combine(Path.GetTempPath(), "vibegauge-features-ui-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root, Path.Combine(root, "local"));
        using var proxy = new ProxyManager(paths);
        using var vm = new DashboardViewModel(new DashboardCoordinator(paths, proxy), new StartupRegistrar(), proxy, new ProxySettings());
        vm.Dispose();
        var panel = new FeatureSettingsPanel(vm);
        var scroll = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var window = new Window { Content = scroll, Width = 420, Height = 740, Left = -20000, Top = -20000, ShowInTaskbar = false, ShowActivated = false };
        try
        {
            window.Show(); Layout(window);
            var sections = Descendants<Expander>(panel).ToArray();
            Assert.Equal(7, sections.Length);
            foreach (var box in Descendants<CheckBox>(panel)) Assert.False(box.IsChecked);
            UiLocalization.SetLanguage("en"); Layout(window);
            Assert.Equal("Notifications and thresholds", sections[3].Header);
            Assert.Equal("12 calls today", UiLocalization.Text("今日 12 次调用"));
            Assert.Equal("Context 123 · Output 45", UiLocalization.Text("上下文 123 · 输出 45"));
            Assert.Equal(@"C:\用户\工作目录", UiLocalization.Text(@"C:\用户\工作目录"));
            Assert.Equal("我的自定义站点", UiLocalization.Text("我的自定义站点"));
            sections[3].IsExpanded = true; Layout(window);
            Assert.Contains(Descendants<CheckBox>(sections[3]), x => x.Content as string == "Low available memory");
            Assert.Contains(Descendants<TextBlock>(sections[3]), x => x.Text == "Quota used threshold % (50–100)");
            foreach (var light in new[] { false, true })
            {
                ((ThemePalette)System.Windows.Application.Current.FindResource("ThemePalette")).IsLight = light;
                window.SetResourceReference(Control.BackgroundProperty, "WindowBrush");
                Layout(window);
                var output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/parity-20260929")); Directory.CreateDirectory(output);
                var image = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); image.Render(window);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                using var file = File.Create(Path.Combine(output, light ? "features-en-light.png" : "features-en-dark.png")); encoder.Save(file);
            }
            UiLocalization.SetLanguage("zh"); Layout(window);
            Assert.Equal("通知与阈值", sections[3].Header);
            var text = new TextBlock(); var source = new ValueSource(); text.SetBinding(TextBlock.TextProperty, new Binding(nameof(ValueSource.Value)) { Source = source });
            var container = (StackPanel)panel.Content; container.Children.Add(text); Layout(window);
            UiLocalization.SetLanguage("en"); Layout(window); Assert.Equal("3 calls today", text.Text);
            source.Value = "今日 8 次调用"; Layout(window); Assert.Equal("8 calls today", text.Text);
            Assert.True(BindingOperations.IsDataBound(text, TextBlock.TextProperty));
            UiLocalization.SetLanguage("zh"); Layout(window); Assert.Equal("今日 8 次调用", text.Text);
            Assert.False(File.Exists(Path.Combine(paths.LocalDataRoot, "features.json")));
        }
        finally { UiLocalization.SetLanguage("zh"); window.Close(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private sealed class ValueSource : INotifyPropertyChanged
    {
        private string value = "今日 3 次调用";
        public string Value { get => value; set { this.value = value; PropertyChanged?.Invoke(this, new(nameof(Value))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); if (child is T target) yield return target;
            foreach (var item in Descendants<T>(child)) yield return item;
        }
    }
    private static void Layout(Window window) { window.UpdateLayout(); window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); }
}
