using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using TurboCore;

namespace TurboHost
{
    public class ClientSessionHandler
    {
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private readonly SemaphoreSlim _writeLock;
        private readonly SecurityContext _sec;
        private readonly Action<string> _log;
        private readonly Action<int, double> _reportBytes;

        private readonly StreamConfig _config = new StreamConfig();
        private readonly DeltaScreenEncoder _encoder = new DeltaScreenEncoder();
        private readonly AutoResetEvent _urgentTrigger = new AutoResetEvent(false);

        private FileStream _incomingFileStream;
        private string _incomingFilePath;
        private long _lastFrameSendDurationMs = 5;

        public ClientSessionHandler(
            TcpClient client,
            NetworkStream stream,
            SemaphoreSlim writeLock,
            SecurityContext sec,
            Action<string> log,
            Action<int, double> reportBytes)
        {
            _client = client;
            _stream = stream;
            _writeLock = writeLock;
            _sec = sec;
            _log = log;
            _reportBytes = reportBytes;
            _config.ApplyPreset(BandwidthProfile.AutoAdaptive);
        }

        public async Task RunAsync(CancellationToken parentCt)
        {
            using (CancellationTokenSource sessionCts = CancellationTokenSource.CreateLinkedTokenSource(parentCt))
            {
                CancellationToken ct = sessionCts.Token;
                Task screenTask = Task.Run(() => ScreenStreamingLoopAsync(ct), ct);
                Task receiveTask = Task.Run(() => CommandReceiveLoopAsync(ct), ct);

                await Task.WhenAny(screenTask, receiveTask);
                sessionCts.Cancel();
                try { NativeInputInjector.BlockInput(false); } catch { }
                if (_incomingFileStream != null) _incomingFileStream.Dispose();
                _urgentTrigger.Dispose();
                _encoder.Dispose();
            }
        }

        private async Task ScreenStreamingLoopAsync(CancellationToken ct)
        {
            Stopwatch sw = Stopwatch.StartNew();
            Stopwatch rateWatch = Stopwatch.StartNew();
            long windowBytes = 0;

            while (!ct.IsCancellationRequested && _client.Connected)
            {
                long frameStart = sw.ElapsedMilliseconds;
                try
                {
                    if (_config.Profile == BandwidthProfile.AutoAdaptive)
                    {
                        if (_lastFrameSendDurationMs > 160)
                        {
                            // Extreme low-bandwidth 2G fallback
                            _config.Scale = 0.42f;
                            _config.JpegQuality = 20;
                            _config.Grayscale = true;
                            _config.LowBitPosterize = true;
                            _config.TargetFps = 20;
                        }
                        else if (_lastFrameSendDurationMs > 60)
                        {
                            // Moderate 3G / WAN
                            _config.Scale = 0.60f;
                            _config.JpegQuality = 38;
                            _config.Grayscale = false;
                            _config.LowBitPosterize = false;
                            _config.TargetFps = 30;
                        }
                        else if (_lastFrameSendDurationMs > 15)
                        {
                            // Standard 4G / Fast WAN
                            _config.Scale = 0.80f;
                            _config.JpegQuality = 55;
                            _config.Grayscale = false;
                            _config.LowBitPosterize = false;
                            _config.TargetFps = 45;
                        }
                        else
                        {
                            // Ultra-fast Local LAN / P2P (<15ms send) -> 60 FPS Native Quality!
                            _config.Scale = 1.0f;
                            _config.JpegQuality = 75;
                            _config.Grayscale = false;
                            _config.LowBitPosterize = false;
                            _config.TargetFps = 60;
                        }
                    }

                    int dirtyTiles, nw, nh;
                    byte[] payload = _encoder.CaptureAndEncodeDelta(_config, out dirtyTiles, out nw, out nh);
                    if (dirtyTiles > 0 && payload.Length > 0)
                    {
                        long sendStart = sw.ElapsedMilliseconds;
                        await TurboProtocol.SendPacketAsync(_stream, _writeLock, PacketType.ScreenFrame, payload, ct, _sec);
                        _lastFrameSendDurationMs = sw.ElapsedMilliseconds - sendStart;

                        windowBytes += payload.Length + 65;
                    }

                    if (rateWatch.ElapsedMilliseconds >= 1000)
                    {
                        double kbps = (windowBytes / 1024.0) / (rateWatch.ElapsedMilliseconds / 1000.0);
                        _reportBytes((int)windowBytes, kbps);
                        windowBytes = 0;
                        rateWatch.Restart();
                    }
                }
                catch
                {
                    break;
                }

                int targetIntervalMs = Math.Max(14, 1000 / Math.Max(10, _config.TargetFps));
                int elapsed = (int)(sw.ElapsedMilliseconds - frameStart);
                int delay = Math.Max(2, targetIntervalMs - elapsed);
                // Wait for next frame timer OR wake up IMMEDIATELY when user clicks/types!
                _urgentTrigger.WaitOne(delay);
            }
        }

        private async Task CommandReceiveLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _client.Connected)
            {
                KeyValuePair<PacketType, byte[]>? pkt = await TurboProtocol.ReadPacketAsync(_stream, ct, _sec);
                if (pkt == null) break;

                PacketType type = pkt.Value.Key;
                byte[] data = pkt.Value.Value;

                switch (type)
                {
                    case PacketType.MouseInput:
                        HandleMouseInput(data);
                        break;
                    case PacketType.KeyInput:
                        HandleKeyInput(data);
                        break;
                    case PacketType.ConfigUpdate:
                        HandleConfigUpdate(data);
                        break;
                    case PacketType.ForceFullFrame:
                        _encoder.ForceFullKeyFrame();
                        break;
                    case PacketType.ShellExec:
                        Task.Run(() => ExecuteRemoteCommandAsync(Encoding.UTF8.GetString(data), ct));
                        break;
                    case PacketType.ClipboardSync:
                        SetHostClipboard(Encoding.UTF8.GetString(data));
                        break;
                    case PacketType.SystemAction:
                        await HandleSystemActionAsync(Encoding.UTF8.GetString(data), ct);
                        break;
                    case PacketType.FileListRequest:
                        await HandleFileListRequestAsync(Encoding.UTF8.GetString(data), ct);
                        break;
                    case PacketType.FileUploadStart:
                        HandleFileUploadStart(Encoding.UTF8.GetString(data));
                        break;
                    case PacketType.FileUploadChunk:
                        await HandleFileUploadChunkAsync(data, ct);
                        break;
                    case PacketType.FileUploadEnd:
                        await HandleFileUploadEndAsync(ct);
                        break;
                    case PacketType.FileDownloadRequest:
                        Task.Run(() => SendFileToClientAsync(Encoding.UTF8.GetString(data), ct));
                        break;
                    case PacketType.Ping:
                        await TurboProtocol.SendPacketAsync(_stream, _writeLock, PacketType.Pong, data, ct, _sec);
                        break;
                }
            }
        }

        private void HandleMouseInput(byte[] data)
        {
            if (data.Length < 13) return;
            MouseActionType action = (MouseActionType)data[0];
            float normX = BitConverter.ToSingle(data, 1);
            float normY = BitConverter.ToSingle(data, 5);
            int wheel = BitConverter.ToInt32(data, 9);
            NativeInputInjector.InjectMouse(action, normX, normY, wheel, _config.MonitorIndex);
            // If user clicked or scrolled, wake up the screen encoder immediately for 0-ms visual response!
            if (action != MouseActionType.Move)
            {
                _urgentTrigger.Set();
            }
        }

        private void HandleKeyInput(byte[] data)
        {
            if (data.Length < 3) return;
            ushort vk = BitConverter.ToUInt16(data, 0);
            bool isDown = data[2] == 1;
            NativeInputInjector.InjectKey(vk, isDown);
            _urgentTrigger.Set();
        }

        private void HandleConfigUpdate(byte[] data)
        {
            if (data.Length < 5) return;
            BandwidthProfile profile = (BandwidthProfile)data[0];
            int monitorIdx = data[1];
            bool forceGray = data[2] == 1;

            _config.MonitorIndex = monitorIdx;
            if (profile != BandwidthProfile.AutoAdaptive)
            {
                _config.ApplyPreset(profile);
            }
            else
            {
                _config.Profile = BandwidthProfile.AutoAdaptive;
            }

            if (forceGray) _config.Grayscale = true;
            _encoder.ForceFullKeyFrame();
            _log(string.Format("[Stream] Profile={0}, Scale={1:P0}, JPEG Q={2}, Gray={3}", profile, _config.Scale, _config.JpegQuality, _config.Grayscale));
        }

        private async Task ExecuteRemoteCommandAsync(string command, CancellationToken ct)
        {
            _log("[Shell] Executing: " + command);
            byte[] payloadToSend = null;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -NonInteractive -Command \"" + command.Replace("\"", "\\\"") + "\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                using (Process proc = Process.Start(psi))
                {
                    string stdout = await proc.StandardOutput.ReadToEndAsync();
                    string stderr = await proc.StandardError.ReadToEndAsync();
                    proc.WaitForExit(15000);

                    StringBuilder sb = new StringBuilder();
                    sb.AppendLine("PS> " + command);
                    if (!string.IsNullOrWhiteSpace(stdout)) sb.AppendLine(stdout.TrimEnd());
                    if (!string.IsNullOrWhiteSpace(stderr)) sb.AppendLine("[STDERR] " + stderr.TrimEnd());
                    sb.AppendLine("[Exit Code: " + proc.ExitCode + "]");

                    payloadToSend = TurboProtocol.CompressFast(Encoding.UTF8.GetBytes(sb.ToString()));
                }
            }
            catch (Exception ex)
            {
                payloadToSend = TurboProtocol.CompressFast(Encoding.UTF8.GetBytes("[Shell Error] " + ex.Message + "\n"));
            }

            if (payloadToSend != null)
            {
                await TurboProtocol.SendPacketAsync(_stream, _writeLock, PacketType.ShellOutput, payloadToSend, ct, _sec);
            }
        }

        private void SetHostClipboard(string text)
        {
            try
            {
                Thread staThread = new Thread(() =>
                {
                    try { Clipboard.SetText(text); } catch { }
                });
                staThread.SetApartmentState(ApartmentState.STA);
                staThread.Start();
                _log("[Clipboard] Synced host clipboard.");
            }
            catch { }
        }

        private async Task HandleSystemActionAsync(string action, CancellationToken ct)
        {
            _log("[SystemAction] Triggered: " + action);
            if (action == "ELEVATE_ADMIN")
            {
                if (!NativeInputInjector.IsRunningAsAdmin())
                {
                    NativeInputInjector.RestartAsAdmin("");
                    Environment.Exit(0);
                }
                return;
            }
            if (action == "GET_CLIPBOARD")
            {
                string clip = "";
                Thread sta = new Thread(() =>
                {
                    try { if (Clipboard.ContainsText()) clip = Clipboard.GetText(); } catch { }
                });
                sta.SetApartmentState(ApartmentState.STA);
                sta.Start();
                sta.Join(800);
                await TurboProtocol.SendPacketAsync(_stream, _writeLock, PacketType.ClipboardSync, Encoding.UTF8.GetBytes(clip), ct, _sec);
                return;
            }

            NativeInputInjector.SendSpecialCombo(action);
        }

        private async Task HandleFileListRequestAsync(string folderPath, CancellationToken ct)
        {
            byte[] payloadToSend = null;
            try
            {
                if (string.IsNullOrWhiteSpace(folderPath) || folderPath == "DEFAULT")
                {
                    folderPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                }

                StringBuilder sb = new StringBuilder();
                sb.AppendLine("PATH|" + folderPath);

                DirectoryInfo dirInfo = new DirectoryInfo(folderPath);
                if (dirInfo.Parent != null)
                {
                    sb.AppendLine("DIR|..|" + dirInfo.Parent.FullName + "|0");
                }

                foreach (DirectoryInfo d in dirInfo.GetDirectories().OrderBy(d => d.Name).Take(250))
                {
                    sb.AppendLine("DIR|" + d.Name + "|" + d.FullName + "|0");
                }

                foreach (FileInfo f in dirInfo.GetFiles().OrderBy(f => f.Name).Take(500))
                {
                    sb.AppendLine("FILE|" + f.Name + "|" + f.FullName + "|" + f.Length);
                }

                payloadToSend = TurboProtocol.CompressFast(Encoding.UTF8.GetBytes(sb.ToString()));
            }
            catch (Exception ex)
            {
                payloadToSend = TurboProtocol.CompressFast(Encoding.UTF8.GetBytes("ERROR|" + ex.Message));
            }

            if (payloadToSend != null)
            {
                await TurboProtocol.SendPacketAsync(_stream, _writeLock, PacketType.FileListResponse, payloadToSend, ct, _sec);
            }
        }

        private void HandleFileUploadStart(string targetFilePath)
        {
            try
            {
                if (_incomingFileStream != null) _incomingFileStream.Dispose();
                string dir = Path.GetDirectoryName(targetFilePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                _incomingFilePath = targetFilePath;
                _incomingFileStream = new FileStream(targetFilePath, FileMode.Create, FileAccess.Write, FileShare.None);
                _log("[FileTransfer] Receiving upload -> " + targetFilePath);
            }
            catch (Exception ex)
            {
                _log("[FileTransfer Error] " + ex.Message);
            }
        }

        private async Task HandleFileUploadChunkAsync(byte[] compressedChunk, CancellationToken ct)
        {
            if (_incomingFileStream == null) return;
            byte[] raw = TurboProtocol.DecompressFast(compressedChunk);
            await _incomingFileStream.WriteAsync(raw, 0, raw.Length, ct);
        }

        private async Task HandleFileUploadEndAsync(CancellationToken ct)
        {
            if (_incomingFileStream != null)
            {
                await _incomingFileStream.FlushAsync(ct);
                _incomingFileStream.Dispose();
                _incomingFileStream = null;
                _log("[FileTransfer] Upload completed: " + _incomingFilePath);
            }
        }

        private async Task SendFileToClientAsync(string sourcePath, CancellationToken ct)
        {
            try
            {
                if (!File.Exists(sourcePath)) return;
                string fileName = Path.GetFileName(sourcePath);
                _log("[FileTransfer] Sending " + fileName + " to client...");

                await TurboProtocol.SendPacketAsync(_stream, _writeLock, PacketType.FileUploadStart, Encoding.UTF8.GetBytes(fileName), ct, _sec);

                byte[] buffer = new byte[64 * 1024];
                using (FileStream fs = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    int read;
                    while ((read = await fs.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
                    {
                        byte[] slice = new byte[read];
                        Buffer.BlockCopy(buffer, 0, slice, 0, read);
                        byte[] compressed = TurboProtocol.CompressFast(slice);
                        await TurboProtocol.SendPacketAsync(_stream, _writeLock, PacketType.FileUploadChunk, compressed, ct, _sec);
                    }
                }

                await TurboProtocol.SendPacketAsync(_stream, _writeLock, PacketType.FileUploadEnd, Encoding.UTF8.GetBytes(fileName), ct, _sec);
                _log("[FileTransfer] Finished sending " + fileName);
            }
            catch (Exception ex)
            {
                _log("[FileTransfer Error] " + ex.Message);
            }
        }
    }

    public class HostMainForm : Form
    {
        private TextBox _txtPort;
        private TextBox _txtPin;
        private TextBox _txtRoomId;
        private string _activePublicTunnelEndpoint = "";
        private Button _btnStartStop;
        private Button _btnElevate;
        private Button _btnAutoStartup;
        private Button _btnUpnp;
        private Button _btnFirewall;
        private TextBox _txtRelayHost;
        private TextBox _txtRelayPort;
        private Button _btnConnectRelay;
        private ListBox _lstEndpoints;
        private ListBox _lstLog;
        private Label _lblStatus;
        private Label _lblAdminBadge;
        private Label _lblStats;

        private TcpListener _listener;
        private CancellationTokenSource _serverCts;
        private Process _tunnelProc;
        private bool _isRunning = false;
        private long _totalBytesSent = 0;
        private int _connectedClients = 0;

        public HostMainForm()
        {
            Text = "TurboRemote — Host Server";
            Size = new Size(775, 645);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(18, 20, 28);
            ForeColor = Color.FromArgb(230, 235, 245);
            Font = new Font("Segoe UI", 9.2f, FontStyle.Regular);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;

            NativeInputInjector.PreventSystemSleep();
            InitializeUi();
            PopulateNetworkEndpoints();
            UpdateAdminBadge();
        }

        private void InitializeUi()
        {
            Panel headerPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 62,
                BackColor = Color.FromArgb(26, 30, 44)
            };

            Label lblTitle = new Label
            {
                Text = "⚡ TURBOREMOTE",
                Font = new Font("Segoe UI", 13f, FontStyle.Bold),
                ForeColor = Color.FromArgb(88, 196, 255),
                Location = new Point(16, 10),
                AutoSize = true
            };

            Label lblSub = new Label
            {
                Text = "Runs on Any Windows PC Without .NET Core or Python Installed • 2G Low-Bit Delta Engine • Full Admin UAC",
                Font = new Font("Segoe UI", 8.5f, FontStyle.Regular),
                ForeColor = Color.FromArgb(160, 175, 200),
                Location = new Point(18, 36),
                AutoSize = true
            };

            _lblAdminBadge = new Label
            {
                Location = new Point(555, 16),
                Size = new Size(190, 30),
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 8.8f, FontStyle.Bold)
            };

            headerPanel.Controls.Add(lblTitle);
            headerPanel.Controls.Add(lblSub);
            headerPanel.Controls.Add(_lblAdminBadge);
            Controls.Add(headerPanel);

            GroupBox grpServer = CreateGroupBox("1. Host Listen, UAC Elevation & Unattended Auto-Start", 16, 72, 430, 172);

            Label lblPort = new Label { Text = "TCP Listen Port:", Location = new Point(16, 28), AutoSize = true };
            _txtPort = new TextBox
            {
                Text = "9876",
                Location = new Point(135, 25),
                Width = 90,
                BackColor = Color.FromArgb(30, 35, 50),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle
            };

            Label lblPin = new Label { Text = "Access Key/PIN:", Location = new Point(16, 60), AutoSize = true };
            _txtPin = new TextBox
            {
                Text = "2580",
                Location = new Point(135, 57),
                Width = 72,
                BackColor = Color.FromArgb(30, 35, 50),
                ForeColor = Color.FromArgb(120, 255, 160),
                Font = new Font("Consolas", 10f, FontStyle.Bold),
                BorderStyle = BorderStyle.FixedSingle
            };

            Button btnGenKey = CreateButton("🎲", 210, 56, 28, 25, Color.FromArgb(65, 85, 130));
            btnGenKey.Click += (s, e) =>
            {
                _txtPin.Text = TurboProtocol.GenerateStrongAccessKey();
                Log("[Security] Generated new cryptographic access key: " + _txtPin.Text);
            };

            _btnStartStop = CreateButton("▶ Start Host Server", 242, 24, 172, 34, Color.FromArgb(34, 155, 95));
            _btnStartStop.Click += async (s, e) => await ToggleServerAsync();

            _btnElevate = CreateButton("🛡 Elevate to Admin (UAC)", 242, 64, 172, 30, Color.FromArgb(190, 115, 30));
            _btnElevate.Click += (s, e) =>
            {
                if (NativeInputInjector.IsRunningAsAdmin())
                {
                    MessageBox.Show("TurboHost is already running with full Administrator privileges!", "Administrator Active", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                if (NativeInputInjector.RestartAsAdmin(string.Format("--port {0} --pin {1}", _txtPort.Text, _txtPin.Text)))
                {
                    Application.Exit();
                }
            };

            _btnFirewall = CreateButton("🔓 Open Windows Firewall", 16, 100, 198, 28, Color.FromArgb(50, 75, 125));
            _btnFirewall.Click += (s, e) =>
            {
                int port;
                if (int.TryParse(_txtPort.Text, out port))
                {
                    bool ok = NetworkHelper.EnsureFirewallRule("TurboRemote_Host_TCP", port);
                    Log(ok ? "[Firewall] Inbound TCP rule added for port " + port : "[Firewall] Elevate to Admin first to add firewall rule.");
                }
            };

            _btnUpnp = CreateButton("🌐 Auto-Map UPnP Router", 222, 100, 192, 28, Color.FromArgb(65, 85, 130));
            _btnUpnp.Click += async (s, e) =>
            {
                int port;
                if (int.TryParse(_txtPort.Text, out port))
                {
                    Log("[UPnP] Probing router gateway...");
                    string msg = await NetworkHelper.TryUpnpPortForwardAsync(port, port);
                    Log("[UPnP] " + msg);
                }
            };

            _btnAutoStartup = CreateButton("⚙ Install Unattended Admin Startup Task (Auto-Run on Boot)", 16, 134, 398, 28, Color.FromArgb(45, 95, 110));
            _btnAutoStartup.Click += (s, e) =>
            {
                if (!NativeInputInjector.IsRunningAsAdmin())
                {
                    MessageBox.Show("Click 'Elevate to Admin (UAC)' first so Windows Task Scheduler can register the Highest-Privilege startup task.", "Admin Required", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                bool ok = NativeInputInjector.InstallUnattendedAdminStartupTask(_txtPort.Text.Trim(), _txtPin.Text.Trim());
                Log(ok
                    ? "[Unattended] Installed 'TurboRemote_Unattended_Host' scheduled task with HIGHEST privileges on boot!"
                    : "[Unattended] Could not register task.");
            };

            grpServer.Controls.AddRange(new Control[] { lblPort, _txtPort, lblPin, _txtPin, btnGenKey, _btnStartStop, _btnElevate, _btnFirewall, _btnUpnp, _btnAutoStartup });
            Controls.Add(grpServer);

            GroupBox grpReverse = CreateGroupBox("2. Reverse WAN Connect (Bypass CGNAT)", 458, 72, 285, 172);
            Label lblRevInfo = new Label
            {
                Text = "If this Host PC is behind strict CGNAT/Firewall, connect outbound to a Client waiting in 'Listen Mode':",
                Location = new Point(14, 24),
                Size = new Size(256, 38),
                ForeColor = Color.FromArgb(170, 185, 210),
                Font = new Font("Segoe UI", 8.2f)
            };
            Label lblRHost = new Label { Text = "Client IP/Host:", Location = new Point(14, 68), AutoSize = true };
            _txtRelayHost = new TextBox
            {
                Text = "127.0.0.1",
                Location = new Point(108, 65),
                Width = 158,
                BackColor = Color.FromArgb(30, 35, 50),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle
            };
            Label lblRPort = new Label { Text = "Client Port:", Location = new Point(14, 98), AutoSize = true };
            _txtRelayPort = new TextBox
            {
                Text = "9877",
                Location = new Point(108, 95),
                Width = 80,
                BackColor = Color.FromArgb(30, 35, 50),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle
            };

            _btnConnectRelay = CreateButton("🚀 Connect Outbound to Client", 14, 130, 254, 30, Color.FromArgb(115, 70, 180));
            _btnConnectRelay.Click += async (s, e) => await ConnectReverseToClientAsync();

            grpReverse.Controls.AddRange(new Control[] { lblRevInfo, lblRHost, _txtRelayHost, lblRPort, _txtRelayPort, _btnConnectRelay });
            Controls.Add(grpReverse);

            GroupBox grpEndpoints = CreateGroupBox("3. Cloud Room ID, Connection Endpoints & Auto Public Internet Tunnel", 16, 252, 727, 130);

            Label lblRoom = new Label { Text = "Host ID:", Location = new Point(542, 26), AutoSize = true, ForeColor = Color.FromArgb(255, 225, 130) };
            _txtRoomId = new TextBox
            {
                Text = Environment.MachineName.ToUpperInvariant(),
                Location = new Point(598, 23),
                Width = 114,
                BackColor = Color.FromArgb(30, 35, 50),
                ForeColor = Color.FromArgb(255, 230, 130),
                Font = new Font("Consolas", 9f, FontStyle.Bold),
                BorderStyle = BorderStyle.FixedSingle
            };

            _lstEndpoints = new ListBox
            {
                Location = new Point(14, 24),
                Size = new Size(515, 92),
                BackColor = Color.FromArgb(24, 28, 38),
                ForeColor = Color.FromArgb(110, 220, 255),
                Font = new Font("Consolas", 9.5f),
                BorderStyle = BorderStyle.FixedSingle
            };

            Button btnCopyIp = CreateButton("⚡ Copy / Publish ID", 542, 54, 170, 28, Color.FromArgb(35, 130, 85));
            btnCopyIp.Click += async (s, e) =>
            {
                int port = 9876;
                int.TryParse(_txtPort.Text.Trim(), out port);
                string ticket = SmartRouteEngine.BuildSmartConnectTicket(port, _activePublicTunnelEndpoint);
                Clipboard.SetText(ticket);
                Log("[SmartTicket] Copied endpoints to Clipboard: " + ticket);
                await PublishTicketToCloudDirectoryAsync();
            };

            Button btnStartPublicTunnel = CreateButton("🌍 Auto Internet Tunnel", 542, 88, 170, 28, Color.FromArgb(30, 130, 115));
            btnStartPublicTunnel.Click += (s, e) => StartBuiltInPublicSshTunnel();

            grpEndpoints.Controls.AddRange(new Control[] { _lstEndpoints, lblRoom, _txtRoomId, btnCopyIp, btnStartPublicTunnel });
            Controls.Add(grpEndpoints);

            GroupBox grpLog = CreateGroupBox("4. Real-Time Session Telemetry & Security Log", 16, 390, 727, 200);
            _lblStatus = new Label
            {
                Text = "● Server Status: STOPPED",
                Location = new Point(14, 22),
                AutoSize = true,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(255, 180, 90)
            };

            _lblStats = new Label
            {
                Text = "Clients: 0 | Bandwidth Out: 0.0 KB/s | Total Sent: 0.0 MB",
                Location = new Point(315, 22),
                AutoSize = true,
                Font = new Font("Consolas", 9f),
                ForeColor = Color.FromArgb(150, 220, 180)
            };

            _lstLog = new ListBox
            {
                Location = new Point(14, 46),
                Size = new Size(698, 140),
                BackColor = Color.FromArgb(14, 16, 22),
                ForeColor = Color.FromArgb(200, 215, 230),
                Font = new Font("Consolas", 8.8f),
                BorderStyle = BorderStyle.FixedSingle
            };

            grpLog.Controls.AddRange(new Control[] { _lblStatus, _lblStats, _lstLog });
            Controls.Add(grpLog);
        }

        private async Task PublishTicketToCloudDirectoryAsync()
        {
            string portStr = InvokeRequired ? (string)Invoke(new Func<string>(() => _txtPort.Text.Trim())) : _txtPort.Text.Trim();
            string room = InvokeRequired ? (string)Invoke(new Func<string>(() => _txtRoomId.Text.Trim())) : _txtRoomId.Text.Trim();
            string pin = InvokeRequired ? (string)Invoke(new Func<string>(() => _txtPin.Text.Trim())) : _txtPin.Text.Trim();

            int port = 9876;
            int.TryParse(portStr, out port);
            string ticket = SmartRouteEngine.BuildSmartConnectTicket(port, _activePublicTunnelEndpoint);

            Log(string.Format("[Cloud Discovery] Publishing AES-256 encrypted endpoints for Host ID '{0}'...", room));
            bool ok = await SmartRouteEngine.PublishHostTicketToCloudAsync(room, pin, ticket);
            if (ok)
            {
                Log(string.Format("[Cloud Discovery READY] Client can now type Host ID '{0}' and PIN '{1}' from anywhere on the Internet!", room, pin));
            }
            else
            {
                Log("[Cloud Discovery] Offline/Local mode active (Use LAN AUTO or Smart Ticket).");
            }
        }

        private void StartBuiltInPublicSshTunnel()
        {
            try
            {
                if (_tunnelProc != null && !_tunnelProc.HasExited)
                {
                    return;
                }

                string port = InvokeRequired
                    ? (string)Invoke(new Func<string>(() => _txtPort.Text.Trim()))
                    : _txtPort.Text.Trim();

                Log("[Auto-Tunnel] Starting background OpenSSH Public Internet TCP Relay for local port " + port + "...");
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = "ssh.exe",
                    Arguments = string.Format("-p 443 -R0:localhost:{0} -o StrictHostKeyChecking=no -o UserKnownHostsFile=NUL -o ServerAliveInterval=20 -o ExitOnForwardFailure=yes tcp@a.pinggy.io", port),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                _tunnelProc = new Process { StartInfo = psi, EnableRaisingEvents = true };
                _tunnelProc.OutputDataReceived += (s, e) => ParseTunnelOutputLine(e.Data);
                _tunnelProc.ErrorDataReceived += (s, e) => ParseTunnelOutputLine(e.Data);
                _tunnelProc.Exited += (s, e) =>
                {
                    if (_isRunning && _serverCts != null && !_serverCts.Token.IsCancellationRequested)
                    {
                        Task.Run(async () =>
                        {
                            try
                            {
                                await Task.Delay(5000, _serverCts.Token);
                                if (_isRunning) StartBuiltInPublicSshTunnel();
                            }
                            catch { }
                        });
                    }
                };
                _tunnelProc.Start();
                _tunnelProc.BeginOutputReadLine();
                _tunnelProc.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                Log("[Auto-Tunnel] Background SSH tunnel note: " + ex.Message);
            }
        }

        private void ParseTunnelOutputLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            // Detect "tcp://xxxx.pinggy.io:12345" in SSH output
            int idx = line.IndexOf("tcp://", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                string sub = line.Substring(idx + 6).Trim();
                string endpoint = sub.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)[0];
                if (endpoint.Contains(":") && _activePublicTunnelEndpoint != endpoint)
                {
                    _activePublicTunnelEndpoint = endpoint;
                    Log("[PUBLIC INTERNET TUNNEL ACTIVE] -> " + endpoint);
                    BeginInvoke(new Action(async () =>
                    {
                        _lstEndpoints.Items.Insert(0, endpoint + " (Public Internet Tunnel)");
                        await PublishTicketToCloudDirectoryAsync();
                    }));
                }
            }
        }

        public void ConfigureFromArgs(string[] args)
        {
            bool autoStart = true;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--port" && i + 1 < args.Length) _txtPort.Text = args[i + 1];
                if (args[i] == "--pin" && i + 1 < args.Length) _txtPin.Text = args[i + 1];
                if (args[i] == "--no-autostart") autoStart = false;
            }

            if (autoStart)
            {
                Shown += async (s, e) => await StartServerAsync();
            }
        }

        private void UpdateAdminBadge()
        {
            bool isAdmin = NativeInputInjector.IsRunningAsAdmin();
            if (isAdmin)
            {
                _lblAdminBadge.Text = "🛡 ADMIN PRIVILEGES: FULL";
                _lblAdminBadge.BackColor = Color.FromArgb(25, 110, 65);
                _lblAdminBadge.ForeColor = Color.FromArgb(180, 255, 200);
            }
            else
            {
                _lblAdminBadge.Text = "⚠ STANDARD USER (Click Elevate)";
                _lblAdminBadge.BackColor = Color.FromArgb(120, 75, 20);
                _lblAdminBadge.ForeColor = Color.FromArgb(255, 225, 160);
            }
        }

        private void PopulateNetworkEndpoints()
        {
            _lstEndpoints.Items.Clear();
            foreach (string ip in NetworkHelper.GetLocalIpAddresses())
            {
                _lstEndpoints.Items.Add(ip);
            }
            _lstEndpoints.Items.Add("127.0.0.1 (Localhost Loopback Test)");
            if (_lstEndpoints.Items.Count > 0) _lstEndpoints.SelectedIndex = 0;
        }

        private async Task ToggleServerAsync()
        {
            if (_isRunning) StopServer();
            else await StartServerAsync();
        }

        private async Task AutoOpenFirewallUpnpAndTunnelAsync(int port)
        {
            // 1. Automatically open Windows Firewall inbound rule (if running as Admin)
            if (NativeInputInjector.IsRunningAsAdmin())
            {
                bool fwOk = NetworkHelper.EnsureFirewallRule("TurboRemote_Host_TCP", port);
                NetworkHelper.EnsureFirewallRule("TurboRemote_Host_UDP", SmartRouteEngine.DiscoveryUdpPort);
                if (fwOk)
                {
                    Log("[Auto-Port] Windows Firewall opened automatically for TCP port " + port + " & UDP " + SmartRouteEngine.DiscoveryUdpPort + ".");
                }
            }

            // 2. Automatically probe UPnP Router Gateway to open external port
            try
            {
                string upnpMsg = await NetworkHelper.TryUpnpPortForwardAsync(port, port);
                Log("[Auto-UPnP] " + upnpMsg);
            }
            catch { }

            // 3. Automatically start & maintain Public Internet Tunnel (with auto-reconnect watchdog)
            StartBuiltInPublicSshTunnel();

            // 4. Periodically refresh Cloud Rendezvous ticket every 4 minutes so Internet clients can always find this Host
            while (_isRunning && _serverCts != null && !_serverCts.Token.IsCancellationRequested)
            {
                await PublishTicketToCloudDirectoryAsync();
                try
                {
                    await Task.Delay(240000, _serverCts.Token);
                }
                catch
                {
                    break;
                }
            }
        }

        private async Task StartServerAsync()
        {
            if (_isRunning) return;
            int port;
            if (!int.TryParse(_txtPort.Text.Trim(), out port) || port < 1 || port > 65535)
            {
                MessageBox.Show("Please enter a valid TCP port (1-65535).", "Invalid Port", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                _serverCts = new CancellationTokenSource();
                _listener = new TcpListener(IPAddress.Any, port);
                _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _listener.Start();

                // Start zero-config UDP LAN Auto-Discovery Beacon on port 9878
                SmartRouteEngine.StartLanBeaconResponder(() => port, _serverCts.Token);

                _isRunning = true;
                _btnStartStop.Text = "⏹ Stop Host Server";
                _btnStartStop.BackColor = Color.FromArgb(185, 55, 55);
                _lblStatus.Text = "● Server Status: LISTENING ON PORT " + port + " (LOCAL + INTERNET ACTIVE)";
                _lblStatus.ForeColor = Color.FromArgb(95, 245, 155);

                Log(string.Format("[Server] TurboHost listening on 0.0.0.0:{0} (Host ID: {1}, PIN: {2})", port, _txtRoomId.Text, _txtPin.Text));

                // Automatically open Firewall, map UPnP router port, launch Public Internet Tunnel, and publish to Cloud
                Task.Run(() => AutoOpenFirewallUpnpAndTunnelAsync(port));

                while (!_serverCts.Token.IsCancellationRequested)
                {
                    TcpClient client = await _listener.AcceptTcpClientAsync();
                    Task.Run(() => HandleClientSessionAsync(client, _serverCts.Token));
                }
            }
            catch (Exception ex)
            {
                if (_isRunning)
                {
                    Log("[Server] Stopped: " + ex.Message);
                    StopServer();
                }
            }
        }

        private void StopServer()
        {
            try
            {
                _isRunning = false;
                if (_serverCts != null) _serverCts.Cancel();
                if (_listener != null) _listener.Stop();
                if (_tunnelProc != null && !_tunnelProc.HasExited)
                {
                    try { _tunnelProc.Kill(); } catch { }
                }
            }
            catch { }

            _btnStartStop.Text = "▶ Start Host Server";
            _btnStartStop.BackColor = Color.FromArgb(34, 155, 95);
            _lblStatus.Text = "● Server Status: STOPPED";
            _lblStatus.ForeColor = Color.FromArgb(255, 180, 90);
            Log("[Server] Listener and Internet tunnel stopped.");
        }

        private async Task ConnectReverseToClientAsync()
        {
            string host = _txtRelayHost.Text.Trim();
            int port;
            if (!int.TryParse(_txtRelayPort.Text.Trim(), out port))
            {
                Log("[Reverse] Invalid client port.");
                return;
            }

            try
            {
                Log(string.Format("[Reverse] Connecting outbound to {0}:{1}...", host, port));
                TcpClient client = new TcpClient();
                client.NoDelay = true;
                await client.ConnectAsync(host, port);
                Log(string.Format("[Reverse] Connected to {0}:{1}!", host, port));
                if (_serverCts == null) _serverCts = new CancellationTokenSource();
                Task.Run(() => HandleClientSessionAsync(client, _serverCts.Token));
            }
            catch (Exception ex)
            {
                Log("[Reverse Error] " + ex.Message);
            }
        }

        private async Task HandleClientSessionAsync(TcpClient client, CancellationToken ct)
        {
            string remoteEp = client.Client.RemoteEndPoint != null ? client.Client.RemoteEndPoint.ToString() : "Unknown";
            string remoteIpOnly = remoteEp.Split(':')[0];

            int lockoutRemaining;
            if (BruteForceProtector.IsIpLockedOut(remoteIpOnly, out lockoutRemaining))
            {
                Log(string.Format("[Security BLOCKED] IP {0} is locked out for {1}s due to failed attempts.", remoteIpOnly, lockoutRemaining));
                try { client.Close(); } catch { }
                return;
            }

            Interlocked.Increment(ref _connectedClients);
            Log("[Connect] Incoming TLS-Grade E2EE connection from " + remoteEp);

            try
            {
                client.NoDelay = true;
                client.SendBufferSize = 256 * 1024;
                client.ReceiveBufferSize = 256 * 1024;

                using (NetworkStream stream = client.GetStream())
                using (SemaphoreSlim writeLock = new SemaphoreSlim(1, 1))
                {
                    // 1. Send 32-byte Cryptographic Challenge (16B Salt + 16B Nonce)
                    byte[] salt16 = new byte[16];
                    byte[] challenge16 = new byte[16];
                    using (RandomNumberGenerator rng = RandomNumberGenerator.Create())
                    {
                        rng.GetBytes(salt16);
                        rng.GetBytes(challenge16);
                    }

                    byte[] challengePayload = new byte[32];
                    Buffer.BlockCopy(salt16, 0, challengePayload, 0, 16);
                    Buffer.BlockCopy(challenge16, 0, challengePayload, 16, 16);
                    await TurboProtocol.SendPacketAsync(stream, writeLock, PacketType.AuthChallenge, challengePayload, ct);

                    // 2. Read Zero-Knowledge HMAC Proof from Client (PIN never sent in cleartext!)
                    KeyValuePair<PacketType, byte[]>? authPacket = await TurboProtocol.ReadPacketAsync(stream, ct);
                    if (authPacket == null || authPacket.Value.Key != PacketType.AuthResponse || authPacket.Value.Value.Length != 32)
                    {
                        Log("[Security] Malformed challenge response from " + remoteEp);
                        return;
                    }

                    string expectedPin = GetPinThreadSafe();
                    using (SecurityContext sec = new SecurityContext(expectedPin, salt16, challenge16))
                    {
                        byte[] expectedClientProof = sec.ComputeHandshakeProof(challenge16, "CLIENT_AUTH_V2");
                        if (!TurboProtocol.ConstantTimeEquals(authPacket.Value.Value, expectedClientProof))
                        {
                            int fails = BruteForceProtector.RecordFailedAttempt(remoteIpOnly);
                            await TurboProtocol.SendPacketAsync(stream, writeLock, PacketType.AuthResult, Encoding.UTF8.GetBytes("FAIL:Invalid Access Key/PIN (Attempt " + fails + "/5)"), ct);
                            Log(string.Format("[Security ALERT] Rejected invalid HMAC proof from {0} (Fail #{1}/5)", remoteEp, fails));
                            return;
                        }

                        BruteForceProtector.ResetAttempts(remoteIpOnly);

                        // 3. Send AES-256 + HMAC-SHA256 Encrypted AuthResult back to Client
                        string machineInfo = string.Format("OK|{0}|{1}|{2}|AES-256-HMAC-SHA256", Environment.MachineName, NativeInputInjector.IsRunningAsAdmin() ? "ADMIN" : "USER", Screen.AllScreens.Length);
                        await TurboProtocol.SendPacketAsync(stream, writeLock, PacketType.AuthResult, Encoding.UTF8.GetBytes(machineInfo), ct, sec);
                        Log("[Security VERIFIED] " + remoteEp + " authenticated via Zero-Knowledge PBKDF2-SHA256 & AES-256 E2EE.");

                        ClientSessionHandler session = new ClientSessionHandler(client, stream, writeLock, sec, Log, AddBytesSent);
                        await session.RunAsync(ct);
                    }
                }
            }
            catch (Exception ex)
            {
                Log("[Session Closed] " + remoteEp + ": " + ex.Message);
            }
            finally
            {
                Interlocked.Decrement(ref _connectedClients);
                UpdateStatsLabel(0);
                try { client.Close(); } catch { }
            }
        }

        private string GetPinThreadSafe()
        {
            if (InvokeRequired)
            {
                return (string)Invoke(new Func<string>(() => _txtPin.Text.Trim()));
            }
            return _txtPin.Text.Trim();
        }

        private void AddBytesSent(int bytes, double currentKbps)
        {
            Interlocked.Add(ref _totalBytesSent, bytes);
            UpdateStatsLabel(currentKbps);
        }

        private void UpdateStatsLabel(double kbps)
        {
            if (IsDisposed) return;
            try
            {
                BeginInvoke(new Action(() =>
                {
                    double mb = _totalBytesSent / (1024.0 * 1024.0);
                    _lblStats.Text = string.Format("Clients: {0} | Stream Out: {1:F1} KB/s | Total Sent: {2:F2} MB", _connectedClients, kbps, mb);
                }));
            }
            catch { }
        }

        private void Log(string message)
        {
            if (IsDisposed) return;
            string line = string.Format("[{0:HH:mm:ss}] {1}", DateTime.Now, message);
            try
            {
                BeginInvoke(new Action(() =>
                {
                    _lstLog.Items.Add(line);
                    if (_lstLog.Items.Count > 200) _lstLog.Items.RemoveAt(0);
                    _lstLog.TopIndex = _lstLog.Items.Count - 1;
                }));
            }
            catch { }
        }

        private static GroupBox CreateGroupBox(string title, int x, int y, int w, int h)
        {
            return new GroupBox
            {
                Text = title,
                Location = new Point(x, y),
                Size = new Size(w, h),
                ForeColor = Color.FromArgb(140, 195, 255),
                Font = new Font("Segoe UI", 9f, FontStyle.Bold)
            };
        }

        private static Button CreateButton(string text, int x, int y, int w, int h, Color bg)
        {
            return new Button
            {
                Text = text,
                Location = new Point(x, y),
                Size = new Size(w, h),
                BackColor = bg,
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 8.6f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
        }
    }
}
