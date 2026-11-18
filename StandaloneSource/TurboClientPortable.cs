using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using TurboCore;

namespace TurboClient
{
    public class DoubleBufferedPanel : Panel
    {
        public DoubleBufferedPanel()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            UpdateStyles();
        }
    }

    public class ClientMainForm : Form
    {
        private TextBox _txtHostIp;
        private TextBox _txtPort;
        private TextBox _txtPin;
        private Button _btnConnect;
        private Button _btnListenReverse;
        private ComboBox _cmbBandwidth;
        private CheckBox _chkGrayscale;
        private ComboBox _cmbMonitor;
        private Label _lblTelemetry;

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
        private TcpListener _reverseListener;
        private bool _isConnected = false;
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
        private long _lastMouseMoveSentTick = 0;

        private FileStream _downloadingStream;
        private string _downloadingFilePath;

        public ClientMainForm()
        {
            Text = "TurboRemote — Viewer Client";
            Size = new Size(1250, 800);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(16, 18, 24);
            ForeColor = Color.FromArgb(230, 235, 245);
            Font = new Font("Segoe UI", 9f);
            KeyPreview = true;

            InitializeUi();

            System.Windows.Forms.Timer statsTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            statsTimer.Tick += async (s, e) => await OnTelemetryTimerTickAsync();
            statsTimer.Start();
        }

        private void InitializeUi()
        {
            Panel topBar = new Panel
            {
                Dock = DockStyle.Top,
                Height = 84,
                BackColor = Color.FromArgb(24, 28, 40)
            };

            Label lblHost = new Label { Text = "Smart Route / IP:", Location = new Point(12, 13), AutoSize = true };
            _txtHostIp = new TextBox
            {
                Text = "AUTO",
                Location = new Point(120, 10),
                Width = 145,
                BackColor = Color.FromArgb(16, 18, 26),
                ForeColor = Color.FromArgb(110, 225, 255),
                Font = new Font("Consolas", 9.5f, FontStyle.Bold),
                BorderStyle = BorderStyle.FixedSingle
            };

            Label lblPort = new Label { Text = "Port:", Location = new Point(272, 13), AutoSize = true };
            _txtPort = new TextBox
            {
                Text = "9876",
                Location = new Point(308, 10),
                Width = 58,
                BackColor = Color.FromArgb(16, 18, 26),
                ForeColor = Color.White,
                Font = new Font("Consolas", 9.5f),
                BorderStyle = BorderStyle.FixedSingle
            };

            Label lblPin = new Label { Text = "PIN:", Location = new Point(374, 13), AutoSize = true };
            _txtPin = new TextBox
            {
                Text = "2580",
                Location = new Point(406, 10),
                Width = 65,
                BackColor = Color.FromArgb(16, 18, 26),
                ForeColor = Color.FromArgb(130, 255, 165),
                Font = new Font("Consolas", 9.5f, FontStyle.Bold),
                BorderStyle = BorderStyle.FixedSingle
            };

            _btnConnect = CreateToolbarButton("⚡ Connect to Host", 482, 8, 145, 28, Color.FromArgb(30, 145, 90));
            _btnConnect.Click += async (s, e) => await ToggleConnectAsync();

            _btnListenReverse = CreateToolbarButton("📡 Listen Mode (9877)", 634, 8, 155, 28, Color.FromArgb(95, 65, 160));
            _btnListenReverse.Click += async (s, e) => await StartReverseListenerAsync();

            Label lblMode = new Label { Text = "Speed Profile:", Location = new Point(800, 13), AutoSize = true };
            _cmbBandwidth = new ComboBox
            {
                Location = new Point(888, 10),
                Width = 195,
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(16, 18, 26),
                ForeColor = Color.FromArgb(255, 220, 120)
            };
            _cmbBandwidth.Items.AddRange(new object[]
            {
                "0: Ultra-Low 2G (38% Gray Q18)",
                "1: Low 3G / Weak Wi-Fi (52% Q32)",
                "2: Balanced 4G / WAN (75% Q50)",
                "3: Turbo Broadband (100% Q78)",
                "4: Auto-Adaptive Smart Speed"
            });
            _cmbBandwidth.SelectedIndex = 4; // Default to Auto-Adaptive Smart Speed
            _cmbBandwidth.SelectedIndexChanged += async (s, e) => await SendStreamConfigAsync();

            _chkGrayscale = new CheckBox
            {
                Text = "Turbo Gray",
                Location = new Point(1094, 12),
                AutoSize = true,
                ForeColor = Color.FromArgb(190, 205, 230)
            };
            _chkGrayscale.CheckedChanged += async (s, e) => await SendStreamConfigAsync();

            Button btnRefreshFrame = CreateToolbarButton("🔄 Keyframe", 12, 46, 94, 28, Color.FromArgb(50, 70, 105));
            btnRefreshFrame.Click += async (s, e) => await SendPacketSafeAsync(PacketType.ForceFullFrame, new byte[0]);

            Button btnElevateHost = CreateToolbarButton("🛡 Elevate Host UAC", 112, 46, 134, 28, Color.FromArgb(175, 105, 30));
            btnElevateHost.Click += async (s, e) => await SendPacketSafeAsync(PacketType.SystemAction, Encoding.UTF8.GetBytes("ELEVATE_ADMIN"));

            Button btnLockInput = CreateToolbarButton("🔒 Lock Host Input", 252, 46, 125, 28, Color.FromArgb(135, 55, 75));
            btnLockInput.Click += async (s, e) =>
            {
                _remoteInputLocked = !_remoteInputLocked;
                btnLockInput.Text = _remoteInputLocked ? "🔓 Unlock Input" : "🔒 Lock Host Input";
                string cmd = _remoteInputLocked ? "LOCK_INPUT_ON" : "LOCK_INPUT_OFF";
                await SendPacketSafeAsync(PacketType.SystemAction, Encoding.UTF8.GetBytes(cmd));
            };

            Button btnTaskMgr = CreateToolbarButton("📊 Task Mgr", 383, 46, 92, 28, Color.FromArgb(55, 65, 95));
            btnTaskMgr.Click += async (s, e) => await SendPacketSafeAsync(PacketType.SystemAction, Encoding.UTF8.GetBytes("TASK_MGR"));

            Button btnShowDesktop = CreateToolbarButton("🖥 Win+D", 481, 46, 78, 28, Color.FromArgb(55, 65, 95));
            btnShowDesktop.Click += async (s, e) => await SendPacketSafeAsync(PacketType.SystemAction, Encoding.UTF8.GetBytes("WIN_D"));

            Button btnPushClip = CreateToolbarButton("📋 Push Clip", 565, 46, 95, 28, Color.FromArgb(55, 65, 95));
            btnPushClip.Click += async (s, e) =>
            {
                if (Clipboard.ContainsText())
                {
                    await SendPacketSafeAsync(PacketType.ClipboardSync, Encoding.UTF8.GetBytes(Clipboard.GetText()));
                }
            };

            Button btnPullClip = CreateToolbarButton("📥 Pull Clip", 666, 46, 95, 28, Color.FromArgb(55, 65, 95));
            btnPullClip.Click += async (s, e) => await SendPacketSafeAsync(PacketType.SystemAction, Encoding.UTF8.GetBytes("GET_CLIPBOARD"));

            _cmbMonitor = new ComboBox
            {
                Location = new Point(768, 48),
                Width = 92,
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(16, 18, 26),
                ForeColor = Color.White
            };
            _cmbMonitor.Items.AddRange(new object[] { "Monitor 1", "Monitor 2", "Monitor 3" });
            _cmbMonitor.SelectedIndex = 0;
            _cmbMonitor.SelectedIndexChanged += async (s, e) => await SendStreamConfigAsync();

            _lblTelemetry = new Label
            {
                Text = "● DISCONNECTED | FPS: 0 | Bandwidth: 0.0 KB/s | RTT: -- ms",
                Location = new Point(868, 52),
                AutoSize = true,
                Font = new Font("Consolas", 8.8f, FontStyle.Bold),
                ForeColor = Color.FromArgb(255, 185, 90)
            };

            topBar.Controls.AddRange(new Control[]
            {
                lblHost, _txtHostIp, lblPort, _txtPort, lblPin, _txtPin,
                _btnConnect, _btnListenReverse, lblMode, _cmbBandwidth, _chkGrayscale,
                btnRefreshFrame, btnElevateHost, btnLockInput, btnTaskMgr, btnShowDesktop, btnPushClip, btnPullClip, _cmbMonitor, _lblTelemetry
            });

            _tabControl = new TabControl
            {
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
            };

            TabPage tabDesktop = new TabPage("🖥 Live Remote Desktop (8x8 Delta-Tile Stream)")
            {
                BackColor = Color.FromArgb(10, 12, 16)
            };

            _screenCanvas = new DoubleBufferedPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(10, 12, 16),
                Cursor = Cursors.Cross
            };
            _screenCanvas.Paint += OnScreenCanvasPaint;
            _screenCanvas.MouseMove += OnCanvasMouseMove;
            _screenCanvas.MouseDown += OnCanvasMouseDown;
            _screenCanvas.MouseUp += OnCanvasMouseUp;
            _screenCanvas.MouseWheel += OnCanvasMouseWheel;

            tabDesktop.Controls.Add(_screenCanvas);

            TabPage tabTerminal = new TabPage("💻 Zero-Bandwidth Remote PowerShell")
            {
                BackColor = Color.FromArgb(14, 16, 22)
            };
            BuildTerminalTab(tabTerminal);

            TabPage tabFiles = new TabPage("📁 Ultra-Fast File Manager & Transfer")
            {
                BackColor = Color.FromArgb(18, 21, 30)
            };
            BuildFileTransferTab(tabFiles);

            _tabControl.TabPages.Add(tabDesktop);
            _tabControl.TabPages.Add(tabTerminal);
            _tabControl.TabPages.Add(tabFiles);

            Controls.Add(_tabControl);
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
                Text = "=== TurboRemote Zero-Bandwidth PowerShell Console ===\n" +
                       "Executes administrative commands remotely using < 1 KB of compressed text even on 2G links.\n\n"
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
            _txtPort.Text = port.ToString();
            _txtPin.Text = pin;
            Shown += async (s, e) => await ConnectToHostAsync();
        }

        private async Task ToggleConnectAsync()
        {
            if (_isConnected) Disconnect();
            else await ConnectToHostAsync();
        }

        private async Task ConnectToHostAsync()
        {
            string hostInput = _txtHostIp.Text.Trim();
            int port;
            if (!int.TryParse(_txtPort.Text.Trim(), out port))
            {
                MessageBox.Show("Invalid TCP port number.", "Validation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                _btnConnect.Enabled = false;
                _lblTelemetry.Text = "● SMART-ROUTING: Racing Local LAN + P2P + Internet endpoints...";
                _lblTelemetry.ForeColor = Color.FromArgb(110, 220, 255);

                KeyValuePair<TcpClient, EndpointCandidate> winner = await SmartRouteEngine.RaceFastestConnectionAsync(hostInput, port, _txtPin.Text.Trim());
                TcpClient client = winner.Key;
                EndpointCandidate route = winner.Value;

                await InitializeActiveSessionAsync(client, route);
            }
            catch (Exception ex)
            {
                _lblTelemetry.Text = "● CONNECTION FAILED: " + ex.Message;
                _lblTelemetry.ForeColor = Color.FromArgb(255, 110, 110);
            }
            finally
            {
                _btnConnect.Enabled = true;
            }
        }

        private async Task StartReverseListenerAsync()
        {
            try
            {
                if (_reverseListener != null)
                {
                    _reverseListener.Stop();
                    _reverseListener = null;
                    _btnListenReverse.Text = "📡 Listen Mode (9877)";
                    return;
                }

                _reverseListener = new TcpListener(IPAddress.Any, 9877);
                _reverseListener.Start();
                _btnListenReverse.Text = "⏳ Waiting on 9877...";
                _lblTelemetry.Text = "● LISTENING ON PORT 9877 FOR OUTBOUND HOST...";

                TcpClient client = await _reverseListener.AcceptTcpClientAsync();
                _reverseListener.Stop();
                _reverseListener = null;
                _btnListenReverse.Text = "📡 Listen Mode (9877)";

                await InitializeActiveSessionAsync(client, new EndpointCandidate
                {
                    Host = client.Client.RemoteEndPoint.ToString(),
                    Port = 9877,
                    RouteLabel = "Reverse Outbound WAN"
                });
            }
            catch (Exception ex)
            {
                _lblTelemetry.Text = "● Listen Mode Stopped: " + ex.Message;
            }
        }

        private async Task InitializeActiveSessionAsync(TcpClient client, EndpointCandidate winningRoute)
        {
            _tcpClient = client;
            _tcpClient.NoDelay = true;
            _tcpClient.ReceiveBufferSize = 256 * 1024;
            _tcpClient.SendBufferSize = 256 * 1024;
            _stream = _tcpClient.GetStream();
            _sessionCts = new CancellationTokenSource();

            // 1. Read 32B Cryptographic Challenge (16B Salt + 16B Nonce) from Host
            KeyValuePair<PacketType, byte[]>? challengePkt = await TurboProtocol.ReadPacketAsync(_stream, _sessionCts.Token);
            if (challengePkt == null || challengePkt.Value.Key != PacketType.AuthChallenge || challengePkt.Value.Value.Length != 32)
            {
                Disconnect();
                throw new Exception("Host did not send valid 32-byte cryptographic challenge.");
            }

            byte[] salt16 = new byte[16];
            byte[] challenge16 = new byte[16];
            Buffer.BlockCopy(challengePkt.Value.Value, 0, salt16, 0, 16);
            Buffer.BlockCopy(challengePkt.Value.Value, 16, challenge16, 0, 16);

            // 2. Derive Session AES-256 + HMAC-SHA256 Keys via PBKDF2-SHA256 & Send Zero-Knowledge Proof
            string pin = _txtPin.Text.Trim();
            _sec = new SecurityContext(pin, salt16, challenge16);
            byte[] proof = _sec.ComputeHandshakeProof(challenge16, "CLIENT_AUTH_V2");
            await TurboProtocol.SendPacketAsync(_stream, _writeLock, PacketType.AuthResponse, proof, _sessionCts.Token);

            // 3. Receive & Decrypt AuthResult
            KeyValuePair<PacketType, byte[]>? authResp = await TurboProtocol.ReadPacketAsync(_stream, _sessionCts.Token);
            if (authResp == null || authResp.Value.Key != PacketType.AuthResult)
            {
                Disconnect();
                throw new Exception("Host closed connection during cryptographic handshake.");
            }

            // If failed, host sends unencrypted FAIL message; if succeeded, host sends AES-256 encrypted payload
            byte[] rawPayload = authResp.Value.Value;
            string checkClear = Encoding.UTF8.GetString(rawPayload);
            if (checkClear.StartsWith("FAIL:"))
            {
                Disconnect();
                throw new Exception("Authentication rejected (" + checkClear + ")");
            }

            string authText = Encoding.UTF8.GetString(_sec.VerifyAndDecrypt(PacketType.AuthResult, rawPayload));
            if (!authText.StartsWith("OK|"))
            {
                Disconnect();
                throw new Exception("Authentication verification failed.");
            }

            string[] parts = authText.Split('|');
            string remoteMachine = parts.Length > 1 ? parts[1] : "RemotePC";
            string privLevel = parts.Length > 2 ? parts[2] : "USER";

            _isConnected = true;
            _btnConnect.Text = "⏹ Disconnect";
            _btnConnect.BackColor = Color.FromArgb(185, 55, 55);
            string routeInfo = winningRoute != null ? string.Format("{0} ({1}:{2})", winningRoute.RouteLabel, winningRoute.Host, winningRoute.Port) : "Direct";
            Text = string.Format("🔒 AES-256 Connected -> {0} [{1} | Route: {2}]", remoteMachine, privLevel, routeInfo);

            await SendStreamConfigAsync();
            await RequestRemoteFileListAsync("DEFAULT");

            Task.Run(() => ReceivePacketsLoopAsync(_sessionCts.Token));
        }

        private void Disconnect()
        {
            _isConnected = false;
            try { if (_sessionCts != null) _sessionCts.Cancel(); } catch { }
            try { if (_stream != null) _stream.Dispose(); } catch { }
            try { if (_tcpClient != null) _tcpClient.Close(); } catch { }
            try { if (_downloadingStream != null) _downloadingStream.Dispose(); } catch { }
            try { if (_sec != null) { _sec.Dispose(); _sec = null; } } catch { }

            _btnConnect.Text = "⚡ Connect to Host";
            _btnConnect.BackColor = Color.FromArgb(30, 145, 90);
            _lblTelemetry.Text = "● DISCONNECTED";
            _lblTelemetry.ForeColor = Color.FromArgb(255, 185, 90);
        }

        private async Task ReceivePacketsLoopAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested && _isConnected)
                {
                    KeyValuePair<PacketType, byte[]>? pkt = await TurboProtocol.ReadPacketAsync(_stream, ct, _sec);
                    if (pkt == null) break;

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
                if (!IsDisposed)
                {
                    BeginInvoke(new Action(Disconnect));
                }
            }
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

            lock (_bitmapLock)
            {
                if (_remoteBitmap != null)
                {
                    e.Graphics.DrawImage(_remoteBitmap, new Rectangle(0, 0, _screenCanvas.Width, _screenCanvas.Height));
                    return;
                }
            }

            using (SolidBrush brush = new SolidBrush(Color.FromArgb(140, 160, 190)))
            using (Font font = new Font("Segoe UI", 12f, FontStyle.Bold))
            {
                string msg = _isConnected
                    ? "Connected — Receiving first Delta Screen Frame..."
                    : "Enter Host IP & PIN above and click '⚡ Connect to Host' to start ultra-low bandwidth streaming.";
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
            long now = Environment.TickCount;
            if (now - _lastMouseMoveSentTick < 22) return;
            _lastMouseMoveSentTick = now;

            float nx, ny;
            GetNormalizedCanvasCoords(e.X, e.Y, out nx, out ny);
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
            await SendMousePacketAsync(act, nx, ny, 0);
        }

        private async void OnCanvasMouseUp(object sender, MouseEventArgs e)
        {
            if (!_isConnected) return;
            float nx, ny;
            GetNormalizedCanvasCoords(e.X, e.Y, out nx, out ny);
            MouseActionType act = MouseActionType.LeftUp;
            if (e.Button == MouseButtons.Right) act = MouseActionType.RightUp;
            else if (e.Button == MouseButtons.Middle) act = MouseActionType.MiddleUp;
            await SendMousePacketAsync(act, nx, ny, 0);
        }

        private async void OnCanvasMouseWheel(object sender, MouseEventArgs e)
        {
            if (!_isConnected) return;
            float nx, ny;
            GetNormalizedCanvasCoords(e.X, e.Y, out nx, out ny);
            await SendMousePacketAsync(MouseActionType.Wheel, nx, ny, e.Delta);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (_isConnected && _tabControl.SelectedIndex == 0 && !_txtHostIp.Focused && !_txtPort.Focused && !_txtPin.Focused)
            {
                e.Handled = true;
                Task.Run(() => SendKeyPacketAsync((ushort)e.KeyValue, true));
                return;
            }
            base.OnKeyDown(e);
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            if (_isConnected && _tabControl.SelectedIndex == 0 && !_txtHostIp.Focused && !_txtPort.Focused && !_txtPin.Focused)
            {
                e.Handled = true;
                Task.Run(() => SendKeyPacketAsync((ushort)e.KeyValue, false));
                return;
            }
            base.OnKeyUp(e);
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

        private async Task SendKeyPacketAsync(ushort vk, bool isDown)
        {
            byte[] buf = new byte[3];
            Buffer.BlockCopy(BitConverter.GetBytes(vk), 0, buf, 0, 2);
            buf[2] = (byte)(isDown ? 1 : 0);
            await SendPacketSafeAsync(PacketType.KeyInput, buf);
        }

        private async Task SendStreamConfigAsync()
        {
            if (!_isConnected) return;
            byte[] cfg = new byte[5];
            cfg[0] = (byte)_cmbBandwidth.SelectedIndex;
            cfg[1] = (byte)_cmbMonitor.SelectedIndex;
            cfg[2] = (byte)(_chkGrayscale.Checked ? 1 : 0);
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
            using (OpenFileDialog ofd = new OpenFileDialog())
            {
                if (ofd.ShowDialog() != DialogResult.OK) return;

                string localPath = ofd.FileName;
                string remoteFolder = _txtRemotePath.Text.Trim();
                if (string.IsNullOrEmpty(remoteFolder) || remoteFolder == "DEFAULT")
                    remoteFolder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

                string targetRemotePath = Path.Combine(remoteFolder, Path.GetFileName(localPath));
                _lblFileStatus.Text = " Uploading " + Path.GetFileName(localPath) + "...";

                await SendPacketSafeAsync(PacketType.FileUploadStart, Encoding.UTF8.GetBytes(targetRemotePath));

                byte[] buffer = new byte[64 * 1024];
                using (FileStream fs = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    int read;
                    while ((read = await fs.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        byte[] slice = new byte[read];
                        Buffer.BlockCopy(buffer, 0, slice, 0, read);
                        byte[] compressed = TurboProtocol.CompressFast(slice);
                        await SendPacketSafeAsync(PacketType.FileUploadChunk, compressed);
                    }
                }

                await SendPacketSafeAsync(PacketType.FileUploadEnd, new byte[0]);
                _lblFileStatus.Text = " Upload completed: " + targetRemotePath;
                await RequestRemoteFileListAsync(remoteFolder);
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
                _lblTelemetry.Text = string.Format("● LIVE | FPS: {0} | Stream In: {1:F1} KB/s | Ping: {2} ms", _currentFps, _currentKbps, _lastPingRttMs);
                _lblTelemetry.ForeColor = _currentKbps < 80
                    ? Color.FromArgb(110, 255, 165)
                    : Color.FromArgb(110, 215, 255);

                byte[] pingPayload = BitConverter.GetBytes(Stopwatch.GetTimestamp());
                await SendPacketSafeAsync(PacketType.Ping, pingPayload);
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

        private static Button CreateToolbarButton(string text, int x, int y, int w, int h, Color bg)
        {
            return new Button
            {
                Text = text,
                Location = new Point(x, y),
                Size = new Size(w, h),
                BackColor = bg,
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 8.4f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
        }
    }
}
