using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows;
using Forms = System.Windows.Forms;
using VibeGauge.Core;
using VibeGauge.Windows.ViewModels;

namespace VibeGauge.Windows;

public sealed class TrayIconManager : IDisposable
{
    private readonly MainWindow window;
    private readonly DashboardViewModel viewModel;
    private readonly Forms.NotifyIcon icon;
    private Icon? currentIcon;
    private TrayContextMenuWindow? contextMenu;
    private int lastAvailablePercent;
    private readonly QuotaAlerts alerts;
    private readonly AttentionPolicy attention;
    private readonly Services.UpdateChecker updates;

    public TrayIconManager(MainWindow window, DashboardViewModel viewModel)
    {
        this.window = window;
        this.viewModel = viewModel;
        alerts = new(viewModel.Paths);
        attention = new(viewModel.Paths);
        updates = new(viewModel.Paths);
        icon = new Forms.NotifyIcon
        {
            Visible = true,
            Text = "VibeGauge 正在读取数据"
        };
        icon.MouseUp += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) ToggleWindow();
            else if (e.Button == Forms.MouseButtons.Right) ShowContextMenu();
        };
        SetIcon(0);
    }

    private void ShowContextMenu()
    {
        try { contextMenu?.Close(); } catch { }
        var summary = UiLocalization.IsEnglish
            ? $"RAM {lastAvailablePercent}%"
            : $"内存 {lastAvailablePercent}%";
        contextMenu = new TrayContextMenuWindow(
            summary,
            window.IsLightTheme,
            window.CurrentBackdropMode,
            ShowWindow,
            () => _ = viewModel.RefreshAsync(),
            () => window.ToggleThemeQuick(),
            () => window.ExitApplication());
        contextMenu.ShowAtCursor();
    }

    public void Update(DashboardSnapshot snapshot)
    {
        var available = snapshot.System.AvailableMemoryPercent;
        lastAvailablePercent = available;
        SetIcon(available);
        icon.Text = UiLocalization.IsEnglish ? $"VibeGauge · Memory free {available}% · {snapshot.Usage.Turns} calls today" : $"VibeGauge · 内存可用 {available}% · 今日 {snapshot.Usage.Turns} 次调用";
        foreach (var message in attention.Evaluate(snapshot, FeaturePreferences.Load(viewModel.Paths)))
            icon.ShowBalloonTip(7000, "VibeGauge", UiLocalization.Text(message), Forms.ToolTipIcon.Warning);
        if (updates.Scan() is { } release && updates.TakeNotification(release))
            icon.ShowBalloonTip(7000, UiLocalization.Text("VibeGauge · 软件更新"), UiLocalization.Text($"{release.Version} 已发布，可在系统页打开正式发布页下载。不会自动安装。"), Forms.ToolTipIcon.Info);
        if (snapshot.Statistics is { } statistics)
            foreach (var message in alerts.Evaluate(snapshot.Platforms, null, snapshot.CapturedAt,
                ForecastNotificationsEnabled(), statistics.ProfileFor))
                icon.ShowBalloonTip(7000, UiLocalization.Text("VibeGauge · 额度预测"), UiLocalization.Text(message), Forms.ToolTipIcon.Warning);
    }

    private static bool ForecastNotificationsEnabled()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\VibeGauge");
        return key?.GetValue("ForecastNotifications") is int value && value != 0;
    }

    public void ShowWindow()
    {
        window.PrepareForTrayShow();
        var cursor = Forms.Cursor.Position;
        var area = Forms.Screen.FromPoint(cursor).WorkingArea;
        window.Show();
        window.WindowState = WindowState.Normal;
        window.UpdateLayout();
        var source = PresentationSource.FromVisual(window);
        var scaleX = source?.CompositionTarget?.TransformFromDevice.M11 ?? 1;
        var scaleY = source?.CompositionTarget?.TransformFromDevice.M22 ?? 1;
        if (window.HasUserPosition)
        {
            var screen = Forms.Screen.FromPoint(new System.Drawing.Point((int)(window.Left / scaleX), (int)(window.Top / scaleY)));
            area = screen.WorkingArea;
        }
        window.Height = Math.Max(window.MinHeight, Math.Min(window.HasUserPosition ? window.Height : 860, area.Height * scaleY - 24));
        window.Width = Math.Max(window.MinWidth, Math.Min(window.Width, area.Width * scaleX - 16));
        window.UpdateLayout();
        window.Left = ClampToScreen(window.HasUserPosition ? window.Left : cursor.X * scaleX - window.ActualWidth * 0.8,
            area.Left * scaleX + 8, area.Right * scaleX - window.ActualWidth - 8);
        window.Top = ClampToScreen(window.HasUserPosition ? window.Top : cursor.Y * scaleY - window.ActualHeight - 12,
            area.Top * scaleY + 8, area.Bottom * scaleY - window.ActualHeight - 8);
        window.OnTrayShown();
        window.Activate();
    }

    private static double ClampToScreen(double value, double minimum, double maximum) =>
        Math.Clamp(value, minimum, Math.Max(minimum, maximum));

    internal bool IsVisible => icon.Visible;

    private void ToggleWindow()
    {
        if (window.IsVisible && window.IsActive) window.Hide(); else ShowWindow();
    }

    private void SetIcon(int percentage)
    {
        var next = IconFactory.Create(percentage);
        icon.Icon = next;
        currentIcon?.Dispose();
        currentIcon = next;
    }

    public void Dispose()
    {
        try { contextMenu?.Close(); } catch { }
        icon.Visible = false;
        icon.Dispose();
        currentIcon?.Dispose();
    }

    internal static class IconFactory
    {
        private const int SmCxSmIcon = 49;

        internal static Icon Create(int percentage)
        {
            var targetSize = Math.Clamp(GetSystemMetrics(SmCxSmIcon), 16, 48);
            // Render at exact 1:1 native taskbar tray pixel dimensions so Explorer never downscales or blurs text.
            using var bitmap = RenderBitmap(percentage, targetSize);
            var handle = bitmap.GetHicon();
            try { return (Icon)Icon.FromHandle(handle).Clone(); }
            finally { DestroyIcon(handle); }
        }

        internal static Bitmap RenderBitmap(int percentage, int size)
        {
            var pct = Math.Clamp(percentage, 0, 100);
            var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.Clear(Color.Transparent);

            // Dynamic neon accent gradient: cyan->emerald (healthy), gold->amber (warning), rose->crimson (critical)
            var (accentStart, accentEnd) = pct < 15
                ? (Color.FromArgb(255, 86, 122), Color.FromArgb(255, 36, 72))
                : pct < 30
                    ? (Color.FromArgb(255, 214, 64), Color.FromArgb(255, 130, 24))
                    : (Color.FromArgb(40, 224, 255), Color.FromArgb(46, 244, 152));

            var rect = new RectangleF(0.4f, 0.4f, size - 0.9f, size - 0.9f);
            var radius = Math.Max(2.8f, size * 0.22f);
            using var chassisPath = GraphicsExtensions.Rounded(rect, radius);

            // Obsidian metallic HUD chassis echoing VibeGauge's dark titanium icon language
            using (var chassisFill = new LinearGradientBrush(rect, Color.FromArgb(27, 34, 48), Color.FromArgb(11, 14, 21), 90f))
                graphics.FillPath(chassisFill, chassisPath);

            // Subtle OLED ambient neon underglow inside the lower half of the badge
            using (var glowRegion = new Region(chassisPath))
            {
                var prevClip = graphics.Clip;
                graphics.Clip = glowRegion;
                var glowRect = new RectangleF(size * 0.05f, size * 0.48f, size * 0.90f, size * 0.65f);
                using var glowPath = new GraphicsPath();
                glowPath.AddEllipse(glowRect);
                using var glowBrush = new PathGradientBrush(glowPath)
                {
                    CenterColor = Color.FromArgb(52, accentEnd),
                    SurroundColors = [Color.FromArgb(0, accentStart)]
                };
                graphics.FillPath(glowBrush, glowPath);
                graphics.Clip = prevClip;
            }

            // Crisp neon perimeter rim
            var rimWidth = Math.Max(1f, size * 0.068f);
            using (var rimBrush = new LinearGradientBrush(rect, Color.FromArgb(190, accentStart), Color.FromArgb(230, accentEnd), 45f))
            using (var rimPen = new Pen(rimBrush, rimWidth))
                graphics.DrawPath(rimPen, chassisPath);

            // Live glowing neon gauge bar along bottom edge
            var barH = Math.Max(1.8f, size * 0.12f);
            var barPadX = Math.Max(2.4f, size * 0.15f);
            var barY = size - barH - Math.Max(1.6f, size * 0.105f);
            var barW = size - barPadX * 2f;
            var trackRect = new RectangleF(barPadX, barY, barW, barH);
            using (var trackPath = GraphicsExtensions.Rounded(trackRect, barH / 2f))
            using (var trackBrush = new SolidBrush(Color.FromArgb(56, 70, 92)))
                graphics.FillPath(trackBrush, trackPath);

            if (pct > 0)
            {
                var fillW = Math.Max(barH, barW * pct / 100f);
                var fillRect = new RectangleF(barPadX, barY, fillW, barH);
                using var fillPath = GraphicsExtensions.Rounded(fillRect, barH / 2f);
                using var fillBrush = new LinearGradientBrush(trackRect, accentStart, accentEnd, 0f);
                graphics.FillPath(fillBrush, fillPath);
            }

            // Razor-sharp pure-white digits centered cleanly above the neon gauge bar
            var text = pct.ToString();
            var ratio = text.Length >= 3 ? 0.50f : text.Length == 1 ? 0.64f : 0.58f;
            var fontSize = Math.Max(8.5f, size * ratio);
            graphics.TextRenderingHint = fontSize < 12.5f
                ? TextRenderingHint.SingleBitPerPixelGridFit
                : TextRenderingHint.AntiAliasGridFit;

            var family = PickDigitFont(text.Length >= 3);
            using var font = new Font(family, fontSize, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
            using var format = new StringFormat(StringFormat.GenericTypographic)
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces
            };

            var offsetY = -size * 0.075f + (size <= 16 ? 0.5f : 0f);
            graphics.DrawString(text, font, Brushes.White, new RectangleF(0f, offsetY, size, size), format);
            return bitmap;
        }

        private static string PickDigitFont(bool condensed)
        {
            if (condensed)
            {
                using var families = new InstalledFontCollection();
                if (families.Families.Any(f => f.Name.Equals("Arial Narrow", StringComparison.OrdinalIgnoreCase)))
                    return "Arial Narrow";
                if (families.Families.Any(f => f.Name.Equals("Bahnschrift Condensed", StringComparison.OrdinalIgnoreCase)))
                    return "Bahnschrift Condensed";
            }
            return "Segoe UI";
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr handle);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);
    }
}

internal static class GraphicsExtensions
{
    internal static void FillRoundedRectangle(this Graphics graphics, Brush brush, RectangleF bounds, float radius)
    {
        using var path = Rounded(bounds, radius);
        graphics.FillPath(brush, path);
    }

    internal static void DrawRoundedRectangle(this Graphics graphics, Pen pen, RectangleF bounds, float radius)
    {
        using var path = Rounded(bounds, radius);
        graphics.DrawPath(pen, path);
    }

    internal static GraphicsPath Rounded(RectangleF bounds, float radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
