using System.Diagnostics;

// Proves the build and the test kit work: the app's self-test passes, and the fake Host and fake viewer
// can complete a sign-in with each other.
class SmokeTests
{
    static int Main(string[] args)
    {
        TestKit.Init(args);

        ProcessStartInfo psi = new ProcessStartInfo(TestKit.ExePath, "--self-test")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        using (Process p = Process.Start(psi))
        {
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(60000);
            TestKit.Check("self-test passes", output.StartsWith("SELF_TEST_PASSED"), output.Trim());
        }

        int port = TestKit.FreePort();
        using (FakeHost host = new FakeHost(port, "2580", "FAKE-PC"))
        using (FakeViewer viewer = FakeViewer.Connect("127.0.0.1", port, "2580"))
        {
            TestKit.WaitUntil(() => host.SessionsAccepted == 1, 3000);
            TestKit.Check("fake host and fake viewer complete sign-in", host.SessionsAccepted == 1, viewer.AuthText);
        }

        return TestKit.Summary();
    }
}
