using System;
using System.IO;
using TurboCore;

// Every app a test starts keeps its files, its OpenSSH tools and its cloud directory where TestKit says: a private
// folder, a fake ssh, and a local stand-in for ntfy.sh. No test touches the user's own settings, ntfy.sh or pinggy.
class TestIsolationTests
{
    static int Main(string[] args)
    {
        TestKit.Init(args);

        TestKit.Check("settings live in TURBOREMOTE_DATA_DIR",
            TurboSettings.FilePath == Path.Combine(TestKit.SandboxDir, "settings.txt"), TurboSettings.FilePath);
        TestKit.Check("the saved access key lives in TURBOREMOTE_DATA_DIR",
            AccessKeyStore.KeyFilePath == Path.Combine(TestKit.SandboxDir, "access_key.txt"), AccessKeyStore.KeyFilePath);
        TestKit.Check("ssh.exe comes from TURBOREMOTE_OPENSSH_DIR",
            PublicTunnel.FindOpenSshTool("ssh.exe") == Path.Combine(TestKit.FakeOpenSshDir, "ssh.exe"), PublicTunnel.FindOpenSshTool("ssh.exe"));

        string keyError;
        string tunnelKey = PublicTunnel.EnsureTunnelKey(out keyError);
        TestKit.Check("the tunnel key is made in TURBOREMOTE_DATA_DIR by the fake ssh-keygen",
            tunnelKey == Path.Combine(TestKit.SandboxDir, "tunnel_ed25519") && File.Exists(tunnelKey), tunnelKey + " " + keyError);

        string topic, secret;
        SmartRouteEngine.DeriveCloudDirectory("PC-ONE", "Tk0123456789ab#7Q", out topic, out secret);
        TestKit.Check("the cloud directory is TURBOREMOTE_DIRECTORY_URL", topic.StartsWith(TestKit.CloudDirectory.BaseUrl + "trbo2_"), topic);

        // Only once the topic is known to be local: the publish must never reach the real ntfy.sh.
        bool local = topic.StartsWith(TestKit.CloudDirectory.BaseUrl);
        string why = local ? SmartRouteEngine.PublishHostTicketToCloudAsync("PC-ONE", "Tk0123456789ab#7Q", "192.168.1.5:9876").Result : "not attempted: the topic is not local";
        string back = local ? SmartRouteEngine.ResolveHostTicketFromCloudAsync("PC-ONE", "Tk0123456789ab#7Q").Result : null;
        TestKit.Check("a published ticket goes to the stand-in and comes back from it",
            why == null && back == "192.168.1.5:9876" && TestKit.CloudDirectory.Posts == 1 && TestKit.CloudDirectory.Polls == 1,
            "publish: " + (why ?? "ok") + ", lookup: " + (back ?? "null") + ", posts " + TestKit.CloudDirectory.Posts + ", polls " + TestKit.CloudDirectory.Polls);

        return TestKit.Summary();
    }
}
