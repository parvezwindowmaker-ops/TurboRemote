# ⚡ TurboRemote — Ultra-Low Bandwidth Windows Remote Suite

**TurboRemote** is a high-speed, zero-dependency, single-file Windows remote control application (`138 KB`) engineered to run smoothly even on very slow internet connections (`2G / 3G / congested Wi-Fi / long-distance WAN`) while providing full **Administrator (UAC) control**, **AES-256 End-to-End Encryption**, **Remote PowerShell**, and **Compressed File Transfer**.

👉 **Executable Location:** [`TurboRemote.exe`](file:///C:/Users/Parvezkhan%20Pathan/.gemini/antigravity/scratch/TurboRemote/TurboRemote.exe)

---

## 📦 1. Quick Setup (Only 1 File Needed on Both PCs)

You do **not** need to install Python, .NET Core, or any third-party libraries. `TurboRemote.exe` runs natively on every Windows 10 and Windows 11 PC out of the box.

1. Copy **[`TurboRemote.exe`](file:///C:/Users/Parvezkhan%20Pathan/.gemini/antigravity/scratch/TurboRemote/TurboRemote.exe)** to both Windows computers:
   - **Host PC** (the computer you want to control remotely)
   - **Client PC** (the computer you are sitting at)
2. Double-click **`TurboRemote.exe`**. A launcher window will appear with 3 buttons:
   - **🖥 SHARE THIS PC (Start Host Server)** — Click this on the Host PC.
   - **⚡ CONTROL A REMOTE PC (Start Viewer Client)** — Click this on the Client PC.
   - **🚀 Launch Both** — Click this to test both Host and Client on a single PC.

---

## 🖥 2. Setting Up the Host PC (`SHARE THIS PC`)

Once you click **🖥 SHARE THIS PC (Start Host Server)**:

1. **Server Auto-Starts on Port `9876`**:
   - The server immediately starts listening on TCP port `9876` (and UDP LAN Discovery port `9878`).
2. **Set or Generate Your Access Key / PIN**:
   - The default PIN is **`2580`**.
   - Click the **`🎲`** button next to the PIN box to generate a random cryptographic Access Key (e.g., `K9X4-M7P2`).
3. **Grant Full Administrator Permissions (Recommended)**:
   - Click **`🛡 Elevate to Admin (UAC)`** so you can remotely control elevated windows, Task Manager, UAC prompts, and lock the remote keyboard/mouse.
   - Click **`🔓 Open Windows Firewall`** once (after elevating) so Windows Firewall allows incoming connections on port `9876`.
4. **Optional — Auto-Start When Windows Boots**:
   - Click **`⚙ Install Unattended Admin Startup Task`** (while elevated as Admin) to register a Windows Scheduled Task (`/RL HIGHEST`) that automatically starts `TurboRemote.exe` in Host mode whenever Windows logs in.

---

## 🌍 3. How to Connect from the Client PC (`CONTROL A REMOTE PC`)

`TurboRemote.exe` automatically races all available routes and picks the fastest path (**Local LAN first → P2P VPN → Public Internet**).

### Case A: Both PCs Are on the Same Wi-Fi / Office LAN (Fastest: `< 1 ms`, 60 FPS)
1. Open `TurboRemote.exe` on the Client PC and click **⚡ CONTROL A REMOTE PC**.
2. In the **Smart Route / IP** box, either:
   - Leave it as **`AUTO`** (automatically discovers the Host on your local network via UDP broadcast), **OR**
   - Type the Host's **Machine Name** (e.g., `WM-DEV-07`) or **Local IP** (e.g., `192.168.1.56`).
3. Enter the **PIN** (`2580`) and click **`⚡ Connect to Host`**.
   -*(If you ever make a typo or want to abort while connecting, click the orange **`✖ Stop / Cancel`** button to cancel immediately.)*

---

### Case B: Connecting Over the Internet (Different Wi-Fi / Anywhere in the World)

Choose whichever of these **3 Internet methods** is easiest for your setup:

#### Method 1: Built-In Auto Internet Tunnel (Zero Router Setup & Zero VPN Install)
1. **On the Host PC**: In Box #3, click **`🌍 Auto Internet Tunnel`**.
   - This uses Windows' built-in OpenSSH (`ssh.exe`) in the background to open a public TCP relay (`tcp://xxxx.pinggy.io:PORT`).
   - Within 3–5 seconds, the Host log will show:
     `[PUBLIC INTERNET TUNNEL ACTIVE] -> xxxx.pinggy.io:12345`
     `[Cloud Discovery READY] Client can now type Host ID 'YOUR-PC-NAME' and PIN '2580' from anywhere on the Internet!`
2. **On the Client PC**:
   - Type the Host's **Host ID** (e.g., `WM-DEV-07`) **or** the tunnel address (`xxxx.pinggy.io:12345`) into **Smart Route / IP**.
   - Enter the **PIN** and click **`⚡ Connect to Host`**.
   - *Smart Routing Bonus:* If you type the **Host ID** or paste the **Smart Ticket** (`⚡ Copy / Publish ID`), the Client automatically tests Local LAN first and falls back to the Internet Tunnel if you are away from the office!

#### Method 2: Tailscale / ZeroTier Direct P2P (Fastest Internet Speed & Lowest Ping)
1. If both PCs have **Tailscale** installed, `TurboRemote` automatically detects the `100.x.y.z (Tailscale / Direct P2P WAN)` IP in Box #3 of the Host.
2. Enter that `100.x.y.z` IP into the Client and click **`⚡ Connect to Host`** for a direct peer-to-peer UDP hole-punched connection.

#### Method 3: Reverse WAN Connection (When Host Is Behind Strict Firewall / CGNAT)
1. **On the Client PC**: Click **`📡 Listen Mode (9877)`** on the top toolbar.
2. **On the Host PC**: In Box #2 (*Reverse WAN Connect*), enter the Client's IP/Host and port `9877`, then click **`🚀 Connect Outbound to Client`**.

---

## ⚡ 4. Choosing the Right Speed Profile (Top Toolbar)

You can switch profiles live during a session from the **Profile** dropdown:

| Profile | Resolution | Color Mode | Target FPS | Best For |
| :--- | :---: | :---: | :---: | :--- |
| **`4: Auto-Adaptive Smart` (Default)** | `42% – 100%` | Auto Color / Gray | `20 – 60 FPS` | **Recommended.** Runs at 60 FPS on LAN/Fast links and automatically scales down if internet slows down. |
| **`0: Ultra-Low 2G`** | `42%` | 16-Level Grayscale | `22 FPS` | Extreme slow internet (`10–25 KB/s`), mobile hotspot, or heavy packet loss. |
| **`1: Low 3G / Weak Wi-Fi`** | `55%` | Color (JPEG Q=35) | `32 FPS` | Weak Wi-Fi or long-distance international connections. |
| **`2: Balanced 4G / WAN`** | `75%` | Color (JPEG Q=52) | `45 FPS` | Everyday remote work over standard internet. |
| **`3: Turbo LAN / Broadband`** | `100%` (Full HD) | Color (JPEG Q=78) | `60 FPS` | Local LAN or high-speed fiber internet. |

> **Zero Idle Bandwidth (`0.0 KB/s`)**: Because `TurboRemote` uses an **8×8 Block-Delta Screen Encoder**, static parts of the screen are never re-sent. When the remote screen is still, bandwidth drops to **`0.0 KB/s`**, and every mouse click or keystroke triggers an instant `< 2 ms` interrupt capture.

---

## 🛠 5. Built-In Remote Tools & Shortcuts

### Top Toolbar Controls (Viewer Client)
- **`🔄 Keyframe`**: Forces a fresh full-screen capture if you switch monitors or resolutions.
- **`🛡 Elevate Host UAC`**: Remotely restarts the Host in Administrator mode.
- **`🔒 Lock Host Input`**: Locks the physical mouse and keyboard on the Host PC (requires Host running as Admin).
- **`📊 Task Mgr` / `🖥 Win+D`**: Opens Task Manager (`Ctrl+Shift+Esc`) or shows the Desktop (`Win+D`) on the remote PC.
- **`📋 Push Clip` / `📥 Pull Clip`**: Syncs clipboard text between your PC and the remote PC.
- **`Monitor 1 / 2 / 3`**: Switches between multiple monitors on the Host PC.

### Workspace Tabs
1. **🖥 Live Remote Desktop**: Real-time 60 FPS delta-tile screen view with `120 Hz` mouse tracking and full keyboard injection.
2. **💻 Zero-Bandwidth Remote PowerShell**: Run administrative PowerShell commands instantaneously using `< 1 KB` of compressed text—works even when video bandwidth is near zero.
3. **📁 Ultra-Fast File Manager & Transfer**: Browse remote folders (double-click any `[DIR]` to open), upload local files to the Host, and download remote files to your `Downloads\TurboRemote` folder with automatic stream compression.

---

## 🔒 6. Security Architecture

- **Zero-Knowledge Challenge-Response Handshake**: Your PIN/Access Key is **never** sent over the network. The Host sends a 32-byte random salt + challenge, and both sides derive a 512-bit key using **PBKDF2-SHA256 (`15,000 iterations`)**.
- **AES-256-CBC + Encrypt-Then-MAC (`HMAC-SHA256`)**: Every screen frame, mouse click, keystroke, PowerShell output, and file chunk is encrypted with AES-256 (fresh CSPRNG IV per packet) and verified with HMAC-SHA256.
- **Anti-Replay & Brute-Force Lockout**: Every packet embeds an authenticated 64-bit sequence counter to block replay attacks, and any IP that fails authentication 5 times is automatically locked out for 5 minutes.

---

## 💻 7. Command-Line Automation & Verification

You can also launch `TurboRemote.exe` directly via command line or batch scripts:

```powershell
# Start directly in Host Server mode on port 9876 with PIN 2580:
.\TurboRemote.exe --host --port 9876 --pin 2580

# Start directly in Viewer Client mode and auto-connect to a Host:
.\TurboRemote.exe --client --connect WM-DEV-07 --port 9876 --pin 2580

# Run the built-in automated diagnostic self-test (Screen Capture + AES-256 E2EE check):
.\TurboRemote.exe --self-test
```
