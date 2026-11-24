using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using TurboCore;

// Shared helpers for the console tests in this folder. None of them sends keyboard or mouse input: on one PC a
// real Host moves the real cursor, so tests talk to the app only through fake counterparts and UI Automation.

public static class TestKit
{
    private static int _passes, _fails;

    /// <summary>Path of the TurboRemote.exe under test (the test program's first argument).</summary>
    public static string ExePath;

    /// <summary>
    /// This run's own data folder (TURBOREMOTE_DATA_DIR): settings, saved access key and tunnel key go here, never
    /// into the user's %LOCALAPPDATA%\TurboRemote. Deleted by <see cref="Summary"/>.
    /// </summary>
    public static string SandboxDir;

    /// <summary>The fake ssh.exe and ssh-keygen.exe (TURBOREMOTE_OPENSSH_DIR): no test opens a real pinggy tunnel.</summary>
    public static string FakeOpenSshDir;

    /// <summary>A local stand-in for ntfy.sh (TURBOREMOTE_DIRECTORY_URL) for this program and every app it starts.</summary>
    public static FakeDirectory CloudDirectory;

    /// <summary>Call first in Main. Sets up the sandbox; the apps the test starts inherit it.</summary>
    public static void Init(string[] args)
    {
        ExePath = args.Length > 0 ? args[0] : "TurboRemote.exe";

        string root = Path.Combine(Path.GetTempPath(), "TurboRemoteTests");
        RemoveOldSandboxes(root);
        SandboxDir = Path.Combine(root, Process.GetCurrentProcess().Id + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        FakeOpenSshDir = Path.Combine(SandboxDir, "openssh");
        Directory.CreateDirectory(FakeOpenSshDir);
        string fakeSsh = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "FakeOpenSsh.exe");
        if (File.Exists(fakeSsh))
        {
            File.Copy(fakeSsh, Path.Combine(FakeOpenSshDir, "ssh.exe"), true);
            File.Copy(fakeSsh, Path.Combine(FakeOpenSshDir, "ssh-keygen.exe"), true);
        }
        CloudDirectory = new FakeDirectory();

        Environment.SetEnvironmentVariable("TURBOREMOTE_DATA_DIR", SandboxDir);
        Environment.SetEnvironmentVariable("TURBOREMOTE_OPENSSH_DIR", FakeOpenSshDir);
        Environment.SetEnvironmentVariable("TURBOREMOTE_DIRECTORY_URL", CloudDirectory.BaseUrl);
    }

    /// <summary>Sandboxes left behind by a test that was killed, once they are a day old.</summary>
    private static void RemoveOldSandboxes(string root)
    {
        try
        {
            if (!Directory.Exists(root)) return;
            foreach (string dir in Directory.GetDirectories(root))
            {
                try { if (Directory.GetCreationTimeUtc(dir) < DateTime.UtcNow.AddDays(-1)) Directory.Delete(dir, true); } catch { }
            }
        }
        catch { }
    }

    public static void Check(string name, bool ok, string detail = null)
    {
        Console.WriteLine("{0}  {1}{2}", ok ? "PASS" : "FAIL", name, string.IsNullOrEmpty(detail) ? "" : "\n      " + detail);
        if (ok) _passes++; else _fails++;
    }

    /// <summary>Prints the verdict, removes the sandbox, returns the exit code.</summary>
    public static int Summary()
    {
        Console.WriteLine(_fails == 0 ? "ALL CHECKS PASSED" : _fails + " CHECK(S) FAILED");
        if (CloudDirectory != null) CloudDirectory.Dispose();
        // Apps killed a moment ago may still hold their files open.
        for (int i = 0; i < 10 && SandboxDir != null && Directory.Exists(SandboxDir); i++)
        {
            try { Directory.Delete(SandboxDir, true); } catch { Thread.Sleep(300); }
        }
        return _fails == 0 ? 0 : 1;
    }

    public static int FreePort()
    {
        TcpListener l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public static bool WaitUntil(Func<bool> condition, int timeoutMs, int pollMs = 200)
    {
        Stopwatch sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            try { if (condition()) return true; } catch { }
            Thread.Sleep(pollMs);
        }
        try { return condition(); } catch { return false; }
    }

    public static Process StartApp(string arguments)
    {
        return Process.Start(ExePath, arguments);
    }

    /// <summary>Starts the app with extra environment variables (on top of the sandbox ones).</summary>
    public static Process StartApp(string arguments, IDictionary<string, string> environment)
    {
        ProcessStartInfo psi = new ProcessStartInfo(ExePath, arguments) { UseShellExecute = false };
        foreach (KeyValuePair<string, string> kv in environment) psi.EnvironmentVariables[kv.Key] = kv.Value;
        return Process.Start(psi);
    }

    public static void Kill(Process p)
    {
        try { if (p != null && !p.HasExited) { p.Kill(); p.WaitForExit(3000); } } catch { }
    }
}

/// <summary>
/// A stand-in for ntfy.sh on 127.0.0.1: keeps what is published per topic and serves it back to "/raw" polls, and
/// counts both. Every app a test starts uses it through TURBOREMOTE_DIRECTORY_URL.
/// </summary>
public sealed class FakeDirectory : IDisposable
{
    private readonly TcpListener _listener;
    private readonly Dictionary<string, List<string>> _topics = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
    private int _posts, _polls;

    public string BaseUrl { get; private set; }
    public int Posts { get { return Volatile.Read(ref _posts); } }
    public int Polls { get { return Volatile.Read(ref _polls); } }

    public FakeDirectory()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        BaseUrl = "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port + "/";
        new Thread(AcceptLoop) { IsBackground = true }.Start();
    }

    public void ResetCounts()
    {
        Interlocked.Exchange(ref _posts, 0);
        Interlocked.Exchange(ref _polls, 0);
    }

    private void AcceptLoop()
    {
        while (true)
        {
            TcpClient c;
            try { c = _listener.AcceptTcpClient(); } catch { return; }
            ThreadPool.QueueUserWorkItem(_ => Serve(c));
        }
    }

    private void Serve(TcpClient c)
    {
        try
        {
            using (c)
            using (NetworkStream s = c.GetStream())
            {
                s.ReadTimeout = 5000;
                string head = ReadHead(s);
                if (head == null) return;
                string[] lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
                string[] request = lines[0].Split(' ');
                if (request.Length < 2) return;
                int length = 0;
                bool expectContinue = false;
                foreach (string line in lines)
                {
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) int.TryParse(line.Substring(15).Trim(), out length);
                    if (line.StartsWith("Expect:", StringComparison.OrdinalIgnoreCase) && line.IndexOf("100-continue", StringComparison.OrdinalIgnoreCase) >= 0) expectContinue = true;
                }
                if (expectContinue)
                {
                    byte[] cont = Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n");
                    s.Write(cont, 0, cont.Length);
                }
                byte[] body = new byte[length];
                for (int read = 0; read < length; )
                {
                    int n = s.Read(body, read, length - read);
                    if (n <= 0) return;
                    read += n;
                }

                string topic = request[1].Split('?')[0];
                if (topic.EndsWith("/raw")) topic = topic.Substring(0, topic.Length - 4);
                string answer = "";
                lock (_topics)
                {
                    List<string> messages;
                    if (!_topics.TryGetValue(topic, out messages)) _topics[topic] = messages = new List<string>();
                    if (request[0] == "POST" || request[0] == "PUT")
                    {
                        messages.Add(Encoding.UTF8.GetString(body));
                        Interlocked.Increment(ref _posts);
                    }
                    else
                    {
                        answer = string.Join("\n", messages);
                        Interlocked.Increment(ref _polls);
                    }
                }
                byte[] payload = Encoding.UTF8.GetBytes(answer);
                byte[] header = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: " + payload.Length + "\r\nConnection: close\r\n\r\n");
                s.Write(header, 0, header.Length);
                s.Write(payload, 0, payload.Length);
            }
        }
        catch { }
    }

    private static string ReadHead(NetworkStream s)
    {
        List<byte> bytes = new List<byte>();
        while (bytes.Count < 16384)
        {
            int b = s.ReadByte();
            if (b < 0) return null;
            bytes.Add((byte)b);
            int n = bytes.Count;
            if (n >= 4 && bytes[n - 4] == '\r' && bytes[n - 3] == '\n' && bytes[n - 2] == '\r' && bytes[n - 1] == '\n')
                return Encoding.ASCII.GetString(bytes.ToArray());
        }
        return null;
    }

    public void Dispose()
    {
        try { _listener.Stop(); } catch { }
    }
}

/// <summary>A stand-in Host: signs viewers in like the real one, answers pings, and can be made to misbehave.</summary>
public sealed class FakeHost : IDisposable
{
    private readonly int _port;
    private readonly string _pin;
    private readonly string _machineName;
    private readonly object _lock = new object();
    private TcpListener _listener;
    private volatile bool _listening;
    private TcpClient _current;
    private NetworkStream _currentStream;
    private SecurityContext _currentSec;
    private SemaphoreSlim _currentWriteLock;
    private readonly List<TcpClient> _all = new List<TcpClient>();
    private int _accepted, _attempts, _connections, _open;

    /// <summary>New connections are accepted but get nothing, not even the sign-in challenge.</summary>
    public volatile bool SilentAfterAccept;
    public int ConnectionsAccepted { get { return Volatile.Read(ref _connections); } }
    /// <summary>Connections accepted and not yet closed by either side.</summary>
    public int OpenConnections { get { return Volatile.Read(ref _open); } }

    /// <summary>Sent instead of the default "OK|machine|USER|1|AES-256-HMAC-SHA256|vVERSION".</summary>
    public string AuthResultOverride;
    /// <summary>When set, sign-ins get "FAIL:" + this text, unencrypted, like the real Host's refusals.</summary>
    public volatile string FailText;
    /// <summary>False: stop answering pings and sending anything, but keep the socket open (a dead link).</summary>
    public volatile bool Responding = true;

    public int SessionsAccepted { get { return Volatile.Read(ref _accepted); } }
    public int SignInAttempts { get { return Volatile.Read(ref _attempts); } }

    public FakeHost(int port, string pin, string machineName)
    {
        _port = port;
        _pin = pin;
        _machineName = machineName;
        StartListening();
    }

    public void StartListening()
    {
        lock (_lock)
        {
            if (_listening) return;
            _listener = new TcpListener(IPAddress.Loopback, _port);
            _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _listener.Start();
            _listening = true;
            TcpListener l = _listener;
            Task.Run(() => AcceptLoopAsync(l));
        }
    }

    public void StopListening()
    {
        lock (_lock)
        {
            _listening = false;
            try { if (_listener != null) _listener.Stop(); } catch { }
        }
    }

    public void CloseSession()
    {
        lock (_lock)
        {
            try { if (_current != null) _current.Close(); } catch { }
            _current = null;
        }
    }

    private async Task AcceptLoopAsync(TcpListener l)
    {
        while (true)
        {
            TcpClient c;
            try { c = await l.AcceptTcpClientAsync(); }
            catch { return; }
            TcpClient client = c;
            Task serve = Task.Run(() => ServeAsync(client));
        }
    }

    /// <summary>
    /// Sends one Pong of <paramref name="payloadBytes"/> spread evenly over <paramref name="durationMs"/>, like a
    /// big screen frame on a 2G link: bytes keep arriving, but no packet completes until the end.
    /// </summary>
    public void SendSlowPacket(int payloadBytes, int durationMs)
    {
        NetworkStream s;
        SecurityContext sec;
        SemaphoreSlim wl;
        lock (_lock) { s = _currentStream; sec = _currentSec; wl = _currentWriteLock; }
        if (s == null) throw new InvalidOperationException("no session");
        byte[] payload = new byte[payloadBytes];
        BitConverter.GetBytes(Stopwatch.GetTimestamp()).CopyTo(payload, 0);
        wl.Wait();
        try
        {
            MemoryStream frame = new MemoryStream();
            TurboProtocol.SendPacketAsync(frame, new SemaphoreSlim(1, 1), PacketType.Pong, payload, CancellationToken.None, sec).Wait();
            byte[] bytes = frame.ToArray();
            const int pieces = 100;
            int per = bytes.Length / pieces + 1;
            for (int off = 0; off < bytes.Length; off += per)
            {
                s.Write(bytes, off, Math.Min(per, bytes.Length - off));
                s.Flush();
                Thread.Sleep(durationMs / pieces);
            }
        }
        finally
        {
            wl.Release();
        }
    }

    private async Task ServeAsync(TcpClient c)
    {
        Interlocked.Increment(ref _connections);
        Interlocked.Increment(ref _open);
        lock (_lock) { _all.Add(c); }
        try
        {
            NetworkStream s = c.GetStream();
            if (SilentAfterAccept)
            {
                // Hold the connection without a word until the other side gives up and closes it.
                byte[] one = new byte[1];
                while (await s.ReadAsync(one, 0, 1) > 0) { }
                return;
            }
            SemaphoreSlim wl = new SemaphoreSlim(1, 1);
            byte[] salt = new byte[16], chal = new byte[16];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create()) { rng.GetBytes(salt); rng.GetBytes(chal); }
            byte[] challenge = new byte[32];
            Buffer.BlockCopy(salt, 0, challenge, 0, 16);
            Buffer.BlockCopy(chal, 0, challenge, 16, 16);
            await TurboProtocol.SendPacketAsync(s, wl, PacketType.AuthChallenge, challenge, CancellationToken.None);

            KeyValuePair<PacketType, byte[]>? response = await TurboProtocol.ReadPacketAsync(s, CancellationToken.None);
            Interlocked.Increment(ref _attempts);
            if (response == null) { c.Close(); return; }

            string fail = FailText;
            SecurityContext sec = new SecurityContext(_pin, salt, chal);
            if (fail == null && !TurboProtocol.ConstantTimeEquals(response.Value.Value, sec.ComputeHandshakeProof(chal, "CLIENT_AUTH_V2")))
            {
                fail = "Invalid Access Key/PIN (Attempt 1/5)";
            }
            if (fail != null)
            {
                await TurboProtocol.SendPacketAsync(s, wl, PacketType.AuthResult, Encoding.UTF8.GetBytes("FAIL:" + fail), CancellationToken.None);
                Thread.Sleep(100);
                c.Close();
                return;
            }

            string ok = AuthResultOverride ?? string.Format("OK|{0}|USER|1|AES-256-HMAC-SHA256|v{1}", _machineName, TurboProtocol.AppVersion);
            await TurboProtocol.SendPacketAsync(s, wl, PacketType.AuthResult, Encoding.UTF8.GetBytes(ok), CancellationToken.None, sec);
            lock (_lock) { _current = c; _currentStream = s; _currentSec = sec; _currentWriteLock = wl; }
            Interlocked.Increment(ref _accepted);

            while (true)
            {
                KeyValuePair<PacketType, byte[]>? p = await TurboProtocol.ReadPacketAsync(s, CancellationToken.None, sec);
                if (p == null) break;
                if (p.Value.Key == PacketType.Ping && Responding)
                {
                    await TurboProtocol.SendPacketAsync(s, wl, PacketType.Pong, p.Value.Value, CancellationToken.None, sec);
                }
            }
        }
        catch { }
        finally
        {
            Interlocked.Decrement(ref _open);
            try { c.Close(); } catch { }
        }
    }

    public void Dispose()
    {
        StopListening();
        CloseSession();
        lock (_lock)
        {
            foreach (TcpClient c in _all) { try { c.Close(); } catch { } }
            _all.Clear();
        }
    }
}

/// <summary>A stand-in viewer: signs in to a Host, pings on request, and can fall silent.</summary>
public sealed class FakeViewer : IDisposable
{
    private TcpClient _tcp;
    private NetworkStream _stream;
    private SecurityContext _sec;
    private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);
    private readonly ManualResetEventSlim _closed = new ManualResetEventSlim(false);
    private volatile bool _silent;

    /// <summary>The Host's decrypted sign-in reply ("OK|machine|...").</summary>
    public string AuthText;

    public static FakeViewer Connect(string host, int port, string pin)
    {
        FakeViewer v = new FakeViewer();
        v._tcp = new TcpClient();
        v._tcp.Connect(host, port);
        v._stream = v._tcp.GetStream();
        byte[] chal = TurboProtocol.ReadPacketAsync(v._stream, CancellationToken.None).Result.Value.Value;
        byte[] salt = new byte[16], ch = new byte[16];
        Buffer.BlockCopy(chal, 0, salt, 0, 16);
        Buffer.BlockCopy(chal, 16, ch, 0, 16);
        v._sec = new SecurityContext(pin, salt, ch);
        TurboProtocol.SendPacketAsync(v._stream, v._writeLock, PacketType.AuthResponse, v._sec.ComputeHandshakeProof(ch, "CLIENT_AUTH_V2"), CancellationToken.None).Wait();
        byte[] res = TurboProtocol.ReadPacketAsync(v._stream, CancellationToken.None).Result.Value.Value;
        string clear = Encoding.UTF8.GetString(res);
        if (clear.StartsWith("FAIL:")) { v.Dispose(); throw new Exception(clear); }
        v.AuthText = Encoding.UTF8.GetString(v._sec.VerifyAndDecrypt(PacketType.AuthResult, res));
        Task.Run(() => v.DrainAsync());
        return v;
    }

    // Keeps reading (frames, pongs) so a Host-side close is noticed; reading sends nothing at the app level.
    private async Task DrainAsync()
    {
        try
        {
            while (await TurboProtocol.ReadPacketAsync(_stream, CancellationToken.None, _sec) != null) { }
        }
        catch { }
        _closed.Set();
    }

    public void Ping()
    {
        if (_silent) return;
        TurboProtocol.SendPacketAsync(_stream, _writeLock, PacketType.Ping, BitConverter.GetBytes(Stopwatch.GetTimestamp()), CancellationToken.None, _sec).Wait();
    }

    /// <summary>From now on nothing more is sent to the Host.</summary>
    public void GoSilent() { _silent = true; }

    public bool WaitForClose(int ms) { return _closed.Wait(ms); }

    public void Dispose()
    {
        try { _tcp.Close(); } catch { }
    }
}

/// <summary>Reads and operates app windows through UI Automation (no mouse or keyboard input).</summary>
public static class Uia
{
    private static IEnumerable<AutomationElement> All(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) yield break;
        AutomationElementCollection all;
        try { all = AutomationElement.FromHandle(hwnd).FindAll(TreeScope.Descendants, Condition.TrueCondition); }
        catch { yield break; }
        foreach (AutomationElement el in all) yield return el;
    }

    private static string NameOf(AutomationElement el)
    {
        try { return el.Current.Name ?? ""; } catch { return ""; }
    }

    /// <summary>
    /// Puts the keyboard focus in the edit box named <paramref name="name"/>, as a click would, without sending any
    /// keystroke. False when there is no such box or Windows keeps the focus elsewhere (another window in front).
    /// </summary>
    public static bool FocusEdit(IntPtr hwnd, string name)
    {
        foreach (AutomationElement el in All(hwnd))
        {
            try
            {
                if (el.Current.ControlType != ControlType.Edit || NameOf(el) != name) continue;
                el.SetFocus();
                Thread.Sleep(200);
                return el.Current.HasKeyboardFocus;
            }
            catch { return false; }
        }
        return false;
    }

    /// <summary>Name of the control that has the keyboard focus right now, anywhere on the desktop.</summary>
    public static string FocusedName()
    {
        try
        {
            AutomationElement f = AutomationElement.FocusedElement;
            return f == null ? "" : NameOf(f);
        }
        catch { return ""; }
    }

    /// <summary>True when the window's UI thread answers a plain message within <paramref name="timeoutMs"/>.</summary>
    public static bool Responds(IntPtr hwnd, int timeoutMs)
    {
        IntPtr result;
        return SendMessageTimeout(hwnd, 0x0000 /*WM_NULL*/, IntPtr.Zero, IntPtr.Zero, 0x0002 /*SMTO_ABORTIFHUNG*/, (uint)timeoutMs, out result) != IntPtr.Zero;
    }

    /// <summary>True when Windows itself considers the window "Not Responding" (no messages taken for 5 s).</summary>
    public static bool IsHung(IntPtr hwnd)
    {
        return IsHungAppWindow(hwnd);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, IntPtr l, uint flags, uint timeoutMs, out IntPtr result);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsHungAppWindow(IntPtr h);

    /// <summary>Names of every visible control in the window, leaving out list items (log lines, file lists).</summary>
    public static List<string> Names(IntPtr hwnd)
    {
        List<string> names = new List<string>();
        foreach (AutomationElement el in All(hwnd))
        {
            try { if (el.Current.ControlType == ControlType.ListItem) continue; } catch { continue; }
            string n = NameOf(el);
            if (n.Length > 0) names.Add(n);
        }
        return names;
    }

    /// <summary>The shown value of every drop-down list in the window.</summary>
    public static List<string> ComboValues(IntPtr hwnd)
    {
        List<string> values = new List<string>();
        foreach (AutomationElement el in All(hwnd))
        {
            try
            {
                if (el.Current.ControlType != ControlType.ComboBox) continue;
                object p;
                if (el.TryGetCurrentPattern(ValuePattern.Pattern, out p)) { values.Add(((ValuePattern)p).Current.Value ?? ""); continue; }
                if (el.TryGetCurrentPattern(SelectionPattern.Pattern, out p))
                {
                    foreach (AutomationElement sel in ((SelectionPattern)p).Current.GetSelection()) values.Add(NameOf(sel));
                }
            }
            catch { }
        }
        return values;
    }

    /// <summary>True when some visible control's name contains <paramref name="text"/> (ignoring case).</summary>
    public static bool HasName(IntPtr hwnd, string text)
    {
        foreach (string n in Names(hwnd))
        {
            if (n.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        }
        return false;
    }

    public static string FindText(IntPtr hwnd, string startsWith)
    {
        foreach (AutomationElement el in All(hwnd))
        {
            string n = NameOf(el);
            if (n.StartsWith(startsWith)) return n;
        }
        return null;
    }

    private static AutomationElement Find(IntPtr hwnd, string nameContains, AutomationPattern pattern)
    {
        foreach (AutomationElement el in All(hwnd))
        {
            if (!NameOf(el).Contains(nameContains)) continue;
            object p;
            if (el.TryGetCurrentPattern(pattern, out p)) return el;
        }
        return null;
    }

    public static bool Invoke(IntPtr hwnd, string nameContains)
    {
        AutomationElement el = Find(hwnd, nameContains, InvokePattern.Pattern);
        if (el == null) return false;
        ((InvokePattern)el.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
        return true;
    }

    public static bool Toggle(IntPtr hwnd, string nameContains)
    {
        AutomationElement el = Find(hwnd, nameContains, TogglePattern.Pattern);
        if (el == null) return false;
        ((TogglePattern)el.GetCurrentPattern(TogglePattern.Pattern)).Toggle();
        return true;
    }

    public static bool? IsChecked(IntPtr hwnd, string nameContains)
    {
        AutomationElement el = Find(hwnd, nameContains, TogglePattern.Pattern);
        if (el == null) return null;
        return ((TogglePattern)el.GetCurrentPattern(TogglePattern.Pattern)).Current.ToggleState == ToggleState.On;
    }

    public static string WindowTitle(Process p)
    {
        try { p.Refresh(); return p.MainWindowTitle ?? ""; } catch { return ""; }
    }

    public static IntPtr Window(Process p)
    {
        try { p.Refresh(); return p.MainWindowHandle; } catch { return IntPtr.Zero; }
    }

    public static List<string> ListItems(IntPtr hwnd)
    {
        List<string> items = new List<string>();
        foreach (AutomationElement el in All(hwnd))
        {
            try { if (el.Current.ControlType == ControlType.ListItem) items.Add(NameOf(el)); } catch { }
        }
        return items;
    }
}
