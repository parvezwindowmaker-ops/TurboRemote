using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using TurboCore;

// The connect race with a scripted dialler. A candidate's host says what its dial does:
// "ok-200" connects after 200 ms, "fail-100" fails after 100 ms, "never" waits until cancelled.
class RouteRaceTests
{
    static Stopwatch _clock;
    static readonly ConcurrentDictionary<string, long> _started = new ConcurrentDictionary<string, long>();
    static readonly ConcurrentBag<CancellationToken> _tokens = new ConcurrentBag<CancellationToken>();

    static async Task<TcpClient> Dial(EndpointCandidate c, CancellationToken token)
    {
        _started[c.Host] = _clock.ElapsedMilliseconds;
        _tokens.Add(token);
        string[] parts = c.Host.Split('-');
        if (parts[0] == "never") { await Task.Delay(Timeout.Infinite, token); }
        await Task.Delay(int.Parse(parts[1]), token);
        if (parts[0] == "fail") throw new Exception("dial failed: " + c.Host);
        return new TcpClient();
    }

    static EndpointCandidate C(string host) { return new EndpointCandidate { Host = host, Port = 1, RouteLabel = host }; }

    static ConnectionRace NewRace()
    {
        _started.Clear();
        while (!_tokens.IsEmpty) { CancellationToken t; _tokens.TryTake(out t); }
        _clock = Stopwatch.StartNew();
        return new ConnectionRace(Dial);
    }

    static string Run(ConnectionRace race, CancellationToken ct, out long elapsedMs, out Exception error)
    {
        error = null;
        string winner = null;
        try { winner = race.RunAsync(ct).Result.Value.Host; }
        catch (AggregateException ex) { error = ex.InnerException; }
        elapsedMs = _clock.ElapsedMilliseconds;
        return winner;
    }

    static int Main(string[] args)
    {
        TestKit.Init(args);
        long ms; Exception err; string won;

        ConnectionRace r1 = NewRace();
        r1.AddDirect(C("ok-200"));
        r1.AddTunnel(C("ok-10"));
        won = Run(r1, CancellationToken.None, out ms, out err);
        TestKit.Check("direct route wins over a faster public tunnel, which is never even dialled",
            won == "ok-200" && !_started.ContainsKey("ok-10"), "winner " + won + " at " + ms + " ms");

        ConnectionRace r2 = NewRace();
        r2.AddDirect(C("fail-100"));
        r2.AddTunnel(C("ok-300"));
        won = Run(r2, CancellationToken.None, out ms, out err);
        long tunnelStart; _started.TryGetValue("ok-300", out tunnelStart);
        TestKit.Check("tunnel is used when the direct route fails, but only after the 2.5 s hold-back",
            won == "ok-300" && tunnelStart >= 2400, "winner " + won + ", tunnel dialled at " + tunnelStart + " ms, won at " + ms + " ms");

        ConnectionRace r3 = NewRace();
        Task late = Task.Delay(4000).ContinueWith(t => r3.AddTunnel(C("ok-5500")));
        won = Run(r3, CancellationToken.None, out ms, out err);
        _started.TryGetValue("ok-5500", out tunnelStart);
        TestKit.Check("a tunnel that arrives after the hold-back starts at once and gets its own 6 s window",
            won == "ok-5500" && tunnelStart < 4300 && ms >= 9300 && ms <= 10500, "dialled at " + tunnelStart + " ms, won at " + ms + " ms");

        ConnectionRace r4 = NewRace();
        r4.AddDirect(C("never-0"));
        Task lateDirect = Task.Delay(1000).ContinueWith(t => r4.AddDirect(C("ok-100")));
        won = Run(r4, CancellationToken.None, out ms, out err);
        TestKit.Check("a direct address that arrives late (cloud ticket) joins the running race",
            won == "ok-100" && ms >= 1000 && ms < 1800, "winner " + won + " at " + ms + " ms");

        ConnectionRace r5 = NewRace();
        r5.AddDirect(C("fail-100"));
        r5.AddDirect(C("fail-300"));
        r5.NoMoreCandidates();
        won = Run(r5, CancellationToken.None, out ms, out err);
        TestKit.Check("all routes failed: the last error comes back right away",
            won == null && err != null && err.Message.Contains("fail-300") && ms < 800, (err != null ? err.Message : "no error") + " at " + ms + " ms");

        ConnectionRace r6 = NewRace();
        r6.AddDirect(C("never-0"));
        won = Run(r6, CancellationToken.None, out ms, out err);
        TestKit.Check("nothing answers: TimeoutException after 15 s",
            err is TimeoutException && ms >= 14500 && ms <= 15500, (err != null ? err.GetType().Name : "none") + " at " + ms + " ms");

        ConnectionRace r7 = NewRace();
        r7.AddDirect(C("never-0"));
        r7.AddDirect(C("ok-5000"));
        CancellationTokenSource cancel = new CancellationTokenSource(300);
        won = Run(r7, cancel.Token, out ms, out err);
        Thread.Sleep(100);
        TestKit.Check("cancel stops the race and every dial",
            err is OperationCanceledException && ms < 800 && _tokens.All(t => t.IsCancellationRequested),
            (err != null ? err.GetType().Name : "none") + " at " + ms + " ms, " + _tokens.Count(t => t.IsCancellationRequested) + "/" + _tokens.Count + " dials cancelled");

        TestKit.Check("pinggy hosts are public tunnels",
            SmartRouteEngine.IsPublicTunnelHost("abc.run.pinggy-free.link") && SmartRouteEngine.IsPublicTunnelHost("abc.A.Pinggy.Link") &&
            SmartRouteEngine.IsPublicTunnelHost("x.pinggy.io"), null);
        TestKit.Check("a Host ID is not a public tunnel", !SmartRouteEngine.IsPublicTunnelHost("wm-dev-016"), null);

        string label = SmartRouteEngine.ParseCandidates("100.77.50.110", 9876)[0].RouteLabel;
        TestKit.Check("Tailscale addresses are labelled as the permanent route", label == "Tailscale (permanent)", label);

        // The real connect path, wired onto the race.
        int port = TestKit.FreePort();
        using (FakeHost host = new FakeHost(port, "2580", "FAKE-PC"))
        {
            Stopwatch sw = Stopwatch.StartNew();
            var winner = SmartRouteEngine.RaceFastestConnectionAsync("127.0.0.1:" + port, port, "2580", CancellationToken.None).Result;
            TestKit.Check("RaceFastestConnectionAsync connects to a typed address", winner.Key.Connected && winner.Value.Port == port, sw.ElapsedMilliseconds + " ms");
            winner.Key.Close();
        }
        int deadPort = TestKit.FreePort();
        Stopwatch dead = Stopwatch.StartNew();
        Exception deadErr = null;
        try { SmartRouteEngine.RaceFastestConnectionAsync("127.0.0.1:" + deadPort, deadPort, "2580", CancellationToken.None).Wait(); }
        catch (AggregateException ex) { deadErr = ex.InnerException; }
        TestKit.Check("RaceFastestConnectionAsync gives up quickly when the only route is refused", deadErr != null && dead.ElapsedMilliseconds < 3000,
            (deadErr != null ? deadErr.GetType().Name + ": " + deadErr.Message : "no error") + " after " + dead.ElapsedMilliseconds + " ms");

        return TestKit.Summary();
    }
}
