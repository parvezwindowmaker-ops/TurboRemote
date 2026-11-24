# UI cleanup — design (short)

Date: 2026-11-24 · Status: approved by the user ("ok do it")

## Intent

The three windows grew controls from earlier experiments. Some are now redundant (Tailscale and the new keyboard
handling cover them) and one never worked. The user asked to "make the UI more sensible and remove things which are
not required". Success: each window shows only what a person needs to share a PC or control one, in plain words.

## Approaches

| Approach | Outcome |
|---|---|
| Restyle everything (new layout system, themes) | Not chosen: the request is about clutter, not looks |
| Hide extras behind an "Advanced" panel | Not chosen: it keeps the dead and redundant features alive |
| **Remove what is broken or redundant, rename the rest plainly, keep the layout style** | **Chosen** (proposal approved) |

## Launcher
- Keep **Share this PC** and **Control another PC**.
- Remove **Launch Both**: on one PC the keyboard can never be used, which only confuses.
- A shorter subtitle.

## Host window
- Header: title + version, admin badge, and a **Run as admin** button that is hidden once elevated.
- **Connect to this PC** box:
  - Host ID (machine name, read-only) with a LAN address hint and **Copy**
  - access key with **🎲 New key**
  - the Tailscale line (+ Copy name / Get Tailscale)
  - the public-tunnel checkbox: `Also allow viewers without Tailscale (public tunnel, address changes every hour)`
- **Start / Stop sharing** button and status line.
- **Start sharing automatically when I sign in to Windows** checkbox, replacing the install button. It creates or
  removes the scheduled task and needs admin rights.
- A log.
- Removed:
  - the port box (`--port` still works; default 9876)
  - **Open Windows Firewall** (the rule is still added automatically when running as admin)
  - **Auto-Map UPnP** (it never mapped a port: it only found the router), together with its start-up probe and the
    code behind it
  - the **Reverse WAN Connect** panel and its code (Tailscale solves CGNAT)
  - the endpoints list and **Copy / Publish ID** (publishing is automatic)
  - the marketing subtitle

## Viewer window
- One row: `Host ID or address` · `Access key` · **⚡ Connect** · `Quality` (Auto · 2G · 3G · 4G · LAN, Auto
  first) · status.
- A second row, shown only while connected:
  - **↻ Refresh**, **📋 Send clipboard**, **📥 Get clipboard**, **🔒 Lock host input**, **🛡 Restart host as admin**
  - a **Screen** picker, only when the Host reports more than one screen (AuthResult field 4)
- Tabs: **🖥 Remote Desktop**, **💻 PowerShell**, **📁 Files**.
- Removed:
  - the Port box (`host:port` can still be typed; `--port` still works)
  - **Listen Mode** (the viewer half of reverse connect)
  - the **Gray** checkbox (the 2G quality uses gray)
  - **Task Mgr** and **Win+D** (the keys now reach the remote PC directly)

## Constraints
- The protocol does not change, and older builds on the other PC keep working. With the Gray box gone, the
  ConfigUpdate grayscale byte is always 0.
- Status words the tests and users rely on stay: `LIVE`, `RECONNECTED`, `Tailscale: ON`, `✖ Stop reconnecting`.

## Left to the builder
Exact positions and sizes within the existing dark style; whether the second row hides or collapses.
