using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using Xunit;

namespace VibeGauge.Windows.Tests;

public sealed class TooltipStyleTests
{
    [Fact]
    public void ApplicationTooltipsAndStatisticsInteractionsRenderCorrectly()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            App? app = null;
            try
            {
                app = new App(startRuntime: false); app.InitializeComponent();
                var palette = (ThemePalette)app.FindResource("ThemePalette");
                foreach (var light in new[] { false, true, false })
                    foreach (var content in new[] { "立即刷新", "2026-09-28\n调用 58 次\n上下文 338.8 万",
                    "固定窗口，点击外部不收起", string.Concat(Enumerable.Repeat("很长的项目名称和用量明细 ", 20)) })
                    {
                        palette.IsLight = light;
                        var tooltip = new ToolTip { Content = content, Style = (Style)app.FindResource(typeof(ToolTip)) };
                        tooltip.ApplyTemplate();
                        tooltip.Measure(new Size(390, double.PositiveInfinity));
                        tooltip.Arrange(new Rect(tooltip.DesiredSize));
                        tooltip.UpdateLayout();
                        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                        TooltipDiagnostics.Verify(tooltip);
                        Assert.InRange(tooltip.ActualWidth, 1, 390);
                        Assert.True(tooltip.ActualHeight >= 30);
                    }
                StatisticsPanelChecks.Verify();
                StatisticsSummaryChecks.Verify();
                UsagePanelChecks.Verify();
                ProviderCardChecks.Verify();
                TokenUnitPanelChecks.Verify();
                ClientVisibilityPanelChecks.Verify();
                ProviderDetailPanelChecks.Verify();
                FeaturePanelChecks.Verify();
                ReauditPanelChecks.Verify();
                NetworkPanelChecks.Verify();
                ProjectPanelChecks.Verify();
                MemoryPanelChecks.Verify();
                TopEdgeAutoHideChecks.Verify();
            }
            catch (Exception error) { failure = error; }
            finally { app?.Shutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(40)), "Tooltip layout test timed out");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
