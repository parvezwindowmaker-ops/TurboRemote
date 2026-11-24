using System;
using System.IO;
using TurboCore;

class TunnelPolicyTests
{
    static TimeSpan S(int seconds) { return TimeSpan.FromSeconds(seconds); }

    static int Main(string[] args)
    {
        TestKit.Init(args);

        TunnelAutoSwitch on = new TunnelAutoSwitch("on"), off = new TunnelAutoSwitch("off");
        bool fixedOk = true;
        foreach (TailscaleState st in Enum.GetValues(typeof(TailscaleState)))
        {
            fixedOk &= on.Update(st, S(100)) && !off.Update(st, S(100));
        }
        TestKit.Check("'on' and 'off' ignore Tailscale", fixedOk && !on.IsAutomatic && !off.IsAutomatic, null);

        TestKit.Check("unknown setting means automatic", new TunnelAutoSwitch("banana").IsAutomatic && new TunnelAutoSwitch(null).IsAutomatic, null);

        TestKit.Check("auto + Tailscale running -> tunnel off", !new TunnelAutoSwitch("auto").Update(TailscaleState.Running, S(0)), null);
        TestKit.Check("auto + Tailscale not installed -> tunnel on at once", new TunnelAutoSwitch("auto").Update(TailscaleState.NotInstalled, S(0)), null);

        TunnelAutoSwitch boot = new TunnelAutoSwitch("auto");
        bool b1 = boot.Update(TailscaleState.Starting, S(10));
        bool b2 = boot.Update(TailscaleState.Starting, S(61));
        bool b3 = boot.Update(TailscaleState.Running, S(91));
        TestKit.Check("auto at sign-in: wait while Tailscale starts, open after 60 s, close once it runs", !b1 && b2 && !b3, b1 + " " + b2 + " " + b3);

        TunnelAutoSwitch drop = new TunnelAutoSwitch("auto");
        bool d1 = drop.Update(TailscaleState.Running, S(0));
        bool d2 = drop.Update(TailscaleState.Stopped, S(30));
        bool d3 = drop.Update(TailscaleState.Stopped, S(60));
        bool d4 = drop.Update(TailscaleState.Running, S(90));
        TestKit.Check("auto: Tailscale must be down on 2 checks in a row before the tunnel opens", !d1 && !d2 && d3 && !d4, d1 + " " + d2 + " " + d3 + " " + d4);

        TunnelAutoSwitch flap = new TunnelAutoSwitch("auto");
        flap.Update(TailscaleState.Running, S(0));
        flap.Update(TailscaleState.Stopped, S(30));
        flap.Update(TailscaleState.Running, S(60));
        bool f4 = flap.Update(TailscaleState.Stopped, S(90));
        TestKit.Check("auto: a Running check in between resets the count", !f4, null);

        string temp = Path.Combine(Path.GetTempPath(), "turbo-settings-test-" + Guid.NewGuid().ToString("N") + ".txt");
        TurboSettings.FilePathOverride = temp;
        try
        {
            TestKit.Check("missing setting returns the default", TurboSettings.Get("publicTunnel", "auto") == "auto", null);
            TurboSettings.Set("other", "kept");
            TurboSettings.Set("publicTunnel", "on");
            TurboSettings.Set("publicTunnel", "off");
            TestKit.Check("Set then Get returns the value", TurboSettings.Get("publicTunnel", "auto") == "off", File.ReadAllText(temp));
            TestKit.Check("other keys survive a Set", TurboSettings.Get("other", "") == "kept", null);
        }
        finally
        {
            TurboSettings.FilePathOverride = null;
            try { File.Delete(temp); } catch { }
        }

        return TestKit.Summary();
    }
}
