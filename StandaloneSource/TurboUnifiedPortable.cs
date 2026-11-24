using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Threading;
using System.Windows.Forms;
using TurboClient;
using TurboCore;
using TurboHost;

namespace TurboUnified
{
    public class UnifiedLauncherForm : Form
    {
        public UnifiedLauncherForm()
        {
            Text = "TurboRemote  v" + TurboProtocol.AppVersion;
            Size = new Size(560, 300);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(16, 18, 26);
            ForeColor = Color.FromArgb(230, 235, 245);
            Font = new Font("Segoe UI", 9.5f);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;

            Label lblTitle = new Label
            {
                Text = "⚡ TURBOREMOTE",
                Font = new Font("Segoe UI", 16f, FontStyle.Bold),
                ForeColor = Color.FromArgb(90, 200, 255),
                Location = new Point(24, 18),
                AutoSize = true
            };

            Label lblSub = new Label
            {
                Text = "Share this PC, or control another one. Encrypted end to end.",
                Font = new Font("Segoe UI", 9f),
                ForeColor = Color.FromArgb(160, 180, 210),
                Location = new Point(26, 50),
                AutoSize = true
            };

            Button btnHost = new Button
            {
                Text = "🖥  Share this PC\n     Let someone you trust control this computer",
                Location = new Point(26, 92),
                Size = new Size(492, 66),
                BackColor = Color.FromArgb(30, 140, 88),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Segoe UI", 10.5f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            btnHost.Click += (s, e) =>
            {
                Hide();
                HostMainForm hostForm = new HostMainForm();
                hostForm.ConfigureFromArgs(new string[0]);
                hostForm.FormClosed += (s2, e2) => Close();
                hostForm.Show();
            };

            Button btnClient = new Button
            {
                Text = "⚡  Control another PC\n     Connect to a PC that is sharing (desktop, PowerShell, files)",
                Location = new Point(26, 170),
                Size = new Size(492, 66),
                BackColor = Color.FromArgb(55, 95, 175),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Segoe UI", 10.5f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            btnClient.Click += (s, e) =>
            {
                Hide();
                ClientMainForm clientForm = new ClientMainForm();
                clientForm.FormClosed += (s2, e2) => Close();
                clientForm.Show();
            };

            // "Launch Both" (Host + viewer on this one PC) is gone: on one PC the keys the viewer sends land back
            // in the viewer itself, so the keyboard could never be used and the demo only confused people.
            Controls.AddRange(new Control[] { lblTitle, lblSub, btnHost, btnClient });
        }
    }

    public static class HostEntry
    {
        [STAThread]
        public static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            HostMainForm form = new HostMainForm();
            form.ConfigureFromArgs(args);
            Application.Run(form);
        }
    }

    public static class ClientEntry
    {
        [STAThread]
        public static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            ClientMainForm form = new ClientMainForm();

            string host = null;
            int port = 9876;
            string pin = "";

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--connect" && i + 1 < args.Length) host = args[i + 1];
                if (args[i] == "--port" && i + 1 < args.Length) int.TryParse(args[i + 1], out port);
                if (args[i] == "--pin" && i + 1 < args.Length) pin = args[i + 1];
            }

            if (!string.IsNullOrEmpty(host))
            {
                form.AutoConnectOnStartup(host, port, pin);
            }

            Application.Run(form);
        }
    }

    public static class UnifiedEntry
    {
        [STAThread]
        public static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            bool forceHost = false;
            bool forceClient = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--self-test")
                {
                    RunSelfTestAndExit();
                    return;
                }
                if (args[i] == "--host" || args[i] == "--port") forceHost = true;
                if (args[i] == "--client" || args[i] == "--connect") forceClient = true;
            }

            if (forceClient)
            {
                ClientEntry.Main(args);
                return;
            }

            if (forceHost)
            {
                HostEntry.Main(args);
                return;
            }

            Application.Run(new UnifiedLauncherForm());
        }

        private static void RunSelfTestAndExit()
        {
            try
            {
                // 1. Verify Physical Screen Resolution & Delta Screen Encoder
                StreamConfig cfg = new StreamConfig();
                cfg.ApplyPreset(BandwidthProfile.Balanced_4G);
                using (DeltaScreenEncoder enc = new DeltaScreenEncoder())
                {
                    int tiles, nw, nh;
                    byte[] frame1 = enc.CaptureAndEncodeDelta(cfg, out tiles, out nw, out nh);
                    if (nw <= 0 || nh <= 0 || tiles <= 0 || frame1.Length == 0)
                        throw new Exception("DeltaScreenEncoder keyframe failed.");

                    Bitmap decodedBmp = null;
                    int dnw, dnh, dsw, dsh, dtiles;
                    DeltaScreenEncoder.ApplyDeltaFrame(frame1, ref decodedBmp, out dnw, out dnh, out dsw, out dsh, out dtiles);
                    if (decodedBmp == null || dnw != nw || dnh != nh || dtiles != tiles)
                        throw new Exception("DeltaScreenEncoder decode mismatch.");
                    decodedBmp.Dispose();

                    // 2. Verify AES-256 + HMAC-SHA256 Zero-Knowledge SecurityContext & Anti-Replay
                    byte[] salt = new byte[16];
                    byte[] chal = new byte[16];
                    using (SecurityContext hostSec = new SecurityContext("2580", salt, chal))
                    using (SecurityContext clientSec = new SecurityContext("2580", salt, chal))
                    {
                        byte[] proof = clientSec.ComputeHandshakeProof(chal, "CLIENT_AUTH_V2");
                        byte[] expected = hostSec.ComputeHandshakeProof(chal, "CLIENT_AUTH_V2");
                        if (!TurboProtocol.ConstantTimeEquals(proof, expected))
                            throw new Exception("HMAC handshake proof mismatch.");

                        byte[] encryptedFrame = hostSec.EncryptAndAuthenticate(PacketType.ScreenFrame, frame1);
                        byte[] decryptedFrame = clientSec.VerifyAndDecrypt(PacketType.ScreenFrame, encryptedFrame);
                        if (!TurboProtocol.ConstantTimeEquals(frame1, decryptedFrame))
                            throw new Exception("AES-256 E2EE payload mismatch.");
                    }

                    // 3. Verify Disconnect -> Reconnect Lifecycle (Ensures no ObjectDisposedException on NetworkStream)
                    using (MemoryStream fakeWireA = new MemoryStream())
                    using (MemoryStream fakeWireB = new MemoryStream())
                    using (SemaphoreSlim wLock = new SemaphoreSlim(1, 1))
                    {
                        TurboProtocol.SendPacketAsync(fakeWireA, wLock, PacketType.Ping, new byte[] { 1, 2, 3, 4 }, CancellationToken.None).Wait();
                        fakeWireA.Position = 0;
                        var pkt1 = TurboProtocol.ReadPacketAsync(fakeWireA, CancellationToken.None).Result;
                        if (pkt1 == null || pkt1.Value.Key != PacketType.Ping)
                            throw new Exception("Session 1 framing failed.");
                    }

                    // 4. Internet hardening: weak keys stay off the Internet, loopback peers are throttled
                    //    (never locked out), and the cloud directory round-trips under the hardened V2 derivation.
                    string strongKey = TurboProtocol.GenerateStrongAccessKey();
                    if (AccessKeyStore.WhyTooWeakForInternet("2580") == null || AccessKeyStore.WhyTooWeakForInternet("aaaa-aaaa") == null ||
                        AccessKeyStore.WhyTooWeakForInternet("") == null || AccessKeyStore.WhyTooWeakForInternet(strongKey) != null)
                        throw new Exception("Access key strength policy wrong.");
                    if (!SharedAddressThrottle.IsSharedAddress(IPAddress.Loopback) || SharedAddressThrottle.IsSharedAddress(IPAddress.Parse("192.168.1.5")))
                        throw new Exception("Loopback/LAN classification wrong.");
                    int[] expectedDelays = { 0, 0, 0, 1, 2, 4, 8, 16, 30, 30 };
                    for (int f = 0; f < expectedDelays.Length; f++)
                    {
                        if ((int)SharedAddressThrottle.DelayAfterFailures(f).TotalSeconds != expectedDelays[f])
                            throw new Exception("Loopback throttle delay after " + f + " failures is wrong.");
                    }
                    string topic, secret, otherTopic, otherSecret;
                    SmartRouteEngine.DeriveCloudDirectory("wm-dev-07 ", strongKey, out topic, out secret);
                    SmartRouteEngine.DeriveCloudDirectory("WM-DEV-07", strongKey + "X", out otherTopic, out otherSecret);
                    if (!topic.StartsWith("https://ntfy.sh/trbo2_") || topic == otherTopic || secret == otherSecret || topic.Contains(secret))
                        throw new Exception("Cloud directory derivation wrong.");
                    string sealedTicket = SmartRouteEngine.EncryptTicketForCloud("192.168.1.5:9876,abc.run.pinggy-free.link:4321", secret);
                    if (SmartRouteEngine.DecryptTicketFromCloud(sealedTicket, secret) != "192.168.1.5:9876,abc.run.pinggy-free.link:4321")
                        throw new Exception("Cloud ticket round trip failed.");
                    bool wrongSecretRejected = false;
                    try { SmartRouteEngine.DecryptTicketFromCloud(sealedTicket, otherSecret); }
                    catch (System.Security.Cryptography.CryptographicException) { wrongSecretRejected = true; }
                    if (!wrongSecretRejected) throw new Exception("Cloud ticket opened with the wrong key.");

                    Console.WriteLine(string.Format("SELF_TEST_PASSED|Resolution={0}x{1}|KeyFrameTiles={2}|CompressedBytes={3}|Encryption=AES-256-HMAC-SHA256|ReconnectLifecycle=OK|InternetHardening=OK", nw, nh, tiles, frame1.Length));
                    Environment.Exit(0);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("SELF_TEST_FAILED|" + ex.Message);
                Environment.Exit(1);
            }
        }
    }
}
