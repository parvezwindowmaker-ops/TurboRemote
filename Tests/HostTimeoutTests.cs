using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TurboCore;

// The real Host closes a session whose viewer went silent, keeps live ones, and takes new viewers meanwhile.
class HostTimeoutTests
{
    static int Main(string[] args)
    {
        TestKit.Init(args);
        int port = TestKit.FreePort();
        Process host = TestKit.StartApp("--host --port " + port + " --pin 2580");
        try
        {
            FakeViewer a = null;
            TestKit.WaitUntil(() => { try { a = FakeViewer.Connect("127.0.0.1", port, "2580"); return true; } catch { return false; } }, 20000, 500);
            if (a == null) { TestKit.Check("viewer A can connect to the Host", false, null); return TestKit.Summary(); }
            FakeViewer c = FakeViewer.Connect("127.0.0.1", port, "2580");

            CancellationTokenSource stopC = new CancellationTokenSource();
            Task cPings = Task.Run(() =>
            {
                while (!stopC.IsCancellationRequested) { try { c.Ping(); } catch { } Thread.Sleep(1000); }
            });

            for (int i = 0; i < 5; i++) { a.Ping(); Thread.Sleep(1000); }
            a.Ping();
            Stopwatch sinceLastPing = Stopwatch.StartNew();
            a.GoSilent();

            Thread.Sleep(5000);
            Stopwatch bTime = Stopwatch.StartNew();
            FakeViewer b = null;
            try { b = FakeViewer.Connect("127.0.0.1", port, "2580"); } catch (Exception ex) { Console.WriteLine("      B: " + ex.Message); }
            TestKit.Check("a new viewer signs in at once while a silent session is still open", b != null && bTime.ElapsedMilliseconds < 5000, bTime.ElapsedMilliseconds + " ms");

            bool closed = a.WaitForClose(60000);
            long closedAfter = sinceLastPing.ElapsedMilliseconds;
            TestKit.Check("the silent viewer's session is closed 45 s after its last packet", closed && closedAfter >= 44000 && closedAfter <= 55000,
                closed ? "closed after " + closedAfter + " ms" : "still open after " + closedAfter + " ms");

            string logLine = Uia.ListItems(Uia.Window(host)).FirstOrDefault(l => l.Contains("No data from the viewer for 45 s"));
            TestKit.Check("the Host log says why it closed the session", logLine != null, logLine);

            TestKit.WaitUntil(() => false, Math.Max(0, 70000 - (int)closedAfter - 11000));
            stopC.Cancel();
            cPings.Wait(3000);
            TestKit.Check("a viewer that keeps pinging stays connected (70 s)", !c.WaitForClose(0), null);

            TailscaleStatus ts = TailscaleInfo.Query();
            if (ts.IsRunning)
            {
                bool viaIp = false, viaName = false;
                try { using (FakeViewer t = FakeViewer.Connect(ts.IPv4, port, "2580")) viaIp = t.AuthText.StartsWith("OK|"); } catch (Exception ex) { Console.WriteLine("      via IP: " + ex.Message); }
                try { using (FakeViewer t = FakeViewer.Connect(ts.DnsName, port, "2580")) viaName = t.AuthText.StartsWith("OK|"); } catch (Exception ex) { Console.WriteLine("      via name: " + ex.Message); }
                TestKit.Check("signs in through this PC's Tailscale address " + ts.IPv4, viaIp, null);
                TestKit.Check("signs in through this PC's Tailscale name " + ts.DnsName, viaName, null);
            }
            else
            {
                Console.WriteLine("skipped  Tailscale path: Tailscale is not running on this PC");
            }

            a.Dispose();
            c.Dispose();
            if (b != null) b.Dispose();
        }
        finally
        {
            TestKit.Kill(host);
        }
        return TestKit.Summary();
    }
}
