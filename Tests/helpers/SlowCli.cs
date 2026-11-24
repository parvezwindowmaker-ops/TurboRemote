// Stands in for a Tailscale CLI that hangs: prints nothing and sleeps for 30 seconds.
class SlowCli
{
    static void Main()
    {
        System.Threading.Thread.Sleep(30000);
    }
}
