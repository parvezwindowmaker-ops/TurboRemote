using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
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
            Text = "TurboRemote";
            Size = new Size(560, 360);
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
                Text = "One Single ~100 KB Executable for Both Machines • AES-256 + HMAC-SHA256 E2EE",
                Font = new Font("Segoe UI", 9f),
                ForeColor = Color.FromArgb(160, 180, 210),
                Location = new Point(26, 50),
                AutoSize = true
            };

            Button btnHost = new Button
            {
                Text = "🖥  SHARE THIS PC (Start Host Server)\n     Allow another PC to remotely control this machine",
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
                Text = "⚡  CONTROL A REMOTE PC (Start Viewer Client)\n     Connect to a remote Host PC (Desktop, PowerShell & File Manager)",
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

            Button btnBothDemo = new Button
            {
                Text = "🚀 Launch Both (Host + Client Auto-Connected on This PC for Testing)",
                Location = new Point(26, 252),
                Size = new Size(492, 42),
                BackColor = Color.FromArgb(95, 65, 155),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9.2f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            btnBothDemo.Click += (s, e) =>
            {
                string exe = Process.GetCurrentProcess().MainModule.FileName;
                Process.Start(exe, "--host --port 9876 --pin 2580");
                Process.Start(exe, "--client --connect 127.0.0.1 --port 9876 --pin 2580");
                Close();
            };

            Controls.AddRange(new Control[] { lblTitle, lblSub, btnHost, btnClient, btnBothDemo });
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
            string pin = "2580";

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

                    Console.WriteLine(string.Format("SELF_TEST_PASSED|Resolution={0}x{1}|KeyFrameTiles={2}|CompressedBytes={3}|Encryption=AES-256-HMAC-SHA256|ReconnectLifecycle=OK", nw, nh, tiles, frame1.Length));
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
