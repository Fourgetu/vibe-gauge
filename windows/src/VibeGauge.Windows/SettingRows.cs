using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;

using Panel = System.Windows.Controls.Panel;
using StackPanel = System.Windows.Controls.StackPanel;
using Border = System.Windows.Controls.Border;
using Grid = System.Windows.Controls.Grid;
using ColumnDefinition = System.Windows.Controls.ColumnDefinition;
using CheckBox = System.Windows.Controls.CheckBox;
using TextBlock = System.Windows.Controls.TextBlock;
using Button = System.Windows.Controls.Button;
using UserControl = System.Windows.Controls.UserControl;
using RadioButton = System.Windows.Controls.RadioButton;
using Orientation = System.Windows.Controls.Orientation;
using Application = System.Windows.Application;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Binding = System.Windows.Data.Binding;

namespace VibeGauge.Windows;

internal static class SettingRows
{
    internal static StackPanel Card(Panel parent, string title)
    {
        var content = new StackPanel();
        var heading = Label(title, 15, "TextMutedBrush", true);
        heading.Margin = new Thickness(0, 0, 0, 7);
        content.Children.Add(heading);
        var border = new Border { Child = content, CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 12, 14, 8), Margin = new Thickness(0, 0, 0, 10) };
        border.SetResourceReference(Border.BackgroundProperty, "CardBrush");
        parent.Children.Add(border);
        return content;
    }

    internal static CheckBox Toggle(Panel parent, string title, string description, bool enabled)
    {
        var row = Layout(title, description);
        var toggle = new CheckBox { Content = title, IsChecked = enabled, VerticalAlignment = VerticalAlignment.Center,
            Style = (Style)Application.Current.FindResource("SettingsSwitchStyle") };
        toggle.SetBinding(AutomationProperties.NameProperty, new Binding(nameof(CheckBox.Content)) { Source = toggle });
        toggle.ToolTip = description;
        Grid.SetColumn(toggle, 1); row.Children.Add(toggle); parent.Children.Add(row);
        return toggle;
    }

    internal static void Navigate(Panel parent, string title, string description, Action open)
    {
        var row = Layout(title, description);
        var arrow = Label("›", 22, "TextMutedBrush"); arrow.VerticalAlignment = VerticalAlignment.Center; arrow.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(arrow, 1); row.Children.Add(arrow);
        var button = new Button { Content = row, Padding = new Thickness(0), BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch, Background = System.Windows.Media.Brushes.Transparent,
            Style = (Style)Application.Current.FindResource("SettingsNavigationStyle") };
        AutomationProperties.SetName(button, title);
        button.Click += (_, _) => open(); parent.Children.Add(button);
    }

    private static Grid Layout(string title, string description)
    {
        var grid = new Grid { Margin = new Thickness(0, 6, 0, 8), Tag = "SettingRow" };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var labels = new StackPanel { Margin = new Thickness(0, 0, 18, 0) };
        labels.Children.Add(Label(title, 13.5, "TextPrimaryBrush", true));
        var detail = Label(description, 11.5, "TextSecondaryBrush"); detail.Margin = new Thickness(0, 3, 0, 0); detail.LineHeight = 16;
        labels.Children.Add(detail); grid.Children.Add(labels);
        return grid;
    }

    private static TextBlock Label(string text, double size, string brush, bool bold = false)
    {
        var label = new TextBlock { Text = text, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap };
        label.SetResourceReference(TextBlock.ForegroundProperty, brush); return label;
    }
}

public sealed class LanguageSelector : UserControl
{
    public LanguageSelector()
    {
        var options = new StackPanel { Orientation = Orientation.Horizontal };
        var surface = new Border { Child = options, CornerRadius = new CornerRadius(8), Padding = new Thickness(2), ToolTip = "语言 / Language" };
        surface.SetResourceReference(Border.BackgroundProperty, "CardBrush"); Content = surface;
        var group = "language-" + Guid.NewGuid().ToString("N");
        var buttons = new List<RadioButton>();
        foreach (var (title, code) in new[] { ("系统", "system"), ("中", "zh"), ("EN", "en") })
        {
            var button = new RadioButton { Content = title, Tag = code, GroupName = group, Style = (Style)Application.Current.FindResource("LanguageSegmentStyle") };
            AutomationProperties.SetName(button, code == "zh" ? "简体中文" : code == "en" ? "English" : "跟随系统");
            button.Click += (_, _) =>
            {
                if (DataContext is not ViewModels.DashboardViewModel vm) return;
                try
                {
                    (VibeGauge.Core.FeaturePreferences.Load(vm.Paths) with { Language = code }).Save(vm.Paths);
                    UiLocalization.SetLanguage(code);
                }
                catch (Exception error) { LocalizedMessageBox.Show(error.Message, "VibeGauge"); Refresh(); }
            };
            buttons.Add(button); options.Children.Add(button);
        }
        void Refresh()
        {
            var code = DataContext is ViewModels.DashboardViewModel vm ? VibeGauge.Core.FeaturePreferences.Load(vm.Paths).Language : "system";
            foreach (var button in buttons) button.IsChecked = (string)button.Tag == code;
        }
        Loaded += (_, _) => Refresh();
    }
}
