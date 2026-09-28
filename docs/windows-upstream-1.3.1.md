# Windows port of upstream v1.3.1

This document records the initial port baseline. The stable Windows release now
includes the subsequent UI, PI-Desktop, date-selection, and sub2api work; see
[v1.4.0 release notes](releases/v1.4.0.md). The platform boundaries below still apply.

Baseline: `MaxHaiCom/VibeGauge` commit `dad6b5b` (v1.3.1 plus documentation). Windows 11 x64, .NET 10, WPF. This is a Windows preview, not a claim that every macOS-specific feature is implemented.

The subsequent [Windows UI correction](windows-ui-refresh.md) adds native caption dragging, saved placement, Desktop Acrylic, and a denser upstream-inspired component layout. Its packages are separate from the original preview.

## Implemented in this update

| Area | Windows implementation |
| --- | --- |
| Statistics | Today / 7D / 30D / all-time CLI totals, month calendar heatmap, seven-day hourly heatmap, today's model distribution; API usage stays separate |
| History | Existing cache remains readable; historical CLI totals survive deletion of source logs; rewritten files replace their own contribution |
| Incremental logs | 1 MB input chunks, 32 MB maximum line, incomplete-line retry, UTF-8 boundaries, fingerprint checks for rewritten tails; JSON cache serialization streams to disk |
| Quota confidence | Reported / estimated / may be stale / awaiting update; expired values are not displayed as a known zero |
| Forecasts | Recent captured samples, window average fallback, seven-day hourly activity profile for weekly windows, last-cycle observations; no forecast for stale/estimated/rolling windows |
| Notifications | Explicitly enabled in System; projection must persist for 15 minutes; once per cycle; reset-time drift does not repeat notifications |
| Sessions | Codex last-request context, not cumulative token totals; context clears on compaction; Claude context from the opt-in bridge; today's compaction counts |
| Pending work | Claude observe-only hooks, confirmed permission prompts and idle/elicitation notifications, subagent correlation, completed-call tombstones, expiry and transcript activity checks; no approval output |
| Claude bridge | Native self-contained helper; backs up settings, merges only its own hooks, restores previous statusline configuration; never stores prompt/tool input |
| Official quotas | Kimi local service with current-user listener ownership check, health check, no proxy, no redirects; optional week/month windows |
| Registered keys | Windows Credential Manager, fixed provider allowlist, HTTPS-only provider requests without redirects; GLM, Z.ai, MiniMax, DeepSeek, OpenRouter key limits, Moonshot |
| Official CLIs | Existing native `arkcli.exe` and `bl.exe`, their own sign-in, bounded output and timeout, five-hour/week/month windows; no automatic installation or sign-in |
| Local models | Ollama loaded models via `/api/ps`; LM Studio and llama.cpp model APIs on their standard loopback ports; no redirect or system proxy for local probes |
| Network | Native active adapters, IPv4/IPv6 addresses, gateways, DNS server addresses, actual traffic deltas; explicit-only egress and direct-IPv6 probes |
| Accounting proxy | Configurable local port, explicit HTTP upstream proxy or system proxy/direct, bypass hosts, per-key fingerprints, reached-upstream flag, bounded non-streaming capture |
| Usage protocols | OpenAI Chat/Responses (including nested completion events), Anthropic, Gemini thinking semantics, Ollama NDJSON; multiline SSE and incomplete-accounting errors |
| Packaging | Separate output directories are supported; builds refuse to erase a directory containing a running application |

## Platform boundaries and remaining parity

- Windows retains its existing safe process cleanup and user-owned child proxy. No LaunchAgent, Windows Service, scheduled task, or permanent background daemon is added.
- CLI integration is **opt-in**. It changes only the current user's Claude `settings.json` after a click and backup. Close existing Claude sessions and start a new session after changing the integration. Restore the statusline and disable observation before removing/moving a portable copy that those commands reference.
- Keys are stored only in Windows Credential Manager. Registration authorizes periodic requests only to that key's selected provider. No test uses real keys. Real-account balances, Kimi, and official CLI login were not validated without those services/accounts.
- OpenRouter shows the registered key's `limit_remaining`; this is not advertised as the entire account credit balance.
- Malformed/unsupported compressed accounting responses are still forwarded, but marked with an accounting error instead of invented usage. Native Ollama proxy requests to private addresses remain blocked by the existing SSRF policy; local-model monitoring does not use the accounting proxy.
- This update does not yet port the full bilingual UI, automatic update notices, AGY statusline installer, `plans.json` provider-specific rolling estimators, Claude error/retry detail pages, optional SSH quota collection, full Clash controller UI, Wi-Fi SSID/BSSID, or Tailscale CLI detail panels. Network adapter information still includes those active interfaces.
- IPv6 reachability is reported as reachability, **not proof of DNS leakage**. Public egress probes contact Cloudflare only after a click.
- Log read buffers are bounded; the persistent per-request history index can still grow. Upstream's old-history compaction is not claimed as fully ported.

## Build and run

```powershell
& "$HOME\.dotnet\dotnet.exe" test .\windows\VibeGauge.slnx -c Release
.\windows\build.ps1 -RequireInstaller -OutputDirectory "$PWD\windows\artifacts\release-v1.3.1"
```

The portable `publish` directory must contain both `VibeGauge.exe` and `VibeGauge.Proxy.exe`. The `VibeGauge-Setup-v1.3.1.exe` file is the installer. The main `VibeGauge.exe` is not an installer.

Configuration and data are in `%LOCALAPPDATA%\VibeGauge`; legacy own-data files in `%USERPROFILE%\.config\vibegauge` are still read as fallbacks. Proxy configuration uses `proxy.json` with `port`, `upstream`, and optional `no_proxy`. Save changes in System, restart the proxy from API, then adjust the CLI BASE_URL prefix if its port changed. Do not put API keys in `proxy.json`.

## Verification

Automated fixtures cover forecasts, stale/rolling quotas, deleted history, hooks and configuration preservation, credential-free usage protocols, stream limits, native process metrics, and proxy forwarding/security. UI captures use explicitly synthetic CLI data and a separate single-instance identity; they do not stop the user's running application or enable its integrations.

See `windows/tests/CreateCaptureFixtures.ps1` for reproducible visual test data. Release self-tests run before packaging. No source changes to the macOS implementation are required for the Windows port.

Verified on Windows on 2026-09-28:

- Release build: 57 automated tests passed (28 Core, 26 Proxy, 3 Windows), followed by published GUI and proxy self-tests.
- Visual checks: subscription, API, system, statistics, and network tabs were captured and reviewed with isolated synthetic CLI fixtures.
- Installer smoke test: installed into a guarded temporary directory, passed installed GUI/proxy self-tests and tray startup, then uninstalled successfully with no installation files or entries left behind.
- Installer and portable ZIP SHA-256 values match their adjacent `.sha256` files; whitespace verification and `git diff --check` passed.
- Packages are self-contained for Windows x64; users do not need to install .NET or Python. The installer is not code-signed, so Windows may display an unknown-publisher or SmartScreen warning. Review the source and verify the checksum before deciding whether to run it.
