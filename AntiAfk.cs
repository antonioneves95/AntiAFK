using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.IO;
using System.Collections.Generic;

namespace AntiAfk
{
    // --- Input Helper (P/Invoke) ---
    public static class InputHelper
    {
        [DllImport("user32.dll")]
        static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, int dwExtraInfo);

        [DllImport("user32.dll")]
        static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, int dwExtraInfo);

        const uint MOUSEEVENTF_MOVE = 0x0001;
        const uint KEYEVENTF_KEYUP = 0x0002;

        public static void JiggleMouse()
        {
            // Visible Jiggle: Move 50 pixels, wait, move back
            int delta = 50;
            mouse_event(MOUSEEVENTF_MOVE, delta, 0, 0, 0);
            System.Threading.Thread.Sleep(200); // Wait so user sees it
            mouse_event(MOUSEEVENTF_MOVE, -delta, 0, 0, 0);
        }

        public static void PressKey(Keys key)
        {
            byte vk = (byte)key;
            keybd_event(vk, 0, 0, 0); // Down
            System.Threading.Thread.Sleep(50); // Small delay for key press to register
            keybd_event(vk, 0, KEYEVENTF_KEYUP, 0); // Up
        }
    }

    // --- Configuration ---
    public class AppSettings
    {
        public bool AlwaysOnTop { get; set; }
        public int ModeIndex { get; set; } // 0=Mouse, 1=Keyboard
        public int IntervalSeconds { get; set; }
        public Keys SelectedKey { get; set; }

        public AppSettings()
        {
            AlwaysOnTop = false;
            ModeIndex = 0;
            IntervalSeconds = 60;
            SelectedKey = Keys.Space;
        }

        private static string ConfigPath 
        { 
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AntiAfk", "settings.txt"); }
        }

        public static void Save(AppSettings settings)
        {
            try
            {
                string dir = Path.GetDirectoryName(ConfigPath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllLines(ConfigPath, new string[] {
                    string.Format("AlwaysOnTop={0}", settings.AlwaysOnTop),
                    string.Format("ModeIndex={0}", settings.ModeIndex),
                    string.Format("IntervalSeconds={0}", settings.IntervalSeconds),
                    string.Format("SelectedKey={0}", settings.SelectedKey)
                });
            }
            catch { }
        }

        public static AppSettings Load()
        {
            var s = new AppSettings();
            try
            {
                if (File.Exists(ConfigPath))
                {
                    foreach (string line in File.ReadAllLines(ConfigPath))
                    {
                        var parts = line.Split('=');
                        if (parts.Length != 2) continue;
                        
                        bool bVal;
                        int iVal;
                        Keys kVal;

                        if (parts[0] == "AlwaysOnTop") { if(bool.TryParse(parts[1], out bVal)) s.AlwaysOnTop = bVal; }
                        else if (parts[0] == "ModeIndex") { if(int.TryParse(parts[1], out iVal)) s.ModeIndex = iVal; }
                        else if (parts[0] == "IntervalSeconds") { if(int.TryParse(parts[1], out iVal)) s.IntervalSeconds = iVal; }
                        else if (parts[0] == "SelectedKey") { if(Enum.TryParse(parts[1], out kVal)) s.SelectedKey = kVal; }
                    }
                }
            }
            catch { }
            return s;
        }
    }

    // --- Main UI Form ---
    public class MainForm : Form
    {
        private readonly Color ColBackground = Color.FromArgb(20, 20, 20);
        // Changed to Blue as requested
        private readonly Color ColBlue = Color.FromArgb(0, 122, 204); 
        private readonly Color ColNeonGreen = Color.FromArgb(0, 255, 100);
        private readonly Color ColDarkAcc = Color.FromArgb(40, 40, 40);

        private bool isActive = false;
        private NotifyIcon trayIcon;
        private ContextMenu trayMenu;
        private AppSettings settings;
        private System.Windows.Forms.Timer actionTimer;
        
        private System.Windows.Forms.Timer uiUpdateTimer;

        // Controls
        private Label lblStatus;
        private Label lblActionInfo;
        private Button btnToggle;
        
        private Label lblMode;
        private ComboBox cmbMode;
        
        private Label lblInt;
        private NumericUpDown numInterval;
        
        private Label lblKeyLabel;
        private ComboBox cmbKey;
        
        private CheckBox chkOnTop;

        public MainForm()
        {
            this.FormBorderStyle = FormBorderStyle.None;
            this.Text = "AntiAFK";
            this.Size = new Size(350, 620); // Taller
            this.BackColor = ColBackground;
            this.Icon = SystemIcons.Shield;
            this.StartPosition = FormStartPosition.CenterScreen;

            settings = AppSettings.Load();
            this.TopMost = settings.AlwaysOnTop;

            // Load Icon Early
            if (File.Exists("icon.ico")) {
                this.Icon = new Icon("icon.ico");
            } else if (File.Exists("logo.png")) {
                 try {
                    Bitmap bmp = new Bitmap("logo.png");
                    this.Icon = Icon.FromHandle(bmp.GetHicon());
                } catch { }
            }

            InitializeUI();
            InitializeTray();

            // Action Timer
            actionTimer = new System.Windows.Forms.Timer();
            actionTimer.Tick += new EventHandler(actionTimer_Tick);

            uiUpdateTimer = new System.Windows.Forms.Timer();
            uiUpdateTimer.Interval = 100;
            uiUpdateTimer.Tick += new EventHandler(uiUpdateTimer_Tick);

            this.MouseDown += new MouseEventHandler(MainForm_MouseDown);
            
            // Initial layout update
            UpdateVisibility();
        }

        private void MainForm_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                ReleaseCapture();
                SendMessage(Handle, WM_NCLBUTTONDOWN, HT_CAPTION, 0);
            }
        }

        [DllImport("user32.dll")] public static extern int SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);
        [DllImport("user32.dll")] public static extern bool ReleaseCapture();
        public const int WM_NCLBUTTONDOWN = 0xA1;
        public const int HT_CAPTION = 0x2;

        private void InitializeUI()
        {
            Label btnClose = new Label() { Text = "X", ForeColor = Color.Gray, Font = new Font("Segoe UI", 10, FontStyle.Bold), Location = new Point(320, 10), Size = new Size(20, 20), Cursor = Cursors.Hand };
            btnClose.Click += new EventHandler(delegate { this.Hide(); trayIcon.ShowBalloonTip(1000, "AntiAFK", "Minimized to tray", ToolTipIcon.None); });
            btnClose.MouseLeave += new EventHandler(delegate { btnClose.ForeColor = Color.Gray; });
            this.Controls.Add(btnClose);

            Label btnMin = new Label() { Text = "_", ForeColor = Color.Gray, Font = new Font("Segoe UI", 10, FontStyle.Bold), Location = new Point(295, 8), Size = new Size(20, 20), Cursor = Cursors.Hand };
            btnMin.Click += new EventHandler(delegate { this.WindowState = FormWindowState.Minimized; });
            this.Controls.Add(btnMin);

            // LOGO
            if (File.Exists("logo.png"))
            {
                PictureBox pbLogo = new PictureBox();
                pbLogo.Image = Image.FromFile("logo.png");
                pbLogo.SizeMode = PictureBoxSizeMode.Zoom;
                
                // HUGE Size: Fill width almost completely
                pbLogo.Size = new Size(340, 140); 
                pbLogo.Location = new Point(5, 20); // Centerish
                
                pbLogo.BackColor = Color.Transparent;
                pbLogo.MouseDown += new MouseEventHandler(MainForm_MouseDown);
                this.Controls.Add(pbLogo);
            }
            else
            {
                // Fallback Text
                Label title = new Label() { Text = "AntiAFK", ForeColor = ColBlue, Font = new Font("Segoe UI", 14, FontStyle.Bold), Location = new Point(20, 10), AutoSize = true };
                this.Controls.Add(title);
            }

            // Status (Shifted down to Y=170)
            lblStatus = new Label() { Text = "STOPPED", ForeColor = Color.Gray, Font = new Font("Segoe UI", 32, FontStyle.Bold), Location = new Point(0, 170), Size = new Size(350, 60), TextAlign = ContentAlignment.MiddleCenter };
            this.Controls.Add(lblStatus);

            lblActionInfo = new Label() { Text = "Ready", ForeColor = Color.White, Font = new Font("Consolas", 10), Location = new Point(0, 230), Size = new Size(350, 20), TextAlign = ContentAlignment.MiddleCenter };
            this.Controls.Add(lblActionInfo);

            // Toggle (Shifted down to Y=270, Blue Color)
            btnToggle = new Button() { Text = "START", FlatStyle = FlatStyle.Flat, ForeColor = Color.White, BackColor = ColBlue, Font = new Font("Segoe UI", 12, FontStyle.Bold), Location = new Point(100, 270), Size = new Size(150, 50), Cursor = Cursors.Hand };
            btnToggle.FlatAppearance.BorderSize = 0;
            btnToggle.Click += new EventHandler(btnToggle_Click);
            this.Controls.Add(btnToggle);

            // -- Controls (Positions set by UpdateVisibility) --
            
            // Mode
            lblMode = new Label() { Text = "MODE", ForeColor = Color.Gray, Font = new Font("Segoe UI", 8), AutoSize = true };
            this.Controls.Add(lblMode);
            
            cmbMode = new ComboBox() { BackColor = ColDarkAcc, ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 10), Size = new Size(250, 30), DropDownStyle = ComboBoxStyle.DropDownList };
            cmbMode.Items.AddRange(new object[] { "Mouse Move", "Keyboard Press" });
            cmbMode.SelectedIndex = settings.ModeIndex;
            cmbMode.SelectedIndexChanged += new EventHandler(delegate { settings.ModeIndex = cmbMode.SelectedIndex; AppSettings.Save(settings); UpdateVisibility(); });
            this.Controls.Add(cmbMode);

            // Interval
            lblInt = new Label() { Text = "INTERVAL (SECONDS)", ForeColor = Color.Gray, Font = new Font("Segoe UI", 8), AutoSize = true };
            this.Controls.Add(lblInt);

            numInterval = new NumericUpDown() { BackColor = ColDarkAcc, ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle, Font = new Font("Segoe UI", 10), Size = new Size(250, 30), Minimum = 1, Maximum = 3600 };
            numInterval.Value = settings.IntervalSeconds < 1 ? 60 : settings.IntervalSeconds;
            numInterval.ValueChanged += new EventHandler(delegate { settings.IntervalSeconds = (int)numInterval.Value; AppSettings.Save(settings); });
            this.Controls.Add(numInterval);

            // Key Selector
            lblKeyLabel = new Label() { Text = "KEY TO PRESS", ForeColor = Color.Gray, Font = new Font("Segoe UI", 8), AutoSize = true };
            this.Controls.Add(lblKeyLabel);

            cmbKey = new ComboBox() { BackColor = ColDarkAcc, ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 10), Size = new Size(250, 30), DropDownStyle = ComboBoxStyle.DropDownList };
            foreach (Keys k in new Keys[] { Keys.Space, Keys.W, Keys.A, Keys.S, Keys.D, Keys.Enter, Keys.Escape, Keys.Up, Keys.Down, Keys.Left, Keys.Right, Keys.F5, Keys.ShiftKey, Keys.ControlKey })
            {
                cmbKey.Items.Add(k);
            }
            cmbKey.SelectedItem = settings.SelectedKey;
            cmbKey.SelectedIndexChanged += new EventHandler(delegate { if(cmbKey.SelectedItem is Keys) settings.SelectedKey = (Keys)cmbKey.SelectedItem; AppSettings.Save(settings); });
            this.Controls.Add(cmbKey);

            // On Top
            chkOnTop = new CheckBox() { Text = "Always on Top", ForeColor = Color.Gray, Font = new Font("Segoe UI", 9), AutoSize = true, Checked = settings.AlwaysOnTop };
            chkOnTop.CheckedChanged += new EventHandler(delegate { this.TopMost = chkOnTop.Checked; settings.AlwaysOnTop = chkOnTop.Checked; AppSettings.Save(settings); });
            this.Controls.Add(chkOnTop);

            // Version (v1.0)
            Label ver = new Label() { Text = "v1.0", ForeColor = Color.DimGray, Font = new Font("Segoe UI", 8), Location = new Point(300, 590), AutoSize = true };
            this.Controls.Add(ver);
        }

        private void UpdateVisibility()
        {
            // Dynamic Layout Start Point (Further down)
            int startY = 340; 
            int gap = 60;
            
            // 1. Mode (Always First)
            lblMode.Location = new Point(50, startY);
            cmbMode.Location = new Point(50, startY + 20);
            
            int nextY = startY + gap;

            bool isKeyboard = (cmbMode.SelectedIndex == 1);

            // 2. Key Selector (Only if Keyboard)
            if (isKeyboard)
            {
                lblKeyLabel.Visible = true;
                cmbKey.Visible = true;
                
                lblKeyLabel.Location = new Point(50, nextY);
                cmbKey.Location = new Point(50, nextY + 20);
                
                nextY += gap;
            }
            else
            {
                lblKeyLabel.Visible = false;
                cmbKey.Visible = false;
            }

            // 3. Interval
            lblInt.Location = new Point(50, nextY);
            numInterval.Location = new Point(50, nextY + 20);
            
            nextY += gap;

            // 4. On Top
            chkOnTop.Location = new Point(50, nextY + 20);
        }

        private void btnToggle_Click(object sender, EventArgs e)
        {
            isActive = !isActive;
            UpdateState();
        }

        private DateTime nextActionTime;

        private void UpdateState()
        {
            if (isActive)
            {
                lblStatus.Text = "RUNNING";
                lblStatus.ForeColor = ColNeonGreen;
                btnToggle.Text = "STOP";
                btnToggle.BackColor = Color.Gray; 
                trayIcon.Text = "AntiAFK: RUNNING";
                
                // Disable controls while running
                cmbMode.Enabled = false;
                numInterval.Enabled = false;
                cmbKey.Enabled = false;

                // Start Timer
                int intervalMs = (int)numInterval.Value * 1000;
                actionTimer.Interval = intervalMs;
                actionTimer.Start();
                
                nextActionTime = DateTime.Now.AddMilliseconds(intervalMs);
                uiUpdateTimer.Start();

                lblActionInfo.Text = string.Format("Next action in {0}s", (int)numInterval.Value);
            }
            else
            {
                lblStatus.Text = "STOPPED";
                lblStatus.ForeColor = Color.Gray;
                btnToggle.Text = "START";
                btnToggle.BackColor = ColBlue; 
                trayIcon.Text = "AntiAFK: STOPPED";

                cmbMode.Enabled = true;
                numInterval.Enabled = true;
                cmbKey.Enabled = true;

                actionTimer.Stop();
                uiUpdateTimer.Stop();
                lblActionInfo.Text = "Ready";
            }
        }

        private void actionTimer_Tick(object sender, EventArgs e)
        {
            PerformAction();
            nextActionTime = DateTime.Now.AddMilliseconds(actionTimer.Interval);
        }

        private void uiUpdateTimer_Tick(object sender, EventArgs e)
        {
            TimeSpan diff = nextActionTime - DateTime.Now;
            if (diff.TotalSeconds < 0) diff = TimeSpan.Zero;
            lblActionInfo.Text = string.Format("Action in {0:0.0}s", diff.TotalSeconds);
        }

        private void PerformAction()
        {
            if (cmbMode.SelectedIndex == 0)
            {
                // Mouse
                InputHelper.JiggleMouse();
            }
            else
            {
                // Keyboard
                try {
                     if (cmbKey.SelectedItem is Keys) {
                         Keys k = (Keys)cmbKey.SelectedItem;
                         InputHelper.PressKey(k);
                     }
                } catch {}
            }
        }

        private void InitializeTray()
        {
            trayMenu = new ContextMenu();
            trayMenu.MenuItems.Add("Show", new EventHandler(delegate { this.Show(); this.WindowState = FormWindowState.Normal; this.Activate(); }));
            trayMenu.MenuItems.Add("Toggle", new EventHandler(delegate { btnToggle_Click(null, null); }));
            trayMenu.MenuItems.Add("-");
            trayMenu.MenuItems.Add("Exit", new EventHandler(delegate { trayIcon.Visible = false; Application.Exit(); }));

            trayIcon = new NotifyIcon();
            trayIcon.Text = "AntiAFK";
            trayIcon.Icon = this.Icon;
            trayIcon.ContextMenu = trayMenu;
            trayIcon.Visible = true;
            trayIcon.DoubleClick += new EventHandler(delegate { this.Show(); this.WindowState = FormWindowState.Normal; this.Activate(); });
        }

        [STAThread]
        static void Main()
        {
            bool createdNew;
            using (System.Threading.Mutex mutex = new System.Threading.Mutex(true, "AntiAFK_Singleton_Mutex", out createdNew))
            {
                if (!createdNew)
                {
                    MessageBox.Show("AntiAFK is already running!", "AntiAFK", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm());
            }
        }
    }
}
