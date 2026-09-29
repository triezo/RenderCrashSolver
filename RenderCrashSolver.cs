// RenderCrashSolver - GUI for resuming Blender animation renders after crashes.
// Build with build.bat (uses the C# compiler that ships with .NET Framework 4, C# 5 syntax only).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Media;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;

namespace RenderCrashSolver
{
    static class Program
    {
        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        [STAThread]
        static void Main()
        {
            try { SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    class Settings
    {
        public string BlenderExe = FindBlender();
        public string BlendFile = "";
        public string LogDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
        public string OutputDir = "";     // empty = keep scene.render.filepath directory
        public string OutputPrefix = "";  // empty = keep scene file name prefix
        public bool OverrideOutput = false;
        public int MaxRetries = 50;
        public int RetryPauseSec = 10;
        public int HangTimeoutMin = 60;
        // Telegram notifications
        public bool TgEnabled = false;
        public string TgToken = "";
        public string TgChats = "";       // chat IDs separated by commas
        public bool TgOnDone = true;
        public bool TgOnFail = true;
        public bool TgOnCrash = false;
        public bool TgPhoto = true;

        // newest blender.exe from the standard installer or Steam locations, "" if none
        public static string FindBlender()
        {
            string best = "";
            Version bestVersion = null;
            try
            {
                var roots = new[]
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                };
                foreach (string root in roots)
                {
                    string foundation = Path.Combine(root, "Blender Foundation");
                    if (!Directory.Exists(foundation)) continue;
                    foreach (string dir in Directory.GetDirectories(foundation))
                    {
                        string exe = Path.Combine(dir, "blender.exe");
                        if (!File.Exists(exe)) continue;
                        // "Blender 4.3" -> 4.3; folders without a version rank lowest
                        Match m = Regex.Match(Path.GetFileName(dir), @"(\d+)\.(\d+)");
                        Version v = m.Success ? new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)) : new Version(0, 0);
                        if (bestVersion == null || v > bestVersion) { best = exe; bestVersion = v; }
                    }
                }
                if (best.Length == 0)
                {
                    string steam = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                        @"Steam\steamapps\common\Blender\blender.exe");
                    if (File.Exists(steam)) best = steam;
                }
            }
            catch { }
            return best;
        }

        static string FilePath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.ini"); }
        }

        public static Settings Load()
        {
            var s = new Settings();
            if (!File.Exists(FilePath)) return s;
            try
            {
                foreach (string raw in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    int eq = raw.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = raw.Substring(0, eq).Trim();
                    string val = raw.Substring(eq + 1).Trim();
                    int n;
                    switch (key)
                    {
                        case "BlenderExe": s.BlenderExe = val; break;
                        case "BlendFile": s.BlendFile = val; break;
                        case "LogDir": s.LogDir = val; break;
                        case "OutputDir": s.OutputDir = val; break;
                        case "OutputPrefix": s.OutputPrefix = val; break;
                        case "OverrideOutput": s.OverrideOutput = val == "1"; break;
                        case "MaxRetries": if (int.TryParse(val, out n)) s.MaxRetries = n; break;
                        case "RetryPauseSec": if (int.TryParse(val, out n)) s.RetryPauseSec = n; break;
                        case "HangTimeoutMin": if (int.TryParse(val, out n)) s.HangTimeoutMin = n; break;
                        case "TgEnabled": s.TgEnabled = val == "1"; break;
                        case "TgToken": s.TgToken = val; break;
                        case "TgChats": s.TgChats = val; break;
                        case "TgOnDone": s.TgOnDone = val == "1"; break;
                        case "TgOnFail": s.TgOnFail = val == "1"; break;
                        case "TgOnCrash": s.TgOnCrash = val == "1"; break;
                        case "TgPhoto": s.TgPhoto = val == "1"; break;
                    }
                }
            }
            catch { }
            return s;
        }

        public void Save()
        {
            try
            {
                File.WriteAllLines(FilePath, new[]
                {
                    "BlenderExe=" + BlenderExe,
                    "BlendFile=" + BlendFile,
                    "LogDir=" + LogDir,
                    "OutputDir=" + OutputDir,
                    "OutputPrefix=" + OutputPrefix,
                    "OverrideOutput=" + (OverrideOutput ? "1" : "0"),
                    "MaxRetries=" + MaxRetries,
                    "RetryPauseSec=" + RetryPauseSec,
                    "HangTimeoutMin=" + HangTimeoutMin,
                    "TgEnabled=" + (TgEnabled ? "1" : "0"),
                    "TgToken=" + TgToken,
                    "TgChats=" + TgChats,
                    "TgOnDone=" + (TgOnDone ? "1" : "0"),
                    "TgOnFail=" + (TgOnFail ? "1" : "0"),
                    "TgOnCrash=" + (TgOnCrash ? "1" : "0"),
                    "TgPhoto=" + (TgPhoto ? "1" : "0"),
                }, new UTF8Encoding(false));
            }
            catch { }
        }
    }

    enum RunState { Idle, Rendering, Waiting, Done, Stopped, Failed }

    class MainForm : Form
    {
        const string AppTitle = "RenderCrashSolver 0.1";
#if TELEGRAM
        static readonly bool TelegramFeature = true;
#else
        // shared test builds (package.bat) ship without Telegram until it is tested
        static readonly bool TelegramFeature = false;
#endif
        const string ScriptTag = "[resume_render] ";
        const int ScriptErrorExitCode = 2;

        [DllImport("kernel32.dll")]
        static extern uint SetThreadExecutionState(uint flags);
        const uint ES_CONTINUOUS = 0x80000000;
        const uint ES_SYSTEM_REQUIRED = 0x00000001;

        Settings settings;

        TextBox txtBlender, txtBlend, txtOutputDir, txtOutputPrefix, txtLogDir, txtLog;
        CheckBox chkOverride;
        Label lblScenePath, lblEffectivePath;
        Button btnSceneRefresh, btnOutputBrowse;

        // scene output settings read from the .blend via "resume_render.py -- --info"
        string sceneFilepath, sceneExt;
        Process infoProc;
        int infoRequestId;
        DateTime sceneInfoDueAt = DateTime.MinValue;
        NumericUpDown numRetries, numPause, numHang;
        Button btnStart, btnStop, btnLogs, btnOutput, btnTelegram;
        Label lblStatus, lblFrames, lblAttempts, lblTimes;
        ProgressBar progress;
        NotifyIcon tray;
        Timer timer;
        List<Control> settingsControls = new List<Control>();
        PreviewPanel preview;

        // session state, UI thread only
        RunState state = RunState.Idle;
        string finishMessage = "";
        int attempt, crashes, hangs;
        DateTime sessionStart, sessionEnd, retryAt;
        Process proc;
        bool stopRequested, killedByWatchdog;

        // shared with the output reader threads, guarded by sync
        readonly object sync = new object();
        readonly Queue<string> uiLines = new Queue<string>();
        readonly HashSet<string> seenWarnings = new HashSet<string>();
        readonly List<double> durations = new List<double>();
        StreamWriter logWriter;
        int total, done, currentFrame;
        DateTime lastProgress;
        bool allDoneSeen;
        string outputDir;
        // preview updates produced by the reader threads, applied on the UI thread
        string renderExt, pendingSequencePath;
        readonly List<KeyValuePair<int, string>> pendingFrames = new List<KeyValuePair<int, string>>();

        public MainForm()
        {
            settings = Settings.Load();
            BuildUi();
            SetRunningUi(false);
            UpdateLabels();
            UpdateTelegramButton();
            sceneInfoDueAt = DateTime.Now; // read the scene output path on startup
        }

        // ─── UI construction ─────────────────────────────────────

        void BuildUi()
        {
            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = AppTitle;
            Font = new Font("Segoe UI", 9F);
            ClientSize = new Size(1500, 820);
            MinimumSize = new Size(900, 540);
            StartPosition = FormStartPosition.CenterScreen;
            Icon = SystemIcons.Application;
            Load += delegate
            {
                // laptops / 125-150% scaling: the default size may not fit the screen
                Rectangle area = Screen.FromControl(this).WorkingArea;
                if (Width > area.Width || Height > area.Height) WindowState = FormWindowState.Maximized;
            };

            // left: settings / progress / log, right: frame preview
            var split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1 };
            Controls.Add(split);
            Shown += delegate
            {
                float scale;
                using (Graphics g = CreateGraphics()) scale = g.DpiX / 96F;
                try
                {
                    split.Panel1MinSize = (int)(840 * scale);
                    split.Panel2MinSize = (int)(200 * scale);
                    split.SplitterDistance = (int)(860 * scale);
                }
                catch { } // window smaller than the minimums: keep defaults
            };

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(10) };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            split.Panel1.Controls.Add(root);

            var grpPreview = new GroupBox { Text = "Просмотр кадров", Dock = DockStyle.Fill, Padding = new Padding(8) };
            preview = new PreviewPanel();
            grpPreview.Controls.Add(preview);
            var previewHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 10, 10, 10) };
            previewHost.Controls.Add(grpPreview);
            split.Panel2.Controls.Add(previewHost);

            // settings
            var grpSettings = new GroupBox { Text = "Настройки", Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(8) };
            var tbl = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3 };
            tbl.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            tbl.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grpSettings.Controls.Add(tbl);

            txtBlender = AddPathRow(tbl, 0, "Blender.exe", settings.BlenderExe, delegate
            {
                BrowseFile(txtBlender, "Blender|blender.exe|Программы (*.exe)|*.exe");
            });
            txtBlend = AddPathRow(tbl, 1, "Сцена .blend", settings.BlendFile, delegate
            {
                BrowseFile(txtBlend, "Blender scene (*.blend)|*.blend");
            });
            // output: the scene's own path is primary, the override is optional
            tbl.Controls.Add(new Label { Text = "Вывод (сцена)", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 8, 3) }, 0, 2);
            lblScenePath = new Label { AutoSize = false, Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, Height = txtBlend.Height + 4, Margin = new Padding(3, 2, 3, 2), Text = "—" };
            tbl.Controls.Add(lblScenePath, 1, 2);
            btnSceneRefresh = new Button { Text = "↻", Width = 36, Height = txtBlend.Height + 2, Margin = new Padding(3, 2, 3, 2) };
            btnSceneRefresh.Click += delegate { RefreshSceneInfo(); };
            new ToolTip().SetToolTip(btnSceneRefresh, "Перечитать путь вывода из .blend");
            tbl.Controls.Add(btnSceneRefresh, 2, 2);
            settingsControls.Add(btnSceneRefresh);

            chkOverride = new CheckBox { Text = "Свой путь вывода (вместо пути из сцены; .blend не изменяется)", AutoSize = true, Checked = settings.OverrideOutput, Margin = new Padding(3, 4, 3, 2) };
            tbl.Controls.Add(chkOverride, 0, 3);
            tbl.SetColumnSpan(chkOverride, 3);
            settingsControls.Add(chkOverride);

            txtOutputDir = AddPathRow(tbl, 4, "   Папка", settings.OutputDir, delegate
            {
                BrowseFolder(txtOutputDir);
            });
            SetCueBanner(txtOutputDir, "пусто — папка из сцены");
            btnOutputBrowse = (Button)tbl.GetControlFromPosition(2, 4);
            txtOutputPrefix = AddTextRow(tbl, 5, "   Имя файла", settings.OutputPrefix);
            SetCueBanner(txtOutputPrefix, "пусто — имя из сцены; например shot_ → shot_0001.jpg");
            lblEffectivePath = new Label { AutoSize = false, Dock = DockStyle.Fill, AutoEllipsis = true, Height = txtBlend.Height, Margin = new Padding(3, 0, 3, 4), ForeColor = SystemColors.GrayText };
            tbl.Controls.Add(lblEffectivePath, 1, 6);
            tbl.SetColumnSpan(lblEffectivePath, 2);

            chkOverride.CheckedChanged += delegate
            {
                // prefill with the scene values so the user edits from what Blender uses
                if (chkOverride.Checked && txtOutputDir.Text.Length == 0 && txtOutputPrefix.Text.Length == 0 && sceneFilepath != null)
                {
                    txtOutputDir.Text = Path.GetDirectoryName(sceneFilepath);
                    txtOutputPrefix.Text = Path.GetFileName(sceneFilepath);
                }
                UpdateOutputUi();
            };
            txtOutputDir.TextChanged += delegate { UpdateOutputUi(); };
            txtOutputPrefix.TextChanged += delegate { UpdateOutputUi(); };
            txtBlend.TextChanged += delegate { sceneInfoDueAt = DateTime.Now.AddMilliseconds(800); };
            txtBlender.TextChanged += delegate { sceneInfoDueAt = DateTime.Now.AddMilliseconds(800); };

            txtLogDir = AddPathRow(tbl, 7, "Папка логов", settings.LogDir, delegate
            {
                BrowseFolder(txtLogDir);
            });

            var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, Margin = new Padding(0, 4, 0, 0) };
            numRetries = AddNumber(flow, "Макс. попыток", 1, 1000, settings.MaxRetries);
            numPause = AddNumber(flow, "Пауза перед перезапуском, с", 0, 600, settings.RetryPauseSec);
            numHang = AddNumber(flow, "Перезапуск, если нет нового кадра, мин (0 = выкл.)", 0, 1440, settings.HangTimeoutMin);
            tbl.Controls.Add(flow, 0, 8);
            tbl.SetColumnSpan(flow, 3);
            root.Controls.Add(grpSettings, 0, 0);

            // progress
            var grpProgress = new GroupBox { Text = "Прогресс", Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(8) };
            var ptbl = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1 };
            ptbl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            lblStatus = new Label { AutoSize = true, Font = new Font("Segoe UI", 13F, FontStyle.Bold), Margin = new Padding(3, 0, 3, 6) };
            progress = new ProgressBar { Dock = DockStyle.Fill, Height = 24, Minimum = 0, Maximum = 1 };
            lblFrames = new Label { AutoSize = true, Margin = new Padding(3, 8, 3, 2) };
            lblAttempts = new Label { AutoSize = true, Margin = new Padding(3, 2, 3, 2) };
            lblTimes = new Label { AutoSize = true, Margin = new Padding(3, 2, 3, 2) };
            ptbl.Controls.Add(lblStatus);
            ptbl.Controls.Add(progress);
            ptbl.Controls.Add(lblFrames);
            ptbl.Controls.Add(lblAttempts);
            ptbl.Controls.Add(lblTimes);
            grpProgress.Controls.Add(ptbl);
            root.Controls.Add(grpProgress, 0, 1);

            // buttons
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 6, 0, 6) };
            btnStart = MakeButton("▶  Старт", delegate { StartSession(); });
            btnStart.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
            btnStop = MakeButton("■  Стоп", delegate { StopSession(); });
            btnLogs = MakeButton("Папка логов", delegate { OpenFolder(txtLogDir.Text); });
            btnOutput = MakeButton("Папка рендера", delegate
            {
                string dir;
                lock (sync) dir = outputDir;
                OpenFolder(dir);
            });
            btnTelegram = MakeButton("Telegram…", delegate
            {
                using (var dlg = new TelegramForm(settings)) dlg.ShowDialog(this);
                UpdateTelegramButton();
            });
            btnTelegram.Visible = TelegramFeature;
            buttons.Controls.AddRange(new Control[] { btnStart, btnStop, btnLogs, btnOutput, btnTelegram });
            root.Controls.Add(buttons, 0, 2);

            // short log
            var grpLog = new GroupBox { Text = "Лог (события, ошибки, уникальные предупреждения; полный лог — в файле)", Dock = DockStyle.Fill, Padding = new Padding(8) };
            txtLog = new TextBox
            {
                Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
                WordWrap = true, Font = new Font("Consolas", 9F), BackColor = SystemColors.Window,
            };
            grpLog.Controls.Add(txtLog);
            root.Controls.Add(grpLog, 0, 3);

            tray = new NotifyIcon { Icon = SystemIcons.Application, Text = "RenderCrashSolver", Visible = true };
            tray.Click += delegate { Show(); if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal; Activate(); };

            timer = new Timer { Interval = 500 };
            timer.Tick += delegate { OnTick(); };
            timer.Start();

            ResumeLayout(true);
        }

        TextBox AddPathRow(TableLayoutPanel tbl, int row, string label, string value, EventHandler browse)
        {
            var lbl = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 8, 3) };
            var txt = new TextBox { Text = value, Dock = DockStyle.Fill, Margin = new Padding(3, 3, 3, 3) };
            var btn = new Button { Text = "…", Width = 36, Height = txt.Height + 2, Margin = new Padding(3, 2, 3, 2) };
            btn.Click += browse;
            tbl.Controls.Add(lbl, 0, row);
            tbl.Controls.Add(txt, 1, row);
            tbl.Controls.Add(btn, 2, row);
            settingsControls.Add(txt);
            settingsControls.Add(btn);
            return txt;
        }

        TextBox AddTextRow(TableLayoutPanel tbl, int row, string label, string value)
        {
            var lbl = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 8, 3) };
            var txt = new TextBox { Text = value, Dock = DockStyle.Fill, Margin = new Padding(3, 3, 3, 3) };
            tbl.Controls.Add(lbl, 0, row);
            tbl.Controls.Add(txt, 1, row);
            settingsControls.Add(txt);
            return txt;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        // grey hint text shown while the box is empty
        static void SetCueBanner(TextBox box, string hint)
        {
            const int EM_SETCUEBANNER = 0x1501;
            box.HandleCreated += delegate { SendMessage(box.Handle, EM_SETCUEBANNER, (IntPtr)1, hint); };
            if (box.IsHandleCreated) SendMessage(box.Handle, EM_SETCUEBANNER, (IntPtr)1, hint);
        }

        void BrowseFolder(TextBox target)
        {
            using (var dlg = new FolderBrowserDialog())
            {
                if (Directory.Exists(target.Text)) dlg.SelectedPath = target.Text;
                if (dlg.ShowDialog(this) == DialogResult.OK) target.Text = dlg.SelectedPath;
            }
        }

        NumericUpDown AddNumber(FlowLayoutPanel flow, string label, int min, int max, int value)
        {
            var lbl = new Label { Text = label, AutoSize = true, Margin = new Padding(3, 6, 3, 3) };
            var num = new NumericUpDown { Minimum = min, Maximum = max, Width = 64, Margin = new Padding(3, 3, 18, 3) };
            num.Value = Math.Max(min, Math.Min(max, value));
            flow.Controls.Add(lbl);
            flow.Controls.Add(num);
            settingsControls.Add(num);
            return num;
        }

        Button MakeButton(string text, EventHandler click)
        {
            var b = new Button { Text = text, AutoSize = true, Padding = new Padding(10, 3, 10, 3), Margin = new Padding(0, 0, 8, 0) };
            b.Click += click;
            return b;
        }

        void BrowseFile(TextBox target, string filter)
        {
            using (var dlg = new OpenFileDialog { Filter = filter })
            {
                try
                {
                    string dir = Path.GetDirectoryName(target.Text);
                    if (Directory.Exists(dir)) dlg.InitialDirectory = dir;
                }
                catch { }
                if (dlg.ShowDialog(this) == DialogResult.OK) target.Text = dlg.FileName;
            }
        }

        void OpenFolder(string dir)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                MessageBox.Show(this, "Папка не найдена:\n" + dir, "RenderCrashSolver", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            Process.Start("explorer.exe", "\"" + dir + "\"");
        }

        void SetRunningUi(bool running)
        {
            foreach (Control c in settingsControls) c.Enabled = !running;
            btnStart.Enabled = !running;
            btnStop.Enabled = running;
            UpdateOutputUi();
        }

        // ─── scene output path ───────────────────────────────────

        // Blender appends the frame number (4 digits) unless the name already has #### in it
        string FramePathDisplay(string pathNoExt)
        {
            string name = Path.GetFileName(pathNoExt);
            return pathNoExt + (name.Contains("#") ? "" : "####") + "." + (sceneExt ?? "ext");
        }

        void UpdateOutputUi()
        {
            bool editable = chkOverride.Enabled && chkOverride.Checked;
            txtOutputDir.Enabled = editable;
            btnOutputBrowse.Enabled = editable;
            txtOutputPrefix.Enabled = editable;

            string effective = EffectiveOutputPath();
            if (!chkOverride.Checked)
                lblEffectivePath.Text = "";
            else if (effective == null)
                lblEffectivePath.Text = "Кадры будут сохраняться в: (прочитайте сцену кнопкой ↻)";
            else
                lblEffectivePath.Text = "Кадры будут сохраняться в:  " + FramePathDisplay(effective);

            // while rendering the preview follows the path reported by Blender itself
            if (btnStart.Enabled && effective != null && sceneExt != null)
                preview.SetSequence(effective, sceneExt);
        }

        // scene.render.filepath with the optional override applied; null while the scene is unknown
        string EffectiveOutputPath()
        {
            string dir = "", prefix = "";
            if (chkOverride.Checked)
            {
                dir = txtOutputDir.Text.Trim().Trim('"').TrimEnd('\\', '/');
                prefix = txtOutputPrefix.Text.Trim();
            }
            if (sceneFilepath == null && (dir.Length == 0 || prefix.Length == 0)) return null;
            if (dir.Length == 0) dir = Path.GetDirectoryName(sceneFilepath);
            if (prefix.Length == 0) prefix = Path.GetFileName(sceneFilepath);
            try { return Path.Combine(dir, prefix); }
            catch { return null; } // invalid characters while typing
        }

        void RefreshSceneInfo()
        {
            sceneInfoDueAt = DateTime.MinValue;
            if (infoProc != null)
            {
                try { infoProc.Kill(); } catch { }
                infoProc = null;
            }
            string blender = txtBlender.Text.Trim().Trim('"');
            string blend = txtBlend.Text.Trim().Trim('"');
            if (!File.Exists(blender) || !File.Exists(blend) || !File.Exists(ScriptPath))
            {
                sceneFilepath = null;
                lblScenePath.Text = "— (укажите blender.exe и .blend)";
                UpdateOutputUi();
                return;
            }

            int id = ++infoRequestId;
            lblScenePath.Text = "Читаю сцену в Blender…";
            var psi = new ProcessStartInfo(blender,
                string.Format("-b \"{0}\" --python-exit-code {1} -P \"{2}\" -- --info", blend, ScriptErrorExitCode, ScriptPath))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = Path.GetDirectoryName(blend),
            };
            psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";

            var lines = new List<string>();
            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            DataReceivedEventHandler collect = (s, e) =>
            {
                if (e.Data == null) return;
                int idx = e.Data.IndexOf(ScriptTag, StringComparison.Ordinal);
                if (idx >= 0) lock (lines) lines.Add(e.Data.Substring(idx + ScriptTag.Length).Trim());
            };
            p.OutputDataReceived += collect;
            p.ErrorDataReceived += collect;
            p.Exited += (s, e) =>
            {
                p.WaitForExit();
                try { if (!IsDisposed) BeginInvoke(new Action(() => ApplySceneInfo(id, p, lines))); } catch { }
            };
            try
            {
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                infoProc = p;
            }
            catch (Exception ex)
            {
                lblScenePath.Text = "Не удалось запустить Blender: " + ex.Message;
            }
        }

        void ApplySceneInfo(int id, Process p, List<string> lines)
        {
            if (infoProc == p) infoProc = null;
            p.Dispose();
            if (id != infoRequestId) return; // a newer request superseded this one

            string path = null, frames = null, status = null;
            string ext = null;
            lock (lines)
            {
                foreach (string msg in lines)
                {
                    if (msg.StartsWith("SCENE_PATH ")) path = msg.Substring(11).Trim();
                    else if (msg.StartsWith("SCENE_EXT ")) ext = msg.Substring(10).Trim();
                    else if (msg.StartsWith("SCENE_FRAMES "))
                    {
                        string[] parts = msg.Split(' ');
                        if (parts.Length >= 3) frames = string.Format("кадры {0}–{1}", parts[1], parts[2]);
                    }
                    else if (msg.StartsWith("STATUS "))
                    {
                        Match m = StatusRe.Match(msg);
                        if (m.Success) status = string.Format("готово {0} из {1}", m.Groups[2].Value, m.Groups[1].Value);
                    }
                }
            }

            if (path == null)
            {
                sceneFilepath = null;
                lblScenePath.Text = "Не удалось прочитать сцену (подробности — при запуске рендера в логе)";
                UpdateOutputUi();
                return;
            }
            sceneFilepath = path;
            sceneExt = ext == "FFMPEG" ? null : ext;
            string text = ext == "FFMPEG"
                ? path + "   (видео FFMPEG — продолжение по кадрам невозможно, переключите на JPG/PNG/EXR)"
                : FramePathDisplay(path);
            if (frames != null) text += "     ·  " + frames;
            if (status != null) text += "  ·  " + status;
            lblScenePath.Text = text;
            UpdateOutputUi();
        }

        // ─── session control ─────────────────────────────────────

        string ScriptPath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "resume_render.py"); }
        }

        void ReadSettingsFromUi()
        {
            settings.BlenderExe = txtBlender.Text.Trim().Trim('"');
            settings.BlendFile = txtBlend.Text.Trim().Trim('"');
            // trailing backslash would escape the closing quote on the command line
            settings.OutputDir = txtOutputDir.Text.Trim().Trim('"').TrimEnd('\\', '/');
            settings.OutputPrefix = txtOutputPrefix.Text.Trim();
            settings.OverrideOutput = chkOverride.Checked;
            settings.LogDir = txtLogDir.Text.Trim().Trim('"');
            settings.MaxRetries = (int)numRetries.Value;
            settings.RetryPauseSec = (int)numPause.Value;
            settings.HangTimeoutMin = (int)numHang.Value;
        }

        void StartSession()
        {
            ReadSettingsFromUi();

            string error = null;
            if (!File.Exists(settings.BlenderExe)) error = "Не найден blender.exe:\n" + settings.BlenderExe;
            else if (!File.Exists(settings.BlendFile)) error = "Не найден .blend файл:\n" + settings.BlendFile;
            else if (!File.Exists(ScriptPath))
                error = "Рядом с программой не найден resume_render.py:\n" + ScriptPath +
                        "\n\nЕсли программа запущена прямо из zip-архива — распакуйте архив целиком в обычную папку и запустите оттуда.";
            else if (settings.OverrideOutput && settings.OutputDir.Length > 0 && !Path.IsPathRooted(settings.OutputDir))
                error = "Папка вывода должна быть полным путём, например E:\\Render\\shot01";
            else if (settings.OverrideOutput && settings.OutputPrefix.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || settings.OutputPrefix.Contains("\""))
                error = "Имя файла содержит недопустимые символы: " + settings.OutputPrefix;
            else
            {
                try { Directory.CreateDirectory(settings.LogDir); }
                catch (Exception ex) { error = "Не удалось создать папку логов:\n" + ex.Message; }
                if (error == null && settings.OverrideOutput && settings.OutputDir.Length > 0)
                {
                    try { Directory.CreateDirectory(settings.OutputDir); }
                    catch (Exception ex) { error = "Не удалось создать папку вывода:\n" + ex.Message; }
                }
            }
            if (error != null)
            {
                MessageBox.Show(this, error, "RenderCrashSolver", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            settings.Save();

            // do not load the scene twice in RAM: cancel a pending scene info read
            sceneInfoDueAt = DateTime.MinValue;
            infoRequestId++;
            if (infoProc != null) { try { infoProc.Kill(); } catch { } infoProc = null; }

            attempt = crashes = hangs = 0;
            stopRequested = false;
            sessionStart = DateTime.Now;
            lock (sync)
            {
                total = done = currentFrame = 0;
                durations.Clear();
                seenWarnings.Clear();
                outputDir = null;
            }
            if (txtLog.TextLength > 0) AddUi("");
            AddUi("Старт: " + Path.GetFileName(settings.BlendFile));
            if (settings.OverrideOutput)
                AddUi(string.Format("Путь вывода переопределён: папка = {0}, имя = {1}",
                    settings.OutputDir.Length > 0 ? settings.OutputDir : "(из сцены)",
                    settings.OutputPrefix.Length > 0 ? settings.OutputPrefix : "(из сцены)"));
            SetRunningUi(true);
            SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED); // keep PC awake during the render
            Launch();
        }

        void Launch()
        {
            attempt++;
            killedByWatchdog = false;
            string logPath = Path.Combine(settings.LogDir, "render_log_" + DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".txt");
            lock (sync)
            {
                allDoneSeen = false;
                currentFrame = 0;
                lastProgress = DateTime.Now;
                try
                {
                    logWriter = new StreamWriter(logPath, true, new UTF8Encoding(false));
                    logWriter.WriteLine();
                    logWriter.WriteLine("===================================================");
                    logWriter.WriteLine("[RenderCrashSolver] Launch attempt #{0} - {1:yyyy-MM-dd HH:mm:ss}", attempt, DateTime.Now);
                    logWriter.WriteLine("===================================================");
                }
                catch (Exception ex)
                {
                    logWriter = null;
                    uiLines.Enqueue(Stamp("Не удалось открыть файл лога: " + ex.Message));
                }
            }
            AddUi(string.Format("── Попытка #{0} из {1} ──", attempt, settings.MaxRetries));

            // everything after "--" is ignored by Blender and read by resume_render.py
            var args = new StringBuilder();
            args.AppendFormat("-b \"{0}\" --python-exit-code {1} -P \"{2}\" --", settings.BlendFile, ScriptErrorExitCode, ScriptPath);
            if (settings.OverrideOutput && settings.OutputDir.Length > 0) args.AppendFormat(" --output-dir \"{0}\"", settings.OutputDir);
            if (settings.OverrideOutput && settings.OutputPrefix.Length > 0) args.AppendFormat(" --prefix \"{0}\"", settings.OutputPrefix);

            var psi = new ProcessStartInfo(settings.BlenderExe, args.ToString())
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = Path.GetDirectoryName(settings.BlendFile),
            };
            psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            psi.EnvironmentVariables["PYTHONUNBUFFERED"] = "1";

            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.OutputDataReceived += (s, e) => { if (e.Data != null) HandleLine(e.Data); };
            p.ErrorDataReceived += (s, e) => { if (e.Data != null) HandleLine(e.Data); };
            p.Exited += (s, e) =>
            {
                p.WaitForExit(); // drains the async output readers
                int code = p.ExitCode;
                try { if (!IsDisposed) BeginInvoke(new Action(() => OnExited(code))); } catch { }
            };

            try
            {
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                CloseLog(null);
                Finish(RunState.Failed, "Не удалось запустить Blender: " + ex.Message);
                return;
            }
            proc = p;
            state = RunState.Rendering;
        }

        void OnExited(int code)
        {
            CloseLog(string.Format("[RenderCrashSolver] Blender exited with code {0}", code));
            if (proc != null) { proc.Dispose(); proc = null; }

            bool allDone;
            lock (sync) allDone = allDoneSeen;

            if (stopRequested) { Finish(RunState.Stopped, "Остановлено вручную."); return; }
            if (allDone) { Finish(RunState.Done, "Все кадры отрендерены."); return; }
            if (code == ScriptErrorExitCode)
            {
                Finish(RunState.Failed, "Ошибка в resume_render.py (код 2) — подробности в логе выше.");
                return;
            }

            if (killedByWatchdog)
                AddUi("Blender остановлен из-за зависания.");
            else if (code != 0)
            {
                crashes++;
                AddUi(string.Format("Blender упал: код {0} (0x{0:X8}).", code));
                if (settings.TgOnCrash && attempt < settings.MaxRetries)
                    NotifyTelegram(string.Format("⚠ Blender упал (код 0x{0:X8}), перезапуск через {1} с — попытка {2} из {3}.",
                        code, settings.RetryPauseSec, attempt + 1, settings.MaxRetries), false);
            }
            else
                AddUi("Blender завершился (код 0), но завершение рендера не подтверждено — перезапуск.");

            if (attempt >= settings.MaxRetries)
            {
                Finish(RunState.Failed, string.Format("Достигнут лимит попыток ({0}).", settings.MaxRetries));
                return;
            }
            retryAt = DateTime.Now.AddSeconds(settings.RetryPauseSec);
            state = RunState.Waiting;
        }

        void StopSession()
        {
            if (state == RunState.Waiting)
            {
                Finish(RunState.Stopped, "Остановлено вручную.");
                return;
            }
            if (state == RunState.Rendering && proc != null)
            {
                stopRequested = true;
                btnStop.Enabled = false;
                AddUi("Остановка Blender…");
                try { proc.Kill(); } catch { }
            }
        }

        void Finish(RunState final, string message)
        {
            state = final;
            finishMessage = message;
            sessionEnd = DateTime.Now;
            AddUi("■ " + message);
            SetRunningUi(false);
            SetThreadExecutionState(ES_CONTINUOUS);
            UpdateLabels();
            sceneInfoDueAt = DateTime.Now; // refresh "done N of M" for the scene path

            if (final == RunState.Done)
            {
                SystemSounds.Asterisk.Play();
                tray.ShowBalloonTip(15000, "Рендер завершён", Path.GetFileName(settings.BlendFile) + ": " + message, ToolTipIcon.Info);
            }
            else if (final == RunState.Failed)
            {
                SystemSounds.Hand.Play();
                tray.ShowBalloonTip(15000, "Рендер остановлен с ошибкой", message, ToolTipIcon.Error);
            }

            if (final == RunState.Done && settings.TgOnDone)
                NotifyTelegram("✅ Рендер готов", true);
            else if (final == RunState.Failed && settings.TgOnFail)
                NotifyTelegram("❌ Рендер остановлен: " + message, true);
        }

        // ─── Telegram ────────────────────────────────────────────

        void UpdateTelegramButton()
        {
            btnTelegram.Text = settings.TgEnabled ? "Telegram ✓" : "Telegram…";
        }

        /// <summary>Sends headline + session details to all configured chats on a background thread.</summary>
        void NotifyTelegram(string headline, bool withPhoto)
        {
            string token = settings.TgToken;
            List<string> chats = TelegramApi.ParseChatIds(settings.TgChats);
            if (!TelegramFeature || !settings.TgEnabled || token.Length == 0 || chats.Count == 0) return;

            int t, d;
            string dir;
            lock (sync) { t = total; d = done; dir = outputDir; }
            bool active = state == RunState.Rendering || state == RunState.Waiting;
            TimeSpan elapsed = (active ? DateTime.Now : sessionEnd) - sessionStart;

            var text = new StringBuilder(headline);
            text.Append("\nСцена: ").Append(Path.GetFileName(settings.BlendFile));
            if (t > 0) text.AppendFormat("\nКадры: {0} из {1}", d, t);
            text.AppendFormat("\nВремя: {0} · крашей: {1}", FormatDuration(elapsed.TotalSeconds), crashes);
            if (!string.IsNullOrEmpty(dir)) text.Append("\nПапка: ").Append(dir);
            text.Append("\nПК: ").Append(Environment.MachineName);
            string message = text.ToString();
            string photo = withPhoto && settings.TgPhoto ? preview.LatestFramePath : null;

            ThreadPool.QueueUserWorkItem(delegate
            {
                var errors = new List<string>();
                foreach (string chat in chats)
                {
                    string err = TelegramApi.SendPhotoOrMessage(token, chat, photo, message);
                    if (err != null) errors.Add(chat + ": " + err);
                }
                lock (sync)
                {
                    uiLines.Enqueue(Stamp(errors.Count == 0
                        ? string.Format("Telegram: уведомление отправлено ({0})", chats.Count)
                        : "Telegram: не удалось отправить — " + string.Join("; ", errors)));
                }
            });
        }

        void CloseLog(string footer)
        {
            lock (sync)
            {
                if (logWriter == null) return;
                try
                {
                    if (footer != null) logWriter.WriteLine(footer);
                    logWriter.Dispose();
                }
                catch { }
                logWriter = null;
            }
        }

        // ─── Blender output parsing (reader threads) ─────────────

        static readonly Regex StatusRe = new Regex(@"total=(\d+)\s+done=(\d+)");
        static readonly Regex DigitsRe = new Regex(@"\d+");

        void HandleLine(string line)
        {
            lock (sync)
            {
                if (logWriter != null)
                {
                    try { logWriter.WriteLine(line); } catch { }
                }

                int idx = line.IndexOf(ScriptTag, StringComparison.Ordinal);
                if (idx >= 0)
                {
                    HandleScriptLine(line.Substring(idx + ScriptTag.Length).Trim());
                    return;
                }

                // checked before errors: modifier warnings contain "BKE_modifier_set_error"
                if (line.StartsWith("WARN", StringComparison.Ordinal) || line.StartsWith("Warning", StringComparison.Ordinal))
                {
                    // the same modifier warnings repeat every frame - show each kind once
                    if (seenWarnings.Count < 1000 && seenWarnings.Add(DigitsRe.Replace(line, "#")))
                        uiLines.Enqueue(Stamp("⚠ " + ShortenWarning(line.Trim())));
                }
                else if (line.IndexOf("Error", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    line.IndexOf("EXCEPTION", StringComparison.Ordinal) >= 0 ||
                    line.StartsWith("Traceback", StringComparison.Ordinal) ||
                    line.StartsWith("  File \"", StringComparison.Ordinal) ||
                    line.IndexOf(".crash.txt", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    uiLines.Enqueue(Stamp(line));
                }
            }
        }

        // called under sync
        void HandleScriptLine(string msg)
        {
            if (msg.StartsWith("FRAME_START "))
            {
                int.TryParse(msg.Substring(12).Trim(), out currentFrame);
                lastProgress = DateTime.Now;
                return;
            }
            if (msg.StartsWith("FRAME_DONE "))
            {
                string[] parts = msg.Split(new[] { ' ' }, 4); // FRAME_DONE <frame> <seconds> <path with spaces>
                double secs = 0;
                if (parts.Length >= 3) double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out secs);
                done = total > 0 ? Math.Min(done + 1, total) : done + 1;
                durations.Add(secs);
                lastProgress = DateTime.Now;
                int frameNo;
                if (parts.Length >= 4 && int.TryParse(parts[1], out frameNo))
                    pendingFrames.Add(new KeyValuePair<int, string>(frameNo, parts[3].Trim()));
                uiLines.Enqueue(Stamp(string.Format("Кадр {0} готов за {1}", parts.Length >= 2 ? parts[1] : "?", FormatDuration(secs))));
                return;
            }
            if (msg.StartsWith("STATUS "))
            {
                Match m = StatusRe.Match(msg);
                if (m.Success)
                {
                    total = int.Parse(m.Groups[1].Value);
                    done = int.Parse(m.Groups[2].Value);
                    uiLines.Enqueue(Stamp(string.Format("Кадров в диапазоне: {0}, готово: {1}, осталось: {2}", total, done, total - done)));
                }
                return;
            }
            if (msg.StartsWith("OUTPUT_EXT "))
            {
                renderExt = msg.Substring(11).Trim();
                return;
            }
            if (msg.StartsWith("OUTPUT_PATH "))
            {
                pendingSequencePath = msg.Substring(12).Trim();
                return;
            }
            if (msg.StartsWith("OUTPUT_DIR "))
            {
                outputDir = msg.Substring(11).Trim();
                uiLines.Enqueue(Stamp("Папка рендера: " + outputDir));
                return;
            }
            if (msg.StartsWith("ALL_FRAMES_DONE")) allDoneSeen = true;
            uiLines.Enqueue(Stamp(msg));
        }

        // "WARN (bke.modifier): C:\...\modifier.cc:424 BKE_modifier_set_error: Object: ..." -> "WARN (bke.modifier): Object: ..."
        static readonly Regex WarnSourceRe = new Regex(@"^(WARN \([^)]*\):)\s+\S+:\d+\s+\w+:\s*");

        static string ShortenWarning(string line)
        {
            return WarnSourceRe.Replace(line, "$1 ");
        }

        static string Stamp(string text)
        {
            return text.Length == 0 ? "" : DateTime.Now.ToString("HH:mm:ss") + "  " + text;
        }

        void AddUi(string text)
        {
            lock (sync) uiLines.Enqueue(Stamp(text));
            DrainUi();
        }

        void DrainUi()
        {
            var sb = new StringBuilder();
            string seqPath, seqExt;
            List<KeyValuePair<int, string>> newFrames = null;
            lock (sync)
            {
                while (uiLines.Count > 0) sb.Append(uiLines.Dequeue()).Append(Environment.NewLine);
                seqPath = pendingSequencePath;
                seqExt = renderExt;
                pendingSequencePath = null;
                if (pendingFrames.Count > 0)
                {
                    newFrames = new List<KeyValuePair<int, string>>(pendingFrames);
                    pendingFrames.Clear();
                }
            }
            if (seqPath != null) preview.SetSequence(seqPath, seqExt);
            if (newFrames != null)
                foreach (var f in newFrames) preview.AddFrame(f.Key, f.Value);

            if (sb.Length == 0) return;
            if (txtLog.TextLength > 400000) txtLog.Text = txtLog.Text.Substring(txtLog.TextLength - 200000);
            txtLog.AppendText(sb.ToString());
        }

        // ─── periodic update ─────────────────────────────────────

        int flushCounter;

        void OnTick()
        {
            DrainUi();

            if (state == RunState.Rendering && proc != null && !stopRequested && !killedByWatchdog && settings.HangTimeoutMin > 0)
            {
                DateTime last;
                lock (sync) last = lastProgress;
                if ((DateTime.Now - last).TotalMinutes >= settings.HangTimeoutMin)
                {
                    killedByWatchdog = true;
                    hangs++;
                    AddUi(string.Format("Нет нового кадра {0} мин — считаю, что Blender завис, перезапускаю.", settings.HangTimeoutMin));
                    try { proc.Kill(); } catch { }
                }
            }

            if (state == RunState.Waiting && DateTime.Now >= retryAt) Launch();

            // scene info is read only while idle, so it never competes with the render for RAM
            bool idle = state != RunState.Rendering && state != RunState.Waiting;
            if (idle && sceneInfoDueAt != DateTime.MinValue && DateTime.Now >= sceneInfoDueAt) RefreshSceneInfo();

            if (++flushCounter >= 10)
            {
                flushCounter = 0;
                lock (sync)
                {
                    if (logWriter != null) try { logWriter.Flush(); } catch { }
                }
            }

            UpdateLabels();
        }

        void UpdateLabels()
        {
            int t, d, cur;
            double last = 0, avg = 0;
            bool hasOutput;
            lock (sync)
            {
                t = total; d = done; cur = currentFrame;
                if (durations.Count > 0)
                {
                    last = durations[durations.Count - 1];
                    int n = Math.Min(20, durations.Count);
                    double sum = 0;
                    for (int i = durations.Count - n; i < durations.Count; i++) sum += durations[i];
                    avg = sum / n;
                }
                hasOutput = !string.IsNullOrEmpty(outputDir);
            }
            btnOutput.Enabled = hasOutput;

            int pct = t > 0 ? (int)(100L * d / t) : 0;
            switch (state)
            {
                case RunState.Idle:
                    SetStatus("Готов к запуску", SystemColors.ControlText); break;
                case RunState.Rendering:
                    SetStatus(cur > 0 ? "Рендер идёт — кадр " + cur : "Blender загружает сцену…", Color.FromArgb(0, 90, 170)); break;
                case RunState.Waiting:
                    int left = Math.Max(0, (int)Math.Ceiling((retryAt - DateTime.Now).TotalSeconds));
                    SetStatus(string.Format("Перезапуск через {0} с…", left), Color.FromArgb(190, 110, 0)); break;
                case RunState.Done:
                    SetStatus("Готово ✔  " + finishMessage, Color.FromArgb(20, 130, 50)); break;
                case RunState.Stopped:
                    SetStatus(finishMessage, SystemColors.ControlText); break;
                case RunState.Failed:
                    SetStatus("Ошибка: " + finishMessage, Color.FromArgb(190, 30, 30)); break;
            }

            progress.Maximum = Math.Max(t, 1);
            progress.Value = Math.Min(d, progress.Maximum);

            lblFrames.Text = t > 0
                ? string.Format("Кадры: {0} / {1}  ({2}%)     Осталось: {3}", d, t, pct, t - d)
                : "Кадры: —";
            lblAttempts.Text = string.Format("Попытка: {0} / {1}     Крашей: {2}     Зависаний: {3}",
                attempt, settings.MaxRetries, crashes, hangs);

            bool active = state == RunState.Rendering || state == RunState.Waiting;
            string times = "";
            if (attempt > 0)
            {
                TimeSpan elapsed = (active ? DateTime.Now : sessionEnd) - sessionStart;
                times = "Прошло: " + FormatDuration(elapsed.TotalSeconds);
            }
            if (last > 0) times += "     Последний кадр: " + FormatDuration(last) + "     Средний: " + FormatDuration(avg);
            if (active && avg > 0 && t > d)
            {
                double eta = avg * (t - d);
                times += string.Format("     Осталось ≈ {0} (к {1:HH:mm, dd.MM})", FormatDuration(eta), DateTime.Now.AddSeconds(eta));
            }
            lblTimes.Text = times.Length > 0 ? times : "Время: —";

            string title = active && t > 0 ? string.Format("{0}% — {1}", pct, AppTitle) : AppTitle;
            if (Text != title) Text = title;
        }

        void SetStatus(string text, Color color)
        {
            if (lblStatus.Text != text) lblStatus.Text = text;
            if (lblStatus.ForeColor != color) lblStatus.ForeColor = color;
        }

        static string FormatDuration(double seconds)
        {
            TimeSpan ts = TimeSpan.FromSeconds(Math.Max(0, seconds));
            if (ts.TotalHours >= 1)
                return string.Format("{0}:{1:mm\\:ss}", (int)ts.TotalHours, ts);
            return ts.ToString(@"m\:ss");
        }

        // ─── closing ─────────────────────────────────────────────

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (state == RunState.Rendering || state == RunState.Waiting)
            {
                var answer = MessageBox.Show(this, "Рендер ещё идёт. Остановить Blender и выйти?",
                    "RenderCrashSolver", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (answer != DialogResult.Yes) { e.Cancel = true; return; }
                stopRequested = true;
                if (proc != null)
                {
                    try { proc.Kill(); proc.WaitForExit(5000); } catch { }
                }
                CloseLog("[RenderCrashSolver] Stopped: window closed");
                SetThreadExecutionState(ES_CONTINUOUS);
            }
            else
            {
                ReadSettingsFromUi();
                settings.Save();
            }
            timer.Stop();
            if (infoProc != null) { try { infoProc.Kill(); } catch { } }
            tray.Visible = false;
            tray.Dispose();
            base.OnFormClosing(e);
        }
    }
}
