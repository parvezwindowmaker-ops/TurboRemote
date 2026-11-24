using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using TurboCore;

// On a 2G link one big screen frame can take longer than the 20 s dead-link limit to arrive. Bytes are still
// flowing, so the viewer must not call that a dead link (and reconnect, get another big frame, and loop).
class SlowLinkTests
{
    static int Main(string[] args)
    {
        TestKit.Init(args);
        int port = TestKit.FreePort();
        FakeHost host = new FakeHost(port, "2580", "FAKE-PC");
        Process viewer = TestKit.StartApp("--client --connect 127.0.0.1 --port " + port + " --pin 2580");
        try
        {
            bool connected = TestKit.WaitUntil(() => host.SessionsAccepted == 1 && Uia.WindowTitle(viewer).Contains("FAKE-PC"), 20000);
            TestKit.Check("viewer connects to the fake Host", connected, Uia.WindowTitle(viewer));
            if (!connected) return TestKit.Summary();

            Task trickle = Task.Run(() => { try { host.SendSlowPacket(300000, 30000); } catch { } });
            bool reconnected = TestKit.WaitUntil(() => host.SessionsAccepted > 1 || Uia.FindText(Uia.Window(viewer), "✖ Stop reconnecting") != null, 32000);
            trickle.Wait(5000);
            TestKit.Check("a 300 KB packet arriving over 30 s does not count as a dead link", !reconnected,
                host.SessionsAccepted + " session(s); status: " + Uia.FindText(Uia.Window(viewer), "●"));
            bool live = TestKit.WaitUntil(() => (Uia.FindText(Uia.Window(viewer), "●") ?? "").Contains("LIVE"), 5000);
            TestKit.Check("...and the session is still live afterwards", live && host.SessionsAccepted == 1, Uia.FindText(Uia.Window(viewer), "●"));
        }
        finally
        {
            TestKit.Kill(viewer);
            host.Dispose();
        }
        return TestKit.Summary();
    }
}
