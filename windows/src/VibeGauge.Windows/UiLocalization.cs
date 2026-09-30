using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Wpf = System.Windows.Controls;

namespace VibeGauge.Windows;

public static class UiLocalization
{
    private sealed record Pattern(Regex Regex, string Translation);
    private static readonly Dictionary<string, string> English = ReadResources();
    private static readonly Pattern[] Patterns = English.Where(x => Regex.IsMatch(x.Key, @"\{\d+\}"))
        .OrderByDescending(x => Regex.Replace(x.Key, @"\{\d+\}", "").Length).Select(x => new Pattern(MakePattern(x.Key), x.Value)).ToArray();
    private static Regex MakePattern(string template)
    {
        var result = new System.Text.StringBuilder("^"); var offset = 0;
        foreach (Match match in Regex.Matches(template, @"\{(\d+)\}"))
        {
            result.Append(Regex.Escape(template[offset..match.Index]));
            result.Append("(?<p").Append(match.Groups[1].Value).Append(">.{0,2048}?)"); offset = match.Index + match.Length;
        }
        result.Append(Regex.Escape(template[offset..])).Append('$');
        return new(result.ToString(), RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(20));
    }
    private static readonly ConditionalWeakTable<DependencyObject, Dictionary<DependencyProperty, TranslationState>> States = new();
    private sealed class TranslationState { public string Raw = ""; public string Display = ""; public bool Updating; }
    private static bool initialized;
    public static string Language { get; private set; } = "zh";
    public static bool IsEnglish => Language == "en" || Language == "system" && !CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
    public static void Initialize()
    {
        if (initialized) return; initialized = true;
        EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, e) =>
        {
            Attach((FrameworkElement)sender);
        }), true);
        // A tooltip assigned after Loaded is enrolled when it is first shown.
        EventManager.RegisterClassHandler(typeof(FrameworkElement), Wpf.ToolTipService.ToolTipOpeningEvent,
            new Wpf.ToolTipEventHandler((sender, _) => Attach((FrameworkElement)sender)), true);
    }
    public static void SetLanguage(string language)
    {
        Language = language is "zh" or "en" ? language : "system";
        foreach (var (element, properties) in States.ToArray())
            foreach (var (property, state) in properties) Apply(element, property, state, true);
        if (System.Windows.Application.Current is { } app)
            foreach (Window window in app.Windows) RefreshTree(window);
    }
    public static string Text(string text) => Translate(text, 0);
    private static string Translate(string text, int depth)
    {
        if (!IsEnglish || string.IsNullOrEmpty(text) || depth > 5 || text.Length > 16384) return text;
        if (English.TryGetValue(text, out var exact)) return exact;
        if (Regex.IsMatch(text, @"^[A-Za-z]:[\\/]|^https?://|^\\\\")) return text;
        foreach (var separator in new[] { "\n", " · ", "；" })
            if (text.Contains(separator, StringComparison.Ordinal)) return string.Join(separator, text.Split(separator).Select(x => Translate(x, depth + 1)));
        foreach (var pattern in Patterns)
        {
            Match match;
            try { match = pattern.Regex.Match(text); } catch (RegexMatchTimeoutException) { continue; }
            if (!match.Success) continue;
            return Regex.Replace(pattern.Translation, @"\{(\d+)\}", m => Translate(match.Groups["p" + m.Groups[1].Value].Value, depth + 1));
        }
        return text;
    }
    private static Dictionary<string, string> ReadResources()
    {
        using var input = typeof(UiLocalization).Assembly.GetManifestResourceStream("VibeGauge.Windows.Resources.en.json")!;
        return JsonSerializer.Deserialize<Dictionary<string, string>>(input) ?? [];
    }
    private static IEnumerable<DependencyProperty> Properties(FrameworkElement element)
    {
        if (element is Wpf.TextBlock) yield return Wpf.TextBlock.TextProperty;
        if (element is Wpf.AccessText) yield return Wpf.AccessText.TextProperty;
        if (element is Wpf.ContentControl) yield return Wpf.ContentControl.ContentProperty;
        if (element is Wpf.HeaderedContentControl) yield return Wpf.HeaderedContentControl.HeaderProperty;
        yield return FrameworkElement.ToolTipProperty;
    }
    private static void Attach(FrameworkElement element)
    {
        States.TryGetValue(element, out var properties);
        foreach (var property in Properties(element))
        {
            if (properties?.ContainsKey(property) == true) continue;
            // Most layout/template objects have no text. Avoid strong descriptor
            // subscriptions and translation state for their empty tooltip/content.
            if (property != Wpf.TextBlock.TextProperty && property != Wpf.AccessText.TextProperty &&
                element.GetValue(property) is not string && !BindingOperations.IsDataBound(element, property)) continue;
            var bindingPath = BindingOperations.GetBindingExpression(element, property)?.ParentBinding.Path?.Path;
            if (bindingPath is "Name" or "Model" or "Provider" or "Directory") continue;
            if (properties is null) { properties = new(); States.Add(element, properties); }
            var state = new TranslationState(); properties[property] = state;
            var descriptor = DependencyPropertyDescriptor.FromProperty(property, element.GetType());
            if (descriptor is null) continue;
            EventHandler changed = (_, _) => Apply(element, property, state, false);
            descriptor.AddValueChanged(element, changed);
            element.Unloaded += Unload;
            void Unload(object sender, RoutedEventArgs args)
            {
                if (!ReferenceEquals(sender, args.OriginalSource)) return;
                descriptor.RemoveValueChanged(element, changed); element.Unloaded -= Unload;
                if (element.GetValue(property) is string text && text == state.Display && state.Raw != text)
                    element.SetCurrentValue(property, state.Raw);
                States.Remove(element);
            }
            Apply(element, property, state, false);
        }
    }
    private static void Apply(DependencyObject element, DependencyProperty property, TranslationState state, bool refresh)
    {
        if (state.Updating || element.GetValue(property) is not string value) return;
        if (value != state.Display || state.Raw.Length == 0) state.Raw = value;
        state.Display = Text(state.Raw);
        if (value == state.Display) return;
        state.Updating = true;
        try { element.SetCurrentValue(property, state.Display); }
        finally { state.Updating = false; }
    }
    internal static void RefreshTree(DependencyObject element) => RefreshTree(element, new HashSet<DependencyObject>());
    private static void RefreshTree(DependencyObject element, HashSet<DependencyObject> seen)
    {
        if (!seen.Add(element)) return;
        if (element is FrameworkElement framework) Attach(framework);
        if (States.TryGetValue(element, out var properties)) foreach (var (property, state) in properties) Apply(element, property, state, true);
        if (element is Visual)
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++) RefreshTree(VisualTreeHelper.GetChild(element, i), seen);
        foreach (var child in LogicalTreeHelper.GetChildren(element).OfType<DependencyObject>()) RefreshTree(child, seen);
    }
}

public static class LocalizedMessageBox
{
    public static MessageBoxResult Show(string text, string caption, MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.None) =>
        System.Windows.MessageBox.Show(UiLocalization.Text(text), UiLocalization.Text(caption), buttons, image);
}
