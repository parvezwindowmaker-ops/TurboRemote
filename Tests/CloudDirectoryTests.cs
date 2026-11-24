using System;
using System.Diagnostics;
using System.Threading;
using TurboCore;

// The cloud directory during a long outage: a reconnect attempt every 5 s used to redo the slow key derivation and
// poll ntfy.sh each time (about 20 polls a minute from an office IP that ntfy limits per day). Runs against
// TestKit's local stand-in for ntfy.sh.
class CloudDirectoryTests
{
    const string Key = "Tk0123456789ab#7Q";

    static int Main(string[] args)
    {
        TestKit.Init(args);

        Stopwatch first = Stopwatch.StartNew();
        string topic1, secret1;
        SmartRouteEngine.DeriveCloudDirectory("PC-SLOW", Key, out topic1, out secret1);
        long firstMs = first.ElapsedMilliseconds;
        Stopwatch again = Stopwatch.StartNew();
        string topic2, secret2;
        SmartRouteEngine.DeriveCloudDirectory("pc-slow ", Key, out topic2, out secret2);
        long againMs = again.ElapsedMilliseconds;
        TestKit.Check("the slow key derivation is done once per Host ID and key", topic1 == topic2 && secret1 == secret2 && againMs * 10 < Math.Max(firstMs, 10),
            "first " + firstMs + " ms, again " + againMs + " ms");

        SmartRouteEngine.PublishHostTicketToCloudAsync("PC-TWO", Key, "192.168.1.7:9876").Wait();
        TestKit.CloudDirectory.ResetCounts();
        bool allFound = true;
        for (int i = 0; i < 3; i++) allFound &= SmartRouteEngine.ResolveHostTicketFromCloudAsync("PC-TWO", Key, true).Result == "192.168.1.7:9876";
        TestKit.Check("reconnect attempts reuse a lookup from the last 30 s", allFound && TestKit.CloudDirectory.Polls == 1,
            TestKit.CloudDirectory.Polls + " polls for 3 lookups, found each time: " + allFound);
        SmartRouteEngine.ResolveHostTicketFromCloudAsync("PC-TWO", Key).Wait();
        TestKit.Check("a Connect click always looks afresh", TestKit.CloudDirectory.Polls == 2, TestKit.CloudDirectory.Polls + " polls");

        // The reconnect path itself: two failed attempts by Host ID within 30 s poll once.
        TestKit.CloudDirectory.ResetCounts();
        int deadPort = TestKit.FreePort();
        for (int i = 0; i < 2; i++)
        {
            try { SmartRouteEngine.RaceFastestConnectionAsync("NO-SUCH-PC-4711", deadPort, Key, CancellationToken.None, true).Wait(); } catch { }
        }
        TestKit.Check("two reconnect attempts by Host ID poll the directory once", TestKit.CloudDirectory.Polls == 1, TestKit.CloudDirectory.Polls + " polls");

        return TestKit.Summary();
    }
}
