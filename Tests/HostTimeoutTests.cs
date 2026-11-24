using System;
using System.Diagnostics;
using System.Linq;
using System.Net.Sockets;
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

            // A connection that takes the challenge and never answers it (a port scanner, a stuck client). Through
            // the tunnel it also kept a tunnel the Host no longer wants open, for ever.
            Task<long> mute = Task.Run(() =>
            {
                using (TcpClient raw = new TcpClient())
                {
                    raw.Connect("127.0.0.1", port);
                    NetworkStream ns = raw.GetStream();
                    ns.ReadTimeout = 60000;
                    Stopwatch open = Stopwatch.StartNew();
                    byte[] buf = new byte[4096];
                    try { while (ns.Read(buf, 0, buf.Length) > 0) { } }
                    catch { return -1L; } // still open after 60 s
                    return open.ElapsedMilliseconds;
                }
            });

            CancellationTokenSource stopC = new CancellationTokenSource();
            Task cPings = Task.Run(() =>
            {
                while (!stopC.IsCancellationRequested) { try { c.Ping(); } catch { } Thread.Sleep(1000); }
            });

            for (int i = 0; i < 5; i++) { a.Ping(); Thread.Sleep(1000); }
            a.Ping();
            Stopwatch sinceLastPing = Stopwatch.StartNew();
            a.GoSilent();

            // B signs in while A has been silent only 2 s: A is not yet known to be dead, so it must stay until
            // its 45 s watchdog (checked below).
            Thread.Sleep(2000);
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

            // A viewer that reconnects after a dead link: its old session on the Host is silent (but not yet 45 s).
            // It must be closed the moment the new session signs in, so the keys and buttons it still held are
            // released at once instead of staying pressed for up to ~25 s into the new session. C keeps pinging.
            FakeViewer e = FakeViewer.Connect("127.0.0.1", port, "2580");
            for (int i = 0; i < 3; i++) { e.Ping(); Thread.Sleep(1000); }
            e.GoSilent();
            Thread.Sleep(7000);
            FakeViewer f = FakeViewer.Connect("127.0.0.1", port, "2580");
            bool eClosed = e.WaitForClose(3000);
            TestKit.Check("a new sign-in closes a session that has been silent for over 5 s", eClosed, null);
            string siblingLine = Uia.ListItems(Uia.Window(host)).FirstOrDefault(l => l.Contains("silent for"));
            TestKit.Check("...and the Host log says so", siblingLine != null, siblingLine);
            f.Dispose();
            e.Dispose();

            long muteClosedAfter = mute.Wait(70000) ? mute.Result : -1;
            TestKit.Check("a connection that never signs in is closed after 20 s", muteClosedAfter >= 18000 && muteClosedAfter <= 30000,
                muteClosedAfter < 0 ? "still open after 60 s" : "closed after " + muteClosedAfter + " ms");
            string muteLine = Uia.ListItems(Uia.Window(host)).FirstOrDefault(l => l.Contains("did not sign in"));
            TestKit.Check("...and the Host log says so", muteLine != null, muteLine);

            stopC.Cancel();
            cPings.Wait(3000);
            TestKit.Check("a viewer that keeps pinging stays connected (over 60 s, through two other sign-ins)", !c.WaitForClose(0), null);

            TailscaleStatus ts = TailscaleInfo.Query();
            if (ts.IsRunning)
            {
                var byName = SmartRouteEngine.RaceFastestConnectionAsync(ts.DnsName, port, "2580", CancellationToken.None).Result;
                TestKit.Check("connecting by Tailscale name is labelled as the permanent Tailscale route", byName.Value.RouteLabel == "Tailscale (permanent)", byName.Value.RouteLabel);
                byName.Key.Close();
                var byHostId = SmartRouteEngine.RaceFastestConnectionAsync(Environment.MachineName, port, "2580", CancellationToken.None).Result;
                TestKit.Check("connecting by Host ID is not labelled as the public tunnel", byHostId.Value.RouteLabel != "Public Internet / WAN Tunnel", byHostId.Value.RouteLabel);
                byHostId.Key.Close();

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
