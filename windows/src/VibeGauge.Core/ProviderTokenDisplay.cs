using System.Globalization;

namespace VibeGauge.Core;

// Keep numeric provider values alongside display text so changing units never
// reparses rounded labels or needs another provider/network request.
public sealed record DesktopTokenDisplay(UsageSourceSummary? Today, UsageSourceSummary? Total, bool Detected)
{
    public (string Detail, string Compact) Format(TokenUnit unit = TokenUnit.Chinese)
    {
        string Tokens(long value) => Formatting.Tokens(value, unit);
        var total = Total ?? Today;
        var available = total?.Turns > 0 || Today?.State == UsageDataState.Available;
        var failed = Total?.State == UsageDataState.ReadFailed;
        var detail = available
            ? $"今日 {Today?.Turns ?? 0} 次 · 总 Token {Tokens(Today?.TotalTokens ?? 0)}\n今日上下文 {Tokens(Today?.ContextTokens ?? 0)} · 输出 {Tokens(Today?.OutputTokens ?? 0)}\n今日思考 {Tokens(Today?.ThinkingTokens ?? 0)} · 缓存读取 {Tokens(Today?.CacheReadTokens ?? 0)} · 写入 {Tokens(Today?.CacheWriteTokens ?? 0)}\n本地累计 {total!.Turns} 次 · 总 Token {Tokens(total.TotalTokens)}\n累计上下文 {Tokens(total.ContextTokens)} · 输出 {Tokens(total.OutputTokens)}\n累计思考 {Tokens(total.ThinkingTokens)} · 缓存读取 {Tokens(total.CacheReadTokens)} · 写入 {Tokens(total.CacheWriteTokens)}"
            : Detected ? "本地无今日 token 统计" : "未检测到本地会话日志";
        if (available)
            detail += $"\n精确总 Token：今日 {Today?.TotalTokens ?? 0:N0} · 累计 {total!.TotalTokens:N0}";
        var compact = available
            ? $"今日 {Tokens(Today?.TotalTokens ?? 0)} · {Today?.Turns ?? 0} 次\n累计 {Tokens(total!.TotalTokens)} Token"
            : detail;
        if (failed)
        {
            detail = total!.Note + "\n" + detail;
            compact = "读取失败 · 保留上次统计\n" + (available ? $"累计 {Tokens(total.TotalTokens)} Token" : "暂无可用记录");
        }
        return (detail, compact);
    }
}

public sealed record ProviderTokenTotals(double? Today, double? Total)
{
    public bool HasValues => Today is not null || Total is not null;
    public string Format(TokenUnit unit = TokenUnit.Chinese)
    {
        string Tokens(double value) => value is >= 0 and < long.MaxValue
            ? Formatting.Tokens((long)value, unit) : value.ToString("N0", CultureInfo.InvariantCulture);
        return string.Join(" · ", new[] { Today is { } today ? $"今日 {Tokens(today)} Token" : "",
            Total is { } total ? $"累计 {Tokens(total)} Token" : "" }.Where(x => x.Length > 0));
    }
}
