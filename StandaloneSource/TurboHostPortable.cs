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
        private readonly Func<string> _getElevatedRestartArgs;

        private readonly StreamConfig _config = new StreamConfig();
        private readonly DeltaScreenEncoder _encoder = new DeltaScreenEncoder();
        private readonly AutoResetEvent _urgentTrigger = new AutoResetEvent(false);
        private readonly SemaphoreSlim _downloadLock = new SemaphoreSlim(1, 1);

        private FileStream _incomingFileStream;
        private string _incomingFilePath;
        private volatile bool _forceGrayscale;

        // Keys and mouse buttons this viewer is holding down, released when the session ends. A connection
        // that dropped mid-keypress (or mid-drag) used to leave Ctrl/Shift/Alt or the mouse button stuck down
        // on this PC until someone pressed it physically.
        private struct HeldKey { public ushort Vk; public byte Scan; public bool? Extended; }
        private readonly object _heldInputLock = new object();
        private readonly Dictionary<int, HeldKey> _heldKeys = new Dictionary<int, HeldKey>();
        private readonly HashSet<MouseActionType> _heldButtons = new HashSet<MouseActionType>();
        private float _lastMouseX = 0.5f, _lastMouseY = 0.5f;

        // Auto-Adaptive quality: 0 = 2G, 1 = 3G/WAN, 2 = 4G/fast WAN, 3 = LAN. Only the screen loop touches these.
        private int _adaptiveLevel = 3;
        private long _adaptiveHoldStartMs;

        public ClientSessionHandler(
            TcpClient client,
            NetworkStream stream,
            SemaphoreSlim writeLock,
            SecurityContext sec,
            Action<string> log,
            Action<int, double> reportBytes,
            Func<string> getElevatedRestartArgs = null)
        {
            _client = client;
            _stream = stream;
            _writeLock = writeLock;
            _sec = sec;
            _log = log;
            _reportBytes = reportBytes;
            _getElevatedRestartArgs = getElevatedRestartArgs;
            _config.ApplyPreset(BandwidthProfile.AutoAdaptive);
        }

        // Every viewer (old builds too) pings once a second. A session that has sent nothing for this long is
        // dead (network gone, viewer frozen); before, it stayed open until TCP gave up, which could take forever.
        public const int ViewerSilenceTimeoutMs = 45000;
        private long _lastReceivedTicks = Stopwatch.GetTimestamp();

        private async Task SilenceWatchdogAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(5000, ct);
                long silentMs = (Stopwatch.GetTimestamp() - Interlocked.Read(ref _lastReceivedTicks)) * 1000 / Stopwatch.Frequency;
                if (silentMs >= ViewerSilenceTimeoutMs)
                {
                    _log("[Session] No data from the viewer for " + (ViewerSilenceTimeoutMs / 1000) + " s; closing the session.");
                    return; // RunAsync then tears the session down (releasing any keys the viewer held)
                }
            }
        }

        public async Task RunAsync(CancellationToken parentCt)
        {
            using (CancellationTokenSource sessionCts = CancellationTokenSource.CreateLinkedTokenSource(parentCt))
            {
                CancellationToken ct = sessionCts.Token;
                Task screenTask = Task.Run(() => ScreenStreamingLoopAsync(ct), ct);
                Task receiveTask = Task.Run(() => CommandReceiveLoopAsync(ct), ct);
                Task watchdogTask = Task.Run(() => SilenceWatchdogAsync(ct), ct);

                await Task.WhenAny(screenTask, receiveTask, watchdogTask);
                sessionCts.Cancel();

                // Close the socket so the other loop's pending read/write fails fast, then wait for BOTH loops
                // before disposing anything. The screen loop works on raw pointers into the encoder's bitmaps;
                // disposing the encoder while it was still mid-frame (whenever the viewer disconnected) could
                // read freed memory and crash the whole host process.
                try { _client.Close(); } catch { }
                try { await Task.WhenAll(screenTask, receiveTask); } catch { }

                ReleaseHeldInput();
                NativeInputInjector.SendSpecialCombo("LOCK_INPUT_OFF");
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
            bool captureFailing = false;

            while (!ct.IsCancellationRequested && _client.Connected)
            {
                long frameStart = sw.ElapsedMilliseconds;
                bool autoAdaptive = _config.Profile == BandwidthProfile.AutoAdaptive;
                if (autoAdaptive)
                {
                    ApplyAdaptiveLevel();
                }

                int dirtyTiles, nw, nh;
                byte[] payload;
                try
                {
                    payload = _encoder.CaptureAndEncodeDelta(_config, out dirtyTiles, out nw, out nh);
                    if (captureFailing)
                    {
                        captureFailing = false;
                        _log("[Stream] Screen capture resumed.");
                    }
                }
                catch (Exception ex)
                {
                    // Capture throws while the host shows the lock screen or a UAC prompt (secure desktop).
                    // This used to end the whole session; keep input, shell and files alive and retry instead.
                    if (!captureFailing)
                    {
                        captureFailing = true;
                        _log("[Stream] Screen capture unavailable (" + ex.Message + "). Retrying...");
                    }
                    _encoder.ForceFullKeyFrame();
                    _urgentTrigger.WaitOne(250);
                    continue;
                }

                try
                {
                    if (dirtyTiles > 0 && payload.Length > 0)
                    {
                        long sendStart = sw.ElapsedMilliseconds;
                        await TurboProtocol.SendPacketAsync(_stream, _writeLock, PacketType.ScreenFrame, payload, ct, _sec);
                        long sendMs = sw.ElapsedMilliseconds - sendStart;
                        if (autoAdaptive) UpdateAdaptiveLevel(sendMs, sw.ElapsedMilliseconds);

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
                    break; // connection lost or session cancelled
                }

                int targetIntervalMs = Math.Max(14, 1000 / Math.Max(10, _config.TargetFps));
                int elapsed = (int)(sw.ElapsedMilliseconds - frameStart);
                int delay = Math.Max(2, targetIntervalMs - elapsed);
                // Wait for next frame timer OR wake up IMMEDIATELY when user clicks/types!
                _urgentTrigger.WaitOne(delay);
            }
        }

        /// <summary>
        /// Auto-Adaptive with hysteresis: drop quality as soon as a frame sends slowly, but climb back only one
        /// level at a time after every frame has been fast for 3 s. Each level change resizes the stream and
        /// forces a full keyframe; switching on every single frame (as before) flooded slow links with keyframes
        /// whenever the send time hovered around a threshold.
        /// </summary>
        private void UpdateAdaptiveLevel(long sendMs, long nowMs)
        {
            int measured = sendMs > 160 ? 0 : sendMs > 60 ? 1 : sendMs > 15 ? 2 : 3;
            if (measured < _adaptiveLevel)
            {
                _adaptiveLevel = measured;
                _adaptiveHoldStartMs = nowMs;
            }
            else if (measured == _adaptiveLevel)
            {
                _adaptiveHoldStartMs = nowMs;
            }
            else if (nowMs - _adaptiveHoldStartMs >= 3000)
            {
                _adaptiveLevel++;
                _adaptiveHoldStartMs = nowMs;
            }
        }

        private void ApplyAdaptiveLevel()
        {
            switch (_adaptiveLevel)
            {
                case 0:
                    // Extreme low-bandwidth 2G fallback
                    _config.Scale = 0.42f;
                    _config.JpegQuality = 20;
                    _config.Grayscale = true;
                    _config.LowBitPosterize = true;
                    _config.TargetFps = 20;
                    break;
                case 1:
                    // Moderate 3G / WAN
                    _config.Scale = 0.60f;
                    _config.JpegQuality = 38;
                    _config.Grayscale = false;
                    _config.LowBitPosterize = false;
                    _config.TargetFps = 30;
                    break;
                case 2:
                    // Standard 4G / Fast WAN
                    _config.Scale = 0.80f;
                    _config.JpegQuality = 55;
                    _config.Grayscale = false;
                    _config.LowBitPosterize = false;
                    _config.TargetFps = 45;
                    break;
                default:
                    // Ultra-fast Local LAN / P2P (<15ms send) -> 60 FPS Native Quality!
                    _config.Scale = 1.0f;
                    _config.JpegQuality = 75;
                    _config.Grayscale = false;
                    _config.LowBitPosterize = false;
                    _config.TargetFps = 60;
                    break;
            }

            // The viewer's "Gray" checkbox used to be overwritten here on every frame in Auto-Adaptive mode.
            if (_forceGrayscale) _config.Grayscale = true;
        }

        private void WakeScreenEncoder()
        {
            try { _urgentTrigger.Set(); } catch (ObjectDisposedException) { }
        }

        private async Task CommandReceiveLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _client.Connected)
            {
                KeyValuePair<PacketType, byte[]>? pkt = await TurboProtocol.ReadPacketAsync(_stream, ct, _sec);
                if (pkt == null) break;
                Interlocked.Exchange(ref _lastReceivedTicks, Stopwatch.GetTimestamp());

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

            lock (_heldInputLock)
            {
                _lastMouseX = normX;
                _lastMouseY = normY;
                switch (action)
                {
                    case MouseActionType.LeftDown: case MouseActionType.RightDown: case MouseActionType.MiddleDown:
                        _heldButtons.Add(action);
                        break;
                    case MouseActionType.LeftUp: _heldButtons.Remove(MouseActionType.LeftDown); break;
                    case MouseActionType.RightUp: _heldButtons.Remove(MouseActionType.RightDown); break;
                    case MouseActionType.MiddleUp: _heldButtons.Remove(MouseActionType.MiddleDown); break;
                }
            }

            // If user clicked or scrolled, wake up the screen encoder as soon as the click has been injected.
            NativeInputInjector.InjectMouse(action, normX, normY, wheel, _config.MonitorIndex,
                action != MouseActionType.Move ? new Action(WakeScreenEncoder) : null);
        }

        private bool _warnedOldViewer;

        private void HandleKeyInput(byte[] data)
        {
            if (data.Length < 3) return;
            if (data.Length < 5 && !_warnedOldViewer)
            {
                // Only builds before v2026.11.23 send 3-byte keys; their keyboard handling leaves keys stuck.
                _warnedOldViewer = true;
                _log("[Input] The viewer PC runs an older TurboRemote: keys can get stuck after Alt+Tab or tab switches there. Copy this TurboRemote.exe (v" + TurboProtocol.AppVersion + ") to that PC.");
            }
            ushort vk = BitConverter.ToUInt16(data, 0);
            bool isDown = data[2] == 1;
            // Newer viewers add the scan code and the extended-key flag (bytes 3-4); older ones send only 3 bytes.
            byte scan = data.Length >= 5 ? data[3] : (byte)0;
            bool? extended = data.Length >= 5 ? (bool?)((data[4] & 1) != 0) : null;

            int id = vk | (extended == true ? 0x10000 : 0);
            lock (_heldInputLock)
            {
                if (isDown) _heldKeys[id] = new HeldKey { Vk = vk, Scan = scan, Extended = extended };
                else _heldKeys.Remove(id);
            }
            NativeInputInjector.InjectKey(vk, isDown, scan, extended, WakeScreenEncoder);
        }

        private void ReleaseHeldInput()
        {
            List<HeldKey> keys;
            List<MouseActionType> buttons;
            float x, y;
            lock (_heldInputLock)
            {
                keys = new List<HeldKey>(_heldKeys.Values);
                buttons = new List<MouseActionType>(_heldButtons);
                _heldKeys.Clear();
                _heldButtons.Clear();
                x = _lastMouseX;
                y = _lastMouseY;
            }

            foreach (HeldKey k in keys)
            {
                NativeInputInjector.InjectKey(k.Vk, false, k.Scan, k.Extended);
            }
            foreach (MouseActionType b in buttons)
            {
                MouseActionType up = b == MouseActionType.RightDown ? MouseActionType.RightUp
                    : b == MouseActionType.MiddleDown ? MouseActionType.MiddleUp
                    : MouseActionType.LeftUp;
                NativeInputInjector.InjectMouse(up, x, y, 0, _config.MonitorIndex);
            }
            if (keys.Count > 0 || buttons.Count > 0)
            {
                _log(string.Format("[Input] Released {0} key(s) and {1} mouse button(s) the viewer was still holding.", keys.Count, buttons.Count));
            }
        }

        private void HandleConfigUpdate(byte[] data)
        {
            if (data.Length < 5) return;
            BandwidthProfile profile = (BandwidthProfile)data[0];
            int monitorIdx = data[1];
            bool forceGray = data[2] == 1;

            _config.MonitorIndex = monitorIdx;
            _forceGrayscale = forceGray;
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
                // Switch the console output encoding to UTF-8 to match how stdout is decoded below; otherwise
                // non-ASCII output (accented file names, localized messages) arrived garbled. The user's command
                // goes on its own line so error positions still point at it.
                string script =
                    "try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false } catch { }\n" +
                    command;
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    // Proper argv quoting: the old Replace("\"", "\\\"") did not double backslashes, so a command
                    // ending in a backslash (`dir C:\`) swallowed the closing quote. (-EncodedCommand would avoid
                    // quoting, but makes Windows PowerShell print errors as CLIXML instead of readable text.)
                    Arguments = "-NoProfile -NonInteractive -Command " + QuoteWindowsArgument(script),
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                using (Process proc = Process.Start(psi))
                {
                    // Drain stdout and stderr at the same time. Reading stdout to the end first deadlocked
                    // (forever) as soon as a command wrote more than the pipe buffer to stderr.
                    Task<string> stdoutTask = proc.StandardOutput.ReadToEndAsync();
                    Task<string> stderrTask = proc.StandardError.ReadToEndAsync();
                    await Task.WhenAll(stdoutTask, stderrTask);
                    string stdout = stdoutTask.Result;
                    string stderr = stderrTask.Result;
                    bool exited = proc.WaitForExit(15000);

                    StringBuilder sb = new StringBuilder();
                    sb.AppendLine("PS> " + command);
                    if (!string.IsNullOrWhiteSpace(stdout)) sb.AppendLine(stdout.TrimEnd());
                    if (!string.IsNullOrWhiteSpace(stderr)) sb.AppendLine("[STDERR] " + stderr.TrimEnd());
                    // ExitCode throws while the process is still running.
                    sb.AppendLine(exited ? "[Exit Code: " + proc.ExitCode + "]" : "[Still running]");

                    payloadToSend = TurboProtocol.CompressFast(Encoding.UTF8.GetBytes(sb.ToString()));
                }
            }
            catch (Exception ex)
            {
                payloadToSend = TurboProtocol.CompressFast(Encoding.UTF8.GetBytes("[Shell Error] " + ex.Message + "\n"));
            }

            if (payloadToSend != null)
            {
                try
                {
                    await TurboProtocol.SendPacketAsync(_stream, _writeLock, PacketType.ShellOutput, payloadToSend, ct, _sec);
                }
                catch { } // viewer disconnected while the command was running
            }
        }

        /// <summary>
        /// Quotes one argument with the standard Windows (CommandLineToArgvW) rules: backslashes are literal
        /// except right before a quote, where they must be doubled - including before the closing quote.
        /// </summary>
        private static string QuoteWindowsArgument(string arg)
        {
            StringBuilder sb = new StringBuilder(arg.Length + 8);
            sb.Append('"');
            int backslashes = 0;
            foreach (char c in arg)
            {
                if (c == '\\')
                {
                    backslashes++;
                    continue;
                }
                if (c == '"')
                {
                    sb.Append('\\', backslashes * 2 + 1);
                }
                else
                {
                    sb.Append('\\', backslashes);
                }
                sb.Append(c);
                backslashes = 0;
            }
            sb.Append('\\', backslashes * 2);
            sb.Append('"');
            return sb.ToString();
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
            // "SYNC_LOCKS:CNS" with 1/0 for Caps, Num and Scroll Lock on the viewer's PC. Sent on every focus
            // change, so it is not logged.
            if (action.StartsWith("SYNC_LOCKS:") && action.Length >= 14)
            {
                NativeInputInjector.SyncLockKeys(action[11] == '1', action[12] == '1', action[13] == '1');
                return;
            }

            _log("[SystemAction] Triggered: " + action);
            if (action == "ELEVATE_ADMIN")
            {
                if (!NativeInputInjector.IsRunningAsAdmin())
                {
                    // Off the receive loop: the UAC prompt blocks until someone at the host answers it.
                    Task.Run(() =>
                    {
                        // Restart in Host mode with the same port/PIN. With no arguments the elevated copy
                        // opened the launcher instead of the host server, and this process exited even when
                        // the UAC prompt was declined - either way the remote viewer lost the host for good.
                        string restartArgs = "--host";
                        try { if (_getElevatedRestartArgs != null) restartArgs = _getElevatedRestartArgs(); } catch { }

                        if (NativeInputInjector.RestartAsAdmin(restartArgs))
                        {
                            Environment.Exit(0);
                        }
                        _log("[SystemAction] UAC elevation was declined or failed; host keeps running unelevated.");
                    });
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
                _incomingFileStream = null;
                // A bare file name means "the host's default folder" (the viewer cannot know this host's profile path).
                if (!Path.IsPathRooted(targetFilePath))
                {
                    targetFilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), targetFilePath);
                }
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
            try
            {
                byte[] raw = TurboProtocol.DecompressFast(compressedChunk);
                await _incomingFileStream.WriteAsync(raw, 0, raw.Length, ct);
            }
            catch (Exception ex)
            {
                // e.g. disk full: abort this upload instead of letting the exception end the whole session.
                _log("[FileTransfer Error] Upload aborted: " + ex.Message);
                try { _incomingFileStream.Dispose(); } catch { }
                _incomingFileStream = null;
            }
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
            // One download at a time: chunks carry no file id, so two parallel downloads interleaved their
            // chunks into the same stream and corrupted both files on the viewer.
            try { await _downloadLock.WaitAsync(ct); }
            catch (OperationCanceledException) { return; }

            try
            {
                if (!File.Exists(sourcePath))
                {
                    _log("[FileTransfer Error] File not found: " + sourcePath);
                    return;
                }
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
            finally
            {
                _downloadLock.Release();
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

        // The key saved in %LOCALAPPDATA%\TurboRemote\access_key.txt, and a --pin key used for this run only.
        private string _savedAccessKey;
        private string _commandLineKey;
        // The weak key Internet access was last refused for, so the warning is not repeated every few minutes.
        private string _lastRefusedKey;
        private System.Windows.Forms.Timer _keyApplyTimer;
        private int _listenPort;

        // Tailscale gives this PC a permanent address, so the hourly public tunnel is only a fallback
        // (see TunnelAutoSwitch). UI thread only, except the volatile/Interlocked fields.
        private TunnelAutoSwitch _tunnelSwitch = new TunnelAutoSwitch(TurboSettings.Get("publicTunnel", "auto"));
        private readonly Stopwatch _hostClock = new Stopwatch();
        private volatile bool _tunnelWanted;
        private volatile bool _tunnelStopPending;
        private int _tunnelSessions; // signed-in or signing-in sessions that came in through the tunnel (loopback)
        private TailscaleStatus _tailscale = new TailscaleStatus();
        private bool _tailscaleRefreshing, _updatingTunnelBox;
        private Label _lblTailscale;
        private Button _btnTailscale;
        private CheckBox _chkPublicTunnel;
        private System.Windows.Forms.Timer _tailscaleTimer;

        public HostMainForm()
        {
            Text = "TurboRemote — Host Server  v" + TurboProtocol.AppVersion;
            Size = new Size(775, 645);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(18, 20, 28);
            ForeColor = Color.FromArgb(230, 235, 245);
            Font = new Font("Segoe UI", 9.2f, FontStyle.Regular);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;

            string keyNote;
            _savedAccessKey = AccessKeyStore.LoadOrCreate(out keyNote);
            // Log needs a window handle, so report a first-run key once the form is shown.
            if (keyNote != null) Shown += (s, e) => Log(keyNote);

            NativeInputInjector.PreventSystemSleep();
            InitializeUi();
            PopulateNetworkEndpoints();
            UpdateAdminBadge();

            _tailscaleTimer = new System.Windows.Forms.Timer { Interval = 30000 };
            _tailscaleTimer.Tick += async (s, e) => await RefreshTailscaleAsync();
            _tailscaleTimer.Start();
            Shown += async (s, e) => await RefreshTailscaleAsync();
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
                Text = _savedAccessKey,
                Location = new Point(135, 57),
                Width = 72,
                BackColor = Color.FromArgb(30, 35, 50),
                ForeColor = Color.FromArgb(120, 255, 160),
                Font = new Font("Consolas", 10f, FontStyle.Bold),
                BorderStyle = BorderStyle.FixedSingle
            };

            // A key edited while the server runs is applied once typing pauses: Internet access turns off for a
            // weak key and back on for a strong one, without passing through every half-typed key.
            _keyApplyTimer = new System.Windows.Forms.Timer { Interval = 1500 };
            _keyApplyTimer.Tick += (s, e) =>
            {
                _keyApplyTimer.Stop();
                ApplyAccessKeyToInternetAccess();
            };
            _txtPin.TextChanged += (s, e) =>
            {
                if (!_isRunning) return;
                _keyApplyTimer.Stop();
                _keyApplyTimer.Start();
            };

            Button btnGenKey = CreateButton("🎲", 210, 56, 28, 25, Color.FromArgb(65, 85, 130));
            btnGenKey.Click += (s, e) =>
            {
                _txtPin.Text = TurboProtocol.GenerateStrongAccessKey();
                Log("[Security] Generated new cryptographic access key: " + _txtPin.Text);
                RememberAccessKey(_txtPin.Text);
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
                if (NativeInputInjector.RestartAsAdmin(BuildElevatedRestartArgs()))
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

            GroupBox grpEndpoints = CreateGroupBox("3. Host ID, Tailscale (Permanent Address) & Connection Endpoints", 16, 252, 727, 182);

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
                await PublishTicketToCloudDirectoryAsync(true);
            };

            _lblTailscale = new Label
            {
                Text = "Tailscale: checking…",
                Location = new Point(14, 122),
                Size = new Size(520, 22),
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.FromArgb(170, 185, 210)
            };

            _btnTailscale = CreateButton("📋 Copy Tailscale name", 542, 120, 170, 26, Color.FromArgb(45, 95, 110));
            _btnTailscale.Visible = false;
            _btnTailscale.Click += (s, e) => OnTailscaleButtonClicked();

            // Replaces the old "Auto Internet Tunnel" button: the hourly tunnel is now a fallback.
            _chkPublicTunnel = new CheckBox
            {
                Location = new Point(14, 150),
                AutoSize = true,
                ForeColor = Color.FromArgb(190, 205, 230),
                Font = new Font("Segoe UI", 8.6f)
            };
            _chkPublicTunnel.CheckedChanged += (s, e) => OnPublicTunnelBoxChanged();
            UpdateTunnelBox();

            grpEndpoints.Controls.AddRange(new Control[] { _lstEndpoints, lblRoom, _txtRoomId, btnCopyIp, _lblTailscale, _btnTailscale, _chkPublicTunnel });
            Controls.Add(grpEndpoints);

            GroupBox grpLog = CreateGroupBox("4. Real-Time Session Telemetry & Security Log", 16, 442, 727, 148);
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
                Size = new Size(698, 92),
                BackColor = Color.FromArgb(14, 16, 22),
                ForeColor = Color.FromArgb(200, 215, 230),
                Font = new Font("Consolas", 8.8f),
                BorderStyle = BorderStyle.FixedSingle
            };

            grpLog.Controls.AddRange(new Control[] { _lblStatus, _lblStats, _lstLog });
            Controls.Add(grpLog);
        }

        // ntfy.sh allows 250 messages a day per public IP and keeps them for 12 hours. Re-publishing every
        // 4 minutes (360 a day) hit that limit after ~16 hours; from then on new tunnel addresses (pinggy's
        // free tunnel changes every 60 minutes) could not be published and Internet clients only found dead
        // ones. Publish when the ticket changes, plus a keep-alive well inside the 12-hour expiry.
        private static readonly TimeSpan CloudKeepAliveInterval = TimeSpan.FromHours(3);
        private string _lastPublishedCloudKey = "";
        private DateTime _lastCloudPublishUtc = DateTime.MinValue;

        private const string TunnelListSuffix = " (Public Internet Tunnel)";

        /// <summary>Safe from any thread: the text boxes are read through Invoke when needed.</summary>
        private async Task PublishTicketToCloudDirectoryAsync(bool force = false)
        {
            string portStr = InvokeRequired ? (string)Invoke(new Func<string>(() => _txtPort.Text.Trim())) : _txtPort.Text.Trim();
            string room = InvokeRequired ? (string)Invoke(new Func<string>(() => _txtRoomId.Text.Trim())) : _txtRoomId.Text.Trim();
            string pin = InvokeRequired ? (string)Invoke(new Func<string>(() => _txtPin.Text.Trim())) : _txtPin.Text.Trim();

            // The directory is found by Host ID (the machine name) + key: under a weak key anyone could read
            // this PC's addresses.
            if (IsInternetRefusedForKey(pin)) return;

            int port = 9876;
            int.TryParse(portStr, out port);
            string ticket = SmartRouteEngine.BuildSmartConnectTicket(port, _activePublicTunnelEndpoint);

            string publishKey = room + "|" + pin + "|" + ticket;
            if (!force && publishKey == _lastPublishedCloudKey && DateTime.UtcNow - _lastCloudPublishUtc < CloudKeepAliveInterval)
            {
                return;
            }

            Log(string.Format("[Cloud Discovery] Publishing AES-256 encrypted endpoints for Host ID '{0}'...", room));
            // Key derivation and the HTTP request (whose DNS lookup is synchronous) run off the UI thread.
            string error = await Task.Run(() => SmartRouteEngine.PublishHostTicketToCloudAsync(room, pin, ticket));
            if (error == null)
            {
                _lastPublishedCloudKey = publishKey;
                _lastCloudPublishUtc = DateTime.UtcNow;
                if (string.IsNullOrEmpty(_activePublicTunnelEndpoint))
                {
                    Log(string.Format("[Cloud Discovery] Host ID '{0}' published with LAN endpoints only; Internet access is ready once the tunnel is up.", room));
                }
                else
                {
                    Log(string.Format("[Cloud Discovery READY] Client can now type Host ID '{0}' and access key '{1}' from anywhere on the Internet!", room, pin));
                }
            }
            else
            {
                Log("[Cloud Discovery] Publish failed (" + error + "). LAN AUTO and the Smart Ticket still work.");
            }
        }

        /// <summary>
        /// True when <paramref name="key"/> must not face the Internet (no tunnel, no cloud publish). Logs why
        /// once per key. Safe from any thread.
        /// </summary>
        private bool IsInternetRefusedForKey(string key)
        {
            string weakness = AccessKeyStore.WhyTooWeakForInternet(key);
            if (weakness == null)
            {
                _lastRefusedKey = null;
                return false;
            }
            if (_lastRefusedKey != key)
            {
                _lastRefusedKey = key;
                Log("[Security] Internet access is OFF: the access key is too weak to expose to the Internet (" + weakness + "). " +
                    "LAN and direct connections still work. Click 🎲 for a random key, or type one with " + AccessKeyStore.MinInternetKeyLength +
                    "+ characters; Internet access then turns on by itself.");
            }
            return true;
        }

        /// <summary>Stores a key the user chose in the Host UI for the next start; --pin keys are for one run only.</summary>
        private void RememberAccessKey(string key)
        {
            key = (key ?? "").Trim();
            if (key.Length == 0 || key == _savedAccessKey || key == _commandLineKey) return;
            if (AccessKeyStore.Save(key))
            {
                _savedAccessKey = key;
                Log("[Security] Access key saved for the next start in " + AccessKeyStore.KeyFilePath + ".");
            }
        }

        /// <summary>UI thread.</summary>
        private void ShowListeningStatus(bool internetAllowed)
        {
            _lblStatus.Text = "● Server Status: LISTENING ON PORT " + _listenPort +
                (internetAllowed ? " (LOCAL + INTERNET ACTIVE)" : " (LAN ONLY: ACCESS KEY TOO WEAK FOR INTERNET)");
        }

        /// <summary>UI thread. Brings the tunnel and cloud directory in line with the key now in the box.</summary>
        private void ApplyAccessKeyToInternetAccess()
        {
            if (!_isRunning) return;
            string key = _txtPin.Text.Trim();
            RememberAccessKey(key);
            bool refused = IsInternetRefusedForKey(key);
            ShowListeningStatus(!refused);
            if (refused)
            {
                Process tunnel = _tunnelProc;
                if (tunnel != null)
                {
                    try
                    {
                        if (!tunnel.HasExited)
                        {
                            tunnel.Kill();
                            Log("[Auto-Tunnel] Public Internet tunnel closed because the new access key is too weak.");
                        }
                    }
                    catch { }
                }
                return;
            }
            StartBuiltInPublicSshTunnel();
            Task publish = PublishTicketToCloudDirectoryAsync();
        }

        /// <summary>UI thread. Re-reads Tailscale (off the UI thread) and applies the public-tunnel decision.</summary>
        private async Task RefreshTailscaleAsync()
        {
            if (_tailscaleRefreshing || IsDisposed) return;
            _tailscaleRefreshing = true;
            try
            {
                TailscaleStatus st = await Task.Run(() => TailscaleInfo.Query());
                if (IsDisposed) return;
                _tailscale = st;
                ShowTailscaleLine(st);
                if (_isRunning)
                {
                    _tunnelWanted = _tunnelSwitch.Update(st.State, _hostClock.Elapsed);
                    ApplyTunnelPolicy();
                }
                UpdateTunnelBox();
            }
            catch { }
            finally
            {
                _tailscaleRefreshing = false;
            }
        }

        private void ShowTailscaleLine(TailscaleStatus st)
        {
            switch (st.State)
            {
                case TailscaleState.Running:
                    string where = string.IsNullOrEmpty(st.DnsName) ? st.IPv4 : st.DnsName + " · " + st.IPv4;
                    string name = string.IsNullOrEmpty(st.HostName) ? Environment.MachineName : st.HostName;
                    _lblTailscale.Text = "Tailscale ON — from anywhere, connect to: " + name + "  (" + where + ")";
                    _lblTailscale.ForeColor = Color.FromArgb(120, 235, 170);
                    _btnTailscale.Text = "📋 Copy Tailscale name";
                    _btnTailscale.Visible = true;
                    break;
                case TailscaleState.Starting:
                    _lblTailscale.Text = "Tailscale is starting…";
                    _lblTailscale.ForeColor = Color.FromArgb(170, 185, 210);
                    _btnTailscale.Visible = false;
                    break;
                case TailscaleState.NotInstalled:
                    _lblTailscale.Text = "For a permanent Internet address, install Tailscale on this PC and the viewer PC (same account).";
                    _lblTailscale.ForeColor = Color.FromArgb(255, 210, 120);
                    _btnTailscale.Text = "⬇ Get Tailscale";
                    _btnTailscale.Visible = true;
                    break;
                default:
                    _lblTailscale.Text = "Tailscale is installed but not connected — open Tailscale and sign in.";
                    _lblTailscale.ForeColor = Color.FromArgb(255, 210, 120);
                    _btnTailscale.Visible = false;
                    break;
            }
        }

        private void OnTailscaleButtonClicked()
        {
            if (_tailscale.IsRunning)
            {
                string name = string.IsNullOrEmpty(_tailscale.DnsName) ? _tailscale.IPv4 : _tailscale.DnsName;
                try { Clipboard.SetText(name); } catch { }
                Log("[Tailscale] Copied " + name + ". On any PC in your Tailscale account, type it (or just the Host ID) in the viewer.");
                return;
            }
            try { Process.Start("https://tailscale.com/download"); } catch { }
        }

        /// <summary>The tunnel switch wants the tunnel and the key is strong enough to face the Internet. Any thread.</summary>
        private bool WantTunnel()
        {
            if (!_tunnelWanted) return false;
            string key;
            try { key = GetPinThreadSafe(); } catch { return false; } // form closing
            return !IsInternetRefusedForKey(key);
        }

        /// <summary>UI thread. Opens or closes the public tunnel to match <see cref="WantTunnel"/>.</summary>
        private void ApplyTunnelPolicy()
        {
            if (!_isRunning) return;
            if (WantTunnel())
            {
                _tunnelStopPending = false;
                StartBuiltInPublicSshTunnel();
                return;
            }

            Process tunnel = _tunnelProc;
            bool open = false;
            try { open = tunnel != null && !tunnel.HasExited; } catch { }
            if (!open) return;

            // Never cut a viewer who is connected through the tunnel; close it once they leave.
            if (Volatile.Read(ref _tunnelSessions) > 0)
            {
                if (!_tunnelStopPending) Log("[Auto-Tunnel] The public tunnel will close when the viewer connected through it disconnects.");
                _tunnelStopPending = true;
                return;
            }

            _tunnelStopPending = false;
            try { tunnel.Kill(); } catch { }
            Log(_tunnelSwitch.IsAutomatic
                ? "[Auto-Tunnel] Public tunnel closed: Tailscale gives this PC a permanent address."
                : "[Auto-Tunnel] Public tunnel closed.");
        }

        private void UpdateTunnelBox()
        {
            _updatingTunnelBox = true;
            try
            {
                _chkPublicTunnel.Text = "Also open a public tunnel for viewers without Tailscale (address changes every hour)" +
                                        (_tunnelSwitch.IsAutomatic ? " (automatic)" : "");
                _chkPublicTunnel.Checked = _isRunning ? _tunnelWanted : TurboSettings.Get("publicTunnel", "auto") == "on";
            }
            finally
            {
                _updatingTunnelBox = false;
            }
        }

        private void OnPublicTunnelBoxChanged()
        {
            if (_updatingTunnelBox) return;
            string setting = _chkPublicTunnel.Checked ? "on" : "off";
            TurboSettings.Set("publicTunnel", setting);
            _tunnelSwitch = new TunnelAutoSwitch(setting);
            _tunnelWanted = _tunnelSwitch.Update(_tailscale.State, _hostClock.Elapsed);
            Log("[Auto-Tunnel] Public tunnel set to " + setting + " (saved for the next start).");
            ApplyTunnelPolicy();
            UpdateTunnelBox();
        }

        private void StartBuiltInPublicSshTunnel()
        {
            try
            {
                if (_tunnelProc != null && !_tunnelProc.HasExited)
                {
                    return;
                }

                // Opening the tunnel exposes the key check to the whole Internet, and with Tailscale running it
                // only adds a route that dies every hour.
                if (!WantTunnel())
                {
                    return;
                }

                string portText = InvokeRequired
                    ? (string)Invoke(new Func<string>(() => _txtPort.Text.Trim()))
                    : _txtPort.Text.Trim();
                int port;
                if (!int.TryParse(portText, out port)) port = 9876;

                string keyError;
                string keyPath = PublicTunnel.EnsureTunnelKey(out keyError);
                if (keyPath == null)
                {
                    Log("[Auto-Tunnel] Could not create the SSH key the tunnel signs in with (" + keyError + "). Make sure the Windows 'OpenSSH Client' optional feature is installed.");
                    return;
                }

                Log("[Auto-Tunnel] Starting background OpenSSH Public Internet TCP Relay for local port " + port + "...");
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = PublicTunnel.FindOpenSshTool("ssh.exe"),
                    Arguments = PublicTunnel.BuildSshArguments(port, keyPath),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    // Our own stdin pipe, kept open: ssh neither takes it for a terminal nor sees EOF and quits.
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                Process proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
                proc.OutputDataReceived += (s, e) => ParseTunnelOutputLine(e.Data);
                proc.ErrorDataReceived += (s, e) => ParseTunnelOutputLine(e.Data);
                proc.Exited += (s, e) => OnTunnelExited(proc);
                _tunnelProc = proc;
                try
                {
                    proc.Start();
                }
                catch
                {
                    // A never-started Process would make the HasExited check above throw on every later attempt.
                    _tunnelProc = null;
                    throw;
                }
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                Log("[Auto-Tunnel] Could not start ssh.exe (" + ex.Message + "). Install the Windows 'OpenSSH Client' optional feature.");
            }
            catch (Exception ex)
            {
                Log("[Auto-Tunnel] Background SSH tunnel note: " + ex.Message);
            }
        }

        private void OnTunnelExited(Process proc)
        {
            // An old tunnel killed by Stop + quick Start must not clear the address of the new one.
            if (!ReferenceEquals(proc, _tunnelProc)) return;

            // A dead tunnel's address must not stay in the ticket the keep-alive publishes.
            string deadEndpoint = _activePublicTunnelEndpoint;
            _activePublicTunnelEndpoint = "";

            if (!IsDisposed && IsHandleCreated && !string.IsNullOrEmpty(deadEndpoint))
            {
                try { BeginInvoke(new Action(RemoveTunnelEndpointsFromList)); } catch (InvalidOperationException) { }
            }

            CancellationTokenSource cts = _serverCts;
            if (_isRunning && cts != null && !cts.IsCancellationRequested)
            {
                // Closed on purpose (weak key, Tailscale running, box unticked): nothing to restart.
                if (!WantTunnel()) return;

                Log("[Auto-Tunnel] Tunnel closed; reconnecting in 5 seconds...");
                CancellationToken token = cts.Token;
                Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(5000, token);
                        if (_isRunning && !token.IsCancellationRequested) StartBuiltInPublicSshTunnel();
                    }
                    catch { }
                });
            }
        }

        private void RemoveTunnelEndpointsFromList()
        {
            for (int i = _lstEndpoints.Items.Count - 1; i >= 0; i--)
            {
                string item = _lstEndpoints.Items[i] as string;
                if (item != null && item.EndsWith(TunnelListSuffix)) _lstEndpoints.Items.RemoveAt(i);
            }
        }

        private void ParseTunnelOutputLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            // pinggy prints e.g. "tcp://abcd-1-2-3-4.run.pinggy-free.link:35793"
            string endpoint = PublicTunnel.TryParseEndpoint(line);
            if (endpoint == null)
            {
                // Show what ssh says ("Permission denied", "Could not resolve hostname"), so a tunnel that
                // fails is no longer silent. pinggy's free-tier banner is about pinggy accounts, not the PIN.
                if (line.IndexOf("Permanently added", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    line.IndexOf("not authenticated", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return;
                }
                if (line.IndexOf("expire in 60 minutes", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Log("[Auto-Tunnel] Free pinggy tunnel: its address changes every 60 minutes; TurboRemote reconnects and re-publishes it automatically.");
                    return;
                }
                Log("[Auto-Tunnel] " + line.Trim());
                return;
            }

            if (_activePublicTunnelEndpoint == endpoint) return;
            _activePublicTunnelEndpoint = endpoint;
            Log("[PUBLIC INTERNET TUNNEL ACTIVE] -> " + endpoint);
            // This runs on an ssh output thread: an exception here (form already closed) would
            // crash the process, so guard the cross-thread call.
            if (IsDisposed || !IsHandleCreated) return;
            try
            {
                BeginInvoke(new Action(async () =>
                {
                    RemoveTunnelEndpointsFromList();
                    _lstEndpoints.Items.Insert(0, endpoint + TunnelListSuffix);
                    await PublishTicketToCloudDirectoryAsync();
                }));
            }
            catch (InvalidOperationException) { }
        }

        public void ConfigureFromArgs(string[] args)
        {
            bool autoStart = true;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--port" && i + 1 < args.Length) _txtPort.Text = args[i + 1];
                if (args[i] == "--pin" && i + 1 < args.Length)
                {
                    _commandLineKey = args[i + 1].Trim();
                    _txtPin.Text = _commandLineKey;
                }
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

        private async Task AutoOpenFirewallUpnpAndTunnelAsync(int port, CancellationToken ct)
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

            // 3. Decide about the public tunnel from Tailscale's state (re-checked every 30 s by the timer). The
            //    tunnel only opens when that decision wants it and the access key is strong enough.
            try { BeginInvoke(new Func<Task>(RefreshTailscaleAsync)); } catch (InvalidOperationException) { }

            // 4. Every 4 minutes, re-publish the Cloud Rendezvous ticket if it changed (new LAN IP, PIN or Host ID)
            //    or the keep-alive is due, so Internet clients can always find this Host.
            while (_isRunning && !ct.IsCancellationRequested)
            {
                await PublishTicketToCloudDirectoryAsync();
                try
                {
                    await Task.Delay(240000, ct);
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

            // Work on locals: after Stop + quick re-Start the fields point at the NEW listener, and the old
            // accept loop's exception must not stop that new server.
            CancellationTokenSource cts = new CancellationTokenSource();
            TcpListener listener = null;
            bool started = false;
            try
            {
                _serverCts = cts;
                listener = new TcpListener(IPAddress.Any, port);
                _listener = listener;
                listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                listener.Start();
                started = true;

                // Start zero-config UDP LAN Auto-Discovery Beacon on port 9878
                SmartRouteEngine.StartLanBeaconResponder(() => port, cts.Token);

                // A fresh decision per start: the 60 s wait for a Tailscale that is still starting counts from here.
                _tunnelSwitch = new TunnelAutoSwitch(TurboSettings.Get("publicTunnel", "auto"));
                _tunnelWanted = false;
                _tunnelStopPending = false;
                _hostClock.Restart();

                _isRunning = true;
                _btnStartStop.Text = "⏹ Stop Host Server";
                _btnStartStop.BackColor = Color.FromArgb(185, 55, 55);
                _listenPort = port;
                _lblStatus.ForeColor = Color.FromArgb(95, 245, 155);

                Log(string.Format("[Server] TurboHost listening on 0.0.0.0:{0} (Host ID: {1}, access key: {2})", port, _txtRoomId.Text, _txtPin.Text));
                RememberAccessKey(_txtPin.Text);
                _lastRefusedKey = null; // say again on every start why Internet access is off
                ShowListeningStatus(!IsInternetRefusedForKey(_txtPin.Text.Trim()));

                // Automatically open Firewall, map UPnP router port, launch Public Internet Tunnel, and publish to Cloud.
                // Pass THIS server's token so a quick Stop + Start cannot leave two publish loops running.
                CancellationToken autoToken = cts.Token;
                Task.Run(() => AutoOpenFirewallUpnpAndTunnelAsync(port, autoToken));

                while (!cts.Token.IsCancellationRequested)
                {
                    TcpClient client = await listener.AcceptTcpClientAsync();
                    CancellationToken sessionToken = cts.Token;
                    Task.Run(() => HandleClientSessionAsync(client, sessionToken));
                }
            }
            catch (Exception ex)
            {
                if (!started)
                {
                    // Used to be swallowed silently, leaving the user with a server that never started.
                    Log("[Server] Could not start listener on port " + port + ": " + ex.Message);
                    try { if (listener != null) listener.Stop(); } catch { }
                }
                else if (_isRunning && ReferenceEquals(listener, _listener))
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
                // After "Stop Host Server" the old token is already cancelled, which killed the reverse
                // session the moment it started.
                if (_serverCts == null || _serverCts.IsCancellationRequested) _serverCts = new CancellationTokenSource();
                CancellationToken sessionToken = _serverCts.Token;
                Task.Run(() => HandleClientSessionAsync(client, sessionToken));
            }
            catch (Exception ex)
            {
                Log("[Reverse Error] " + ex.Message);
            }
        }

        private async Task HandleClientSessionAsync(TcpClient client, CancellationToken ct)
        {
            string remoteEp = client.Client.RemoteEndPoint != null ? client.Client.RemoteEndPoint.ToString() : "Unknown";
            // Split(':') turned every IPv6 peer ("[2001:db8::1]:5000") into "[", sharing one lockout bucket.
            IPEndPoint remoteIpEp = client.Client.RemoteEndPoint as IPEndPoint;
            string remoteIpOnly = remoteIpEp != null ? remoteIpEp.Address.ToString() : remoteEp;
            // Every Internet client arrives from 127.0.0.1 through the tunnel's ssh.exe, so a loopback address
            // says nothing about who is connecting: a per-IP lockout there locked out all Internet users at once.
            bool sharedAddress = remoteIpEp != null && SharedAddressThrottle.IsSharedAddress(remoteIpEp.Address);

            int lockoutRemaining;
            if (!sharedAddress && BruteForceProtector.IsIpLockedOut(remoteIpOnly, out lockoutRemaining))
            {
                Log(string.Format("[Security BLOCKED] IP {0} is locked out for {1}s due to failed attempts.", remoteIpOnly, lockoutRemaining));
                try { client.Close(); } catch { }
                return;
            }

            Interlocked.Increment(ref _connectedClients);
            if (sharedAddress) Interlocked.Increment(ref _tunnelSessions);
            Log("[Connect] Incoming TLS-Grade E2EE connection from " + remoteEp);

            try
            {
                client.NoDelay = true;
                client.SendBufferSize = 256 * 1024;
                client.ReceiveBufferSize = 256 * 1024;
                NetworkHelper.EnableKeepAlive(client.Client);

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

                    SharedAddressThrottle.Turn turn = null;
                    if (sharedAddress)
                    {
                        turn = await SharedAddressThrottle.WaitForTurnAsync(ct);
                        if (turn == null)
                        {
                            await TurboProtocol.SendPacketAsync(stream, writeLock, PacketType.AuthResult, Encoding.UTF8.GetBytes("FAIL:Host is busy checking other sign-ins from the Internet. Try again in a minute."), ct);
                            Log("[Security] Too many sign-ins waiting via loopback/Internet tunnel; turned away " + remoteEp + ".");
                            return;
                        }
                    }

                    SecurityContext sec = null;
                    bool keyIsRight = false;
                    try
                    {
                        sec = new SecurityContext(GetPinThreadSafe(), salt16, challenge16);
                        byte[] expectedClientProof = sec.ComputeHandshakeProof(challenge16, "CLIENT_AUTH_V2");
                        keyIsRight = TurboProtocol.ConstantTimeEquals(authPacket.Value.Value, expectedClientProof);
                        if (turn != null) turn.RecordResult(keyIsRight);
                    }
                    catch
                    {
                        if (sec != null) sec.Dispose();
                        throw;
                    }
                    finally
                    {
                        // Hand the turn on now, not after the (possibly hours-long) session.
                        if (turn != null) turn.Dispose();
                    }

                    using (sec)
                    {
                        if (!keyIsRight)
                        {
                            if (sharedAddress)
                            {
                                int streak = SharedAddressThrottle.FailureStreak;
                                await TurboProtocol.SendPacketAsync(stream, writeLock, PacketType.AuthResult, Encoding.UTF8.GetBytes("FAIL:Invalid Access Key/PIN"), ct);
                                Log(string.Format("[Security ALERT] Rejected wrong access key from {0} via loopback/Internet tunnel ({1} wrong in a row; next key check waits {2}s; nobody is locked out)",
                                    remoteEp, streak, (int)SharedAddressThrottle.DelayAfterFailures(streak).TotalSeconds));
                            }
                            else
                            {
                                int fails = BruteForceProtector.RecordFailedAttempt(remoteIpOnly);
                                await TurboProtocol.SendPacketAsync(stream, writeLock, PacketType.AuthResult, Encoding.UTF8.GetBytes("FAIL:Invalid Access Key/PIN (Attempt " + fails + "/5)"), ct);
                                Log(string.Format("[Security ALERT] Rejected invalid HMAC proof from {0} (Fail #{1}/5)", remoteEp, fails));
                            }
                            return;
                        }

                        if (!sharedAddress) BruteForceProtector.ResetAttempts(remoteIpOnly);

                        // 3. Send AES-256 + HMAC-SHA256 Encrypted AuthResult back to Client
                        // Field 6 (the version) is new; older viewers read only the first fields and ignore it.
                        string machineInfo = string.Format("OK|{0}|{1}|{2}|AES-256-HMAC-SHA256|v{3}", Environment.MachineName, NativeInputInjector.IsRunningAsAdmin() ? "ADMIN" : "USER", Screen.AllScreens.Length, TurboProtocol.AppVersion);
                        await TurboProtocol.SendPacketAsync(stream, writeLock, PacketType.AuthResult, Encoding.UTF8.GetBytes(machineInfo), ct, sec);
                        Log("[Security VERIFIED] " + remoteEp + " authenticated via Zero-Knowledge PBKDF2-SHA256 & AES-256 E2EE.");

                        ClientSessionHandler session = new ClientSessionHandler(client, stream, writeLock, sec, Log, AddBytesSent, BuildElevatedRestartArgs);
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
                // The last viewer on the tunnel left: a close that was waiting for them can happen now.
                if (sharedAddress && Interlocked.Decrement(ref _tunnelSessions) == 0 && _tunnelStopPending && !IsDisposed)
                {
                    try { BeginInvoke(new Action(ApplyTunnelPolicy)); } catch (InvalidOperationException) { }
                }
                UpdateStatsLabel(0);
                try { client.Close(); } catch { }
            }
        }

        /// <summary>Command line that restarts this PC as an elevated Host with the same port and PIN.</summary>
        private string BuildElevatedRestartArgs()
        {
            if (InvokeRequired)
            {
                return (string)Invoke(new Func<string>(BuildElevatedRestartArgs));
            }
            return string.Format("--host --port {0} --pin \"{1}\"", _txtPort.Text.Trim(), _txtPin.Text.Trim());
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // A viewer sending Alt+F4 to close a remote program closed this window instead whenever it was the
            // focused one, and the viewer lost this PC for good. Ask first; "No" is the default, so a stray
            // Enter from the viewer keeps the Host running.
            if (e.CloseReason == CloseReason.UserClosing && Volatile.Read(ref _connectedClients) > 0)
            {
                DialogResult answer = MessageBox.Show(this,
                    "A remote viewer is connected to this PC.\n\nClose TurboRemote Host and disconnect them?",
                    "TurboRemote Host", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                if (answer != DialogResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
            }
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            // Stop first so the tunnel watchdog does not schedule a restart when the tunnel is killed below.
            _isRunning = false;
            try { _keyApplyTimer.Dispose(); } catch { }
            try { _tailscaleTimer.Dispose(); } catch { }
            try { if (_serverCts != null) _serverCts.Cancel(); } catch { }
            try { if (_listener != null) _listener.Stop(); } catch { }
            // The ssh.exe tunnel used to outlive TurboRemote as an orphan process, keeping the public relay open.
            try { if (_tunnelProc != null && !_tunnelProc.HasExited) _tunnelProc.Kill(); } catch { }
            base.OnFormClosed(e);
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
