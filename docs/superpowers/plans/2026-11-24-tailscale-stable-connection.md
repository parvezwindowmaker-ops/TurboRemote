# Tailscale-First Stable Connection Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Sessions over Tailscale use a permanent address, survive drops by reconnecting on their own, and
detect dead links on both ends. The hourly pinggy tunnel becomes a fallback that is off while Tailscale runs.

**Architecture:** Testable logic lives in new classes in `TurboCorePortable.cs`: Tailscale detection, the
connect race, the tunnel auto-switch, the reconnect schedule and settings. The Host and viewer forms wire
them into the UI and session loops. Tests are console programs in `Tests/` that reference the built
`TurboRemote.exe`. They use fake counterparts and UI Automation, and never inject keyboard or mouse input.

**Tech Stack:** C# 5, .NET Framework 4.x, WinForms, inbox `csc.exe`, no NuGet. Tests use
`System.Windows.Automation` (WPF `UIAutomationClient`/`UIAutomationTypes`).

**Spec:** `docs/superpowers/specs/2026-11-24-tailscale-stable-connection-design.md`

## Global Constraints

- Build: `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe -target:winexe -platform:anycpu -optimize+ -unsafe -main:TurboUnified.UnifiedEntry`, references `System.dll System.Core.dll System.Drawing.dll System.Windows.Forms.dll`, all `StandaloneSource\*.cs`. C# 5 only: no `?.`, `nameof`, `$""`, expression-bodied members.
- Windows 10/11. It must keep working against older Host/viewer builds. No protocol change.
- Tests NEVER run a real Host and a real viewer together, and NEVER send keyboard/mouse input. Use fake counterparts plus UIA `InvokePattern`/`TogglePattern` and reading names.
- Tailscale CLI: `%ProgramFiles%\Tailscale\tailscale.exe status --json`, 3000 ms timeout. Tailscale IPv4 range: `100.64.0.0/10`.
- Settings file: `%LOCALAPPDATA%\TurboRemote\settings.txt`, lines `key=value`. Key `publicTunnel` takes `on|off|auto`, default `auto`.
- Timings (exact): viewer dead link after 20 000 ms of no packets. Host closes a silent viewer after 45 000 ms. TCP keep-alive starts after 15 000 ms idle, then every 5 000 ms. Tunnel candidates are held back 2 500 ms. Each candidate gets a 6 000 ms window; the race gives up after 15 000 ms. Reconnect delays are 1000, 2000, 3000, 5000, then 5000 forever. Tailscale is re-checked every 30 000 ms. Startup waits up to 60 000 ms for Tailscale to settle. Two consecutive not-Running checks switch the tunnel on.
- Exact UI copy:
  - Host Tailscale line, Running: `Tailscale ON — from anywhere, connect to: {HostName}  ({DnsName} · {IPv4})`
  - Not connected: `Tailscale is installed but not connected — open Tailscale and sign in.`
  - Not installed: `For a permanent Internet address, install Tailscale on this PC and the viewer PC (same account).`
  - Checkbox: `Also open a public tunnel for viewers without Tailscale (address changes every hour)`, plus ` (automatic)` while the setting is `auto`.
  - Reconnect banner: `Connection lost — reconnecting to {name} (attempt {n}, next try in {s} s)…`
  - Telemetry after a reconnect: `● RECONNECTED after {s} s`
  - Connect button while reconnecting: `✖ Stop reconnecting`
  - Failure hint: `If this PC is outside the Host's network, install Tailscale on both PCs and sign in to the same account.`
- Bump `TurboProtocol.AppVersion` to `2026.11.24` (Task 8).

## Review Focus

1. **The Host answers "busy"** (`FAIL:Host is busy…` from `SharedAddressThrottle`) during a reconnect. It is not a wrong key, so the viewer keeps retrying. Test in Task 7.
2. **A long outage** (the Host gone for over 60 s: sleep, reboot, ISP down). The viewer keeps retrying at 5 s intervals and reconnects within one interval of the Host coming back. Test in Task 7.
3. **Reconnect while the Host still holds the old dead session** (for up to 45 s). The new session must be accepted at once, not wait for the timeout. Test in Task 6.
4. **The Tailscale CLI hangs or is slow.** Detection returns within about 3.5 s using the adapter fallback, and the Host/viewer UI never freezes (queried off the UI thread). Test in Task 2.
5. **The address the session started on is gone** (a typed pinggy address after the hourly cut, or a changed port). Reconnect reaches the same PC through its reported machine name. Test in Task 7.

---

### Task 1: Build script and test kit

**Files:**
- Create: `build.ps1` (compile to `TurboRemote.exe` at repo root; exit code = csc exit code; filter CS4014 warnings)
- Create: `Tests/TestKit.cs`, `Tests/run-tests.ps1`, `Tests/SmokeTests.cs`
- Modify: `.gitignore` (create if missing): add `Tests/bin/`

**Interfaces:**
- Produces (`Tests/TestKit.cs`, namespace-less, all `public`):
  - `static class TestKit`: `void Check(string name, bool ok, string detail = null)`, `int Summary()` (prints `ALL CHECKS PASSED` or `{n} CHECK(S) FAILED`, returns 0/1), `string ExePath` (from `args[0]`), `int FreePort()`.
  - `class FakeHost : IDisposable`: `FakeHost(int port, string pin, string machineName)`. Properties: `AuthResultOverride` (string, sent instead of the default `OK|{machine}|USER|1|AES-256-HMAC-SHA256|v{AppVersion}`), `FailText` (if set, it sends `FAIL:{FailText}` unencrypted, like the real Host), `bool Responding` (false = it stops answering Ping and sending anything, but keeps the socket open), `int SessionsAccepted`. Methods: `void CloseSession()`, `void StopListening()`, `void StartListening()`. It answers Ping with Pong while `Responding`.
  - `class FakeViewer : IDisposable`: `static FakeViewer Connect(string host, int port, string pin)` (completes sign-in; throws on FAIL). Methods: `void Ping()`, `bool WaitForClose(int ms)` (true when the Host closed the socket). It drains frames in the background until `GoSilent()`, after which it neither sends nor reads.
  - `static class Uia`: `string FindText(IntPtr hwnd, string startsWith)`, `bool Invoke(IntPtr hwnd, string nameContains)` (InvokePattern, buttons), `bool Toggle(IntPtr hwnd, string nameContains)` (TogglePattern, checkboxes), `bool? IsChecked(IntPtr hwnd, string nameContains)`, `string WindowTitle(Process p)`, `List<string> ListItems(IntPtr hwnd)`.
- `Tests/run-tests.ps1 [-Only <Name>]`:
  1. runs `build.ps1`
  2. copies `TurboRemote.exe` to `Tests/bin/`
  3. compiles each `Tests/*Tests.cs` with `TestKit.cs` into `Tests/bin/<Name>.exe`, using `-r:TurboRemote.exe System.dll System.Core.dll System.Drawing.dll System.Windows.Forms.dll` and the WPF UIA dlls
  4. runs each one with the exe path and exits non-zero on any failure

  Each test removes `%LOCALAPPDATA%\TurboRemote\access_key.txt` and `settings.txt` at the end if they did not exist before it ran.

- [ ] **Step 1:** Write `Tests/SmokeTests.cs`: runs `TurboRemote.exe --self-test`, then `Check("self-test passes", stdout.StartsWith("SELF_TEST_PASSED"))`; starts a `FakeHost` on a free port; `FakeViewer.Connect("127.0.0.1", port, "2580")` succeeds; `Check("fake host and fake viewer complete sign-in", host.SessionsAccepted == 1)`.
- [ ] **Step 2:** Run `powershell -ExecutionPolicy Bypass -File .\Tests\run-tests.ps1 -Only SmokeTests`. Expected: `ALL CHECKS PASSED`.
- [ ] **Step 3:** Commit: `git add build.ps1 Tests .gitignore` → "Add build script and console test kit".

### Task 2: Tailscale detection

**Files:**
- Modify: `StandaloneSource/TurboCorePortable.cs` (new types after `PublicTunnel`)
- Test: `Tests/TailscaleInfoTests.cs`, fixtures under `Tests/fixtures/tailscale-*.json`

**Interfaces:**
- Produces:
  - `public enum TailscaleState { NotInstalled, Stopped, SignedOut, Starting, Running }`
  - `public sealed class TailscaleStatus`: `State` (TailscaleState), `DnsName` (no trailing dot), `HostName`, `IPv4` (all strings, empty if unknown), `bool IsRunning`.
  - `public static class TailscaleInfo`:
    - `TailscaleStatus Parse(string statusJson)`. Maps `BackendState`: `Running`→Running; `NeedsLogin`/`NeedsMachineAuth`→SignedOut; `NoState`/`Starting`→Starting; `Stopped`→Stopped; else Stopped. Reads `DNSName`/`HostName`/first IPv4 of `TailscaleIPs` from inside the `"Self": { … }` object (brace-matched), never from `Peer`.
    - `TailscaleStatus Query(string cliPath = null, int timeoutMs = 3000)`: runs the CLI hidden. On a missing CLI, timeout (kill it) or a parse failure, falls back to the adapter (an interface whose name or description contains "Tailscale" with an IPv4 in range: Running if up). NotInstalled if neither is found.
    - `bool IsTailscaleAddress(string ip)` (true only for 100.64.0.0–100.127.255.255).

- [ ] **Step 1:** Write the fixtures by hand (no real tailnet data):
  - `running.json`: `BackendState` Running; Self `DNSName` `office-pc.tail0000.ts.net.`, `HostName` `OFFICE-PC`, `TailscaleIPs` `["100.101.102.103","fd7a::1"]`; one Peer with `DNSName` `other.tail0000.ts.net.`
  - `needslogin.json`: `BackendState` `NeedsLogin`, no Self IPs
  - `starting.json`: `BackendState` `NoState`
- [ ] **Step 2:** Write the tests:
  - `Parse(running)`: State Running, DnsName `office-pc.tail0000.ts.net`, HostName `OFFICE-PC`, IPv4 `100.101.102.103`, not the peer's name.
  - `Parse(needslogin).State == SignedOut`; `Parse(starting).State == Starting`; `Parse("garbage").State == Stopped`.
  - `IsTailscaleAddress`: `100.64.0.1` true, `100.127.255.255` true, `100.63.0.1` false, `100.128.0.1` false, `192.168.1.2` false, `"x"` false.
  - `Query(@"C:\nope\tailscale.exe")` does not throw and returns within 1 s.
  - **Review Focus 4:** `Query(<path to Tests/bin/SlowCli.exe>, 3000)` returns in under 3 500 ms. `SlowCli.cs` sleeps 30 s; `run-tests.ps1` compiles it.
  - On a PC with Tailscale Running (detect via the CLI's existence and print "skipped" otherwise): `Query().State == Running` and `DnsName` ends with `.ts.net`.
- [ ] **Step 3:** Run `-Only TailscaleInfoTests`. Expected: FAIL to compile (`TailscaleInfo` missing).
- [ ] **Step 4:** Implement the types above.
- [ ] **Step 5:** Run `-Only TailscaleInfoTests`. Expected: `ALL CHECKS PASSED`.
- [ ] **Step 6:** Commit → "Detect Tailscale state, name and address".

### Task 3: Direct-first connect race

**Files:**
- Modify: `StandaloneSource/TurboCorePortable.cs` (`SmartRouteEngine.RaceFastestConnectionAsync`, `ClassifyRoute`, new `ConnectionRace`)
- Test: `Tests/RouteRaceTests.cs`

**Interfaces:**
- Consumes: `TailscaleInfo.IsTailscaleAddress(string)`.
- Produces:
  - `public static bool SmartRouteEngine.IsPublicTunnelHost(string host)`: true when the host ends with `.pinggy.link`, `.pinggy-free.link` or `.pinggy.io` (case-insensitive).
  - `ClassifyRoute` returns `"Tailscale (permanent)"` for Tailscale addresses. Other labels are unchanged.
  - `public sealed class ConnectionRace`:
    - `ConnectionRace(Func<EndpointCandidate, CancellationToken, Task<TcpClient>> dial, int tunnelHoldBackMs = 2500, int candidateWindowMs = 6000, int overallTimeoutMs = 15000)`
    - `void AddDirect(EndpointCandidate c)` and `void AddTunnel(EndpointCandidate c)`, callable before or during `RunAsync`. Duplicates by host+port are ignored.
    - `void NoMoreCandidates()`: after this, the race fails as soon as every started candidate has failed.
    - `Task<KeyValuePair<TcpClient, EndpointCandidate>> RunAsync(CancellationToken ct)`
  - Rules:
    - A direct candidate starts at once.
    - A tunnel candidate starts at max(added, race start + holdBack), and only if nothing has connected yet.
    - Each dial is cancelled after `candidateWindowMs`.
    - The first success wins and losers are closed.
    - It throws `TimeoutException` after `overallTimeoutMs`, or the last dial error once all candidates failed after `NoMoreCandidates`.
    - It throws `OperationCanceledException` on `ct`.
  - `RaceFastestConnectionAsync` keeps its signature. It builds a `ConnectionRace` with `TcpClient.ConnectAsync` as `dial`:
    - typed candidates are `AddDirect`, even if they are pinggy hosts
    - LAN discovery results are `AddDirect` when they arrive
    - cloud ticket entries are `AddTunnel` if `IsPublicTunnelHost`, else `AddDirect`
    - `NoMoreCandidates()` is called once LAN discovery and the cloud lookup have both finished
    - the existing 700 ms single-candidate fast path is removed, since the race now covers it

- [ ] **Step 1:** Write the tests with a fake `dial` (`Task.Delay(ms)`, then returns `new TcpClient()` or throws):
  - Direct succeeds at 200 ms while a tunnel would succeed at 10 ms → the direct one wins, and the tunnel dial was never started.
  - Direct fails at 100 ms and the tunnel succeeds at 300 ms → the tunnel wins; its dial started at ≥ 2 400 ms after the race began.
  - A tunnel added at 4 000 ms starts at once (hold-back already over) and gets the full 6 s window: it succeeds at 5 500 ms after starting → wins at ~9 500 ms.
  - A direct candidate added at 1 000 ms (as a late ticket) succeeds → wins.
  - All fail plus `NoMoreCandidates()` → throws the last dial error within 500 ms of the last failure.
  - Nothing ever answers → `TimeoutException` at 15 000 ms ±500.
  - Cancel at 300 ms → `OperationCanceledException`, and every dial's token was cancelled.
  - `IsPublicTunnelHost("abc.run.pinggy-free.link")` true, `("abc.a.pinggy.link")` true, `("wm-dev-016")` false.
  - The `ClassifyRoute` label for `100.77.50.110` is `Tailscale (permanent)`, checked via `ParseCandidates("100.77.50.110", 9876)[0].RouteLabel`.
- [ ] **Step 2:** Run `-Only RouteRaceTests`. Expected: compile FAIL.
- [ ] **Step 3:** Implement `ConnectionRace`, `IsPublicTunnelHost`, the label, and rewire `RaceFastestConnectionAsync`.
- [ ] **Step 4:** Run `-Only RouteRaceTests` and `-Only SmokeTests`. Expected: both pass.
- [ ] **Step 5:** Commit → "Try LAN and Tailscale before the hourly public tunnel".

### Task 4: Settings store and tunnel auto-switch

**Files:**
- Modify: `StandaloneSource/TurboCorePortable.cs`
- Test: `Tests/TunnelPolicyTests.cs`

**Interfaces:**
- Produces:
  - `public static class TurboSettings`: `string Get(string key, string defaultValue)` and `void Set(string key, string value)`, on `%LOCALAPPDATA%\TurboRemote\settings.txt`. A test-only `public static string FilePathOverride` lets tests point it at a temp file.
  - `public sealed class TunnelAutoSwitch`:
    - `TunnelAutoSwitch(string setting)` takes `on|off|auto`; anything else means auto.
    - `bool Update(TailscaleState state, TimeSpan sinceHostStart)`: returns whether the tunnel should be open.
    - Property `bool IsAutomatic`.
  - Rules: `on` → true and `off` → false, always. `auto`:
    - Running → false, and the not-running count resets.
    - Starting while `sinceHostStart` < 60 s → false (undecided).
    - Before the first decision, NotInstalled / SignedOut / Stopped, or Starting at ≥ 60 s → true immediately.
    - After it has been Running, a not-Running state must be seen on 2 consecutive `Update` calls before returning true.

- [ ] **Step 1:** Write the tests:
  - `on`/`off` are fixed whatever the state.
  - auto + Running → false.
  - auto + NotInstalled at t=0 → true.
  - auto + Starting at 10 s → false; then Starting at 61 s → true; then Running → false.
  - auto: Running → (Stopped) false → (Stopped) true → (Running) false.
  - auto: Running → Stopped → Running → Stopped → false (count reset).
  - `TurboSettings.Set("publicTunnel","off")` then `Get` returns `off`; other keys survive a Set.
- [ ] **Step 2:** Run `-Only TunnelPolicyTests`. Expected: compile FAIL.
- [ ] **Step 3:** Implement both classes.
- [ ] **Step 4:** Run `-Only TunnelPolicyTests`. Expected: PASS.
- [ ] **Step 5:** Commit → "Add settings store and Tailscale-aware tunnel switch".

### Task 5: Host Tailscale line and tunnel checkbox

**Files:**
- Modify: `StandaloneSource/TurboHostPortable.cs`: `InitializeUi` section 3 (grow the group box as needed), `AutoOpenFirewallUpnpAndTunnelAsync`, `StartBuiltInPublicSshTunnel`, `OnTunnelExited`, `ApplyAccessKeyToInternetAccess`, `StopServer`
- Test: `Tests/HostTunnelTests.cs`

**Interfaces:**
- Consumes: `TailscaleInfo.Query`, `TunnelAutoSwitch`, `TurboSettings`.
- Produces (Host-internal):
  - The field `_tunnelSwitch`.
  - `bool WantTunnel()`: the switch decision AND not refused for a weak key.
  - Every path that starts the tunnel goes through `WantTunnel()`.
  - A 30 s `System.Windows.Forms.Timer` re-checks Tailscale. The `Query` runs on the thread pool and the UI is updated through `BeginInvoke`, then the tunnel is started or stopped.
  - **Stopping is deferred while any session from a loopback address is active.** Count those sessions with `Interlocked` inside `HandleClientSessionAsync`, using `SharedAddressThrottle.IsSharedAddress`.
  - `OnTunnelExited` restarts the tunnel only if `WantTunnel()`.
  - Clicking the checkbox does `TurboSettings.Set("publicTunnel", on/off)`, rebuilds `_tunnelSwitch` and applies it at once.
  - The Copy button copies `DnsName`. "Get Tailscale" runs `Process.Start("https://tailscale.com/download")`.

- [ ] **Step 1:** Write the tests:
  - **On this PC (Tailscale Running), with `settings.txt` absent:** start the real Host with `--host --port P --pin <random 16-char strong key>`. After 8 s:
    - `Uia.FindText(hostWnd, "Tailscale ON")` contains the CLI's `DnsName`.
    - `Uia.IsChecked(hostWnd, "Also open a public tunnel") == false`, and the label contains `(automatic)`.
    - No `ssh.exe` process has `-R0:127.0.0.1:P ` in its command line (WMI).
  - Then `Uia.Toggle` the checkbox. Within 10 s an `ssh.exe` for that port exists and `settings.txt` contains `publicTunnel=on`. Toggle again: the ssh process is gone within 5 s and `publicTunnel=off`.
  - Kill the Host. Remove leftover ssh processes for port P.
- [ ] **Step 2:** Run `-Only HostTunnelTests`. Expected: FAIL (no Tailscale line or checkbox).
- [ ] **Step 3:** Implement it.
- [ ] **Step 4:** Run `-Only HostTunnelTests`. Expected: PASS.
- [ ] **Step 5:** Commit → "Host shows Tailscale address; public tunnel off while Tailscale runs".

### Task 6: Host drops silent viewers; TCP keep-alive

**Files:**
- Modify: `StandaloneSource/TurboHostPortable.cs`: `ClientSessionHandler.RunAsync` / `CommandReceiveLoopAsync`, and `HandleClientSessionAsync` (keep-alive)
- Modify: `StandaloneSource/TurboCorePortable.cs`: `NetworkHelper`
- Test: `Tests/HostTimeoutTests.cs`

**Interfaces:**
- Produces:
  - `public static void NetworkHelper.EnableKeepAlive(Socket s, int idleMs = 15000, int intervalMs = 5000)`, via `IOControl(IOControlCode.KeepAliveValues, …)`, swallowing errors.
  - `ClientSessionHandler` records the time of the last received packet.
  - A watchdog task checks every 5 s. After 45 000 ms of silence it logs `[Session] No data from the viewer for 45 s; closing the session.` and cancels the session. Existing teardown releases held input.

- [ ] **Step 1:** Write the tests against the real Host (`--pin 2580`, so it stays LAN-only):
  - Viewer A connects, pings for 5 s, then `GoSilent()`. `A.WaitForClose(60000)` is true. The close happens between 44 and 55 s after the last ping, and the Host log (`Uia.ListItems`) contains `No data from the viewer for 45 s`.
  - **Review Focus 3:** while A is silent but not yet closed, viewer B connects and signs in within 5 s.
  - Viewer C pings every second for 70 s and stays open.
  - **Real Tailscale path** (skipped with a note when `TailscaleInfo.Query()` is not Running): a fake viewer signs in to the Host through `Query().IPv4`, and again through `Query().DnsName`.
- [ ] **Step 2:** Run `-Only HostTimeoutTests`. Expected: FAIL (A is never closed).
- [ ] **Step 3:** Implement the watchdog and keep-alive. Keep-alive also goes on the viewer's socket in `InitializeActiveSessionAsync`.
- [ ] **Step 4:** Run `-Only HostTimeoutTests`. Expected: PASS.
- [ ] **Step 5:** Commit → "Host closes sessions whose viewer went silent".

### Task 7: Viewer dead-link detection and auto-reconnect

**Files:**
- Modify: `StandaloneSource/TurboClientPortable.cs`: `ConnectToHostAsync`, `InitializeActiveSessionAsync`, `ReceivePacketsLoopAsync`, `Disconnect`, `OnTelemetryTimerTickAsync`, `OnScreenCanvasPaint`, `ToggleConnectAsync`
- Modify: `StandaloneSource/TurboCorePortable.cs`: new `ReconnectSchedule`, `AuthRefusedException`
- Test: `Tests/ReconnectTests.cs`

**Interfaces:**
- Produces:
  - `public sealed class AuthRefusedException : Exception`. It is thrown for a `FAIL:` reply unless the text contains `busy` (case-insensitive); "busy" stays a normal `Exception`.
  - `public static class ReconnectSchedule`: `int DelayBeforeAttemptMs(int attempt)` (1-based: 1000, 2000, 3000, 5000, then 5000).
  - `public enum SessionEndReason { UserDisconnect, Cancelled, HostClosed, NetworkError, DeadLink, AuthRefused }`
  - `public static bool ReconnectSchedule.ShouldReconnect(SessionEndReason r)`: true only for HostClosed, NetworkError and DeadLink.
  - The viewer keeps `_reconnectTarget`: typed input, port, key, expected machine name. It is set after a successful sign-in, but not for Listen Mode sessions.
  - On an unexpected end it runs `ReconnectLoopAsync(CancellationToken)`. Each attempt dials `typedInput + "," + expectedMachineName` (when they differ) through `RaceFastestConnectionAsync`, then signs in. A different machine name closes that connection and counts as a failed attempt. `AuthRefusedException` stops the loop and shows its message.
  - Dead link: `_lastPacketTicks` is updated per received packet. The 1 s telemetry tick ends the session with DeadLink after 20 000 ms of nothing.
  - While reconnecting:
    - the session is torn down with the existing `Disconnect` logic (keyboard capture off, hook removed, held input cleared), except that `_remoteBitmap` is kept
    - the last frame is painted dimmed, with the banner text from Global Constraints
    - the Connect button reads `✖ Stop reconnecting`, and clicking it cancels the loop and resets
  - On success the telemetry shows `● RECONNECTED after {s} s` for 5 s.

- [ ] **Step 1:** Write the tests with the real viewer (`--client --connect 127.0.0.1 --port P --pin 2580`) and `FakeHost(P, "2580", "FAKE-PC")`. Read the state via `Uia.WindowTitle` and `Uia.FindText(viewerWnd, "●")`, and the button via `Uia.FindText(viewerWnd, "✖ Stop reconnecting")`:
  - **Dead link:** connected → `Responding=false`. The viewer shows "reconnecting" between 18 and 26 s. Then `Responding=true` plus `CloseSession()` → the viewer reconnects (`SessionsAccepted == 2`) and the telemetry shows `RECONNECTED`.
  - **Host closes:** `CloseSession()` → `SessionsAccepted == 2` within 5 s.
  - **Review Focus 2:** `StopListening()` plus `CloseSession()`, wait 65 s, then `StartListening()` → reconnected within 7 s.
  - **Wrong key:** after connecting, set `FailText = "Invalid Access Key/PIN (Attempt 1/5)"` and `CloseSession()` → exactly one more sign-in attempt in the next 20 s, and the telemetry contains `Invalid Access Key`.
  - **Review Focus 1:** `FailText = "Host is busy checking other sign-ins from the Internet. Try again in a minute."` → at least 3 attempts within 20 s; then clear `FailText` → reconnected.
  - **Stop:** while reconnecting, `Uia.Invoke(viewerWnd, "Stop reconnecting")` → no new attempts for 15 s, and the button reads `⚡ Connect to Host`.
  - **Different PC:** a second fake host answering with machine `OTHER-PC` → not accepted as the session (title still names `FAKE-PC` after the real one returns).
  - **Review Focus 5:**
    - The viewer is launched with `--connect 127.0.0.1:P2 --port P`. `FakeHost(P2, …, "localhost")` accepts it.
    - Then that host `StopListening()` + `CloseSession()`. `FakeHost(P, …, "localhost")` starts.
    - The viewer reconnects to port P through the machine name (the second host's `SessionsAccepted == 1`).
- [ ] **Step 2:** Run `-Only ReconnectTests`. Expected: FAIL (no reconnect).
- [ ] **Step 3:** Implement it.
- [ ] **Step 4:** Run `-Only ReconnectTests`. Expected: PASS.
- [ ] **Step 5:** Commit → "Viewer reconnects on its own and detects dead links".

### Task 8: Viewer Tailscale status, failure hint, README, version

**Files:**
- Modify: `StandaloneSource/TurboClientPortable.cs`: the telemetry text while disconnected, and the `ConnectToHostAsync` failure message
- Modify: `StandaloneSource/TurboCorePortable.cs` (`AppVersion` = `2026.11.24`), `README.md`
- Test: `Tests/ViewerStatusTests.cs`

**Interfaces:**
- Consumes: `TailscaleInfo.Query` (off the UI thread, every 30 s while disconnected).
- Produces:
  - While disconnected, the telemetry reads `● DISCONNECTED | Tailscale: ON ({HostName})`, or `Tailscale: not connected` / `Tailscale: not installed`.
  - A connect failure on input with no `.`, no `:` and not `AUTO` appends the failure hint.

- [ ] **Step 1:** Write the tests:
  - The viewer started with no `--connect`: telemetry contains `Tailscale: ON` (if the CLI exists and reports Running).
  - The viewer with `--connect NO-SUCH-PC-12345 --port <free> --pin 2580`: within 20 s the telemetry contains `install Tailscale on both PCs` and the reconnect loop did not start (no `Stop reconnecting` button).
  - `TurboRemote.exe --self-test` still passes, and the window titles contain `v2026.11.24`.
- [ ] **Step 2:** Run `-Only ViewerStatusTests`. Expected: FAIL.
- [ ] **Step 3:** Implement it.
- [ ] **Step 4:** README: add a "Recommended: Tailscale (free, permanent address)" section before Method 1 covering install, signing in on both PCs with the same account, and connecting by Host ID. Re-describe pinggy as a fallback that is off while Tailscale runs and whose address changes hourly. Mention auto-reconnect.
- [ ] **Step 5:** Run `-Only ViewerStatusTests`. Expected: PASS.
- [ ] **Step 6:** Commit → "Viewer shows Tailscale status; README recommends Tailscale".

### Task 9: Full run and shipped exe

- [ ] **Step 1:** Run `powershell -ExecutionPolicy Bypass -File .\Tests\run-tests.ps1`. Expected: every test prints `ALL CHECKS PASSED`, exit 0.
- [ ] **Step 2:** Confirm `TurboRemote.exe` at the repo root is the build from step 1, then `git status` (only intended files).
- [ ] **Step 3:** Commit → "Rebuild TurboRemote.exe v2026.11.24".
- [ ] **Step 4:** Report to the user what needs the real two-PC test: a session over an hour, a Wi-Fi switch mid-session, and Host sleep/wake.
