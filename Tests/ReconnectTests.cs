using System;
using System.Diagnostics;
using System.Threading;
using TurboCore;

// The real viewer against fake Hosts: dead links are noticed, dropped sessions come back by themselves,
// and a wrong key is never retried.
class ReconnectTests
{
    static Process _viewer;
    static IntPtr Wnd { get { return Uia.Window(_viewer); } }
    static bool Reconnecting { get { return Uia.FindText(Wnd, "✖ Stop reconnecting") != null; } }
    static string Telemetry { get { return Uia.FindText(Wnd, "●") ?? ""; } }

    static int Main(string[] args)
    {
        TestKit.Init(args);

        TestKit.Check("schedule: 1, 2, 3, 5, then every 5 s",
            ReconnectSchedule.DelayBeforeAttemptMs(1) == 1000 && ReconnectSchedule.DelayBeforeAttemptMs(2) == 2000 &&
            ReconnectSchedule.DelayBeforeAttemptMs(3) == 3000 && ReconnectSchedule.DelayBeforeAttemptMs(4) == 5000 &&
            ReconnectSchedule.DelayBeforeAttemptMs(40) == 5000, null);
        TestKit.Check("only host-closed, network errors and dead links reconnect",
            ReconnectSchedule.ShouldReconnect(SessionEndReason.HostClosed) && ReconnectSchedule.ShouldReconnect(SessionEndReason.NetworkError) &&
            ReconnectSchedule.ShouldReconnect(SessionEndReason.DeadLink) && !ReconnectSchedule.ShouldReconnect(SessionEndReason.UserDisconnect) &&
            !ReconnectSchedule.ShouldReconnect(SessionEndReason.Cancelled) && !ReconnectSchedule.ShouldReconnect(SessionEndReason.AuthRefused), null);

        int port = TestKit.FreePort();
        FakeHost host = new FakeHost(port, "2580", "FAKE-PC");
        _viewer = TestKit.StartApp("--client --connect 127.0.0.1 --port " + port + " --pin 2580");
        try
        {
            bool first = TestKit.WaitUntil(() => host.SessionsAccepted == 1 && Uia.WindowTitle(_viewer).Contains("FAKE-PC"), 20000);
            TestKit.Check("viewer connects to the fake Host", first, Uia.WindowTitle(_viewer));
            if (!first) return TestKit.Summary();

            // Dead link: the Host stops answering but the socket stays open.
            host.Responding = false;
            Stopwatch dead = Stopwatch.StartNew();
            bool noticed = TestKit.WaitUntil(() => Reconnecting, 30000);
            TestKit.Check("a dead link is noticed after about 20 s", noticed && dead.ElapsedMilliseconds >= 18000 && dead.ElapsedMilliseconds <= 26000,
                dead.ElapsedMilliseconds + " ms");
            host.Responding = true;
            host.CloseSession();
            bool back = TestKit.WaitUntil(() => host.SessionsAccepted == 2, 15000);
            bool saidSo = TestKit.WaitUntil(() => Telemetry.Contains("RECONNECTED"), 5000);
            TestKit.Check("...and the viewer reconnects by itself and says so", back && saidSo, Telemetry);

            // The Host closes the session.
            host.CloseSession();
            TestKit.Check("Host closed the session: back within 5 s", TestKit.WaitUntil(() => host.SessionsAccepted == 3, 5000), host.SessionsAccepted + " sessions");

            // Long outage: nothing listening for 65 s.
            host.StopListening();
            host.CloseSession();
            Thread.Sleep(65000);
            TestKit.Check("still retrying after a 65 s outage", Reconnecting, Telemetry);
            host.StartListening();
            Stopwatch outage = Stopwatch.StartNew();
            TestKit.Check("reconnects within 7 s of the Host coming back", TestKit.WaitUntil(() => host.SessionsAccepted == 4, 7000), outage.ElapsedMilliseconds + " ms");

            // Wrong key: one attempt, then stop.
            Thread.Sleep(1500);
            host.FailText = "Invalid Access Key/PIN (Attempt 1/5)";
            int before = host.SignInAttempts;
            host.CloseSession();
            Thread.Sleep(20000);
            TestKit.Check("a wrong key is tried once and never retried", host.SignInAttempts - before == 1 && !Reconnecting,
                (host.SignInAttempts - before) + " attempt(s), reconnecting: " + Reconnecting);
            TestKit.Check("...and the viewer shows why", Telemetry.Contains("Invalid Access Key"), Telemetry);

            // Busy: keep trying.
            host.FailText = null;
            Uia.Invoke(Wnd, "Connect to Host");
            TestKit.WaitUntil(() => host.SessionsAccepted == 5, 10000);
            host.FailText = "Host is busy checking other sign-ins from the Internet. Try again in a minute.";
            before = host.SignInAttempts;
            host.CloseSession();
            Thread.Sleep(20000);
            TestKit.Check("'Host is busy' is retried", host.SignInAttempts - before >= 3 && Reconnecting, (host.SignInAttempts - before) + " attempts");
            host.FailText = null;
            TestKit.Check("...and the session comes back once the Host accepts", TestKit.WaitUntil(() => host.SessionsAccepted == 6, 10000), null);

            // Stop while reconnecting.
            Thread.Sleep(1500);
            host.FailText = "Host is busy checking other sign-ins from the Internet. Try again in a minute.";
            host.CloseSession();
            TestKit.WaitUntil(() => Reconnecting && host.SignInAttempts > 0, 10000);
            Uia.Invoke(Wnd, "Stop reconnecting");
            Thread.Sleep(1000);
            before = host.SignInAttempts;
            Thread.Sleep(15000);
            TestKit.Check("Stop ends the retries", host.SignInAttempts == before && Uia.FindText(Wnd, "⚡ Connect to Host") != null,
                (host.SignInAttempts - before) + " attempts after Stop");
            host.FailText = null;

            // A different PC answers on the same address: not accepted.
            Uia.Invoke(Wnd, "Connect to Host");
            TestKit.WaitUntil(() => host.SessionsAccepted == 7, 10000);
            host.StopListening();
            host.CloseSession();
            FakeHost other = new FakeHost(port, "2580", "OTHER-PC");
            Thread.Sleep(12000);
            int otherSessions = other.SessionsAccepted;
            other.Dispose();
            host.StartListening();
            bool homeAgain = TestKit.WaitUntil(() => host.SessionsAccepted == 8, 15000);
            TestKit.Check("a different PC at the same address is refused; the right one is taken once it is back",
                otherSessions >= 1 && homeAgain && Uia.WindowTitle(_viewer).Contains("FAKE-PC"), otherSessions + " sign-ins at OTHER-PC; " + Uia.WindowTitle(_viewer));
        }
        finally
        {
            TestKit.Kill(_viewer);
            host.Dispose();
        }

        // The address the session started on is gone: found again through the PC's own name.
        int p2 = TestKit.FreePort(), p3 = TestKit.FreePort();
        FakeHost oldAddress = new FakeHost(p2, "2580", "localhost");
        _viewer = TestKit.StartApp("--client --connect 127.0.0.1:" + p2 + " --port " + p3 + " --pin 2580");
        try
        {
            TestKit.WaitUntil(() => oldAddress.SessionsAccepted == 1, 20000);
            oldAddress.StopListening();
            oldAddress.CloseSession();
            FakeHost byName = new FakeHost(p3, "2580", "localhost");
            TestKit.Check("reconnects through the Host's machine name when the old address is gone",
                TestKit.WaitUntil(() => byName.SessionsAccepted == 1, 15000), byName.SessionsAccepted + " sessions at the name's address");
            byName.Dispose();
        }
        finally
        {
            TestKit.Kill(_viewer);
            oldAddress.Dispose();
        }

        return TestKit.Summary();
    }
}
