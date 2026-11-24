using System;
using System.Diagnostics;
using System.IO;
using TurboCore;

class TailscaleInfoTests
{
    static string Fixture(string name)
    {
        return File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "fixtures", "tailscale-" + name + ".json"));
    }

    static int Main(string[] args)
    {
        TestKit.Init(args);

        TailscaleStatus running = TailscaleInfo.Parse(Fixture("running"));
        TestKit.Check("Running fixture: state Running", running.State == TailscaleState.Running && running.IsRunning, running.State.ToString());
        TestKit.Check("Running fixture: MagicDNS name of this PC without the trailing dot", running.DnsName == "office-pc.tail0000.ts.net", running.DnsName);
        TestKit.Check("Running fixture: host name of this PC, not the peer's", running.HostName == "OFFICE-PC", running.HostName);
        TestKit.Check("Running fixture: first IPv4 of this PC", running.IPv4 == "100.101.102.103", running.IPv4);

        TestKit.Check("NeedsLogin -> SignedOut", TailscaleInfo.Parse(Fixture("needslogin")).State == TailscaleState.SignedOut, null);
        TestKit.Check("NoState -> Starting", TailscaleInfo.Parse(Fixture("starting")).State == TailscaleState.Starting, null);
        TailscaleStatus garbage = TailscaleInfo.Parse("garbage");
        TestKit.Check("unparseable output -> Stopped, empty fields", garbage.State == TailscaleState.Stopped && garbage.DnsName == "" && garbage.IPv4 == "", null);

        TestKit.Check("100.64.0.1 is a Tailscale address", TailscaleInfo.IsTailscaleAddress("100.64.0.1"), null);
        TestKit.Check("100.127.255.255 is a Tailscale address", TailscaleInfo.IsTailscaleAddress("100.127.255.255"), null);
        TestKit.Check("100.63.0.1 is not", !TailscaleInfo.IsTailscaleAddress("100.63.0.1"), null);
        TestKit.Check("100.128.0.1 is not", !TailscaleInfo.IsTailscaleAddress("100.128.0.1"), null);
        TestKit.Check("192.168.1.2 is not", !TailscaleInfo.IsTailscaleAddress("192.168.1.2"), null);
        TestKit.Check("garbage is not", !TailscaleInfo.IsTailscaleAddress("x") && !TailscaleInfo.IsTailscaleAddress(null), null);

        Stopwatch sw = Stopwatch.StartNew();
        TailscaleStatus missing = TailscaleInfo.Query(@"C:\nope\tailscale.exe");
        TestKit.Check("missing CLI: no exception, answer within 1 s", sw.ElapsedMilliseconds < 1000, sw.ElapsedMilliseconds + " ms, state " + missing.State);

        sw.Restart();
        TailscaleStatus slow = TailscaleInfo.Query(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SlowCli.exe"), 3000);
        TestKit.Check("hung CLI: gives up and answers within 3.5 s", sw.ElapsedMilliseconds < 3500, sw.ElapsedMilliseconds + " ms, state " + slow.State);

        string cli = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale.exe");
        if (File.Exists(cli))
        {
            TailscaleStatus real = TailscaleInfo.Query();
            TestKit.Check("this PC: Tailscale Running with a .ts.net name", real.State == TailscaleState.Running && real.DnsName.EndsWith(".ts.net") && TailscaleInfo.IsTailscaleAddress(real.IPv4),
                real.State + " " + real.DnsName + " " + real.IPv4);
        }
        else
        {
            Console.WriteLine("skipped  this PC has no Tailscale CLI");
        }

        return TestKit.Summary();
    }
}
