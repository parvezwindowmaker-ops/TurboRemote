using System;
using System.IO;
using System.Threading;

// Stands in for ssh.exe and ssh-keygen.exe (TestKit copies it under both names), so no test opens a real public
// tunnel. As ssh-keygen ("-f <path>") it writes a dummy key pair, after FAKE_SSH_KEYGEN_DELAY_MS if that is set.
// As ssh it prints nothing and waits until its stdin closes or it is killed, like a tunnel that is up.
class FakeOpenSsh
{
    static int Main(string[] args)
    {
        int f = Array.IndexOf(args, "-f");
        if (f >= 0 && f + 1 < args.Length)
        {
            int delay;
            if (int.TryParse(Environment.GetEnvironmentVariable("FAKE_SSH_KEYGEN_DELAY_MS"), out delay) && delay > 0) Thread.Sleep(delay);
            File.WriteAllText(args[f + 1], "fake private key");
            File.WriteAllText(args[f + 1] + ".pub", "ssh-ed25519 AAAAfake TurboRemote-tunnel");
            return 0;
        }

        Console.In.ReadToEnd();
        return 0;
    }
}
