using System.Drawing;
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
    private readonly QuotaAlerts alerts;

    public TrayIconManager(MainWindow window, DashboardViewModel viewModel)
    {
        this.window = window;
        this.viewModel = viewModel;
        alerts = new(viewModel.Paths);
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开 VibeGauge", null, (_, _) => ShowWindow());
        menu.Items.Add("立即刷新", null, async (_, _) => await viewModel.RefreshAsync());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => window.ExitApplication());
        icon = new Forms.NotifyIcon
        {
            Visible = true,
            Text = "VibeGauge 正在读取数据",
            ContextMenuStrip = menu
        };
        icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) ToggleWindow(); };
        SetIcon(0);
    }

    public void Update(DashboardSnapshot snapshot)
    {
        var available = snapshot.System.AvailableMemoryPercent;
        SetIcon(available);
        icon.Text = $"VibeGauge · 内存可用 {available}% · 今日 {snapshot.Usage.Turns} 次调用";
        if (snapshot.Statistics is { } statistics && ForecastNotificationsEnabled())
            foreach (var message in alerts.Evaluate(snapshot.Platforms, ActivityProfile.From(statistics.ActivityHours), snapshot.CapturedAt))
                icon.ShowBalloonTip(7000, "VibeGauge · 额度预测", message, Forms.ToolTipIcon.Warning);
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
        icon.Visible = false;
        icon.Dispose();
        currentIcon?.Dispose();
    }

    private static class IconFactory
    {
        internal static Icon Create(int percentage)
        {
            using var bitmap = new Bitmap(64, 64, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                graphics.Clear(Color.Transparent);
                var fill = percentage < 15 ? Color.FromArgb(195, 52, 66) : percentage < 30 ? Color.FromArgb(194, 117, 17) : Color.FromArgb(24, 105, 89);
                using var brush = new SolidBrush(fill);
                using var pen = new Pen(Color.FromArgb(230, 32, 34, 37), 4);
                graphics.FillRoundedRectangle(brush, new RectangleF(4, 8, 56, 48), 10);
                graphics.DrawRoundedRectangle(pen, new RectangleF(4, 8, 56, 48), 10);
                using var font = new Font("Segoe UI", percentage >= 100 ? 17 : 20, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
                var text = percentage.ToString();
                var size = graphics.MeasureString(text, font);
                graphics.DrawString(text, font, Brushes.White, (64 - size.Width) / 2, (64 - size.Height) / 2 - 1);
            }
            var handle = bitmap.GetHicon();
            try { return (Icon)Icon.FromHandle(handle).Clone(); }
            finally { DestroyIcon(handle); }
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr handle);
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

    private static System.Drawing.Drawing2D.GraphicsPath Rounded(RectangleF bounds, float radius)
    {
        var diameter = radius * 2;
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
