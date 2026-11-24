using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Threading;
using TurboCore;

// The Host on a PC where Tailscale runs: shows the permanent address and keeps the public tunnel off,
// until the user ticks the box.
class HostTunnelTests
{
    static List<int> SshFor(int port)
    {
        List<int> pids = new List<int>();
        using (ManagementObjectSearcher s = new ManagementObjectSearcher("SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name='ssh.exe'"))
        {
            foreach (ManagementObject o in s.Get())
            {
                string cmd = o["CommandLine"] as string;
                if (cmd != null && cmd.Contains("-R0:127.0.0.1:" + port + " ")) pids.Add(Convert.ToInt32(o["ProcessId"]));
            }
        }
        return pids;
    }

    static int Main(string[] args)
    {
        TestKit.Init(args);
        TailscaleStatus ts = TailscaleInfo.Query();
        if (!ts.IsRunning)
        {
            Console.WriteLine("skipped  Tailscale is not running on this PC");
            return TestKit.Summary();
        }

        string settings = TurboSettings.FilePath;
        string savedSettings = File.Exists(settings) ? File.ReadAllText(settings) : null;
        if (savedSettings != null) File.Delete(settings);

        string key = "Tk" + Guid.NewGuid().ToString("N").Substring(0, 12) + "#7Q";
        TestKit.Check("test key is strong enough for Internet use", AccessKeyStore.WhyTooWeakForInternet(key) == null, AccessKeyStore.WhyTooWeakForInternet(key));

        int port = TestKit.FreePort();
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

            Uia.Toggle(wnd, "Also allow viewers without Tailscale");
            bool up = TestKit.WaitUntil(() => SshFor(port).Count > 0, 10000);
            TestKit.Check("ticking the box opens the tunnel and remembers 'on'", up && TurboSettings.Get("publicTunnel", "") == "on",
                "ssh running: " + up + ", setting: " + TurboSettings.Get("publicTunnel", "(none)"));

            Uia.Toggle(wnd, "Also allow viewers without Tailscale");
            bool gone = TestKit.WaitUntil(() => SshFor(port).Count == 0, 5000);
            TestKit.Check("unticking closes it and remembers 'off'", gone && TurboSettings.Get("publicTunnel", "") == "off",
                "ssh gone: " + gone + ", setting: " + TurboSettings.Get("publicTunnel", "(none)"));
        }
        finally
        {
            TestKit.Kill(host);
            foreach (int pid in SshFor(port)) { try { Process.GetProcessById(pid).Kill(); } catch { } }
            if (savedSettings != null) File.WriteAllText(settings, savedSettings);
        }
        return TestKit.Summary();
    }
}
