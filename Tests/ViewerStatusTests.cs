using System;
using System.Diagnostics;
using System.IO;
using TurboCore;

// The viewer says whether Tailscale is set up on this PC, and points to it when a Host ID cannot be reached.
class ViewerStatusTests
{
    static int Main(string[] args)
    {
        TestKit.Init(args);

        ProcessStartInfo psi = new ProcessStartInfo(TestKit.ExePath, "--self-test") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
        using (Process p = Process.Start(psi))
        {
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(60000);
            TestKit.Check("self-test passes", output.StartsWith("SELF_TEST_PASSED"), output.Trim());
        }

        bool tailscaleRunning = File.Exists(TailscaleInfo.DefaultCliPath) && TailscaleInfo.Query().IsRunning;

        Process idle = TestKit.StartApp("--client");
        try
        {
            IntPtr wnd = IntPtr.Zero;
            TestKit.WaitUntil(() => (wnd = Uia.Window(idle)) != IntPtr.Zero, 10000);
            TestKit.Check("viewer title shows v2026.11.24", Uia.WindowTitle(idle).Contains("v2026.11.24"), Uia.WindowTitle(idle));
            if (tailscaleRunning)
            {
                bool shown = TestKit.WaitUntil(() => (Uia.FindText(wnd, "●") ?? "").Contains("Tailscale: ON"), 15000);
                TestKit.Check("disconnected viewer shows that Tailscale is on", shown, Uia.FindText(wnd, "●"));
            }
            else
            {
                Console.WriteLine("skipped  Tailscale is not running on this PC");
            }
        }
        finally
        {
            TestKit.Kill(idle);
        }

        Process failing = TestKit.StartApp("--client --connect NO-SUCH-PC-12345 --port " + TestKit.FreePort() + " --pin 2580");
        try
        {
            IntPtr wnd = IntPtr.Zero;
            TestKit.WaitUntil(() => (wnd = Uia.Window(failing)) != IntPtr.Zero, 10000);
            bool hint = TestKit.WaitUntil(() => (Uia.FindText(wnd, "●") ?? "").Contains("install Tailscale on both PCs"), 20000);
            TestKit.Check("a Host ID that cannot be reached points to Tailscale", hint, Uia.FindText(wnd, "●"));
            TestKit.Check("...and no reconnect loop starts for a first connect that failed", Uia.FindText(wnd, "✖ Stop reconnecting") == null, null);
        }
        finally
        {
            TestKit.Kill(failing);
        }

        int port = TestKit.FreePort();
        Process host = TestKit.StartApp("--host --port " + port + " --pin 2580");
        try
        {
            TestKit.WaitUntil(() => Uia.WindowTitle(host).Length > 0, 10000);
            TestKit.Check("Host title shows v2026.11.24", Uia.WindowTitle(host).Contains("v2026.11.24"), Uia.WindowTitle(host));
        }
        finally
        {
            TestKit.Kill(host);
        }

        return TestKit.Summary();
    }
}
