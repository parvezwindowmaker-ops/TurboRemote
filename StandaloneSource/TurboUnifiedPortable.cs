using System;
using System.Diagnostics;
using System.Drawing;
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
    }
}
