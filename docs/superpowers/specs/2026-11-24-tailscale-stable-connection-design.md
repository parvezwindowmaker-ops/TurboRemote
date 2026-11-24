# Tailscale-first stable connection — design

Date: 2026-11-24 · Status: reviewed, awaiting the user's approval

## Intent

Remote sessions over the Internet must stay up for hours and use an address that never changes, the way
RustDesk feels. Today they drop and the address changes:

1. Internet sessions run through the free pinggy tunnel. pinggy ends it every 60 minutes, which kills the
   live session and gives the Host a new address.
2. The viewer never reconnects by itself after any drop (tunnel cut, Wi-Fi blip, network change).
3. A silently dead connection is never detected. The viewer pings every second but never gives up waiting,
   so the picture can freeze with no error. The Host never times a viewer out either.

The user chose **Tailscale** (free) as the permanent network: every PC in their Tailscale account gets a
fixed address (`100.x.y.z`) and a fixed name (MagicDNS, e.g. `wm-dev-016.tail71ae0c.ts.net`, or just
`WM-DEV-016`). Tailscale handles NAT traversal and roaming and falls back to its own relays, so it already
gives what RustDesk's ID server and relay give. No server, no domain, no cost.

## Goals

- With Tailscale on both PCs, the viewer reaches the Host by its Host ID (machine name) from anywhere, over
  Tailscale, and that route never expires or changes.
- A session that drops for any reason comes back on its own, without the user clicking Connect again.
- A dead connection is noticed within seconds on both sides.
- Both windows show clearly whether Tailscale is set up, and what to do when it isn't.

## Non-goals

- Our own rendezvous/relay server, UDP hole punching, Cloudflare, paid pinggy.
- Running the Host before Windows sign-in (that would need a Windows service).
- Changing the encryption or sign-in protocol.
- Installing or signing in to Tailscale for the user (TurboRemote only detects it and points to it).

## Anti-goals (would make it a failure even if it "works")

- A reconnect loop that keeps retrying a **wrong access key**. That trips the Host's lockout and locks the
  user out.
- A session that silently goes through the hourly-expiring pinggy tunnel when Tailscale was available.
- False "connection lost" on slow links (README promises 2G/3G use): a large keyframe can take several
  seconds to arrive there.
- Exposing the Host publicly when nothing needs it.

## Constraints

- Single-file .NET Framework 4 WinForms app, C# 5 (inbox `csc.exe`), no NuGet, Windows 10/11.
- Must stay compatible with older builds on the other PC (older Host or older viewer).
- Tailscale CLI lives at `%ProgramFiles%\Tailscale\tailscale.exe`; `tailscale status --json` gives
  `BackendState` (`Running`, `NeedsLogin`, `Stopped`...), `Self.DNSName`, `Self.HostName`,
  `Self.TailscaleIPs`, `CurrentTailnet.MagicDNSEnabled`. Tailscale addresses are in `100.64.0.0/10`.
- Testing must never drive a real Host and a real viewer together on one PC (the Host moves the real
  cursor; see project memory). Use a fake counterpart.

## Approaches considered

| Approach | Trade-off | Outcome |
|---|---|---|
| Own relay server (RustDesk-style) | Fully ours and stable, but needs an always-on public machine | Not chosen (no free host) |
| Cloudflare Tunnel on the user's domain | Free, nothing extra on viewers, but needs `cloudflared` plus per-PC dashboard setup, and the terms are vague on heavy traffic | Not chosen |
| **Tailscale** | Free, direct and fastest, stable name/IP, nothing to host; every PC needs the Tailscale app and the same account (free plan: 3 users) | **Chosen by the user** |

## Design

### 1. Tailscale detection (Host and viewer)

A shared `TailscaleInfo` helper in `TurboCorePortable.cs`:

- Runs `tailscale.exe status --json` (hidden, ~3 s timeout) when the CLI exists. Takes `BackendState`,
  `Self.DNSName` (trailing dot removed), `Self.HostName`, and the first IPv4 of `Self.TailscaleIPs`.
- If the CLI is missing or fails, falls back to the network adapter: an interface named or described as
  "Tailscale" with an IPv4 in `100.64.0.0/10` (state then "Running" if that adapter is up).
- States: **Running**, **SignedOut** (installed but `NeedsLogin`/`NoState`), **Stopped**, **NotInstalled**.
- Refreshed when the window opens, every 30 s while it is open, and when the user clicks a refresh link.

*My call: parse the JSON with simple string search for those few fields (no `System.Web.Extensions`
dependency). It is only a handful of top-level keys.*

### 2. Host window

- Section 3 ("Connection Endpoints") gets a Tailscale line at the top:
  - Running: `Tailscale ON — from anywhere, connect to: WM-DEV-016  (wm-dev-016.tail71ae0c.ts.net · 100.77.50.110)`,
    with a "Copy" button that copies the MagicDNS name.
  - SignedOut/Stopped: `Tailscale is installed but not connected — open Tailscale and sign in.`
  - NotInstalled: `For a permanent Internet address, install Tailscale on this PC and the viewer PC (same account).`
    with a "Get Tailscale" button that opens `https://tailscale.com/download` in the browser.
- **Public pinggy tunnel becomes opt-in when Tailscale is running.** A new checkbox in section 3:
  `Also open a public tunnel for viewers without Tailscale (address changes every hour)`.
  - Default: **off** when Tailscale is Running at Host start; **on** when it is not (today's behaviour,
    still subject to the strong-key rule).
  - Toggling it starts or stops the tunnel at once. The choice is saved per user
    (`%LOCALAPPDATA%\TurboRemote\settings.txt`, key `publicTunnel=on|off|auto`, default `auto`).
  - **Decided by the user (2026-11-24): off when Tailscale is running.** With both PCs on Tailscale the
    tunnel only adds public exposure and sessions that die hourly. A viewer PC away from the office then
    needs Tailscale too.
  - **`auto` follows Tailscale while the Host runs** *(my call)*:
    - At Host start, if Tailscale is installed but still starting (`NoState`/`Starting`, typical at sign-in
      when the unattended task runs), wait up to 60 s for it to settle before deciding. If it doesn't
      settle, decide as "not running".
    - Then re-check every 30 s. Tailscale Running → tunnel off. Not Running on two checks in a row (≥ 60 s)
      → tunnel on (strong key still required).
    - Turning the tunnel off never cuts a live session: if a viewer is connected through it (a loopback
      peer), the tunnel closes only after that session ends.
    - The tunnel's restart watchdog (`OnTunnelExited`) restarts it only while the current setting wants it
      open.
    - The checkbox shows the effective state, and its label adds "(automatic)" while the setting is `auto`.
      Clicking it switches to a fixed `on`/`off`.
- The cloud directory (ntfy) keeps publishing the ticket. It already lists every IPv4 including the
  Tailscale one, and needs no change beyond the tunnel being absent when off.

### 3. Viewer route choice

`SmartRouteEngine.RaceFastestConnectionAsync` races all candidates at once today. A pinggy endpoint can
win the TCP race (its edge answers fast) and put the session on the hourly-expiring route. New order:

1. **Direct routes first, started as soon as they are known.** These are the Host ID typed by the user
   (resolved by Windows; with MagicDNS this reaches the Tailscale address when away from the office), LAN
   discovery results, and the LAN/Tailscale (`100.64.0.0/10`) addresses from the cloud ticket. The race no
   longer waits for the cloud lookup to finish before dialling the routes it already has. Ticket
   addresses join the running race when the lookup returns.
2. **Public tunnel candidates are held back.** A tunnel candidate starts at the later of (a) its ticket
   arriving and (b) 2.5 s after the race started, and only if no direct route has connected by then.
3. **Timing** *(my call)*: every candidate gets its own connect window of up to 6 s from the moment it
   starts, so a held-back tunnel is not starved on 2G/3G. The attempt gives up when every candidate has
   failed, or 15 s after the race started, whichever comes first. The user can cancel at any time, as
   today.

A typed `xxxx.pinggy-free.link:port` is still honoured as a direct candidate, since the user asked for it
explicitly. The window title already shows the winning route; Tailscale routes are labelled
"Tailscale (permanent)".

### 4. Dead-connection detection

- **Viewer:** tracks the time of the last packet received (any type; the Host answers every 1 s ping with a
  Pong, and sends frames). If nothing has arrived for **20 s**, the session is declared lost and goes to
  auto-reconnect (section 5). *20 s rather than less: on 2G links a keyframe can take several seconds and
  Pongs queue behind it.*
- **Host:** tracks the last packet received from the viewer (viewers of every version ping each second).
  If nothing has arrived for **45 s**, it closes that session. That releases held keys and buttons (already
  implemented) and frees the Host for the next viewer.
- Both sides enable TCP keep-alive on the session socket (first probe after 15 s idle, every 5 s) as a
  second safety net.

### 5. Viewer auto-reconnect

- Starts when a live session ends without the user asking: the Host closed it, a read/write error, or the
  20 s dead-link check. It does **not** start on a user Disconnect, a cancelled connect, or a **sign-in
  refusal** (wrong key, locked out). A refusal shows its message and stops, so the lockout is never tripped
  by retries.
- While reconnecting:
  - The last remote picture stays on screen, dimmed, with a banner:
    `Connection lost — reconnecting to WM-DEV-016 (attempt 3, next try in 5 s)…  [Stop]`.
  - The Connect button reads "✖ Stop reconnecting".
  - Keyboard capture is off and held keys were already released (existing focus-loss logic). Mouse and
    keys are not forwarded until the session is back.
- Each attempt runs the normal connect flow with the same port and key (route discovery included, so a
  network change moves to whatever route works now). **What it dials** *(my call)*:
  - The Host input the user typed, **plus the Host's own machine name** as reported at sign-in (field 2 of
    AuthResult). The name is looked up by Host ID as usual: Windows/MagicDNS name resolution and the cloud
    directory.
  - So a session started from a pinggy address typed by hand still comes back after pinggy's hourly cut:
    the dead address fails, and the name finds the Host over Tailscale, LAN or its new tunnel address.
  - After each reconnect the reported machine name must match the original. If a different PC answers
    (possible with `AUTO` or a LAN address reused by another PC), that connection is closed and retrying
    continues.
- Delays between attempts: 1 s, 2 s, 3 s, 5 s, then every 5 s, **with no attempt limit** until the user
  clicks Stop or Disconnect. *My call: long unattended sessions are the point; a capped retry would leave
  the user to notice and click Connect again.*
- On success: the banner goes, the profile/monitor settings are sent again (already done on every connect),
  focus returns to the remote screen if that tab is shown, and the telemetry line reads
  `● RECONNECTED after 12 s`.
- Viewers using Listen Mode (reverse connect) are excluded. There the Host dials in and the viewer has
  nothing to redial.

### 6. Messages that point to Tailscale

When a connect attempt by Host ID fails while the user is away from the Host's LAN, the error adds:
`If this PC is outside the Host's network, install Tailscale on both PCs and sign in to the same account.`
The viewer also shows its own Tailscale state in its status line when not connected (Running / not
installed).

### 7. README

A new "Recommended: Tailscale (free, permanent address)" section ahead of the other Internet methods.
It explains install, sign in on both PCs with the same account, and connect by Host ID. The pinggy method
is described as a fallback whose address changes hourly.

## Compatibility

- Older Host + new viewer: the viewer still connects (any route). Dead-link detection works because old
  Hosts answer pings. The old Host itself never times out a dead viewer.
- New Host + older viewer: works. The Host's 45 s timeout is safe because old viewers ping every second.
- No protocol change is needed.

## Testing

- **Unit-style (no UI input):** route ordering (direct before tunnel, 2.5 s gate); Tailscale JSON parsing
  against this PC's real output and canned NeedsLogin/NotInstalled cases; the 100.64.0.0/10 check.
- **Fake Host tests against the real viewer (no keyboard driving):**
  - The fake Host stops answering, and the viewer must show "reconnecting" within ~20 s and then reconnect
    when the fake Host comes back.
  - The fake Host closes the socket, and the viewer reconnects.
  - The fake Host refuses the key, and the viewer must not retry.
  - Stop while reconnecting, and no further attempts are made.
- **Fake viewer against the real Host:** the viewer goes silent, and the Host must close the session within
  ~45 s and log it. The tunnel checkbox defaults to off when Tailscale is Running.
- **Real Tailscale path on this PC:** a fake viewer connects to the Host through `100.77.50.110` and through
  the MagicDNS name.
- **Left to the user:** a real two-PC test over Tailscale, including a long session (an hour or more) and
  switching Wi-Fi/network on the viewer PC mid-session.

## Left to the builder

Exact UI layout and colours within the existing style; how the reconnect banner is drawn over the remote
picture; refactoring of `ConnectToHostAsync` needed to reuse it for reconnect attempts; the settings-file
format beyond the one key above.
