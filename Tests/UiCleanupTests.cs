using System;
using System.Diagnostics;
using System.Linq;
using TurboCore;

// The three windows show only what is needed, in plain words (docs/superpowers/specs/2026-11-24-ui-cleanup-design.md).
class UiCleanupTests
{
    static void Must(IntPtr wnd, string window, params string[] texts)
    {
        foreach (string t in texts) TestKit.Check(window + " shows '" + t + "'", Uia.HasName(wnd, t), null);
    }

    static void MustNot(IntPtr wnd, string window, params string[] texts)
    {
        foreach (string t in texts) TestKit.Check(window + " no longer shows '" + t + "'", !Uia.HasName(wnd, t), null);
    }

    static IntPtr WaitWindow(Process p)
    {
        IntPtr w = IntPtr.Zero;
        TestKit.WaitUntil(() => (w = Uia.Window(p)) != IntPtr.Zero, 10000);
        System.Threading.Thread.Sleep(1500);
        return Uia.Window(p);
    }

    static int Main(string[] args)
    {
        TestKit.Init(args);

        Process launcher = TestKit.StartApp("");
        try
        {
            IntPtr w = WaitWindow(launcher);
            Must(w, "launcher", "Share this PC", "Control another PC");
            MustNot(w, "launcher", "Launch Both");
        }
        finally { TestKit.Kill(launcher); }

        int hostPort = TestKit.FreePort();
        Process host = TestKit.StartApp("--host --port " + hostPort + " --pin 2580");
        try
        {
            IntPtr w = WaitWindow(host);
            TestKit.WaitUntil(() => Uia.HasName(Uia.Window(host), "Stop sharing"), 10000);
            w = Uia.Window(host);
            Must(w, "Host", "Host ID", Environment.MachineName, "Copy", "New key", "Stop sharing",
                 "Start sharing automatically when I sign in to Windows", "Also allow viewers without Tailscale");
            MustNot(w, "Host", "UPnP", "Firewall", "Reverse", "Outbound", "Publish", "TCP Listen Port", "Runs on Any Windows PC");
        }
        finally { TestKit.Kill(host); }

        Process idle = TestKit.StartApp("--client");
        try
        {
            IntPtr w = WaitWindow(idle);
            Must(w, "viewer", "Host ID or address", "Access key", "⚡ Connect", "Remote Desktop", "PowerShell", "Files");
            TestKit.Check("viewer quality starts at 'Auto'", Uia.ComboValues(w).Any(v => v.StartsWith("Auto")), string.Join(" | ", Uia.ComboValues(w)));
            MustNot(w, "viewer", "Port:", "Listen Mode", "Gray", "Task Mgr", "Win+D", "Keyframe", "Delta-Tile", "Zero-Bandwidth", "Ultra-Fast");
            MustNot(w, "disconnected viewer", "Refresh", "Send clipboard", "Lock host input");
        }
        finally { TestKit.Kill(idle); }

        int p1 = TestKit.FreePort();
        using (FakeHost oneScreen = new FakeHost(p1, "2580", "FAKE-PC"))
        {
            Process v = TestKit.StartApp("--client --connect 127.0.0.1 --port " + p1 + " --pin 2580");
            try
            {
                TestKit.WaitUntil(() => oneScreen.SessionsAccepted == 1 && Uia.WindowTitle(v).Contains("FAKE-PC"), 20000);
                IntPtr w = Uia.Window(v);
                Must(w, "connected viewer", "Refresh", "Send clipboard", "Get clipboard", "Lock host input", "Restart host as admin");
                // A drop-down shows its choice as its value, not its name.
                TestKit.Check("viewer connected to a 1-screen Host has no screen picker",
                    !Uia.ComboValues(w).Any(c => c.StartsWith("Screen")), string.Join(" | ", Uia.ComboValues(w)));
            }
            finally { TestKit.Kill(v); }
        }

        int p2 = TestKit.FreePort();
        using (FakeHost twoScreens = new FakeHost(p2, "2580", "FAKE-PC") { AuthResultOverride = "OK|FAKE-PC|USER|2|AES-256-HMAC-SHA256|v" + TurboProtocol.AppVersion })
        {
            Process v = TestKit.StartApp("--client --connect 127.0.0.1 --port " + p2 + " --pin 2580");
            try
            {
                TestKit.WaitUntil(() => twoScreens.SessionsAccepted == 1 && Uia.WindowTitle(v).Contains("FAKE-PC"), 20000);
                IntPtr w = Uia.Window(v);
                TestKit.Check("viewer connected to a 2-screen Host shows the screen picker at 'Screen 1'",
                    Uia.ComboValues(w).Contains("Screen 1"), string.Join(" | ", Uia.ComboValues(w)));
            }
            finally { TestKit.Kill(v); }
        }

        return TestKit.Summary();
    }
}
