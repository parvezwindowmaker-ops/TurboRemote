using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Threading;
using TurboCore;

// The Host on a PC where Tailscale runs: shows the permanent address and keeps the public tunnel off,
// until the user ticks the box. Runs in TestKit's sandbox: the "tunnel" is the fake ssh.exe and the cloud
// directory is the local stand-in, so nothing reaches pinggy or ntfy.sh and the user's settings stay as they are.
class HostTunnelTests
{
    sealed class SshProcess { public int Pid; public string Path; }

    static List<SshProcess> SshFor(int port)
    {
        List<SshProcess> found = new List<SshProcess>();
        using (ManagementObjectSearcher s = new ManagementObjectSearcher("SELECT ProcessId, CommandLine, ExecutablePath FROM Win32_Process WHERE Name='ssh.exe'"))
        {
            foreach (ManagementObject o in s.Get())
            {
                string cmd = o["CommandLine"] as string;
                if (cmd != null && cmd.Contains("-R0:127.0.0.1:" + port + " "))
                    found.Add(new SshProcess { Pid = Convert.ToInt32(o["ProcessId"]), Path = o["ExecutablePath"] as string ?? "" });
            }
        }
        return found;
    }

    static string ReadOrNull(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; } catch { return "(unreadable)"; }
    }

    static string StrongKey() { return "Tk" + Guid.NewGuid().ToString("N").Substring(0, 12) + "#7Q"; }

    /// <summary>A Host with its own data folder, whose settings say "publicTunnel=on".</summary>
    static Process StartHostWithTunnelOn(string name, int port, Dictionary<string, string> env)
    {
        string dataDir = Path.Combine(TestKit.SandboxDir, name);
        Directory.CreateDirectory(dataDir);
        File.WriteAllText(Path.Combine(dataDir, "settings.txt"), "publicTunnel=on" + Environment.NewLine);
        env["TURBOREMOTE_DATA_DIR"] = dataDir;
        return TestKit.StartApp("--host --port " + port + " --pin \"" + StrongKey() + "\"", env);
    }

    static void KillTunnels(int port)
    {
        foreach (SshProcess p in SshFor(port)) { try { Process.GetProcessById(p.Pid).Kill(); } catch { } }
    }

    /// <summary>Starting the tunnel never blocks the Host window, and a missing OpenSSH is not reported over and over.</summary>
    static void TunnelStartChecks()
    {
        // Making the tunnel's key can take a while on first use (ssh-keygen gets up to 15 s). A second start
        // meanwhile (the box clicked again, a Tailscale check) must not launch a second tunnel: the first ssh.exe
        // would be orphaned, keeping a public relay open after TurboRemote is gone.
        int port = TestKit.FreePort();
        Process host = StartHostWithTunnelOn("slow-keygen", port, new Dictionary<string, string> { { "FAKE_SSH_KEYGEN_DELAY_MS", "8000" } });
        try
        {
            IntPtr wnd = IntPtr.Zero;
            TestKit.WaitUntil(() => (wnd = Uia.Window(host)) != IntPtr.Zero, 10000);
            TestKit.WaitUntil(() => Uia.IsChecked(Uia.Window(host), "Also allow viewers without Tailscale") == true, 10000);
            Thread.Sleep(3000); // the key is being made now
            Uia.Toggle(Uia.Window(host), "Also allow viewers without Tailscale");
            Thread.Sleep(500);
            Uia.Toggle(Uia.Window(host), "Also allow viewers without Tailscale");
            bool started = TestKit.WaitUntil(() => SshFor(port).Count > 0, 20000);
            Thread.Sleep(10000); // long enough for a second key wait to finish too
            int tunnels = SshFor(port).Count;
            TestKit.Check("the tunnel starts once its key is made, and only once despite a second start meanwhile",
                started && tunnels == 1, tunnels + " ssh.exe for port " + port);
        }
        finally
        {
            TestKit.Kill(host);
            KillTunnels(port);
        }

        // No OpenSSH on this PC: say so once, not again at every Tailscale check (every 30 s, or "Check again").
        port = TestKit.FreePort();
        string noSsh = Path.Combine(TestKit.SandboxDir, "no-openssh");
        Directory.CreateDirectory(noSsh);
        host = StartHostWithTunnelOn("no-openssh-data", port, new Dictionary<string, string> { { "TURBOREMOTE_OPENSSH_DIR", noSsh } });
        try
        {
            IntPtr wnd = IntPtr.Zero;
            TestKit.WaitUntil(() => (wnd = Uia.Window(host)) != IntPtr.Zero, 10000);
            Func<List<string>> problems = () => Uia.ListItems(Uia.Window(host)).Where(l => l.Contains("OpenSSH")).ToList();
            TestKit.WaitUntil(() => problems().Count > 0, 15000);
            Stopwatch sinceFirst = Stopwatch.StartNew();
            bool button = Uia.Invoke(wnd, "Check again");
            TestKit.Check("the Host has a 'Check again' button for Tailscale", button, null);
            Thread.Sleep(3000);
            Uia.Invoke(wnd, "Check again");
            // Past the next 30 s Tailscale check too.
            Thread.Sleep((int)Math.Max(0, 35000 - sinceFirst.ElapsedMilliseconds));
            List<string> lines = problems();
            TestKit.Check("a missing OpenSSH is reported once, not at every check", lines.Count == 1, lines.Count + " lines: " + string.Join(" | ", lines));
        }
        finally
        {
            TestKit.Kill(host);
        }
    }

    static int Main(string[] args)
    {
        TestKit.Init(args);
        TunnelStartChecks();

        TailscaleStatus ts = TailscaleInfo.Query();
        if (!ts.IsRunning)
        {
            Console.WriteLine("skipped  Tailscale is not running on this PC");
            return TestKit.Summary();
        }

        string realSettings = Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TurboRemote"), "settings.txt");
        string realBefore = ReadOrNull(realSettings);

        string key = "Tk" + Guid.NewGuid().ToString("N").Substring(0, 12) + "#7Q";
        TestKit.Check("test key is strong enough for Internet use", AccessKeyStore.WhyTooWeakForInternet(key) == null, AccessKeyStore.WhyTooWeakForInternet(key));

        int port = TestKit.FreePort();
        TestKit.CloudDirectory.ResetCounts(); // the Hosts above published too
        Process host = TestKit.StartApp("--host --port " + port + " --pin \"" + key + "\"");
        try
        {
            IntPtr wnd = IntPtr.Zero;
            TestKit.WaitUntil(() => (wnd = Uia.Window(host)) != IntPtr.Zero, 10000);
            TestKit.WaitUntil(() => Uia.FindText(wnd, "Tailscale ON") != null, 15000);
            string line = Uia.FindText(wnd, "Tailscale ON");
            TestKit.Check("Host shows its permanent Tailscale address", line != null && line.Contains(ts.DnsName) && line.Contains(ts.IPv4), line);

            Thread.Sleep(6000); // past the tunnel's usual start-up time
            string box = Uia.FindText(wnd, "Also allow viewers without Tailscale");
            TestKit.Check("public tunnel box is unticked and automatic while Tailscale runs",
                Uia.IsChecked(wnd, "Also allow viewers without Tailscale") == false && box != null && box.Contains("(automatic)"), box);
            TestKit.Check("no public tunnel was opened", SshFor(port).Count == 0, SshFor(port).Count + " ssh.exe for port " + port);
            TestKit.Check("the Host published its addresses to the local stand-in, not ntfy.sh",
                TestKit.WaitUntil(() => TestKit.CloudDirectory.Posts > 0, 10000), TestKit.CloudDirectory.Posts + " posts");

            Uia.Toggle(wnd, "Also allow viewers without Tailscale");
            bool up = TestKit.WaitUntil(() => SshFor(port).Count > 0, 10000);
            TestKit.Check("ticking the box opens the tunnel and remembers 'on'", up && TurboSettings.Get("publicTunnel", "") == "on",
                "ssh running: " + up + ", setting: " + TurboSettings.Get("publicTunnel", "(none)"));
            List<SshProcess> ssh = SshFor(port);
            TestKit.Check("the tunnel is the sandbox's fake ssh.exe, not the real one",
                ssh.Count > 0 && ssh.TrueForAll(p => p.Path.StartsWith(TestKit.FakeOpenSshDir, StringComparison.OrdinalIgnoreCase)),
                string.Join(", ", ssh.ConvertAll(p => p.Path)));

            Uia.Toggle(wnd, "Also allow viewers without Tailscale");
            bool gone = TestKit.WaitUntil(() => SshFor(port).Count == 0, 5000);
            TestKit.Check("unticking closes it and remembers 'off'", gone && TurboSettings.Get("publicTunnel", "") == "off",
                "ssh gone: " + gone + ", setting: " + TurboSettings.Get("publicTunnel", "(none)"));
        }
        finally
        {
            TestKit.Kill(host);
            foreach (SshProcess p in SshFor(port)) { try { Process.GetProcessById(p.Pid).Kill(); } catch { } }
        }

        TestKit.Check("the user's own settings.txt was not touched", ReadOrNull(realSettings) == realBefore,
            "before: " + (realBefore ?? "(none)") + ", after: " + (ReadOrNull(realSettings) ?? "(none)"));
        return TestKit.Summary();
    }
}
