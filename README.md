# ⚡ TurboRemote — Ultra-Low Bandwidth Windows Remote Suite

**TurboRemote** is a high-speed, zero-dependency, single-file Windows remote control application (`138 KB`) engineered to run smoothly even on very slow internet connections (`2G / 3G / congested Wi-Fi / long-distance WAN`) while providing full **Administrator (UAC) control**, **AES-256 End-to-End Encryption**, **Remote PowerShell**, and **Compressed File Transfer**.

👉 **Executable Location:** [`TurboRemote.exe`](file:///C:/Users/Parvezkhan%20Pathan/.gemini/antigravity/scratch/TurboRemote/TurboRemote.exe)

---

## 📦 1. Quick Setup (Only 1 File Needed on Both PCs)

You do **not** need to install Python, .NET Core, or any third-party libraries. `TurboRemote.exe` runs natively on every Windows 10 and Windows 11 PC out of the box.

1. Copy **[`TurboRemote.exe`](file:///C:/Users/Parvezkhan%20Pathan/.gemini/antigravity/scratch/TurboRemote/TurboRemote.exe)** to both Windows computers:
   - **Host PC** (the computer you want to control remotely)
   - **Client PC** (the computer you are sitting at)
2. Double-click **`TurboRemote.exe`**. A launcher window appears with 2 buttons:
   - **🖥 Share this PC** — click this on the Host PC.
   - **⚡ Control another PC** — click this on the Client PC.

---

## 🖥 2. Setting Up the Host PC (`Share this PC`)

Once you click **🖥 Share this PC**, sharing starts right away. The **Connect to this PC** box shows everything the other PC needs:

1. **Host ID** (this PC's name, e.g. `WM-DEV-07`) with a **Copy** button, and its LAN address.
2. **Access key**:
   - There is no shared default PIN any more. On first run the Host generates a random access key (e.g., `K9X4-M7P2`) and saves it for your Windows user in `%LOCALAPPDATA%\TurboRemote\access_key.txt`, so it stays the same on every start. Give this key to whoever connects.
   - Click **`🎲 New key`** for a new random key. A key you generate or type in the Host window is saved for the next start (a key passed with `--pin` is used for that run only).
   - **Internet access needs a strong key**: at least 8 characters (dashes and spaces don't count) with at least 4 different characters. With a weaker key, such as the old `2580`, the Host stays LAN-only: it opens no Internet tunnel, publishes nothing under its Host ID, and the log says why. Change the key while sharing and Internet access turns on or off by itself.
3. **Tailscale** (recommended, see Case B): `Tailscale ON — from anywhere, connect to: WM-DEV-07`, or a **Get Tailscale** button when it is not installed.
4. **Also allow viewers without Tailscale (public tunnel)**: the built-in fallback tunnel (`tcp://xxxx.run.pinggy-free.link:PORT`). It opens by itself when Tailscale is **not** running on this PC; tick the box to open it anyway. The free tunnel's address changes every 60 minutes; the Host reconnects and re-publishes it on its own.

Also in the Host window:
- **`🛡 Run as admin`** (top right; hidden once the Host runs as admin) — needed to control elevated windows, Task Manager, UAC prompts, and to lock the Host's keyboard/mouse. As admin, the Host also opens Windows Firewall for its port automatically.
- **Start sharing automatically when I sign in to Windows** — creates (or removes) a Windows Scheduled Task (`/RL HIGHEST`) that starts `TurboRemote.exe` in Host mode at every sign-in. Needs admin.
- Behind the scenes: TCP port `9876` (change it with `--port`), LAN discovery on UDP `9878`, and your encrypted endpoints published under your **Host ID** whenever they change (refreshed every 3 hours) while the key is strong enough.

---

## 🌍 3. How to Connect from the Client PC (`Control another PC`)

`TurboRemote.exe` tries the routes in order of quality: **Local LAN and Tailscale first**, and the hourly public tunnel only if neither answers.

### Case A: Both PCs Are on the Same Wi-Fi / Office LAN (Fastest: `< 1 ms`, 60 FPS)
1. Open `TurboRemote.exe` on the Client PC and click **⚡ Control another PC**.
2. In the **Host ID or address** box, either:
   - Leave it as **`AUTO`** (automatically discovers the Host on your local network via UDP broadcast), **OR**
   - Type the Host's **Host ID** (e.g., `WM-DEV-07`) or **Local IP** (e.g., `192.168.1.56`). Add `:port` only if the Host uses a port other than `9876`.
3. Enter the Host's **access key** (shown in the Host window, e.g. `K9X4-M7P2`) in the **Access key** box and click **`⚡ Connect`**.
   -*(If you ever make a typo or want to abort while connecting, click the orange **`✖ Stop / Cancel`** button to cancel immediately.)*

---

### Case B: Connecting Over the Internet (Different Wi-Fi / Anywhere in the World)

#### ⭐ Recommended: Tailscale (free, permanent address, sessions that last)
[Tailscale](https://tailscale.com/download) is a free app that puts your PCs on one private network, wherever they are. Each PC gets an address and name that **never change**, and the connection is direct and encrypted, with no router setup. It has a free plan for personal use.
1. **Install Tailscale on the Host PC and on the Client PC**, and **sign in to the same Tailscale account** on both.
2. **On the Host PC**: start the Host as usual. The **Connect to this PC** box shows `Tailscale ON — from anywhere, connect to: WM-DEV-07  (wm-dev-07.tailXXXX.ts.net · 100.x.y.z)`. If it says Tailscale is not connected, open the Tailscale app and sign in.
3. **On the Client PC**: type the Host's **Host ID** (e.g., `WM-DEV-07`) into **Host ID or address**, enter the **access key**, and click **`⚡ Connect`**. The viewer tries the office LAN and Tailscale first, so you connect the same way in the office and away from it.
4. The address never changes and the session never expires. While Tailscale runs, the Host keeps the public tunnel below switched **off**, so the Host is reachable only from your own Tailscale devices and the LAN.

> **Sessions reconnect by themselves.** If the connection drops (Wi-Fi switch, network change, the Host PC restarting its network), the viewer keeps the last picture on screen, shows *Connection lost — reconnecting…*, and retries every few seconds until the session is back. Click **`✖ Stop reconnecting`** to give up. A wrong access key is never retried. A connection that goes silent is noticed within 20 s by the viewer and 45 s by the Host.

#### Fallback: Built-In Public Tunnel (for viewer PCs without Tailscale; address changes every hour)
1. **On the Host PC**: the tunnel opens by itself when Tailscale is **not** running on the Host. With Tailscale running, tick **`Also allow viewers without Tailscale`** in the **Connect to this PC** box (the choice is remembered).
   - This uses Windows' built-in OpenSSH Client (`ssh.exe`) in the background to open a public TCP relay (`tcp://xxxx.run.pinggy-free.link:PORT`). On first use it creates its own sign-in key in `%LOCALAPPDATA%\TurboRemote\tunnel_ed25519`. If the log says `ssh.exe` could not be started, add the **OpenSSH Client** optional feature in Windows Settings.
   - Within 3–5 seconds, the Host log will show:
     `[PUBLIC INTERNET TUNNEL ACTIVE] -> xxxx.run.pinggy-free.link:12345`
     `[Cloud Discovery READY] Client can now type Host ID 'YOUR-PC-NAME' and access key 'K9X4-M7P2' from anywhere on the Internet!`
   - If the log instead says `Internet access is OFF: the access key is too weak...`, click **`🎲 New key`** (or type a stronger key) and the tunnel starts by itself.
2. **On the Client PC**:
   - Type the Host's **Host ID** (e.g., `WM-DEV-07`) into **Host ID or address**. The tunnel address itself works too, but it changes every hour, so the Host ID is the better choice.
   - Enter the Host's **access key** and click **`⚡ Connect`**. Finding a Host by its Host ID needs this TurboRemote version (or newer) on both PCs.
   - pinggy cuts the free tunnel every 60 minutes. The viewer then reconnects on its own (by Host ID) through the tunnel's new address.

---

## ⚡ 4. Choosing the Picture Quality (Top Bar)

You can switch quality live during a session from the **Quality** list:

| Quality | Resolution | Color Mode | Target FPS | Best For |
| :--- | :---: | :---: | :---: | :--- |
| **`Auto` (Default)** | `42% – 100%` | Auto Color / Gray | `20 – 60 FPS` | **Recommended.** Runs at 60 FPS on LAN/Fast links and automatically scales down if internet slows down. |
| **`2G`** | `42%` | 16-Level Grayscale | `22 FPS` | Extreme slow internet (`10–25 KB/s`), mobile hotspot, or heavy packet loss. |
| **`3G`** | `55%` | Color (JPEG Q=35) | `32 FPS` | Weak Wi-Fi or long-distance international connections. |
| **`4G`** | `75%` | Color (JPEG Q=52) | `45 FPS` | Everyday remote work over standard internet. |
| **`LAN`** | `100%` (Full HD) | Color (JPEG Q=78) | `60 FPS` | Local LAN or high-speed fiber internet. |

> **Zero Idle Bandwidth (`0.0 KB/s`)**: Because `TurboRemote` uses an **8×8 Block-Delta Screen Encoder**, static parts of the screen are never re-sent. When the remote screen is still, bandwidth drops to **`0.0 KB/s`**, and every mouse click or keystroke triggers an instant `< 2 ms` interrupt capture.

---

## 🛠 5. Built-In Remote Tools & Shortcuts

### Session Toolbar (Viewer, shown while connected)
- **`↻ Refresh`**: Sends a fresh full picture of the remote screen.
- **`📋 Send clipboard` / `📥 Get clipboard`**: Copies clipboard text to or from the remote PC.
- **`🔒 Lock host input`**: Locks the physical mouse and keyboard on the Host PC (requires Host running as Admin).
- **`🛡 Restart host as admin`**: Restarts the Host in Administrator mode (someone at the Host confirms the UAC prompt).
- **`Screen 1 / 2 / …`**: Switches between the Host's monitors (shown only when the Host has more than one).
- Task Manager and Show Desktop need no buttons: press `Ctrl+Shift+Esc` or `Win+D` and they happen on the remote PC.

### Workspace Tabs
1. **🖥 Remote Desktop**: Real-time 60 FPS delta-tile screen view with `120 Hz` mouse tracking and full keyboard injection.
2. **💻 PowerShell**: Run administrative PowerShell commands instantaneously using `< 1 KB` of compressed text—works even when video bandwidth is near zero.
3. **📁 Files**: Browse remote folders (double-click any `[DIR]` to open), upload local files to the Host, and download remote files to your `Downloads\TurboRemote` folder with automatic stream compression.

### Keyboard in the Remote Desktop
While the remote screen has keyboard focus (click it once), **every key goes to the remote PC**, including the Windows key and Win+combos, `Alt+Tab`, `Ctrl+Tab`, `Ctrl+Esc`, `Alt+Space`, `Alt+F4` and `Print Screen`. Switching tabs, using the toolbar, or clicking another program moves the keyboard back to your PC as expected.
- **No stuck keys:** whenever the remote screen loses focus (another tab, `Alt+Tab` away, a dialog), any key or mouse button you were still holding is released on the remote PC. If the connection drops, the Host releases them itself.
- **Caps / Num / Scroll Lock** work on your PC as usual, and the remote PC is kept in the same state.
- **Stays on your PC:** `Win+L` (locks *your* PC; a locked remote PC could not be unlocked through TurboRemote), volume and media keys, and `Ctrl+Alt+Del` (Windows never lets a program capture it).
- If a viewer sends `Alt+F4` while the TurboRemote Host window itself is focused, the Host asks before closing (`No` is the default), so the remote PC isn't lost by accident.

---

## 🔒 6. Security Architecture

- **Zero-Knowledge Challenge-Response Handshake**: Your PIN/Access Key is **never** sent over the network. The Host sends a 32-byte random salt + challenge, and both sides derive a 512-bit key using **PBKDF2-SHA256 (`15,000 iterations`)**.
- **AES-256-CBC + Encrypt-Then-MAC (`HMAC-SHA256`)**: Every screen frame, mouse click, keystroke, PowerShell output, and file chunk is encrypted with AES-256 (fresh CSPRNG IV per packet) and verified with HMAC-SHA256.
- **Anti-Replay**: Every packet embeds an authenticated 64-bit sequence counter to block replay attacks.
- **Brute-Force Protection**:
  - LAN, Tailscale and direct connections: any IP that enters a wrong key 5 times is locked out for 5 minutes.
  - Internet tunnel (and anything else arriving from `127.0.0.1`): every tunnel user shares the address `127.0.0.1`, so nobody is locked out by address. Instead keys are checked one at a time, and after repeated wrong keys the checks are spaced out (1, 2, 4, 8, 16, then 30 seconds), so the whole Internet together gets at most one guess every 30 seconds. A user with the right key still gets in at their turn, which clears the slowdown; 10 quiet minutes clear it too. While someone floods the tunnel with sign-ins, others may see "Host is busy" and need to retry.
- **Strong Keys for the Internet**: Every Host gets its own random access key; a weak key keeps the Host off the Internet (see Section 2).
- **Private Host ID Directory**: The cloud directory entry is found and encrypted with a key derived from Host ID + access key through PBKDF2-SHA256 (`100,000 iterations`), so neither the directory service nor anyone else can test access keys quickly offline.

---

## 💻 7. Command-Line Automation & Verification

You can also launch `TurboRemote.exe` directly via command line or batch scripts:

```powershell
# Start directly in Host Server mode on port 9876 with this user's saved access key:
.\TurboRemote.exe --host --port 9876

# ...or with a specific access key for this run only:
.\TurboRemote.exe --host --port 9876 --pin K9X4-M7P2

# Start directly in Viewer Client mode and auto-connect to a Host:
.\TurboRemote.exe --client --connect WM-DEV-07 --port 9876 --pin K9X4-M7P2

# Run the built-in automated diagnostic self-test (Screen Capture + AES-256 E2EE + Internet hardening checks):
.\TurboRemote.exe --self-test
```
