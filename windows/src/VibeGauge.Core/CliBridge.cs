using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace VibeGauge.Core;

public static class CliBridge
{
    public static readonly string[] Events = ["PermissionRequest", "Notification", "PostToolUse", "PostToolUseFailure",
        "PermissionDenied", "UserPromptSubmit", "Stop", "SessionEnd", "SubagentStop"];
    private const string HookFlag = "--vibegauge-hook=claude";
    private const string StatusFlag = "--vibegauge-statusline=claude";
    private static double? Number(JsonNode? node) => node is JsonValue value
        ? value.TryGetValue<double>(out var floating) ? floating : value.TryGetValue<long>(out var integer) ? integer : null
        : null;

    private static JsonObject Read(string path) => File.Exists(path)
        ? JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? new() : new();
    private static void Write(string path, JsonObject data)
    {
        if (File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("Refusing linked settings files");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".vibegauge-tmp";
        File.WriteAllText(temporary, data.ToJsonString(new() { WriteIndented = true }), new UTF8Encoding(false));
        File.Move(temporary, path, true);
    }
    private static T Locked<T>(AppPaths paths, Func<T> action)
    {
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(paths.Home)))[..24];
        using var mutex = new Mutex(false, "VibeGauge.Bridge." + id);
        var owned = false;
        try
        {
            try { owned = mutex.WaitOne(TimeSpan.FromSeconds(3)); } catch (AbandonedMutexException) { owned = true; }
            if (!owned) throw new IOException("CLI settings are busy");
            return action();
        }
        finally { if (owned) mutex.ReleaseMutex(); }
    }

    public static bool HooksInstalled(AppPaths paths)
    {
        try
        {
            var state = Read(Path.Combine(paths.LocalDataRoot, "claude-bridge-state.json"));
            var command = state["hookCommand"]?.GetValue<string>();
            if (string.IsNullOrEmpty(command)) return false;
            var settings = Read(Path.Combine(paths.ClaudeRoot, "settings.json"));
            return Events.All(ev => (settings["hooks"]?[ev] as JsonArray)?.OfType<JsonObject>().Any(group =>
                group["matcher"] is null && (group["hooks"] as JsonArray)?.OfType<JsonObject>()
                    .Any(h => h["command"]?.GetValue<string>() == command) == true) == true);
        }
        catch { return false; }
    }

    public static void Configure(AppPaths paths, string executable, bool enable, bool hooks, string tool = "claude") => Locked(paths, () =>
    {
        if (tool is not ("claude" or "agy") || tool == "agy" && hooks) throw new ArgumentException("Unsupported bridge");
        var path = tool == "agy" ? Path.Combine(paths.GeminiRoot, "antigravity-cli", "settings.json") : Path.Combine(paths.ClaudeRoot, "settings.json");
        var statePath = Path.Combine(paths.LocalDataRoot, tool + "-bridge-state.json");
        var settings = Read(path);
        var state = Read(statePath);
        var command = $"\"{executable}\" {(hooks ? HookFlag : "--vibegauge-statusline=" + tool)}";
        if (enable && File.Exists(path))
        {
            var backup = path + ".vibegauge-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "-" + Guid.NewGuid().ToString("N")[..6] + ".bak";
            File.Copy(path, backup);
        }
        if (hooks)
        {
            var hookRows = settings["hooks"] as JsonObject ?? new();
            var previous = state["hookCommand"]?.GetValue<string>();
            foreach (var ev in Events)
            {
                var groups = hookRows[ev] as JsonArray ?? new();
                foreach (var group in groups.OfType<JsonObject>().ToArray())
                    if (group["hooks"] is JsonArray entries)
                    {
                        foreach (var h in entries.OfType<JsonObject>().Where(h => previous is not null && h["command"]?.GetValue<string>() == previous).ToArray()) entries.Remove(h);
                        if (entries.Count == 0) groups.Remove(group);
                    }
                if (enable) groups.Add(new JsonObject
                {
                    ["hooks"] = new JsonArray(new JsonObject
                    { ["type"] = "command", ["command"] = command, ["timeout"] = 5 })
                });
                if (groups.Parent is null) hookRows[ev] = groups;
            }
            if (hookRows.Parent is null) settings["hooks"] = hookRows;
            state["hookCommand"] = enable ? command : null;
        }
        else
        {
            var previous = state["statusCommand"]?.GetValue<string>();
            var current = settings["statusLine"] as JsonObject;
            if (enable)
            {
                if (current?["command"]?.GetValue<string>() != previous)
                    state["originalStatusLine"] = current?.DeepClone();
                settings["statusLine"] = new JsonObject { ["type"] = "command", ["command"] = command };
                state["statusCommand"] = command;
            }
            else if (current?["command"]?.GetValue<string>() == previous && previous is not null)
            {
                if (state["originalStatusLine"] is { } original) settings["statusLine"] = original.DeepClone();
                else settings.Remove("statusLine");
                state.Remove("statusCommand");
            }
        }
        Write(statePath, state);
        Write(path, settings);
        return true;
    });

    public static bool StatusInstalled(AppPaths paths, string tool)
    {
        if (tool is not ("claude" or "agy")) return false;
        try
        {
            var file = tool == "agy" ? Path.Combine(paths.GeminiRoot, "antigravity-cli", "settings.json") : Path.Combine(paths.ClaudeRoot, "settings.json");
            return Read(file)["statusLine"]?["command"]?.GetValue<string>()?.Contains("--vibegauge-statusline=" + tool, StringComparison.Ordinal) == true;
        }
        catch { return false; }
    }

    public static string RecordAgyStatus(AppPaths paths, JsonObject payload, DateTimeOffset now) => Locked(paths, () =>
    {
        if ((payload["quota"] ?? payload["quotas"]) is not JsonObject quota) return "";
        var model = payload["model"] is JsonObject m ? m["id"]?.GetValue<string>() ?? "" : payload["model"]?.GetValue<string>() ?? "";
        var defaultPool = new[] { "claude", "gpt", "3p" }.Any(x => model.Contains(x, StringComparison.OrdinalIgnoreCase)) ? "3p" : "gemini";
        var path = Path.Combine(paths.LocalDataRoot, "agy-quota.json");
        var cache = Read(path);
        var pools = cache["pools"] as JsonObject ?? new();
        var pieces = new List<string>();
        foreach (var (name, node) in quota)
        {
            if (node is not JsonObject value) continue;
            var key = name.ToLowerInvariant();
            var pool = key.StartsWith("3p") ? "3p" : key.StartsWith("gemini") ? "gemini" : defaultPool;
            var window = key.Contains("5h") || key.Contains("five") ? "5h" : key.Contains("week") || key.Contains("7d") ? "weekly" : null;
            var remaining = Number(value["remaining_fraction"]) ?? (Number(value["used_percentage"]) is { } used ? 1 - used / 100 : null);
            if (window is null || remaining is null || !double.IsFinite(remaining.Value)) continue;
            var reset = Number(value["reset_at"]) ?? (Number(value["reset_in_seconds"]) is { } seconds && double.IsFinite(seconds) ? now.ToUnixTimeSeconds() + seconds : null);
            var windows = pools[pool] as JsonObject ?? new();
            windows[window] = new JsonObject { ["remaining_fraction"] = Math.Clamp(remaining.Value, 0, 1),
                ["reset_at"] = reset, ["recorded_at"] = now.ToUnixTimeSeconds() };
            if (windows.Parent is null) pools[pool] = windows;
            pieces.Add($"{pool} {window} {(1 - Math.Clamp(remaining.Value, 0, 1)) * 100:0}%");
        }
        if (pools.Parent is null) cache["pools"] = pools;
        cache["updated_at"] = now.ToUnixTimeSeconds();
        Write(path, cache);
        return string.Join(" · ", pieces);
    });

    public static async Task<string?> ForwardOriginalAgyAsync(AppPaths paths, string payload)
    {
        var command = Read(Path.Combine(paths.LocalDataRoot, "agy-bridge-state.json"))["originalStatusLine"]?["command"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(command) || command.Contains("--vibegauge-statusline=", StringComparison.Ordinal)) return null;
        using var process = new System.Diagnostics.Process { StartInfo = new("cmd.exe") {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        process.StartInfo.ArgumentList.Add("/d"); process.StartInfo.ArgumentList.Add("/s"); process.StartInfo.ArgumentList.Add("/c"); process.StartInfo.ArgumentList.Add(command);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            process.Start();
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            try { await process.StandardInput.WriteAsync(payload.AsMemory(), timeout.Token); process.StandardInput.Close(); }
            catch (IOException) { /* Some original status lines exit without reading stdin. */ }
            await process.WaitForExitAsync(timeout.Token);
            await error;
            return await output;
        }
        catch (OperationCanceledException) { try { process.Kill(true); } catch { } return null; }
    }

    public static string RecordStatus(AppPaths paths, JsonObject payload, DateTimeOffset now) => Locked(paths, () =>
    {
        if (payload["rate_limits"] is JsonObject quotas)
        {
            var copy = (JsonObject)quotas.DeepClone();
            copy["_captured_at"] = now.ToUnixTimeSeconds();
            Write(Path.Combine(paths.LocalDataRoot, "claude-usage.json"), copy);
        }
        if (payload["session_id"]?.GetValue<string>() is { Length: > 0 } sid && payload["context_window"] is JsonObject context)
        {
            var file = Path.Combine(paths.LocalDataRoot, "claude-sessions.json");
            var root = Read(file);
            var sessions = root["sessions"] as JsonObject ?? new();
            sessions[sid] = new JsonObject
            {
                ["used_pct"] = context["used_percentage"]?.DeepClone(),
                ["window"] = context["context_window_size"]?.DeepClone(),
                ["at"] = now.ToUnixTimeSeconds(),
                ["cwd"] = payload["workspace"]?["current_dir"]?.DeepClone(),
                ["model"] = payload["model"] is JsonObject m ? m["id"]?.DeepClone() : payload["model"]?.DeepClone(),
                ["transcript"] = payload["transcript_path"]?.DeepClone()
            };
            foreach (var row in sessions.ToArray())
                if ((Number(row.Value?["at"]) ?? 0) < now.ToUnixTimeSeconds() - 86400) sessions.Remove(row.Key);
            if (sessions.Parent is null) root["sessions"] = sessions;
            Write(file, root);
        }
        var pieces = new List<string>();
        foreach (var (key, label) in new[] { ("five_hour", "5h"), ("seven_day", "7d") })
            if (Number(payload["rate_limits"]?[key]?["used_percentage"]) is { } pct)
                pieces.Add($"{label} {pct:0}%");
        return string.Join(" · ", pieces);
    });

    public static void RecordHook(AppPaths paths, JsonObject payload, DateTimeOffset now) => Locked(paths, () =>
    {
        var sid = payload["session_id"]?.GetValue<string>();
        var ev = payload["hook_event_name"]?.GetValue<string>();
        if (string.IsNullOrEmpty(sid) || ev is null || !Events.Contains(ev)) return false;
        var note = payload["notification_type"]?.GetValue<string>();
        var input = ev == "Notification" && note is "idle_prompt" or "elicitation_dialog" or "elicitation_url_dialog";
        if (ev == "Notification" && note != "permission_prompt" && !input) return false;
        var creates = ev == "PermissionRequest" || ev == "Notification";
        var path = Path.Combine(paths.LocalDataRoot, "claude-waiting.json");
        var root = Read(path);
        var rows = root["sessions"] as JsonObject ?? new();
        var row = rows[sid] as JsonObject;
        if (row is null && !creates) return false;
        row ??= new();
        var calls = row["calls"] as JsonObject ?? new();
        var done = row["done"] as JsonObject ?? new();
        var agent = payload["agent_id"]?.GetValue<string>() ?? "";
        var use = payload["tool_use_id"]?.GetValue<string>() ?? "";
        var key = agent + "|" + use;
        var epoch = now.ToUnixTimeSeconds();
        foreach (var item in done.ToArray()) if ((Number(item.Value) ?? 0) < epoch - 600) done.Remove(item.Key);
        if (ev is "UserPromptSubmit" or "Stop" or "SessionEnd") rows.Remove(sid);
        else
        {
            if (ev == "PermissionRequest")
            {
                if (use.Length > 0 && done.ContainsKey(key)) return false;
                calls[key] ??= new JsonObject { ["state"] = "pending", ["since"] = epoch, ["tool"] = payload["tool_name"]?.DeepClone() };
            }
            else if (ev == "Notification" && note == "permission_prompt")
            {
                foreach (var call in calls.Select(x => x.Value).OfType<JsonObject>()) call["state"] = "permission";
                if (calls.Count == 0) calls["|notify"] = new JsonObject { ["state"] = "permission", ["since"] = epoch };
            }
            else if (input) row["input"] ??= JsonValue.Create(epoch);
            else if (ev == "SubagentStop")
            {
                foreach (var call in calls.Where(x => agent.Length > 0 && x.Key.StartsWith(agent + "|")).ToArray()) calls.Remove(call.Key);
            }
            else
            {
                foreach (var call in calls.Where(x => use.Length == 0 ? x.Key.StartsWith(agent + "|") :
                    x.Key == key || x.Key == agent + "|" || x.Key == agent + "|notify").ToArray()) calls.Remove(call.Key);
                if (use.Length > 0) done[key] = epoch;
                if (agent.Length == 0) row.Remove("input");
            }
            if (calls.Parent is null) row["calls"] = calls;
            if (done.Parent is null) row["done"] = done;
            row["at"] = epoch;
            foreach (var field in new[] { "cwd", "transcript_path" }) if (payload[field] is JsonValue v) row[field] = v.DeepClone();
            if (row.Parent is null) rows[sid] = row;
        }
        foreach (var item in rows.ToArray()) if ((Number(item.Value?["at"]) ?? 0) < epoch - 43200) rows.Remove(item.Key);
        if (rows.Parent is null) root["sessions"] = rows;
        Write(path, root);
        return true;
    });
}
