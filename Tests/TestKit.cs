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
    private static readonly string StateDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TurboRemote");
    private static bool _keyFileExisted, _settingsExisted;

    /// <summary>Path of the TurboRemote.exe under test (the test program's first argument).</summary>
    public static string ExePath;

    /// <summary>Call first in Main: remembers which per-user files existed, so Summary can remove the rest.</summary>
    public static void Init(string[] args)
    {
        ExePath = args.Length > 0 ? args[0] : "TurboRemote.exe";
        _keyFileExisted = File.Exists(Path.Combine(StateDir, "access_key.txt"));
        _settingsExisted = File.Exists(Path.Combine(StateDir, "settings.txt"));
    }

    public static void Check(string name, bool ok, string detail = null)
    {
        Console.WriteLine("{0}  {1}{2}", ok ? "PASS" : "FAIL", name, string.IsNullOrEmpty(detail) ? "" : "\n      " + detail);
        if (ok) _passes++; else _fails++;
    }

    /// <summary>Prints the verdict, removes per-user files the test created, returns the exit code.</summary>
    public static int Summary()
    {
        if (!_keyFileExisted) TryDelete(Path.Combine(StateDir, "access_key.txt"));
        if (!_settingsExisted) TryDelete(Path.Combine(StateDir, "settings.txt"));
        Console.WriteLine(_fails == 0 ? "ALL CHECKS PASSED" : _fails + " CHECK(S) FAILED");
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

    public static void Kill(Process p)
    {
        try { if (p != null && !p.HasExited) p.Kill(); } catch { }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
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
    private int _accepted, _attempts;

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

    private async Task ServeAsync(TcpClient c)
    {
        try
        {
            NetworkStream s = c.GetStream();
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
            lock (_lock) { _current = c; }
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
            try { c.Close(); } catch { }
        }
    }

    public void Dispose()
    {
        StopListening();
        CloseSession();
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
