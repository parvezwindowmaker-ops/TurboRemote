using System;
using System.Diagnostics;
using System.Threading;
using TurboCore;

// The viewer window across a lost session: the title says what is happening, and a reconnect never pulls the
// keyboard out of a box the user moved into during the outage (their next keys would go to the remote PC).
class SessionUiTests
{
    static int Main(string[] args)
    {
        TestKit.Init(args);
        int port = TestKit.FreePort();
        using (FakeHost host = new FakeHost(port, "2580", "FAKE-PC"))
        {
            Process v = TestKit.StartApp("--client --connect 127.0.0.1 --port " + port + " --pin 2580");
            try
            {
                bool up = TestKit.WaitUntil(() => host.SessionsAccepted == 1 && Uia.WindowTitle(v).Contains("Connected"), 20000);
                TestKit.Check("viewer connects to the fake Host", up, Uia.WindowTitle(v));
                if (!up) return TestKit.Summary();
                IntPtr w = Uia.Window(v);

                // The Host goes away; the viewer keeps retrying.
                host.StopListening();
                host.CloseSession();
                bool retrying = TestKit.WaitUntil(() => Uia.FindText(w, "✖ Stop reconnecting") != null, 10000);
                string title = Uia.WindowTitle(v);
                TestKit.Check("while reconnecting the title says so, not 'Connected'",
                    retrying && !title.Contains("Connected") && title.IndexOf("reconnecting", StringComparison.OrdinalIgnoreCase) >= 0, title);

                // Meanwhile the user clicks into the access key box (focus only: no keystrokes are sent).
                bool focused = Uia.FocusEdit(w, "Access key");
                host.StartListening();
                bool back = TestKit.WaitUntil(() => host.SessionsAccepted == 2 && Uia.WindowTitle(v).Contains("Connected"), 15000);
                Thread.Sleep(1000);
                string focus = Uia.FocusedName();
                if (focused)
                    TestKit.Check("a reconnect leaves the keyboard in the box the user moved to", back && focus == "Access key", "back: " + back + ", focus on: '" + focus + "'");
                else
                    Console.WriteLine("skipped  focus check: Windows did not let the test focus the viewer (another window is in front)");

                // Stop while reconnecting: the title is the plain one again.
                host.StopListening();
                host.CloseSession();
                TestKit.WaitUntil(() => Uia.FindText(w, "✖ Stop reconnecting") != null, 10000);
                Uia.Invoke(w, "Stop reconnecting");
                TestKit.WaitUntil(() => Uia.FindText(w, "⚡ Connect") != null, 5000);
                title = Uia.WindowTitle(v);
                TestKit.Check("after Stop the title no longer says 'Connected'",
                    !title.Contains("Connected") && title.IndexOf("reconnecting", StringComparison.OrdinalIgnoreCase) < 0 && title.StartsWith("TurboRemote"), title);
            }
            finally { TestKit.Kill(v); }
        }
        return TestKit.Summary();
    }
}
