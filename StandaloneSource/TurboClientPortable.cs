using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using TurboCore;

namespace TurboClient
{
    /// <summary>A key for the remote PC: virtual key, hardware scan code, extended-key flag, pressed or released.</summary>
    public delegate void RemoteKeyHandler(ushort vk, byte scanCode, bool extended, bool isDown);

    public class DoubleBufferedPanel : Panel
    {
        private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_CHAR = 0x0102, WM_DEADCHAR = 0x0103;
        private const int WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105, WM_SYSCHAR = 0x0106, WM_SYSDEADCHAR = 0x0107;

        [DllImport("user32.dll")]
        private static extern IntPtr GetMessageExtraInfo();

        public DoubleBufferedPanel()
        {
            DoubleBuffered = true;
            // Selectable so a click gives the remote view keyboard focus (a plain Panel cannot take focus,
            // so keys kept going to whatever toolbar control had it).
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, true);
            TabStop = true;
            // Keys go to the remote PC as keys; a local input method editor must not turn them into text here.
            ImeMode = ImeMode.Disable;
            UpdateStyles();
        }

        /// <summary>True while a remote session is live.</summary>
        public bool CaptureAllKeys { get; set; }

        /// <summary>Raised for every key that reaches the remote view while <see cref="CaptureAllKeys"/> is on.</summary>
        public event RemoteKeyHandler RemoteKey;

        // While connected, treat every key as input so arrow keys, Tab, Enter and Escape raise KeyDown/KeyUp (and
        // get forwarded to the host) instead of being consumed locally for focus navigation. While disconnected,
        // keep normal behaviour so Tab can still move focus away from the canvas.
        protected override bool IsInputKey(Keys keyData)
        {
            return CaptureAllKeys || base.IsInputKey(keyData);
        }

        // While connected no parent (tab control, form) may treat a keystroke as one of its own shortcuts.
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            return CaptureAllKeys ? false : base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void WndProc(ref Message m)
        {
            if (CaptureAllKeys)
            {
                switch (m.Msg)
                {
                    case WM_KEYDOWN:
                    case WM_SYSKEYDOWN:
                    case WM_KEYUP:
                    case WM_SYSKEYUP:
                        ushort vk = (ushort)(m.WParam.ToInt64() & 0xFFFF);
                        if (RemoteKeyboardHook.IsLocalOnlyKey(vk)) break; // volume/media keys act on this PC
                        // Keys a Host on this same PC injected are not sent back to it (that echoed forever).
                        if (!NativeInputInjector.IsInjectedByTurboRemote(GetMessageExtraInfo()))
                        {
                            long lParam = m.LParam.ToInt64();
                            RemoteKeyHandler handler = RemoteKey;
                            if (handler != null)
                            {
                                handler(vk, (byte)((lParam >> 16) & 0xFF), ((lParam >> 24) & 1) != 0,
                                    m.Msg == WM_KEYDOWN || m.Msg == WM_SYSKEYDOWN);
                            }
                        }
                        // Handled here, never by the default window procedure: Alt+Space used to open this
                        // window's own menu (taking the next keys and leaving Alt down on the remote PC).
                        return;
                    case WM_CHAR:
                    case WM_SYSCHAR:
                    case WM_DEADCHAR:
                    case WM_SYSDEADCHAR:
                        return; // the remote PC types the character; no local beep or menu mnemonic
                }
            }
            base.WndProc(ref m);
        }
    }

    /// <summary>A Button that never takes keyboard focus, so clicking it leaves typing going to the remote PC.</summary>
    public class NoFocusButton : Button
    {
        public NoFocusButton()
        {
            SetStyle(ControlStyles.Selectable, false);
            TabStop = false;
        }
    }

    /// <summary>
    /// Low-level keyboard hook, active only while the remote view has focus in the foreground viewer window.
    /// Windows handles Win, Win+R, Alt+Tab, Ctrl+Esc and Print Screen before any window sees them, so without
    /// it those keys acted on THIS PC instead of the remote one. Lock keys and volume/media keys pass through.
    /// </summary>
    public sealed class RemoteKeyboardHook : IDisposable
    {
        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public UIntPtr dwExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        private const int WH_KEYBOARD_LL = 13;
        private const uint LLKHF_EXTENDED = 0x01;
        private const uint LLKHF_UP = 0x80;

        private readonly LowLevelKeyboardProc _proc; // kept referenced: the GC must not collect the callback
        private readonly Func<bool> _isActive;
        private readonly RemoteKeyHandler _onKey;
        private readonly HashSet<uint> _swallowedDown = new HashSet<uint>();
        private IntPtr _hook;

        public RemoteKeyboardHook(Func<bool> isActive, RemoteKeyHandler onKey)
        {
            _isActive = isActive;
            _onKey = onKey;
            _proc = HookProc;
            _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        }

        public bool IsInstalled { get { return _hook != IntPtr.Zero; } }

        /// <summary>Volume and media keys keep working on this PC (TurboRemote does not stream remote audio).</summary>
        public static bool IsLocalOnlyKey(uint vk)
        {
            return (vk >= 0xAD && vk <= 0xB3) || vk == 0xE7 /* VK_PACKET */;
        }

        /// <summary>Caps/Num/Scroll Lock toggle on this PC as usual; the remote PC is synced to that state.</summary>
        public static bool IsLockKey(uint vk)
        {
            return vk == 0x14 || vk == 0x90 || vk == 0x91;
        }

        private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                try
                {
                    KBDLLHOOKSTRUCT k = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
                    bool isUp = (k.flags & LLKHF_UP) != 0;
                    bool isOurEcho = k.dwExtraInfo == NativeInputInjector.InjectedInputMarker;
                    if (!isOurEcho)
                    {
                        if (isUp)
                        {
                            // Swallow only releases of presses we swallowed. A key already held when the viewer
                            // took focus (e.g. Shift) must reach Windows, or Windows would think it is still down.
                            if (_swallowedDown.Remove(k.vkCode))
                            {
                                _onKey((ushort)k.vkCode, (byte)k.scanCode, (k.flags & LLKHF_EXTENDED) != 0, false);
                                return (IntPtr)1;
                            }
                        }
                        else if (!IsLockKey(k.vkCode) && !IsLocalOnlyKey(k.vkCode) && _isActive())
                        {
                            _swallowedDown.Add(k.vkCode);
                            _onKey((ushort)k.vkCode, (byte)k.scanCode, (k.flags & LLKHF_EXTENDED) != 0, true);
                            return (IntPtr)1;
                        }
                    }
                }
                catch { }
            }
            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        public void Dispose()
        {
            if (_hook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
            }
        }
    }

    public class ClientMainForm : Form
    {
        private TextBox _txtHostIp;
        private TextBox _txtPin;
        private Button _btnConnect;
        private ComboBox _cmbBandwidth;
        private ComboBox _cmbMonitor;
        private Label _lblTelemetry;
        // Refresh, clipboard, lock and restart-as-admin: only shown while connected.
        private Control[] _sessionTools = new Control[0];
        private bool _fillingScreenList;
        // The port the Host listens on unless started with --port; "address:port" in the box overrides it.
        private int _defaultPort = 9876;

        // Quality choices, in the order shown, and the Host's profile number for each (the wire format keeps
        // the old order: 0 = 2G, 1 = 3G, 2 = 4G, 3 = LAN, 4 = Auto).
        private static readonly string[] QualityNames = { "Auto (recommended)", "2G — very slow", "3G — weak Wi-Fi", "4G — normal internet", "LAN — fast" };
        private static readonly byte[] QualityProfiles = { 4, 0, 1, 2, 3 };

        private TabControl _tabControl;
        private DoubleBufferedPanel _screenCanvas;

        private RichTextBox _rtbTerminalOutput;
        private TextBox _txtTerminalInput;

        private TextBox _txtRemotePath;
        private ListView _lvRemoteFiles;
        private Label _lblFileStatus;

        private TcpClient _tcpClient;
        private NetworkStream _stream;
        private SecurityContext _sec;
        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);
        private CancellationTokenSource _sessionCts;
        private CancellationTokenSource _connectCts;
        private bool _isConnected = false;
        private bool _isConnecting = false;
        private bool _remoteInputLocked = false;

        private Bitmap _remoteBitmap;
        private readonly object _bitmapLock = new object();
        private int _remoteNativeW = 1920;
        private int _remoteNativeH = 1080;
        private int _framesThisSec = 0;
        private int _currentFps = 0;
        private long _bytesThisSec = 0;
        private double _currentKbps = 0;
        private long _lastPingRttMs = 0;
        // Stopwatch, not Environment.TickCount: TickCount goes negative after 24.9 days of uptime, and the
        // "now - last < 8" throttle then dropped every mouse move until the PC was rebooted.
        private readonly Stopwatch _mouseMoveClock = Stopwatch.StartNew();
        private long _lastMouseMoveSentMs = -1000;

        private FileStream _downloadingStream;
        private string _downloadingFilePath;
        private bool _uploadInProgress;

        // What the remote PC currently has pressed because of us. Whenever the remote view loses focus (tab
        // switch, Alt+Tab, another app, a dialog) these are released there; keys and buttons used to stay stuck
        // down on the remote PC because their release went to another tab or program.
        private struct HeldRemoteKey { public ushort Vk; public byte Scan; public bool Extended; }
        private readonly Dictionary<int, HeldRemoteKey> _remoteKeysDown = new Dictionary<int, HeldRemoteKey>();
        private readonly HashSet<MouseActionType> _remoteButtonsDown = new HashSet<MouseActionType>();
        private float _lastMouseNx = 0.5f, _lastMouseNy = 0.5f;
        private RemoteKeyboardHook _keyboardHook;
        private Button _btnLockInput;
        private Label _lblNotice;

        // Auto-reconnect. What to redial after a session ends without the user asking,
        // and the state shown while it retries.
        private sealed class ReconnectTarget
        {
            public string HostInput;
            public int Port;
            public string Pin;
            public string MachineName;
        }
        private ReconnectTarget _reconnectTarget;
        private CancellationTokenSource _reconnectCts;
        private bool _isReconnecting;
        private int _reconnectAttempt, _reconnectNextInSec;
        private string _reconnectName = "";
        private readonly Stopwatch _lostClock = new Stopwatch();
        private readonly Stopwatch _uiClock = Stopwatch.StartNew();
        private long _reconnectedNoticeUntilMs = -1;
        private string _reconnectedNoticeText = "";
        private long _lastPacketTicks;
        private string _connectedMachine;

        // This PC's own Tailscale state, shown while disconnected (refreshed every 30 s, off the UI thread).
        private string _ownTailscaleText = "";
        private bool _ownTailscaleRefreshing;
        private int _ticksSinceTailscaleCheck;
        private const string TailscaleHint = "If this PC is outside the Host's network, install Tailscale on both PCs and sign in to the same account.";
        private const int ChallengeTimeoutMs = 15000;
        private const int AuthResultTimeoutMs = 100000;

        private const ushort VK_SNAPSHOT = 0x2C, VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_L = 0x4C;

        // The title while not connected. "Connected -> X" used to stay up while reconnecting and after Stop.
        private static readonly string BaseTitle = "TurboRemote — Viewer Client  v" + TurboProtocol.AppVersion;
        // The reconnect banner last drawn: the canvas is repainted only when it changes (once a second at most).
        private string _lastBannerText;

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool LockWorkStation();

        public ClientMainForm()
        {
            Text = BaseTitle;
            Size = new Size(1250, 800);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(16, 18, 24);
            ForeColor = Color.FromArgb(230, 235, 245);
            Font = new Font("Segoe UI", 9f);

            InitializeUi();

            System.Windows.Forms.Timer statsTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            statsTimer.Tick += async (s, e) => await OnTelemetryTimerTickAsync();
            statsTimer.Start();
            Shown += async (s, e) => await RefreshOwnTailscaleAsync();
        }

        private void InitializeUi()
        {
            Panel topBar = new Panel
            {
                Dock = DockStyle.Top,
                Height = 84,
                BackColor = Color.FromArgb(24, 28, 40)
            };

            Label lblHost = new Label { Text = "Host ID or address:", Location = new Point(12, 13), AutoSize = true };
            _txtHostIp = new TextBox
            {
                Text = "AUTO",
                AccessibleName = "Host ID or address",
                Location = new Point(140, 10),
                Width = 200,
                BackColor = Color.FromArgb(16, 18, 26),
                ForeColor = Color.FromArgb(110, 225, 255),
                Font = new Font("Consolas", 9.5f, FontStyle.Bold),
                BorderStyle = BorderStyle.FixedSingle
            };

            Label lblPin = new Label { Text = "Access key:", Location = new Point(352, 13), AutoSize = true };
            // Each Host has its own random access key now; there is no shared default to pre-fill.
            _txtPin = new TextBox
            {
                Text = "",
                AccessibleName = "Access key",
                Location = new Point(432, 10),
                Width = 150,
                BackColor = Color.FromArgb(16, 18, 26),
                ForeColor = Color.FromArgb(130, 255, 165),
                Font = new Font("Consolas", 9.5f, FontStyle.Bold),
                BorderStyle = BorderStyle.FixedSingle
            };

            _btnConnect = CreateToolbarButton("⚡ Connect", 592, 8, 150, 28, Color.FromArgb(30, 145, 90));
            _btnConnect.Click += async (s, e) => await ToggleConnectAsync();

            Label lblMode = new Label { Text = "Quality:", Location = new Point(756, 13), AutoSize = true };
            _cmbBandwidth = new ComboBox
            {
                Location = new Point(812, 10),
                Width = 190,
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(16, 18, 26),
                ForeColor = Color.FromArgb(255, 220, 120)
            };
            _cmbBandwidth.Items.AddRange(QualityNames);
            _cmbBandwidth.SelectedIndex = 0; // Auto
            _cmbBandwidth.SelectedIndexChanged += async (s, e) => await SendStreamConfigAsync();
            _cmbBandwidth.DropDownClosed += (s, e) => FocusSelectedTabSoon();

            // Second row: session tools, shown only while connected (see ShowSessionTools). They never take
            // keyboard focus, so after clicking one, typing keeps going to the remote PC.
            Button btnRefreshFrame = CreateToolbarButton("↻ Refresh", 12, 46, 90, 28, Color.FromArgb(50, 70, 105), false);
            btnRefreshFrame.Click += async (s, e) => await SendPacketSafeAsync(PacketType.ForceFullFrame, new byte[0]);

            Button btnPushClip = CreateToolbarButton("📋 Send clipboard", 108, 46, 128, 28, Color.FromArgb(55, 65, 95), false);
            btnPushClip.Click += async (s, e) =>
            {
                if (Clipboard.ContainsText())
                {
                    await SendPacketSafeAsync(PacketType.ClipboardSync, Encoding.UTF8.GetBytes(Clipboard.GetText()));
                }
            };

            Button btnPullClip = CreateToolbarButton("📥 Get clipboard", 242, 46, 120, 28, Color.FromArgb(55, 65, 95), false);
            btnPullClip.Click += async (s, e) => await SendPacketSafeAsync(PacketType.SystemAction, Encoding.UTF8.GetBytes("GET_CLIPBOARD"));

            _btnLockInput = CreateToolbarButton("🔒 Lock host input", 368, 46, 140, 28, Color.FromArgb(135, 55, 75), false);
            _btnLockInput.Click += async (s, e) =>
            {
                if (!_isConnected) return;
                _remoteInputLocked = !_remoteInputLocked;
                _btnLockInput.Text = _remoteInputLocked ? "🔓 Unlock host input" : "🔒 Lock host input";
                string cmd = _remoteInputLocked ? "LOCK_INPUT_ON" : "LOCK_INPUT_OFF";
                await SendPacketSafeAsync(PacketType.SystemAction, Encoding.UTF8.GetBytes(cmd));
            };

            Button btnElevateHost = CreateToolbarButton("🛡 Restart host as admin", 514, 46, 170, 28, Color.FromArgb(175, 105, 30), false);
            btnElevateHost.Click += async (s, e) => await SendPacketSafeAsync(PacketType.SystemAction, Encoding.UTF8.GetBytes("ELEVATE_ADMIN"));

            // Only when the Host reports more than one screen (sign-in reply, field 4).
            _cmbMonitor = new ComboBox
            {
                Location = new Point(690, 48),
                Width = 96,
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(16, 18, 26),
                ForeColor = Color.White,
                Visible = false
            };
            _cmbMonitor.Items.Add("Screen 1");
            _cmbMonitor.SelectedIndex = 0;
            _cmbMonitor.SelectedIndexChanged += async (s, e) => await SendStreamConfigAsync();
            _cmbMonitor.DropDownClosed += (s, e) => FocusSelectedTabSoon();

            _sessionTools = new Control[] { btnRefreshFrame, btnPushClip, btnPullClip, _btnLockInput, btnElevateHost };
            foreach (Control c in _sessionTools) c.Visible = false;

            _lblTelemetry = new Label
            {
                Text = "● DISCONNECTED",
                Location = new Point(14, 52),
                AutoSize = true,
                Font = new Font("Consolas", 8.8f, FontStyle.Bold),
                ForeColor = Color.FromArgb(255, 185, 90)
            };

            topBar.Controls.AddRange(new Control[]
            {
                lblHost, _txtHostIp, lblPin, _txtPin, _btnConnect, lblMode, _cmbBandwidth,
                btnRefreshFrame, btnPushClip, btnPullClip, _btnLockInput, btnElevateHost, _cmbMonitor, _lblTelemetry
            });

            _tabControl = new TabControl
            {
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
            };

            TabPage tabDesktop = new TabPage("🖥 Remote Desktop")
            {
                BackColor = Color.FromArgb(10, 12, 16)
            };

            _screenCanvas = new DoubleBufferedPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(10, 12, 16),
                Cursor = Cursors.Default
            };
            _screenCanvas.Paint += OnScreenCanvasPaint;
            _screenCanvas.MouseMove += OnCanvasMouseMove;
            _screenCanvas.MouseDown += OnCanvasMouseDown;
            _screenCanvas.MouseUp += OnCanvasMouseUp;
            _screenCanvas.MouseWheel += OnCanvasMouseWheel;
            _screenCanvas.RemoteKey += OnRemoteKey;
            // Losing focus covers every way of leaving: another tab, Alt+Tab, another app, a dialog, minimize.
            _screenCanvas.LostFocus += (s, e) => ReleaseRemoteInput();
            // Caps/Num/Scroll Lock may have been toggled elsewhere meanwhile; match the remote PC to this one.
            _screenCanvas.GotFocus += async (s, e) => await SendLockKeyStateAsync();

            tabDesktop.Controls.Add(_screenCanvas);

            TabPage tabTerminal = new TabPage("💻 PowerShell")
            {
                BackColor = Color.FromArgb(14, 16, 22)
            };
            BuildTerminalTab(tabTerminal);

            TabPage tabFiles = new TabPage("📁 Files")
            {
                BackColor = Color.FromArgb(18, 21, 30)
            };
            BuildFileTransferTab(tabFiles);

            _tabControl.TabPages.Add(tabDesktop);
            _tabControl.TabPages.Add(tabTerminal);
            _tabControl.TabPages.Add(tabFiles);
            // Put the keyboard where the user expects it after switching tabs (clicking a tab header used to
            // leave focus on the header, where arrow keys flip tabs instead of reaching the remote PC).
            _tabControl.SelectedIndexChanged += (s, e) => FocusSelectedTabSoon();
            _tabControl.MouseUp += (s, e) => FocusSelectedTabSoon();
            Deactivate += (s, e) => ReleaseRemoteInput();

            _lblNotice = new Label
            {
                Dock = DockStyle.Top,
                Height = 40,
                Visible = false,
                Padding = new Padding(12, 0, 12, 0),
                TextAlign = ContentAlignment.MiddleLeft,
                BackColor = Color.FromArgb(120, 82, 10),
                ForeColor = Color.FromArgb(255, 238, 190),
                Font = new Font("Segoe UI", 9f, FontStyle.Bold)
            };

            // Docked in reverse order: toolbar on top, the notice below it, the tabs filling the rest.
            Controls.Add(_tabControl);
            Controls.Add(_lblNotice);
            Controls.Add(topBar);
        }

        private void BuildTerminalTab(TabPage tab)
        {
            Panel bottomInputPanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 42,
                BackColor = Color.FromArgb(24, 28, 40)
            };

            Label lblPrompt = new Label
            {
                Text = "PS Remote>",
                Location = new Point(10, 11),
                AutoSize = true,
                ForeColor = Color.FromArgb(95, 225, 150),
                Font = new Font("Consolas", 10f, FontStyle.Bold)
            };

            _txtTerminalInput = new TextBox
            {
                Location = new Point(105, 8),
                Width = 940,
                Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top,
                BackColor = Color.FromArgb(14, 16, 22),
                ForeColor = Color.White,
                Font = new Font("Consolas", 10.5f),
                BorderStyle = BorderStyle.FixedSingle
            };
            _txtTerminalInput.KeyDown += async (s, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    await SendTerminalCommandAsync();
                }
            };

            Button btnRunCmd = CreateToolbarButton("▶ Execute", 1055, 7, 130, 28, Color.FromArgb(35, 145, 90));
            btnRunCmd.Anchor = AnchorStyles.Right | AnchorStyles.Top;
            btnRunCmd.Click += async (s, e) => await SendTerminalCommandAsync();

            bottomInputPanel.Controls.AddRange(new Control[] { lblPrompt, _txtTerminalInput, btnRunCmd });

            _rtbTerminalOutput = new RichTextBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(10, 12, 18),
                ForeColor = Color.FromArgb(140, 245, 175),
                Font = new Font("Consolas", 10f),
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                Text = "Remote PowerShell. Type a command below and press Enter; it runs on the Host PC.\n" +
                       "Works even on very slow links, because only text is sent.\n\n"
            };

            tab.Controls.Add(_rtbTerminalOutput);
            tab.Controls.Add(bottomInputPanel);
        }

        private void BuildFileTransferTab(TabPage tab)
        {
            Panel topFileBar = new Panel
            {
                Dock = DockStyle.Top,
                Height = 46,
                BackColor = Color.FromArgb(24, 28, 40)
            };

            Label lblPath = new Label { Text = "Remote Folder:", Location = new Point(12, 14), AutoSize = true };
            _txtRemotePath = new TextBox
            {
                Text = "DEFAULT",
                Location = new Point(115, 11),
                Width = 540,
                BackColor = Color.FromArgb(14, 16, 22),
                ForeColor = Color.White,
                Font = new Font("Consolas", 9.5f),
                BorderStyle = BorderStyle.FixedSingle
            };

            Button btnBrowseRemote = CreateToolbarButton("📂 List Remote Files", 665, 9, 150, 28, Color.FromArgb(55, 85, 135));
            btnBrowseRemote.Click += async (s, e) => await RequestRemoteFileListAsync(_txtRemotePath.Text);

            Button btnUploadFile = CreateToolbarButton("⬆ Upload File to Host", 825, 9, 165, 28, Color.FromArgb(35, 140, 90));
            btnUploadFile.Click += async (s, e) => await UploadLocalFileToHostAsync();

            Button btnDownloadFile = CreateToolbarButton("⬇ Download Selected", 1000, 9, 165, 28, Color.FromArgb(120, 75, 175));
            btnDownloadFile.Click += async (s, e) => await DownloadSelectedFileFromHostAsync();

            topFileBar.Controls.AddRange(new Control[] { lblPath, _txtRemotePath, btnBrowseRemote, btnUploadFile, btnDownloadFile });

            _lblFileStatus = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 28,
                Text = " Ready for high-speed compressed file transfer.",
                BackColor = Color.FromArgb(22, 26, 36),
                ForeColor = Color.FromArgb(140, 220, 255),
                TextAlign = ContentAlignment.MiddleLeft
            };

            _lvRemoteFiles = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                BackColor = Color.FromArgb(14, 16, 22),
                ForeColor = Color.FromArgb(225, 235, 245),
                Font = new Font("Consolas", 9.5f)
            };
            _lvRemoteFiles.Columns.Add("Type", 90);
            _lvRemoteFiles.Columns.Add("Name", 380);
            _lvRemoteFiles.Columns.Add("Size (Bytes)", 160);
            _lvRemoteFiles.Columns.Add("Full Remote Path", 520);
            _lvRemoteFiles.DoubleClick += async (s, e) =>
            {
                if (_lvRemoteFiles.SelectedItems.Count == 0) return;
                ListViewItem item = _lvRemoteFiles.SelectedItems[0];
                if (item.Text == "[DIR]")
                {
                    await RequestRemoteFileListAsync(item.SubItems[3].Text);
                }
            };

            tab.Controls.Add(_lvRemoteFiles);
            tab.Controls.Add(_lblFileStatus);
            tab.Controls.Add(topFileBar);
        }

        public void AutoConnectOnStartup(string host, int port, string pin)
        {
            _txtHostIp.Text = host;
            _defaultPort = port;
            _txtPin.Text = pin;
            Shown += async (s, e) => await ConnectToHostAsync();
        }

        private async Task ToggleConnectAsync()
        {
            if (_isReconnecting)
            {
                StopReconnecting("● RECONNECT STOPPED.");
                return;
            }
            if (_isConnected || _isConnecting)
            {
                Disconnect();
            }
            else
            {
                await ConnectToHostAsync();
            }
        }

        private async Task ConnectToHostAsync()
        {
            // Cleanly tear down any previous session before opening a new one
            Disconnect();

            string hostInput = _txtHostIp.Text.Trim();
            int port = _defaultPort;
            if (_txtPin.Text.Trim().Length == 0)
            {
                MessageBox.Show("Enter the Host's access key (shown in the Host window under 'Connect to this PC').", "TurboRemote", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _isConnecting = true;
            _connectCts = new CancellationTokenSource();
            CancellationToken myConnectToken = _connectCts.Token;
            _btnConnect.Text = "✖ Stop / Cancel";
            _btnConnect.BackColor = Color.FromArgb(195, 110, 30);
            _btnConnect.Enabled = true;

            try
            {
                _lblTelemetry.Text = "● CONNECTING TO " + hostInput + " (Click '✖ Stop / Cancel' to abort)...";
                _lblTelemetry.ForeColor = Color.FromArgb(110, 220, 255);

                KeyValuePair<TcpClient, EndpointCandidate> winner = await SmartRouteEngine.RaceFastestConnectionAsync(hostInput, port, _txtPin.Text.Trim(), myConnectToken);
                myConnectToken.ThrowIfCancellationRequested();

                TcpClient client = winner.Key;
                EndpointCandidate route = winner.Value;

                await InitializeActiveSessionAsync(client, route, myConnectToken);
                _reconnectTarget = new ReconnectTarget { HostInput = hostInput, Port = port, Pin = _txtPin.Text.Trim(), MachineName = _connectedMachine };
            }
            catch (OperationCanceledException)
            {
                _lblTelemetry.Text = "● CONNECTION CANCELLED BY USER.";
                _lblTelemetry.ForeColor = Color.FromArgb(255, 185, 90);
                ResetConnectButton();
            }
            catch (ObjectDisposedException)
            {
                if (myConnectToken.IsCancellationRequested)
                {
                    _lblTelemetry.Text = "● CONNECTION CANCELLED BY USER.";
                    _lblTelemetry.ForeColor = Color.FromArgb(255, 185, 90);
                }
                else
                {
                    _lblTelemetry.Text = "● CONNECTION FAILED: Remote Host closed or reset the socket (Ensure latest TurboRemote.exe is running on Host).";
                    _lblTelemetry.ForeColor = Color.FromArgb(255, 110, 110);
                }
                ResetConnectButton();
            }
            catch (Exception ex)
            {
                string message = "● CONNECTION FAILED: " + ex.Message;
                // A Host ID that cannot be reached from here usually means "not on the Host's LAN": Tailscale fixes that.
                if (!(ex is AuthRefusedException) && hostInput.Split(',', ';', ' ').Any(t => SmartRouteEngine.LooksLikeHostId(t.Trim().TrimStart('@'))))
                {
                    message += "  " + TailscaleHint;
                }
                _lblTelemetry.Text = message;
                _lblTelemetry.ForeColor = Color.FromArgb(255, 110, 110);
                ResetConnectButton();
            }
            finally
            {
                _isConnecting = false;
            }
        }

        /// <summary>
        /// Shows the second toolbar row while connected (and the screen picker when the Host has more than one
        /// screen); while disconnected that row holds only the status line.
        /// </summary>
        private void ShowSessionTools(bool connected, int screenCount)
        {
            foreach (Control c in _sessionTools) c.Visible = connected;

            if (connected)
            {
                // Refilled on every sign-in: a reconnect keeps the chosen screen, another Host may have fewer.
                int count = Math.Max(1, screenCount);
                int keep = _cmbMonitor.SelectedIndex;
                _fillingScreenList = true;
                try
                {
                    _cmbMonitor.Items.Clear();
                    for (int i = 1; i <= count; i++) _cmbMonitor.Items.Add("Screen " + i);
                    _cmbMonitor.SelectedIndex = keep >= 0 && keep < count ? keep : 0;
                }
                finally
                {
                    _fillingScreenList = false;
                }
            }
            _cmbMonitor.Visible = connected && screenCount > 1;

            int right = _cmbMonitor.Visible ? _cmbMonitor.Right
                      : _sessionTools.Length > 0 ? _sessionTools[_sessionTools.Length - 1].Right : 0;
            _lblTelemetry.Location = connected ? new Point(right + 14, 52) : new Point(14, 52);
        }

        private void ResetConnectButton()
        {
            _isConnecting = false;
            _btnConnect.Enabled = true;
            _btnConnect.Text = "⚡ Connect";
            _btnConnect.BackColor = Color.FromArgb(30, 145, 90);
        }

        /// <summary>
        /// Signs in on <paramref name="client"/> and starts the session. With <paramref name="expectedMachine"/> set
        /// (reconnects), a different PC answering is refused. Throws <see cref="AuthRefusedException"/> when the Host
        /// refuses the key; a "busy" refusal is an ordinary failure, worth retrying.
        /// </summary>
        private async Task InitializeActiveSessionAsync(TcpClient client, EndpointCandidate winningRoute, CancellationToken connectCt, string expectedMachine = null, string pinOverride = null)
        {
            _tcpClient = client;
            _tcpClient.NoDelay = true;
            NetworkHelper.EnableKeepAlive(_tcpClient.Client);
            _tcpClient.ReceiveBufferSize = 256 * 1024;
            _tcpClient.SendBufferSize = 256 * 1024;
            _stream = _tcpClient.GetStream();
            _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(connectCt);

            KeyValuePair<PacketType, byte[]>? challengePkt, authResp;
            byte[] salt16 = new byte[16];
            byte[] challenge16 = new byte[16];

            // The sign-in has time limits, and Stop or a timeout closes the socket: on .NET Framework a pending
            // NetworkStream read ignores its token, so an address that accepted the connection but never spoke
            // used to hang a reconnect (and its Stop button) forever.
            using (CancellationTokenSource handshake = CancellationTokenSource.CreateLinkedTokenSource(_sessionCts.Token))
            using (handshake.Token.Register(() => { try { client.Close(); } catch { } }))
            {
                try
                {
                    // 1. Read 32B Cryptographic Challenge (16B Salt + 16B Nonce) from Host
                    handshake.CancelAfter(ChallengeTimeoutMs);
                    challengePkt = await TurboProtocol.ReadPacketAsync(_stream, handshake.Token);
                    if (challengePkt == null || challengePkt.Value.Key != PacketType.AuthChallenge || challengePkt.Value.Value.Length != 32)
                    {
                        CloseAttemptSocket(client);
                        throw new Exception("Host did not send valid 32-byte cryptographic challenge.");
                    }

                    Buffer.BlockCopy(challengePkt.Value.Value, 0, salt16, 0, 16);
                    Buffer.BlockCopy(challengePkt.Value.Value, 16, challenge16, 0, 16);

                    // 2. Derive Session AES-256 + HMAC-SHA256 Keys via PBKDF2-SHA256 & Send Zero-Knowledge Proof
                    string pin = pinOverride ?? _txtPin.Text.Trim();
                    _sec = new SecurityContext(pin, salt16, challenge16);
                    byte[] proof = _sec.ComputeHandshakeProof(challenge16, "CLIENT_AUTH_V2");
                    await TurboProtocol.SendPacketAsync(_stream, _writeLock, PacketType.AuthResponse, proof, handshake.Token);

                    // 3. Receive & Decrypt AuthResult. The Host's Internet sign-in throttle may hold it for ~90 s.
                    handshake.CancelAfter(AuthResultTimeoutMs);
                    authResp = await TurboProtocol.ReadPacketAsync(_stream, handshake.Token);
                }
                catch (Exception ex)
                {
                    bool timedOut = handshake.IsCancellationRequested && !_sessionCts.IsCancellationRequested;
                    CloseAttemptSocket(client);
                    if (timedOut) throw new TimeoutException("The Host did not answer the sign-in in time.");
                    if (connectCt.IsCancellationRequested) throw new OperationCanceledException(connectCt);
                    throw new Exception("Sign-in was interrupted: " + ex.Message, ex);
                }
            }
            if (connectCt.IsCancellationRequested)
            {
                CloseAttemptSocket(client);
                throw new OperationCanceledException(connectCt);
            }

            if (authResp == null || authResp.Value.Key != PacketType.AuthResult)
            {
                CloseAttemptSocket(client);
                throw new Exception("Host closed connection during cryptographic handshake.");
            }

            // If failed, host sends unencrypted FAIL message; if succeeded, host sends AES-256 encrypted payload
            byte[] rawPayload = authResp.Value.Value;
            string checkClear = Encoding.UTF8.GetString(rawPayload);
            if (checkClear.StartsWith("FAIL:"))
            {
                CloseAttemptSocket(client);
                string why = "Authentication rejected (" + checkClear + ")";
                // "Busy" means try later; anything else is the key itself, and retrying it trips the lockout.
                if (checkClear.IndexOf("busy", StringComparison.OrdinalIgnoreCase) >= 0) throw new Exception(why);
                throw new AuthRefusedException(why);
            }

            string authText = Encoding.UTF8.GetString(_sec.VerifyAndDecrypt(PacketType.AuthResult, rawPayload));
            if (!authText.StartsWith("OK|"))
            {
                CloseAttemptSocket(client);
                throw new Exception("Authentication verification failed.");
            }

            string[] parts = authText.Split('|');
            string remoteMachine = parts.Length > 1 ? parts[1] : "RemotePC";
            string privLevel = parts.Length > 2 ? parts[2] : "USER";
            // Hosts from v2026.11.23 on add their version as field 6; older ones send nothing there.
            string hostVersion = parts.Length > 5 && parts[5].StartsWith("v") ? parts[5].Substring(1) : null;

            if (expectedMachine != null && !string.Equals(remoteMachine, expectedMachine, StringComparison.OrdinalIgnoreCase))
            {
                CloseAttemptSocket(client);
                throw new WrongHostException("A different PC (" + remoteMachine + ") answered instead of " + expectedMachine + ".");
            }

            int screenCount;
            if (parts.Length <= 3 || !int.TryParse(parts[3], out screenCount)) screenCount = 1;

            _connectedMachine = remoteMachine;
            Interlocked.Exchange(ref _lastPacketTicks, Stopwatch.GetTimestamp());
            _isConnected = true;
            ShowSessionTools(true, screenCount);
            _btnConnect.Text = "⏹ Disconnect";
            _btnConnect.BackColor = Color.FromArgb(185, 55, 55);
            string routeInfo = winningRoute != null ? string.Format("{0} ({1}:{2})", winningRoute.RouteLabel, winningRoute.Host, winningRoute.Port) : "Direct";
            Text = string.Format("🔒 AES-256 Connected -> {0} [{1} | Route: {2} | Host {3}]", remoteMachine, privLevel, routeInfo, hostVersion != null ? "v" + hostVersion : "older build");
            ShowConnectionNotice(remoteMachine, hostVersion);

            await SendStreamConfigAsync();
            await RequestRemoteFileListAsync("DEFAULT");

            // Stop or Cancel clicked while the session was being set up: leave nothing half-connected behind (it
            // used to stay "connected" with a dead receive loop, and its dead link later restarted reconnecting).
            if (connectCt.IsCancellationRequested)
            {
                string status = _lblTelemetry.Text;
                Color statusColor = _lblTelemetry.ForeColor;
                if (ReferenceEquals(_tcpClient, client)) EndSession();
                else CloseAttemptSocket(client);
                _lblTelemetry.Text = status;
                _lblTelemetry.ForeColor = statusColor;
                throw new OperationCanceledException(connectCt);
            }

            _screenCanvas.CaptureAllKeys = true;
            if (_keyboardHook == null)
            {
                _keyboardHook = new RemoteKeyboardHook(IsRemoteKeyboardActive, OnRemoteKey);
            }
            if (_tabControl.SelectedIndex == 0)
            {
                if (_screenCanvas.Focused) await SendLockKeyStateAsync();
                // A new session takes the keyboard so typing goes to the Host right away. A reconnect does not:
                // the user may have moved into the address or key box during the outage, and their next keys
                // would land on the remote PC.
                else if (expectedMachine == null) _screenCanvas.Focus();
            }

            // Hand this session's stream/keys/token to its loop, so it can never pick up a later session's.
            NetworkStream activeStream = _stream;
            SecurityContext activeSec = _sec;
            CancellationToken activeToken = _sessionCts.Token;
            Task.Run(() => ReceivePacketsLoopAsync(activeStream, activeSec, activeToken));
        }

        /// <summary>The user's Disconnect (or the start of a new connect): also stops reconnecting.</summary>
        private void Disconnect()
        {
            if (_isReconnecting) StopReconnecting(null);
            _reconnectTarget = null;
            EndSession();
        }

        /// <summary>
        /// Closes a failed or abandoned attempt's own connection. While the shared session fields still belong to
        /// it, they are cleared too; once a later Connect owns them, only the attempt's socket is closed.
        /// </summary>
        private void CloseAttemptSocket(TcpClient attempt)
        {
            if (attempt == null) return;
            if (ReferenceEquals(_tcpClient, attempt))
            {
                if (!_isConnected) CloseSessionSocket();
                return;
            }
            try { attempt.Close(); } catch { }
        }

        /// <summary>Closes the socket and keys without touching the UI (failed sign-ins, failed reconnect attempts).</summary>
        private void CloseSessionSocket()
        {
            try { if (_sessionCts != null) _sessionCts.Cancel(); } catch { }
            NetworkStream oldStream = _stream;
            TcpClient oldClient = _tcpClient;
            SecurityContext oldSec = _sec;
            _stream = null;
            _tcpClient = null;
            _sec = null;
            try { if (oldStream != null) oldStream.Dispose(); } catch { }
            try { if (oldClient != null) oldClient.Close(); } catch { }
            try { if (oldSec != null) oldSec.Dispose(); } catch { }
        }

        /// <summary>
        /// Ends the current session (keyboard capture off, hook removed, held input forgotten) but keeps the last
        /// remote picture, which stays on screen while reconnecting.
        /// </summary>
        private void EndSession()
        {
            _isConnected = false;
            _isConnecting = false;
            _screenCanvas.CaptureAllKeys = false;
            if (_keyboardHook != null)
            {
                _keyboardHook.Dispose();
                _keyboardHook = null;
            }
            // The Host releases whatever this session still held when the connection closes.
            _remoteKeysDown.Clear();
            _remoteButtonsDown.Clear();
            // The Host unlocks its input when a session ends; the button must not keep saying "Unlock".
            _remoteInputLocked = false;
            _btnLockInput.Text = "🔒 Lock host input";
            ShowSessionTools(false, 0);
            _lblNotice.Visible = false;
            try { if (_connectCts != null) _connectCts.Cancel(); } catch { }
            try { if (_sessionCts != null) _sessionCts.Cancel(); } catch { }

            NetworkStream oldStream = _stream;
            TcpClient oldClient = _tcpClient;
            SecurityContext oldSec = _sec;
            _stream = null;
            _tcpClient = null;
            _sec = null;

            try { if (oldStream != null) oldStream.Dispose(); } catch { }
            try { if (oldClient != null) oldClient.Close(); } catch { }
            try { if (_downloadingStream != null) { _downloadingStream.Dispose(); _downloadingStream = null; } } catch { }
            try { if (oldSec != null) oldSec.Dispose(); } catch { }

            ResetConnectButton();
            Text = BaseTitle;
            _lblTelemetry.Text = "● DISCONNECTED";
            _lblTelemetry.ForeColor = Color.FromArgb(255, 185, 90);
            ShowDisconnectedStatus();
        }

        /// <summary>Off the UI thread: reads this PC's Tailscale state for the status line.</summary>
        private async Task RefreshOwnTailscaleAsync()
        {
            if (_ownTailscaleRefreshing || IsDisposed) return;
            _ownTailscaleRefreshing = true;
            try
            {
                TailscaleStatus st = await Task.Run(() => TailscaleInfo.Query());
                if (IsDisposed) return;
                _ownTailscaleText = st.IsRunning
                    ? "Tailscale: ON (" + (string.IsNullOrEmpty(st.HostName) ? Environment.MachineName : st.HostName) + ")"
                    : st.State == TailscaleState.NotInstalled ? "Tailscale: not installed" : "Tailscale: not connected";
                ShowDisconnectedStatus();
            }
            catch { }
            finally
            {
                _ownTailscaleRefreshing = false;
            }
        }

        /// <summary>Adds this PC's Tailscale state to a plain "DISCONNECTED" line; failure and stop messages stay.</summary>
        private void ShowDisconnectedStatus()
        {
            if (_isConnected || _isConnecting || _isReconnecting) return;
            string text = _lblTelemetry.Text;
            if (text != "● DISCONNECTED" && !text.StartsWith("● DISCONNECTED | ")) return;
            _lblTelemetry.Text = "● DISCONNECTED" + (_ownTailscaleText.Length > 0 ? " | " + _ownTailscaleText : "");
        }

        private async Task ReceivePacketsLoopAsync(NetworkStream sessionStream, SecurityContext sessionSec, CancellationToken ct)
        {
            SessionEndReason endReason = SessionEndReason.NetworkError;
            // Any bytes arriving keep the dead-link check happy, not only whole packets (slow links, big frames).
            Stream reader = new ActivityStream(sessionStream, () => Interlocked.Exchange(ref _lastPacketTicks, Stopwatch.GetTimestamp()));
            try
            {
                while (!ct.IsCancellationRequested && _isConnected)
                {
                    KeyValuePair<PacketType, byte[]>? pkt = await TurboProtocol.ReadPacketAsync(reader, ct, sessionSec);
                    if (pkt == null)
                    {
                        endReason = SessionEndReason.HostClosed;
                        break;
                    }
                    Interlocked.Exchange(ref _lastPacketTicks, Stopwatch.GetTimestamp());

                    PacketType type = pkt.Value.Key;
                    byte[] payload = pkt.Value.Value;
                    Interlocked.Add(ref _bytesThisSec, payload.Length + 9);

                    switch (type)
                    {
                        case PacketType.ScreenFrame:
                            ProcessIncomingScreenFrame(payload);
                            break;
                        case PacketType.ShellOutput:
                            string output = Encoding.UTF8.GetString(TurboProtocol.DecompressFast(payload));
                            BeginInvoke(new Action(() =>
                            {
                                _rtbTerminalOutput.AppendText(output + "\n");
                                _rtbTerminalOutput.ScrollToCaret();
                            }));
                            break;
                        case PacketType.FileListResponse:
                            string listText = Encoding.UTF8.GetString(TurboProtocol.DecompressFast(payload));
                            BeginInvoke(new Action(() => PopulateRemoteFilesView(listText)));
                            break;
                        case PacketType.FileUploadStart:
                            StartReceivingDownloadedFile(Encoding.UTF8.GetString(payload));
                            break;
                        case PacketType.FileUploadChunk:
                            if (_downloadingStream != null)
                            {
                                byte[] rawChunk = TurboProtocol.DecompressFast(payload);
                                await _downloadingStream.WriteAsync(rawChunk, 0, rawChunk.Length, ct);
                            }
                            break;
                        case PacketType.FileUploadEnd:
                            FinishReceivingDownloadedFile();
                            break;
                        case PacketType.ClipboardSync:
                            string clipText = Encoding.UTF8.GetString(payload);
                            BeginInvoke(new Action(() =>
                            {
                                try { if (!string.IsNullOrEmpty(clipText)) Clipboard.SetText(clipText); } catch { }
                            }));
                            break;
                        case PacketType.Pong:
                            if (payload.Length >= 8)
                            {
                                long sentTick = BitConverter.ToInt64(payload, 0);
                                _lastPingRttMs = Math.Max(1, (Stopwatch.GetTimestamp() - sentTick) * 1000 / Stopwatch.Frequency);
                            }
                            break;
                    }
                }
            }
            catch { }
            finally
            {
                // Only when the HOST ended the CURRENT session. After a user Disconnect (token cancelled, _stream
                // cleared or replaced) a late teardown here could end a connection the user just started.
                if (!ct.IsCancellationRequested && !IsDisposed && _stream == sessionStream)
                {
                    SessionEndReason reason = endReason;
                    try { BeginInvoke(new Action(() => OnSessionLost(sessionStream, reason))); } catch (InvalidOperationException) { }
                }
            }
        }

        /// <summary>UI thread. The current session ended without the user asking: reconnect if that makes sense.</summary>
        private void OnSessionLost(NetworkStream sessionStream, SessionEndReason reason)
        {
            if (!_isConnected || (sessionStream != null && !ReferenceEquals(_stream, sessionStream))) return;
            ReconnectTarget target = _reconnectTarget;
            EndSession();
            if (target != null && ReconnectSchedule.ShouldReconnect(reason))
            {
                Task loop = ReconnectLoopAsync(target);
                return;
            }
            _lblTelemetry.Text = reason == SessionEndReason.HostClosed
                ? "● DISCONNECTED: the Host ended the session."
                : "● DISCONNECTED: the connection was lost.";
        }

        /// <summary>
        /// UI thread. Redials until the session is back, the user stops it, or the Host refuses the key. Dials what
        /// the user typed plus the Host's own name, so a dead typed address (e.g. an expired tunnel) is no dead end.
        /// </summary>
        private async Task ReconnectLoopAsync(ReconnectTarget target)
        {
            CancellationTokenSource cts = new CancellationTokenSource();
            _reconnectCts = cts;
            CancellationToken ct = cts.Token;
            _isReconnecting = true;
            _lastBannerText = null;
            _lostClock.Restart();
            _reconnectName = string.IsNullOrEmpty(target.MachineName) ? target.HostInput : target.MachineName;
            _btnConnect.Text = "✖ Stop reconnecting";
            _btnConnect.BackColor = Color.FromArgb(195, 110, 30);
            Text = "Reconnecting to " + _reconnectName + "… — TurboRemote";

            string fullDial = target.HostInput;
            bool canFallBackToName = !string.IsNullOrEmpty(target.MachineName) &&
                                     !string.Equals(target.HostInput, target.MachineName, StringComparison.OrdinalIgnoreCase);
            if (canFallBackToName) fullDial += "," + target.MachineName;

            // An address typed by hand can later belong to another TurboRemote PC (DHCP), and a typed IP usually
            // wins the race against the name. When that PC refuses the key or turns out to be a different PC,
            // the next attempt dials the Host's own name alone. Only that one attempt: the right PC may come back
            // at the typed address, and an unresolvable name must not hide it.
            bool nameOnlyNext = false;
            AuthRefusedException firstRefusal = null;

            for (int attempt = 1; !ct.IsCancellationRequested; attempt++)
            {
                _reconnectAttempt = attempt;
                string dial = nameOnlyNext ? target.MachineName : fullDial;
                nameOnlyNext = false;
                TcpClient attemptClient = null;
                try
                {
                    for (int left = ReconnectSchedule.DelayBeforeAttemptMs(attempt); left > 0; left -= 250)
                    {
                        _reconnectNextInSec = (left + 999) / 1000;
                        ShowReconnectStatus();
                        await Task.Delay(Math.Min(250, left), ct);
                    }
                    _reconnectNextInSec = 0;
                    ShowReconnectStatus();

                    KeyValuePair<TcpClient, EndpointCandidate> winner = await SmartRouteEngine.RaceFastestConnectionAsync(dial, target.Port, target.Pin, ct, true);
                    attemptClient = winner.Key;
                    await InitializeActiveSessionAsync(winner.Key, winner.Value, ct, target.MachineName, target.Pin);

                    if (ReferenceEquals(_reconnectCts, cts)) _reconnectCts = null;
                    _isReconnecting = false;
                    _reconnectedNoticeText = "● RECONNECTED after " + ((_lostClock.ElapsedMilliseconds + 500) / 1000) + " s";
                    _reconnectedNoticeUntilMs = _uiClock.ElapsedMilliseconds + 5000;
                    _lblTelemetry.Text = _reconnectedNoticeText;
                    _lblTelemetry.ForeColor = Color.FromArgb(110, 255, 165);
                    _screenCanvas.Invalidate();
                    return;
                }
                catch (AuthRefusedException ex)
                {
                    // Refused at the typed address: possibly not our PC any more. One try by name decides it; a
                    // refusal there too means the key really is wrong (two attempts, well below any lockout).
                    if (canFallBackToName && firstRefusal == null)
                    {
                        firstRefusal = ex;
                        nameOnlyNext = true;
                        continue;
                    }
                    StopReconnecting("● RECONNECT STOPPED: " + ex.Message);
                    return;
                }
                catch (WrongHostException)
                {
                    CloseAttemptSocket(attemptClient);
                    if (ct.IsCancellationRequested) return;
                    if (firstRefusal != null)
                    {
                        StopReconnecting("● RECONNECT STOPPED: " + firstRefusal.Message);
                        return;
                    }
                    nameOnlyNext = canFallBackToName;
                }
                catch (Exception)
                {
                    CloseAttemptSocket(attemptClient); // a half-finished attempt, never a newer Connect's socket
                    if (ct.IsCancellationRequested) return;
                    // The name led nowhere, so the refusal at the typed address stands: the key is wrong there.
                    if (firstRefusal != null)
                    {
                        StopReconnecting("● RECONNECT STOPPED: " + firstRefusal.Message);
                        return;
                    }
                }
            }
        }

        private void ShowReconnectStatus()
        {
            if (!_isReconnecting) return;
            _lblTelemetry.Text = "● RECONNECTING to " + _reconnectName + " (attempt " + _reconnectAttempt + ")…";
            _lblTelemetry.ForeColor = Color.FromArgb(255, 200, 110);
            // Called every 250 ms; the banner text changes once a second at most, so repaint only then.
            string banner = ReconnectBannerText();
            if (banner != _lastBannerText)
            {
                _lastBannerText = banner;
                _screenCanvas.Invalidate();
            }
        }

        private string ReconnectBannerText()
        {
            return _reconnectNextInSec > 0
                ? string.Format("Connection lost — reconnecting to {0} (attempt {1}, next try in {2} s)…", _reconnectName, _reconnectAttempt, _reconnectNextInSec)
                : string.Format("Connection lost — reconnecting to {0} (attempt {1}, trying now)…", _reconnectName, _reconnectAttempt);
        }

        private void StopReconnecting(string message)
        {
            CancellationTokenSource cts = _reconnectCts;
            _reconnectCts = null;
            // Forgotten, so nothing can restart reconnecting later (a dead link noticed after Stop used to).
            _reconnectTarget = null;
            try { if (cts != null) cts.Cancel(); } catch { }
            if (!_isConnected) CloseSessionSocket(); // an attempt still signing in
            _isReconnecting = false;
            ResetConnectButton();
            if (!_isConnected) Text = BaseTitle;
            if (message != null)
            {
                _lblTelemetry.Text = message;
                _lblTelemetry.ForeColor = Color.FromArgb(255, 150, 110);
            }
            _screenCanvas.Invalidate();
        }

        private void ProcessIncomingScreenFrame(byte[] payload)
        {
            int sw, sh, tiles;
            lock (_bitmapLock)
            {
                DeltaScreenEncoder.ApplyDeltaFrame(payload, ref _remoteBitmap,
                    out _remoteNativeW, out _remoteNativeH, out sw, out sh, out tiles);
            }
            Interlocked.Increment(ref _framesThisSec);
            _screenCanvas.Invalidate();
        }

        private void OnScreenCanvasPaint(object sender, PaintEventArgs e)
        {
            e.Graphics.InterpolationMode = InterpolationMode.Bilinear;
            e.Graphics.CompositingQuality = CompositingQuality.HighSpeed;

            Rectangle area = new Rectangle(0, 0, _screenCanvas.Width, _screenCanvas.Height);
            bool drewFrame = false;
            lock (_bitmapLock)
            {
                if (_remoteBitmap != null)
                {
                    e.Graphics.DrawImage(_remoteBitmap, area);
                    drewFrame = true;
                }
            }

            if (_isReconnecting)
            {
                // The last picture stays, dimmed, under the reconnect banner.
                if (drewFrame)
                {
                    using (SolidBrush dim = new SolidBrush(Color.FromArgb(150, 0, 0, 0))) e.Graphics.FillRectangle(dim, area);
                }
                string banner = ReconnectBannerText();
                using (SolidBrush text = new SolidBrush(Color.FromArgb(255, 215, 140)))
                using (Font font = new Font("Segoe UI", 13f, FontStyle.Bold))
                {
                    SizeF sz = e.Graphics.MeasureString(banner, font);
                    e.Graphics.DrawString(banner, font, text, Math.Max(20, (area.Width - sz.Width) / 2), Math.Max(20, (area.Height - sz.Height) / 2));
                }
                return;
            }
            if (drewFrame) return;

            using (SolidBrush brush = new SolidBrush(Color.FromArgb(140, 160, 190)))
            using (Font font = new Font("Segoe UI", 12f, FontStyle.Bold))
            {
                string msg = _isConnected
                    ? "Connected — waiting for the first picture…"
                    : "Type the Host ID and access key above, then click '⚡ Connect'.";
                SizeF sz = e.Graphics.MeasureString(msg, font);
                e.Graphics.DrawString(msg, font, brush,
                    Math.Max(20, (_screenCanvas.Width - sz.Width) / 2),
                    Math.Max(20, (_screenCanvas.Height - sz.Height) / 2));
            }
        }

        private void GetNormalizedCanvasCoords(int x, int y, out float nx, out float ny)
        {
            nx = x / (float)Math.Max(1, _screenCanvas.Width);
            ny = y / (float)Math.Max(1, _screenCanvas.Height);
            if (nx < 0f) nx = 0f; if (nx > 1f) nx = 1f;
            if (ny < 0f) ny = 0f; if (ny > 1f) ny = 1f;
        }

        private async void OnCanvasMouseMove(object sender, MouseEventArgs e)
        {
            if (!_isConnected) return;
            long now = _mouseMoveClock.ElapsedMilliseconds;
            if (now - _lastMouseMoveSentMs < 8) return; // 120 Hz ultra-responsive cursor tracking
            _lastMouseMoveSentMs = now;

            float nx, ny;
            GetNormalizedCanvasCoords(e.X, e.Y, out nx, out ny);
            _lastMouseNx = nx;
            _lastMouseNy = ny;
            await SendMousePacketAsync(MouseActionType.Move, nx, ny, 0);
        }

        private async void OnCanvasMouseDown(object sender, MouseEventArgs e)
        {
            _screenCanvas.Focus();
            if (!_isConnected) return;
            float nx, ny;
            GetNormalizedCanvasCoords(e.X, e.Y, out nx, out ny);
            MouseActionType act = MouseActionType.LeftDown;
            if (e.Button == MouseButtons.Right) act = MouseActionType.RightDown;
            else if (e.Button == MouseButtons.Middle) act = MouseActionType.MiddleDown;
            else if (e.Button != MouseButtons.Left) return; // X1/X2 side buttons are not supported by the Host
            _lastMouseNx = nx;
            _lastMouseNy = ny;
            _remoteButtonsDown.Add(act);
            await SendMousePacketAsync(act, nx, ny, 0);
        }

        private async void OnCanvasMouseUp(object sender, MouseEventArgs e)
        {
            if (!_isConnected) return;
            float nx, ny;
            GetNormalizedCanvasCoords(e.X, e.Y, out nx, out ny);
            MouseActionType down = MouseActionType.LeftDown, act = MouseActionType.LeftUp;
            if (e.Button == MouseButtons.Right) { down = MouseActionType.RightDown; act = MouseActionType.RightUp; }
            else if (e.Button == MouseButtons.Middle) { down = MouseActionType.MiddleDown; act = MouseActionType.MiddleUp; }
            else if (e.Button != MouseButtons.Left) return;
            // Already released when focus left the remote view (e.g. Alt+Tab in the middle of a drag).
            if (!_remoteButtonsDown.Remove(down)) return;
            await SendMousePacketAsync(act, nx, ny, 0);
        }

        private async void OnCanvasMouseWheel(object sender, MouseEventArgs e)
        {
            if (!_isConnected) return;
            float nx, ny;
            GetNormalizedCanvasCoords(e.X, e.Y, out nx, out ny);
            await SendMousePacketAsync(MouseActionType.Wheel, nx, ny, e.Delta);
        }

        /// <summary>The low-level hook takes keys only while the remote view has focus in the foreground window.</summary>
        private bool IsRemoteKeyboardActive()
        {
            return _isConnected && _screenCanvas.CaptureAllKeys && _screenCanvas.Focused &&
                   WindowState != FormWindowState.Minimized && GetForegroundWindow() == Handle;
        }

        /// <summary>
        /// Every key for the remote PC arrives here, from the keyboard hook or (if Windows removed the hook) from
        /// the remote view's own window messages. Runs on the UI thread, so packets queue in keystroke order.
        /// </summary>
        private void OnRemoteKey(ushort vk, byte scanCode, bool extended, bool isDown)
        {
            if (!_isConnected) return;

            if (RemoteKeyboardHook.IsLockKey(vk))
            {
                // Toggled on this PC as usual; once released, the remote PC is set to the same state.
                if (!isDown)
                {
                    Task sync = SendLockKeyStateAsync();
                }
                return;
            }

            if (isDown && vk == VK_L && (IsRemoteKeyDown(VK_LWIN) || IsRemoteKeyDown(VK_RWIN)))
            {
                // Win+L locks THIS PC, as everywhere else. Sent on, it locked the remote PC, whose lock screen
                // TurboRemote can neither show nor type into, so the remote PC was lost until someone was there.
                ReleaseRemoteInput();
                LockWorkStation();
                return;
            }

            int id = vk | (extended ? 0x10000 : 0);
            if (isDown)
            {
                _remoteKeysDown[id] = new HeldRemoteKey { Vk = vk, Scan = scanCode, Extended = extended };
            }
            else if (!_remoteKeysDown.Remove(id))
            {
                // Windows delivers only the release of Print Screen to programs; press it here first.
                if (vk != VK_SNAPSHOT) return; // its press never reached the remote (or it was already released)
                Task press = SendKeyPacketAsync(vk, scanCode, extended, true);
            }

            Task send = SendKeyPacketAsync(vk, scanCode, extended, isDown);
        }

        private bool IsRemoteKeyDown(ushort vk)
        {
            return _remoteKeysDown.ContainsKey(vk) || _remoteKeysDown.ContainsKey(vk | 0x10000);
        }

        /// <summary>Releases on the remote PC every key and mouse button it still has pressed because of us.</summary>
        private void ReleaseRemoteInput()
        {
            if (!_isConnected)
            {
                _remoteKeysDown.Clear();
                _remoteButtonsDown.Clear();
                return;
            }

            foreach (HeldRemoteKey k in new List<HeldRemoteKey>(_remoteKeysDown.Values))
            {
                Task up = SendKeyPacketAsync(k.Vk, k.Scan, k.Extended, false);
            }
            _remoteKeysDown.Clear();

            foreach (MouseActionType b in new List<MouseActionType>(_remoteButtonsDown))
            {
                MouseActionType up = b == MouseActionType.RightDown ? MouseActionType.RightUp
                    : b == MouseActionType.MiddleDown ? MouseActionType.MiddleUp
                    : MouseActionType.LeftUp;
                Task send = SendMousePacketAsync(up, _lastMouseNx, _lastMouseNy, 0);
            }
            _remoteButtonsDown.Clear();
        }

        private async Task SendLockKeyStateAsync()
        {
            if (!_isConnected) return;
            string state = "SYNC_LOCKS:" +
                (IsKeyLocked(Keys.CapsLock) ? "1" : "0") +
                (IsKeyLocked(Keys.NumLock) ? "1" : "0") +
                (IsKeyLocked(Keys.Scroll) ? "1" : "0");
            await SendPacketSafeAsync(PacketType.SystemAction, Encoding.UTF8.GetBytes(state));
        }

        /// <summary>
        /// Explains, up front, the two setups in which typing looks broken while the mouse still works, instead of
        /// leaving the user to guess.
        /// </summary>
        private void ShowConnectionNotice(string remoteMachine, string hostVersion)
        {
            string notice = null;
            if (string.Equals(remoteMachine, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
            {
                notice = "⚠ This viewer is connected to the PC it is running on. Keys you type are injected straight back into " +
                         "this viewer window, and the mouse pointer follows the remote view, so the keyboard cannot be used or " +
                         "tested this way. Connect from a second PC.";
            }
            else if (hostVersion == null)
            {
                notice = "⚠ " + remoteMachine + " runs an older TurboRemote. With it, Ctrl/Alt/Shift can stay stuck after Alt+Tab or " +
                         "switching tabs, and then typing stops working while the mouse still does. Copy this TurboRemote.exe " +
                         "(v" + TurboProtocol.AppVersion + ") to " + remoteMachine + " and restart the Host there.";
            }

            _lblNotice.Text = notice ?? "";
            _lblNotice.Visible = notice != null;
        }

        /// <summary>After switching tabs (or using a toolbar list), give the keyboard to that tab's main input.</summary>
        private void FocusSelectedTabSoon()
        {
            if (IsDisposed || !IsHandleCreated) return;
            BeginInvoke(new Action(() =>
            {
                if (_tabControl.SelectedIndex == 0)
                {
                    if (_screenCanvas.CanFocus && !_screenCanvas.Focused) _screenCanvas.Focus();
                }
                else if (_tabControl.SelectedIndex == 1)
                {
                    if (_txtTerminalInput.CanFocus && !_txtTerminalInput.Focused) _txtTerminalInput.Focus();
                }
            }));
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (_isReconnecting) StopReconnecting(null);
            if (_keyboardHook != null)
            {
                _keyboardHook.Dispose();
                _keyboardHook = null;
            }
            base.OnFormClosed(e);
        }

        private async Task SendMousePacketAsync(MouseActionType action, float nx, float ny, int wheel)
        {
            byte[] buf = new byte[13];
            buf[0] = (byte)action;
            Buffer.BlockCopy(BitConverter.GetBytes(nx), 0, buf, 1, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(ny), 0, buf, 5, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(wheel), 0, buf, 9, 4);
            await SendPacketSafeAsync(PacketType.MouseInput, buf);
        }

        /// <summary>Payload: virtual key (2 bytes), down (1), scan code (1), flags (1, bit 0 = extended key).
        /// Older Hosts read only the first 3 bytes.</summary>
        private async Task SendKeyPacketAsync(ushort vk, byte scanCode, bool extended, bool isDown)
        {
            byte[] buf = new byte[5];
            Buffer.BlockCopy(BitConverter.GetBytes(vk), 0, buf, 0, 2);
            buf[2] = (byte)(isDown ? 1 : 0);
            buf[3] = scanCode;
            buf[4] = (byte)(extended ? 1 : 0);
            await SendPacketSafeAsync(PacketType.KeyInput, buf);
        }

        private async Task SendStreamConfigAsync()
        {
            if (!_isConnected) return;
            byte[] cfg = new byte[5];
            if (_fillingScreenList) return;
            int quality = Math.Max(0, Math.Min(QualityProfiles.Length - 1, _cmbBandwidth.SelectedIndex));
            cfg[0] = QualityProfiles[quality];
            cfg[1] = (byte)Math.Max(0, _cmbMonitor.SelectedIndex);
            cfg[2] = 0; // colour (grayscale was removed from the UI)
            cfg[3] = 0;
            cfg[4] = 0;
            await SendPacketSafeAsync(PacketType.ConfigUpdate, cfg);
        }

        private async Task SendTerminalCommandAsync()
        {
            string cmd = _txtTerminalInput.Text.Trim();
            if (string.IsNullOrEmpty(cmd) || !_isConnected) return;
            _txtTerminalInput.Clear();
            await SendPacketSafeAsync(PacketType.ShellExec, Encoding.UTF8.GetBytes(cmd));
        }

        private async Task RequestRemoteFileListAsync(string path)
        {
            if (!_isConnected) return;
            await SendPacketSafeAsync(PacketType.FileListRequest, Encoding.UTF8.GetBytes(path));
        }

        private void PopulateRemoteFilesView(string response)
        {
            _lvRemoteFiles.BeginUpdate();
            _lvRemoteFiles.Items.Clear();

            string[] lines = response.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string line in lines)
            {
                string[] parts = line.Split('|');
                if (parts[0] == "PATH" && parts.Length > 1)
                {
                    _txtRemotePath.Text = parts[1];
                }
                else if (parts[0] == "DIR" && parts.Length >= 4)
                {
                    ListViewItem item = new ListViewItem("[DIR]");
                    item.ForeColor = Color.FromArgb(255, 220, 115);
                    item.SubItems.Add(parts[1]);
                    item.SubItems.Add("<Folder>");
                    item.SubItems.Add(parts[2]);
                    _lvRemoteFiles.Items.Add(item);
                }
                else if (parts[0] == "FILE" && parts.Length >= 4)
                {
                    ListViewItem item = new ListViewItem("FILE");
                    item.ForeColor = Color.FromArgb(150, 235, 255);
                    item.SubItems.Add(parts[1]);
                    item.SubItems.Add(parts[3]);
                    item.SubItems.Add(parts[2]);
                    _lvRemoteFiles.Items.Add(item);
                }
                else if (parts[0] == "ERROR" && parts.Length > 1)
                {
                    _lblFileStatus.Text = " Remote Error: " + parts[1];
                }
            }

            _lvRemoteFiles.EndUpdate();
        }

        private async Task UploadLocalFileToHostAsync()
        {
            if (!_isConnected) return;
            if (_uploadInProgress)
            {
                // Two uploads at once interleaved their chunks on the host and corrupted the file.
                _lblFileStatus.Text = " An upload is already in progress. Please wait for it to finish.";
                return;
            }

            using (OpenFileDialog ofd = new OpenFileDialog())
            {
                if (ofd.ShowDialog() != DialogResult.OK) return;

                _uploadInProgress = true;
                try
                {
                    string localPath = ofd.FileName;
                    string remoteFolder = _txtRemotePath.Text.Trim();
                    bool useHostDefaultFolder = string.IsNullOrEmpty(remoteFolder) || remoteFolder == "DEFAULT";

                    // For the default folder send only the file name; the host puts it in ITS profile folder.
                    // (This used to send THIS PC's profile path, which may not exist on the host.)
                    string targetRemotePath = useHostDefaultFolder
                        ? Path.GetFileName(localPath)
                        : Path.Combine(remoteFolder, Path.GetFileName(localPath));
                    _lblFileStatus.Text = " Uploading " + Path.GetFileName(localPath) + "...";

                    await SendPacketSafeAsync(PacketType.FileUploadStart, Encoding.UTF8.GetBytes(targetRemotePath));

                    byte[] buffer = new byte[64 * 1024];
                    using (FileStream fs = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        int read;
                        while ((read = await fs.ReadAsync(buffer, 0, buffer.Length)) > 0)
                        {
                            if (!_isConnected)
                            {
                                _lblFileStatus.Text = " Upload interrupted: disconnected from host.";
                                return;
                            }
                            byte[] slice = new byte[read];
                            Buffer.BlockCopy(buffer, 0, slice, 0, read);
                            byte[] compressed = TurboProtocol.CompressFast(slice);
                            await SendPacketSafeAsync(PacketType.FileUploadChunk, compressed);
                        }
                    }

                    await SendPacketSafeAsync(PacketType.FileUploadEnd, new byte[0]);
                    _lblFileStatus.Text = " Upload completed: " + targetRemotePath;
                    await RequestRemoteFileListAsync(useHostDefaultFolder ? "DEFAULT" : remoteFolder);
                }
                catch (Exception ex)
                {
                    _lblFileStatus.Text = " Upload failed: " + ex.Message;
                }
                finally
                {
                    _uploadInProgress = false;
                }
            }
        }

        private async Task DownloadSelectedFileFromHostAsync()
        {
            if (!_isConnected || _lvRemoteFiles.SelectedItems.Count == 0) return;
            ListViewItem item = _lvRemoteFiles.SelectedItems[0];
            if (item.Text != "FILE") return;

            string remoteFullPath = item.SubItems[3].Text;
            _lblFileStatus.Text = " Requesting download of " + remoteFullPath + "...";
            await SendPacketSafeAsync(PacketType.FileDownloadRequest, Encoding.UTF8.GetBytes(remoteFullPath));
        }

        private void StartReceivingDownloadedFile(string fileName)
        {
            try
            {
                if (_downloadingStream != null) _downloadingStream.Dispose();
                string downloadsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "TurboRemote");
                Directory.CreateDirectory(downloadsDir);
                _downloadingFilePath = Path.Combine(downloadsDir, Path.GetFileName(fileName));
                _downloadingStream = new FileStream(_downloadingFilePath, FileMode.Create, FileAccess.Write, FileShare.None);

                BeginInvoke(new Action(() =>
                {
                    _lblFileStatus.Text = " Receiving file -> " + _downloadingFilePath + "...";
                }));
            }
            catch { }
        }

        private void FinishReceivingDownloadedFile()
        {
            try
            {
                if (_downloadingStream != null)
                {
                    _downloadingStream.Flush();
                    _downloadingStream.Dispose();
                    _downloadingStream = null;
                }
                BeginInvoke(new Action(() =>
                {
                    _lblFileStatus.Text = " Download saved to: " + _downloadingFilePath;
                }));
            }
            catch { }
        }

        private async Task OnTelemetryTimerTickAsync()
        {
            _currentFps = Interlocked.Exchange(ref _framesThisSec, 0);
            long bytes = Interlocked.Exchange(ref _bytesThisSec, 0);
            _currentKbps = bytes / 1024.0;

            if (_isConnected)
            {
                // The Host answers the ping below every second and streams frames; 20 s of nothing at all means
                // the link is dead (Wi-Fi gone, network changed, Host asleep) even though the socket looks open.
                long silentMs = (Stopwatch.GetTimestamp() - Interlocked.Read(ref _lastPacketTicks)) * 1000 / Stopwatch.Frequency;
                if (silentMs >= ReconnectSchedule.DeadLinkMs)
                {
                    OnSessionLost(_stream, SessionEndReason.DeadLink);
                    return;
                }

                if (_uiClock.ElapsedMilliseconds < _reconnectedNoticeUntilMs)
                {
                    _lblTelemetry.Text = _reconnectedNoticeText;
                    _lblTelemetry.ForeColor = Color.FromArgb(110, 255, 165);
                }
                else
                {
                    _lblTelemetry.Text = string.Format("● LIVE · {0} fps · {1:F1} KB/s · {2} ms", _currentFps, _currentKbps, _lastPingRttMs);
                    _lblTelemetry.ForeColor = _currentKbps < 80
                        ? Color.FromArgb(110, 255, 165)
                        : Color.FromArgb(110, 215, 255);
                }

                byte[] pingPayload = BitConverter.GetBytes(Stopwatch.GetTimestamp());
                await SendPacketSafeAsync(PacketType.Ping, pingPayload);
            }
            else if (!_isReconnecting && ++_ticksSinceTailscaleCheck >= 30)
            {
                _ticksSinceTailscaleCheck = 0;
                await RefreshOwnTailscaleAsync();
            }
        }

        private async Task SendPacketSafeAsync(PacketType type, byte[] payload)
        {
            if (!_isConnected || _stream == null) return;
            try
            {
                await TurboProtocol.SendPacketAsync(_stream, _writeLock, type, payload, _sessionCts.Token, _sec);
            }
            catch { }
        }

        private static Button CreateToolbarButton(string text, int x, int y, int w, int h, Color bg, bool takesFocus = true)
        {
            Button btn = takesFocus ? new Button() : new NoFocusButton();
            btn.Text = text;
            btn.Location = new Point(x, y);
            btn.Size = new Size(w, h);
            btn.BackColor = bg;
            btn.ForeColor = Color.White;
            btn.FlatStyle = FlatStyle.Flat;
            btn.Font = new Font("Segoe UI", 8.4f, FontStyle.Bold);
            btn.Cursor = Cursors.Hand;
            return btn;
        }
    }
}
