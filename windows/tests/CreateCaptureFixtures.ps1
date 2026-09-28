param([Parameter(Mandatory=$true)][string]$Destination)
$ErrorActionPreference = 'Stop'
$homePath = [IO.Path]::GetFullPath($Destination)
$project = Join-Path $homePath '.claude\projects\demo'
$local = Join-Path $homePath 'local'
New-Item -ItemType Directory -Force $project,$local | Out-Null
@{ oauthAccount = @{ organizationRateLimitTier = 'default_claude_max_5x' } } | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $homePath '.claude.json') -Encoding utf8
$now = [DateTimeOffset]::Now
$lines = for ($day = 39; $day -ge 0; $day--) {
    for ($call = 0; $call -lt (3 + $day % 7); $call++) {
        $at = $now.AddDays(-$day).AddMinutes(-$call * 35)
        @{ type='assistant'; timestamp=$at.ToString('O'); requestId="fixture-$day-$call"; message=@{
            model= @('claude-sonnet-4-5','claude-opus-4-1')[$call % 2]; usage=@{input_tokens=1000 + $day * 50; cache_read_input_tokens=3500; cache_creation_input_tokens=500; output_tokens=300 + $call * 40}
        }} | ConvertTo-Json -Depth 8 -Compress
    }
}
$lines | Set-Content (Join-Path $project 'demo.jsonl') -Encoding utf8
@{_captured_at=$now.ToUnixTimeSeconds();five_hour=@{used_percentage=48;resets_at=$now.AddHours(2).ToUnixTimeSeconds()};seven_day=@{used_percentage=38;resets_at=$now.AddDays(3).ToUnixTimeSeconds()}} |
    ConvertTo-Json -Depth 8 | Set-Content (Join-Path $local 'claude-usage.json') -Encoding utf8
@{sessions=@{demo=@{used_pct=68;window=200000;at=$now.ToUnixTimeSeconds();cwd='C:\Demo\sample-app';model='claude-sonnet-4-5'}}} |
    ConvertTo-Json -Depth 8 | Set-Content (Join-Path $local 'claude-sessions.json') -Encoding utf8
# These files are synthetic and belong only to the explicitly selected fixture home.
$codex = Join-Path $homePath '.codex\sessions\demo'
New-Item -ItemType Directory -Force $codex | Out-Null
@{ auth_mode='chatgpt' } | ConvertTo-Json | Set-Content (Join-Path $homePath '.codex\auth.json') -Encoding utf8
$codexLines = @(
    @{ type='session_meta'; timestamp=$now.AddMinutes(-4).ToString('O'); payload=@{id='ui-fixture';cwd='C:\Demo\api-server';model='gpt-6-astra'} },
    @{ type='event_msg'; timestamp=$now.AddMinutes(-1).ToString('O'); payload=@{type='token_count';info=@{
        model_context_window=272000;last_token_usage=@{input_tokens=88000;cached_input_tokens=81000;output_tokens=1400;reasoning_output_tokens=600;total_tokens=90000}
    };rate_limits=@{plan_type='pro';limit_id='codex';primary=@{used_percent=12;window_minutes=300;resets_at=$now.AddHours(4).ToUnixTimeSeconds()};secondary=@{used_percent=41;window_minutes=10080;resets_at=$now.AddDays(3).ToUnixTimeSeconds()}}}}
) | ForEach-Object { $_ | ConvertTo-Json -Depth 12 -Compress }
$codexLines | Set-Content (Join-Path $codex 'session.jsonl') -Encoding utf8
@{updated_at=$now.ToUnixTimeSeconds();pools=@{
    gemini=@{'5h'=@{remaining_fraction=.78;reset_at=$now.AddHours(3).ToUnixTimeSeconds()};weekly=@{remaining_fraction=.65;reset_at=$now.AddDays(4).ToUnixTimeSeconds()}};
    '3p'=@{'5h'=@{remaining_fraction=.92;reset_at=$now.AddHours(4).ToUnixTimeSeconds()};weekly=@{remaining_fraction=.86;reset_at=$now.AddDays(5).ToUnixTimeSeconds()}}
}} | ConvertTo-Json -Depth 12 | Set-Content (Join-Path $local 'agy-quota.json') -Encoding utf8
$pi = Join-Path $homePath '.pi-desktop\sessions'
New-Item -ItemType Directory -Force $pi | Out-Null
$piLines = for ($day = 1; $day -ge 0; $day--) {
    for ($call = 0; $call -lt 3; $call++) {
        @{ type='message'; role='assistant'; id="pi-fixture-$day-$call"; createdAt=$now.AddDays(-$day).AddMinutes(-15-$call).ToString('O'); meta=@{
            status='complete'; modelId='pi-demo-model'; usage=@{inputTokens=2400;cacheReadTokens=32000;cacheWriteTokens=0;outputTokens=900;reasoningTokens=300;totalTokens=35300}
        }} | ConvertTo-Json -Depth 8 -Compress
    }
}
$piLines | Set-Content (Join-Path $pi 'demo.jsonl') -Encoding utf8
Write-Output $homePath
