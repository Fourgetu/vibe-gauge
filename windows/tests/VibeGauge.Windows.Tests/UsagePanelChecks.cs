using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Xunit;

namespace VibeGauge.Windows.Tests;

internal static class UsagePanelChecks
{
    internal static void Verify()
    {
        var palette = (ThemePalette)Application.Current.FindResource("ThemePalette");
        foreach (var light in new[] { false, true })
        foreach (var width in new[] { 380, 480 })
        {
            palette.IsLight = light;
            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new Border { Width = 1000, Height = 1000 }
            };
            var window = new Window { Width = 300, Height = 200, Left = -20000, Top = -20000,
                ShowInTaskbar = false, ShowActivated = false, Content = scroll };
            try
            {
                window.Show();
                Layout(window);
                var vertical = (ScrollBar)scroll.Template.FindName("PART_VerticalScrollBar", scroll);
                var horizontal = (ScrollBar)scroll.Template.FindName("PART_HorizontalScrollBar", scroll);
                Assert.Equal(8, vertical.ActualWidth);
                Assert.Equal(8, horizontal.ActualHeight);
                var track = (Track)vertical.Template.FindName("PART_Track", vertical);
                var grip = (Border)track.Thumb.Template.FindName("Grip", track.Thumb);
                Assert.Equal(3, grip.ActualWidth);
                var horizontalTrack = (Track)horizontal.Template.FindName("PART_Track", horizontal);
                var horizontalGrip = (Border)horizontalTrack.Thumb.Template.FindName("Grip", horizontalTrack.Thumb);
                Assert.Equal(3, horizontalGrip.ActualHeight);
                scroll.ScrollToVerticalOffset(100); scroll.ScrollToHorizontalOffset(70);
                Layout(window);
                Assert.Equal(100, vertical.Value);
                Assert.Equal(70, horizontal.Value);
                ScrollBar.PageDownCommand.Execute(null, vertical);
                ScrollBar.PageRightCommand.Execute(null, horizontal);
                Layout(window);
                Assert.True(scroll.VerticalOffset > 100);
                Assert.True(scroll.HorizontalOffset > 70);
                var old = scroll.VerticalOffset;
                track.Thumb.RaiseEvent(new DragDeltaEventArgs(0, 10) { RoutedEvent = Thumb.DragDeltaEvent });
                Layout(window);
                Assert.True(scroll.VerticalOffset > old);
                var combo = new ComboBox { ItemsSource = Enumerable.Range(0, 40).Select(x => "Site " + x).ToArray() };
                window.Content = combo;
                Layout(window);
                combo.IsDropDownOpen = true;
                Layout(window);
                var popup = (Popup)combo.Template.FindName("PART_Popup", combo);
                var dropdown = Assert.Single(Visuals<ScrollViewer>(popup.Child));
                dropdown.UpdateLayout();
                var dropdownBar = (ScrollBar)dropdown.Template.FindName("PART_VerticalScrollBar", dropdown);
                Assert.Equal(8, dropdownBar.ActualWidth);
                Assert.True(dropdown.ScrollableHeight > 0);
                combo.IsDropDownOpen = false;
            }
            finally { window.Close(); }

            // Measure offscreen without Loaded: never enumerates the real Credential Manager.
            var panel = new UsageKeysPanel();
            var host = Named<ComboBox>(panel, "UsageProvider");
            var secret = Named<PasswordBox>(panel, "UsageSecret");
            Assert.Equal(Visibility.Collapsed, Named<StackPanel>(panel, "UsageCustomFields").Visibility);
            secret.Password = "fixture-only-not-a-real-key";
            host.SelectedIndex = host.Items.Count - 1;
            Assert.Empty(secret.Password);
            Assert.Equal(Visibility.Visible, Named<StackPanel>(panel, "UsageCustomFields").Visibility);
            Assert.Equal(Visibility.Visible, Named<Button>(panel, "UsageTest").Visibility);
            Named<TextBox>(panel, "UsageSiteName").Text = "我的 sub2api";
            Named<TextBox>(panel, "UsageEndpoint").Text = "https://my-site.example/v1";
            secret.Password = "fixture-only-not-a-real-key";
            var surface = new Border { Child = panel, Padding = new Thickness(18), Background = (Brush)Application.Current.FindResource("WindowBrush"), UseLayoutRounding = true };
            surface.Measure(new Size(width, double.PositiveInfinity));
            surface.Arrange(new Rect(0, 0, width, surface.DesiredSize.Height));
            surface.UpdateLayout();
            Assert.InRange(panel.ActualWidth, width - 40, width);
            foreach (var box in Logical<TextBox>(panel)) Assert.True(box.ActualWidth > 200);
            var output = Environment.GetEnvironmentVariable("VIBEGAUGE_TEST_CAPTURE_DIR");
            if (!string.IsNullOrEmpty(output))
            {
                Directory.CreateDirectory(output);
                var bitmap = new RenderTargetBitmap(width, (int)Math.Ceiling(surface.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(surface);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(output, $"sub2api-{(light ? "light" : "dark")}-{width}.png"));
                encoder.Save(file);
            }
            host.SelectedIndex = 0;
            Assert.Empty(secret.Password);
            Assert.Equal(Visibility.Collapsed, Named<Button>(panel, "UsageTest").Visibility);
        }
    }
    private static void Layout(FrameworkElement element)
    {
        element.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        element.UpdateLayout();
    }
    private static T Named<T>(DependencyObject root, string name) where T : FrameworkElement =>
        Assert.Single(Logical<T>(root), x => x.Name == name);
    private static IEnumerable<T> Logical<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T value) yield return value;
            foreach (var nested in Logical<T>(child)) yield return nested;
        }
    }
    private static IEnumerable<T> Visuals<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T value) yield return value;
            foreach (var nested in Visuals<T>(child)) yield return nested;
        }
    }
}
