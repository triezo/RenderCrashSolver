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
        public bool CustomOutput = false;
        public bool CustomFrames = false; // render Frames instead of the scene range
        public string Frames = "";        // "1-100, 120, 150-200"
        // Override: re-render every frame, overwriting existing files. Frames saved before OverrideSince
        // (unix seconds, set at the first Start) count as stale, so crashes / Stop / reboot resume the re-render.
        public bool Override = false;
        public long OverrideSince = 0;
        public string OverrideKey = "";   // blend + output the OverrideSince belongs to
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
                        case "CustomOutput":
                        case "OverrideOutput": /* name used up to 0.3 */ s.CustomOutput = val == "1"; break;
                        case "CustomFrames": s.CustomFrames = val == "1"; break;
                        case "Frames": s.Frames = val; break;
                        case "Override": s.Override = val == "1"; break;
                        case "OverrideSince": long since; if (long.TryParse(val, out since)) s.OverrideSince = since; break;
                        case "OverrideKey": s.OverrideKey = val; break;
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
                    "CustomOutput=" + (CustomOutput ? "1" : "0"),
                    "CustomFrames=" + (CustomFrames ? "1" : "0"),
                    "Frames=" + Frames,
                    "Override=" + (Override ? "1" : "0"),
                    "OverrideSince=" + OverrideSince,
                    "OverrideKey=" + OverrideKey,
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
        const string AppTitle = "RenderCrashSolver 0.5";
#if TELEGRAM
        static readonly bool TelegramFeature = true;
#else
        // shared test builds (package.bat) ship without Telegram until it is tested
        static readonly bool TelegramFeature = false;
#endif
        const string ScriptTag = "[resume_render] ";
        const int ScriptErrorExitCode = 2;   // unexpected Python error in resume_render.py: stop, retrying won't help
        const int RenderErrorExitCode = 3;   // render failed inside Blender (GPU/CUDA): retry like a crash

        [DllImport("kernel32.dll")]
        static extern uint SetThreadExecutionState(uint flags);
        const uint ES_CONTINUOUS = 0x80000000;
        const uint ES_SYSTEM_REQUIRED = 0x00000001;

        Settings settings;

        // the TextBoxes inside the Fields: the session code reads and writes them directly
        TextBox txtBlender, txtBlend, txtOutputDir, txtOutputPrefix, txtFrames, txtLogDir;
        LogView txtLog;
        Toggle chkCustomOutput, chkCustomFrames, chkOverride;
        Label lblScenePath, lblSceneMeta, lblEffectivePath, lblFramesInfo, lblStatus, lblSubtitle, lblTelegram;
        UiButton btnSceneRefresh;
        readonly List<Control> customOutputRows = new List<Control>(); // shown only with "Другая папка вывода"
        readonly List<Control> customFramesRows = new List<Control>(); // shown only with "Свои кадры"

        // scene output settings read from the .blend via "resume_render.py -- --info"
        string sceneFilepath, sceneExt;
        int sceneTotal, sceneDone;   // selected frames / of them already on disk, shown before the first Start
        int sceneFrameStart, sceneFrameEnd = -1;
        bool infoFramesSelected;     // the pending --info read was given --frames
        Process infoProc;
        int infoRequestId;
        DateTime sceneInfoDueAt = DateTime.MinValue;
        IntField numRetries, numPause, numHang;
        UiButton btnStart, btnStop, btnLogs, btnOutput, btnTelegram;
        Badge badge;
        ProgressStrip progress;
        StatTile tileFrames, tileEta, tileAvg, tileElapsed, tileAttempts;
        ToolTip tips;
        NotifyIcon tray;
        Timer timer;
        List<Control> settingsControls = new List<Control>();
        PreviewPanel preview;
        TaskbarState taskbarState = TaskbarState.None;
        int taskbarValue = -1, taskbarTotal = -1;

        // session state, UI thread only
        RunState state = RunState.Idle;
        string finishMessage = "";
        int attempt, crashes, hangs;
        DateTime sessionStart, sessionEnd, retryAt;
        Process proc;
        bool stopRequested, killedByWatchdog;

        // shared with the output reader threads, guarded by sync
        readonly object sync = new object();
        readonly Queue<LogLine> uiLines = new Queue<LogLine>();
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

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.DarkTitleBar(Handle);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.F5 && btnStart.Enabled) { StartSession(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // ─── UI construction ─────────────────────────────────────

        void BuildUi()
        {
            SuspendLayout();
            AutoScaleMode = AutoScaleMode.None; // pixel sizes go through Theme.S, fonts follow the system DPI
            Text = AppTitle;
            Font = Theme.Base;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            ClientSize = new Size(Theme.S(1440), Theme.S(860));
            MinimumSize = new Size(Theme.S(1040), Theme.S(640));
            StartPosition = FormStartPosition.CenterScreen;
            Icon = Theme.AppIcon(Theme.S(32));
            tips = Theme.MakeToolTip();
            Load += delegate
            {
                // laptops / 125-150% scaling: the default size may not fit the screen
                Rectangle area = Screen.FromControl(this).WorkingArea;
                if (Width > area.Width || Height > area.Height) WindowState = FormWindowState.Maximized;
            };

            // header with status and numbers; below it settings on the left, frames over the log on the right
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = Theme.P(12, 12, 12, 12), Margin = Padding.Empty };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(root);
            root.Controls.Add(BuildHeader(), 0, 0);

            var body = new Split { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1, Margin = Theme.P(0, 12, 0, 0) };
            var right = new Split { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
            body.Panel1.Controls.Add(BuildSettings());
            right.Panel1.Controls.Add(BuildPreview());
            right.Panel2.Controls.Add(BuildLog());
            body.Panel2.Controls.Add(right);
            root.Controls.Add(body, 0, 1);
            // the settings column keeps its width (default or dragged by the user), but on a narrow window
            // it gives way so the preview toolbar still fits, and comes back when the window grows
            int settingsWidth = 0;
            bool fitting = false;
            Action fitSettings = delegate
            {
                if (settingsWidth == 0) return;
                int target = Math.Max(body.Panel1MinSize, Math.Min(settingsWidth, body.Width - body.SplitterWidth - Theme.S(600)));
                if (body.SplitterDistance == target) return;
                fitting = true;
                try { body.SplitterDistance = target; } catch { }
                fitting = false;
            };
            body.SizeChanged += delegate { fitSettings(); };
            body.SplitterMoved += delegate { if (!fitting && settingsWidth > 0) settingsWidth = body.SplitterDistance; };
            Shown += delegate
            {
                try
                {
                    body.Panel1MinSize = Theme.S(460);
                    body.Panel2MinSize = Theme.S(420);
                    right.Panel1MinSize = Theme.S(220);
                    right.Panel2MinSize = Theme.S(120);
                    right.SplitterDistance = right.Height * 3 / 5;
                }
                catch { } // window smaller than the minimums: keep defaults
                settingsWidth = Theme.S(540);
                fitSettings();
                ActiveControl = btnStart;
            };

            tray = new NotifyIcon { Icon = Theme.AppIcon(Theme.S(16)), Text = "RenderCrashSolver", Visible = true };
            tray.Click += delegate { Show(); if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal; Activate(); };

            timer = new Timer { Interval = 500 };
            timer.Tick += delegate { OnTick(); };
            timer.Start();

            ResumeLayout(true);
        }

        Control BuildHeader()
        {
            var card = new Card { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = Theme.P(18, 14, 18, 16), Margin = Padding.Empty };
            var t = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 3, Margin = Padding.Empty };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            card.Controls.Add(t);

            badge = new Badge { Anchor = AnchorStyles.Left, Margin = Theme.P(0, 0, 10, 0) };
            lblStatus = new Label
            {
                AutoSize = false, AutoEllipsis = true, UseMnemonic = false, Anchor = AnchorStyles.Left | AnchorStyles.Right,
                Font = Theme.Title, Height = Theme.Title.Height + Theme.S(4), TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty,
            };
            lblSubtitle = new Label
            {
                AutoSize = false, AutoEllipsis = true, UseMnemonic = false, Anchor = AnchorStyles.Left | AnchorStyles.Right,
                ForeColor = Theme.TextDim, Height = Font.Height + Theme.S(4), Margin = Theme.P(0, 2, 0, 0),
            };
            t.Controls.Add(badge, 0, 0);
            t.Controls.Add(lblStatus, 1, 0);
            t.Controls.Add(lblSubtitle, 0, 1);
            t.SetColumnSpan(lblSubtitle, 2);

            btnStop = new UiButton("Стоп", Glyph.Stop, ButtonKind.Danger) { ButtonHeight = Theme.S(38), MinWidth = Theme.S(104), Margin = Theme.P(0, 0, 8, 0) };
            btnStop.Click += delegate { StopSession(); };
            btnStart = new UiButton("Старт", Glyph.Play, ButtonKind.Primary) { ButtonHeight = Theme.S(38), MinWidth = Theme.S(132), Font = Theme.Strong, Margin = Padding.Empty };
            btnStart.Click += delegate { StartSession(); };
            tips.SetToolTip(btnStart, "Запустить рендер (F5)");
            var buttons = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Anchor = AnchorStyles.Right, Margin = Theme.P(16, 0, 0, 0) };
            buttons.Controls.Add(btnStop);
            buttons.Controls.Add(btnStart);
            t.Controls.Add(buttons, 2, 0);
            t.SetRowSpan(buttons, 2);

            progress = new ProgressStrip { Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = Theme.P(0, 14, 0, 14) };
            t.Controls.Add(progress, 0, 2);
            t.SetColumnSpan(progress, 3);

            var tiles = new TableLayoutPanel { Anchor = AnchorStyles.Left | AnchorStyles.Right, Height = StatTile.PreferredHeight, ColumnCount = 5, RowCount = 1, Margin = Padding.Empty };
            for (int i = 0; i < 5; i++) tiles.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));
            tiles.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            tileFrames = AddTile(tiles, 0, "Кадры", null);
            tileEta = AddTile(tiles, 1, "Осталось", "Примерно: среднее время кадра × оставшиеся кадры");
            tileAvg = AddTile(tiles, 2, "Среднее время кадра", "По последним 20 кадрам");
            tileElapsed = AddTile(tiles, 3, "Прошло", null);
            tileAttempts = AddTile(tiles, 4, "Попытка", "Запуски Blender в этой сессии");
            t.Controls.Add(tiles, 0, 3);
            t.SetColumnSpan(tiles, 3);
            return card;
        }

        StatTile AddTile(TableLayoutPanel tiles, int col, string caption, string tip)
        {
            var tile = new StatTile(caption) { Dock = DockStyle.Fill, Margin = col < 4 ? Theme.P(0, 0, 10, 0) : Padding.Empty };
            if (tip != null) tips.SetToolTip(tile, tip);
            tiles.Controls.Add(tile, col, 0);
            return tile;
        }

        Control BuildSettings()
        {
            var card = new Card { Dock = DockStyle.Fill, AutoScroll = true, Padding = Theme.P(18, 16, 18, 16) };
            card.HandleCreated += delegate { Theme.DarkScrollbars(card); };
            var t = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 3, Margin = Padding.Empty };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            card.Controls.Add(t);

            AddSection(t, "Сцена", true);
            txtBlender = AddPathRow(t, "Blender", settings.BlenderExe, "путь к blender.exe", delegate
            {
                BrowseFile(txtBlender, "Blender|blender.exe|Программы (*.exe)|*.exe");
            }, null);
            txtBlend = AddPathRow(t, "Сцена", settings.BlendFile, "файл сцены .blend", delegate
            {
                BrowseFile(txtBlend, "Blender scene (*.blend)|*.blend");
            }, null);

            // output: the scene's own path is primary, a different output folder is optional
            int row = NextRow(t);
            t.Controls.Add(RowLabel("Вывод"), 0, row);
            lblScenePath = new Label
            {
                AutoSize = false, AutoEllipsis = true, UseMnemonic = false, Anchor = AnchorStyles.Left | AnchorStyles.Right,
                Height = Font.Height + Theme.S(4), Margin = Theme.P(0, 7, 8, 0), Cursor = Cursors.Hand, Text = "—",
            };
            lblScenePath.Click += delegate { if (sceneFilepath != null) OpenFolder(Path.GetDirectoryName(sceneFilepath)); };
            t.Controls.Add(lblScenePath, 1, row);
            btnSceneRefresh = new UiButton("", Glyph.Refresh, ButtonKind.Ghost) { Anchor = AnchorStyles.Top, Margin = Theme.P(0, 3, 0, 3) };
            btnSceneRefresh.Click += delegate { RefreshSceneInfo(); };
            tips.SetToolTip(btnSceneRefresh, "Перечитать путь вывода из .blend");
            t.Controls.Add(btnSceneRefresh, 2, row);
            t.SetRowSpan(btnSceneRefresh, 2);
            settingsControls.Add(btnSceneRefresh);
            row = NextRow(t);
            lblSceneMeta = new Label
            {
                AutoSize = false, AutoEllipsis = true, UseMnemonic = false, Anchor = AnchorStyles.Left | AnchorStyles.Right,
                Font = Theme.Small, ForeColor = Theme.TextDim, Height = Theme.Small.Height + Theme.S(4), Margin = Theme.P(0, 0, 8, 4),
            };
            t.Controls.Add(lblSceneMeta, 1, row);

            // frames: the scene range by default, or a list of frames / ranges typed by the animator
            row = NextRow(t);
            chkCustomFrames = new Toggle("Свои кадры", "Только выбранные кадры вместо диапазона сцены.")
            {
                Checked = settings.CustomFrames, Margin = Theme.P(0, 14, 0, 4),
            };
            t.Controls.Add(chkCustomFrames, 0, row);
            t.SetColumnSpan(chkCustomFrames, 3);
            settingsControls.Add(chkCustomFrames);
            txtFrames = AddTextRow(t, "Кадры", settings.Frames, "например 1-100, 120, 150-200", customFramesRows);
            tips.SetToolTip(txtFrames, "Номера и диапазоны через запятую или пробел.\nГотовые кадры не перерендериваются, как и в обычном режиме.");
            row = NextRow(t);
            lblFramesInfo = new Label
            {
                AutoSize = false, AutoEllipsis = true, UseMnemonic = false, Anchor = AnchorStyles.Left | AnchorStyles.Right,
                Font = Theme.Small, ForeColor = Theme.TextDim, Height = Theme.Small.Height + Theme.S(4), Margin = Theme.P(0, 2, 0, 2),
            };
            t.Controls.Add(lblFramesInfo, 1, row);
            t.SetColumnSpan(lblFramesInfo, 2);
            customFramesRows.Add(lblFramesInfo);
            chkCustomFrames.CheckedChanged += delegate
            {
                // start from the scene range so the animator edits what Blender would render
                if (chkCustomFrames.Checked && txtFrames.Text.Trim().Length == 0 && sceneFrameEnd >= sceneFrameStart)
                    txtFrames.Text = sceneFrameStart == sceneFrameEnd
                        ? sceneFrameStart.ToString() : string.Format("{0}-{1}", sceneFrameStart, sceneFrameEnd);
                UpdateFramesUi();
                sceneInfoDueAt = DateTime.Now.AddMilliseconds(800); // "done N of M" for the new selection
            };
            txtFrames.TextChanged += delegate
            {
                UpdateFramesUi();
                if (chkCustomFrames.Checked) sceneInfoDueAt = DateTime.Now.AddMilliseconds(1500);
            };

            row = NextRow(t);
            chkCustomOutput = new Toggle("Другая папка вывода", "Своя папка или имя файла; сам .blend не меняется.")
            {
                Checked = settings.CustomOutput, Margin = Theme.P(0, 14, 0, 4),
            };
            t.Controls.Add(chkCustomOutput, 0, row);
            t.SetColumnSpan(chkCustomOutput, 3);
            settingsControls.Add(chkCustomOutput);

            txtOutputDir = AddPathRow(t, "Папка", settings.OutputDir, "пусто — папка из сцены", delegate
            {
                BrowseFolder(txtOutputDir);
            }, customOutputRows);
            txtOutputPrefix = AddTextRow(t, "Имя файла", settings.OutputPrefix, "пусто — из сцены; shot_ → shot_0001.jpg", customOutputRows);
            row = NextRow(t);
            lblEffectivePath = new Label
            {
                AutoSize = false, AutoEllipsis = true, UseMnemonic = false, Anchor = AnchorStyles.Left | AnchorStyles.Right,
                Font = Theme.Small, ForeColor = Theme.TextDim, Height = Theme.Small.Height + Theme.S(4), Margin = Theme.P(0, 2, 0, 2),
            };
            t.Controls.Add(lblEffectivePath, 1, row);
            t.SetColumnSpan(lblEffectivePath, 2);
            customOutputRows.Add(lblEffectivePath);

            chkCustomOutput.CheckedChanged += delegate
            {
                // prefill with the scene values so the user edits from what Blender uses
                if (chkCustomOutput.Checked && txtOutputDir.Text.Length == 0 && txtOutputPrefix.Text.Length == 0 && sceneFilepath != null)
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

            row = NextRow(t);
            chkOverride = new Toggle("Override — перерендерить все кадры заново", "") { Checked = settings.Override, Margin = Theme.P(0, 12, 0, 0) };
            chkOverride.CheckedChanged += delegate
            {
                // unticking cancels a re-render in progress: from then on existing frames count as done again
                if (!chkOverride.Checked) { settings.OverrideSince = 0; settings.OverrideKey = ""; }
                UpdateOverrideText();
            };
            t.Controls.Add(chkOverride, 0, row);
            t.SetColumnSpan(chkOverride, 3);
            settingsControls.Add(chkOverride);
            UpdateOverrideText();

            AddSection(t, "Перезапуск", false);
            row = NextRow(t);
            var nums = new TableLayoutPanel
            {
                Anchor = AnchorStyles.Left | AnchorStyles.Right, ColumnCount = 3, RowCount = 2, Margin = Padding.Empty,
                Height = Theme.Small.Height + Theme.S(4) + Theme.S(28) + Theme.S(2),
            };
            for (int i = 0; i < 3; i++) nums.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.3F));
            nums.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            nums.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            numRetries = AddNumber(nums, 0, "Макс. попыток", 1, 1000, settings.MaxRetries, "",
                "Сколько раз запускать Blender, прежде чем остановиться с ошибкой");
            numPause = AddNumber(nums, 1, "Пауза перед повтором", 0, 600, settings.RetryPauseSec, "с",
                "Пауза между крашем и новым запуском Blender");
            numHang = AddNumber(nums, 2, "Нет нового кадра", 0, 1440, settings.HangTimeoutMin, "мин",
                "Если нового кадра нет дольше этого времени,\nBlender считается зависшим и перезапускается.\n0 — не следить");
            t.Controls.Add(nums, 0, row);
            t.SetColumnSpan(nums, 3);

            AddSection(t, TelegramFeature ? "Логи и уведомления" : "Логи", false);
            txtLogDir = AddPathRow(t, "Папка логов", settings.LogDir, null, delegate
            {
                BrowseFolder(txtLogDir);
            }, null);

            row = NextRow(t);
            var lblTg = RowLabel("Telegram");
            var tg = new TableLayoutPanel { Anchor = AnchorStyles.Left | AnchorStyles.Right, ColumnCount = 2, RowCount = 1, Height = Theme.S(34), Margin = Padding.Empty };
            tg.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            tg.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            lblTelegram = new Label
            {
                AutoSize = false, AutoEllipsis = true, Anchor = AnchorStyles.Left | AnchorStyles.Right, ForeColor = Theme.TextDim,
                Height = Font.Height + Theme.S(4), Margin = Padding.Empty,
            };
            btnTelegram = new UiButton("Настроить…", Glyph.None, ButtonKind.Normal) { Anchor = AnchorStyles.Right, Margin = Padding.Empty };
            btnTelegram.Click += delegate
            {
                using (var dlg = new TelegramForm(settings)) dlg.ShowDialog(this);
                UpdateTelegramButton();
            };
            tg.Controls.Add(lblTelegram, 0, 0);
            tg.Controls.Add(btnTelegram, 1, 0);
            t.Controls.Add(lblTg, 0, row);
            t.Controls.Add(tg, 1, row);
            t.SetColumnSpan(tg, 2);
            lblTg.Visible = tg.Visible = TelegramFeature;
            return card;
        }

        Control BuildPreview()
        {
            var card = new Card { Dock = DockStyle.Fill, Padding = Theme.P(16, 12, 16, 14) };
            TableLayoutPanel t = CardLayout(card);
            btnOutput = new UiButton("Папка рендера", Glyph.Folder, ButtonKind.Ghost);
            btnOutput.Click += delegate
            {
                string dir;
                lock (sync) dir = outputDir;
                OpenFolder(dir);
            };
            t.Controls.Add(CardHeader("Кадры", null, btnOutput), 0, 0);
            preview = new PreviewPanel { Margin = Padding.Empty };
            t.Controls.Add(preview, 0, 1);
            return card;
        }

        Control BuildLog()
        {
            var card = new Card { Dock = DockStyle.Fill, Padding = Theme.P(16, 12, 16, 16) };
            TableLayoutPanel t = CardLayout(card);
            btnLogs = new UiButton("Папка логов", Glyph.Folder, ButtonKind.Ghost);
            btnLogs.Click += delegate { OpenFolder(txtLogDir.Text); };
            t.Controls.Add(CardHeader("Журнал", "события, ошибки и уникальные предупреждения · полный лог — в файле", btnLogs), 0, 0);
            var well = new Card { Dock = DockStyle.Fill, BackColor = Theme.Input, Radius = Theme.S(4), Padding = Theme.P(10, 8, 2, 8), Margin = Padding.Empty };
            txtLog = new LogView { Dock = DockStyle.Fill };
            well.Controls.Add(txtLog);
            t.Controls.Add(well, 0, 1);
            return card;
        }

        static TableLayoutPanel CardLayout(Card card)
        {
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            card.Controls.Add(t);
            return t;
        }

        static Control CardHeader(string title, string hint, Control tool)
        {
            var h = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 3, RowCount = 1, Margin = Theme.P(0, 0, 0, 8) };
            h.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            h.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            h.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            h.Controls.Add(new Label
            {
                Text = title.ToUpperInvariant(), AutoSize = true, Font = Theme.Caption, ForeColor = Theme.TextFaint,
                Anchor = AnchorStyles.Left, Margin = Theme.P(0, 0, 12, 0),
            }, 0, 0);
            if (hint != null)
                h.Controls.Add(new Label
                {
                    Text = hint, AutoSize = false, AutoEllipsis = true, Anchor = AnchorStyles.Left | AnchorStyles.Right, Font = Theme.Small,
                    ForeColor = Theme.TextFaint, Height = Theme.Small.Height + Theme.S(2), Margin = Padding.Empty,
                }, 1, 0);
            tool.Anchor = AnchorStyles.Right;
            tool.Margin = Padding.Empty;
            h.Controls.Add(tool, 2, 0);
            return h;
        }

        static int NextRow(TableLayoutPanel t)
        {
            t.RowCount++;
            return t.RowCount - 1;
        }

        static void AddSection(TableLayoutPanel t, string title, bool first)
        {
            var lbl = new Label
            {
                Text = title.ToUpperInvariant(), AutoSize = true, Font = Theme.Caption, ForeColor = Theme.TextFaint,
                Margin = Theme.P(0, first ? 0 : 22, 0, 6),
            };
            t.Controls.Add(lbl, 0, NextRow(t));
            t.SetColumnSpan(lbl, 3);
        }

        static Label RowLabel(string text)
        {
            return new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = Theme.TextDim, Margin = Theme.P(0, 0, 14, 0) };
        }

        TextBox AddPathRow(TableLayoutPanel t, string label, string value, string hint, EventHandler browse, List<Control> group)
        {
            int row = NextRow(t);
            var lbl = RowLabel(label);
            var field = new Field(value) { Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = Theme.P(0, 3, 6, 3) };
            var btn = new UiButton("", Glyph.Dots, ButtonKind.Normal) { Margin = Theme.P(0, 3, 0, 3) };
            btn.Click += browse;
            tips.SetToolTip(btn, "Выбрать…");
            if (hint != null) SetCueBanner(field.Box, hint);
            t.Controls.Add(lbl, 0, row);
            t.Controls.Add(field, 1, row);
            t.Controls.Add(btn, 2, row);
            settingsControls.Add(field);
            settingsControls.Add(btn);
            if (group != null) group.AddRange(new Control[] { lbl, field, btn });
            return field.Box;
        }

        TextBox AddTextRow(TableLayoutPanel t, string label, string value, string hint, List<Control> group)
        {
            int row = NextRow(t);
            var lbl = RowLabel(label);
            var field = new Field(value) { Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = Theme.P(0, 3, 6, 3) };
            if (hint != null) SetCueBanner(field.Box, hint);
            t.Controls.Add(lbl, 0, row);
            t.Controls.Add(field, 1, row);
            settingsControls.Add(field);
            if (group != null) group.AddRange(new Control[] { lbl, field });
            return field.Box;
        }

        IntField AddNumber(TableLayoutPanel nums, int col, string label, int min, int max, int value, string suffix, string tip)
        {
            var lbl = new Label { Text = label, AutoSize = true, Font = Theme.Small, ForeColor = Theme.TextDim, Margin = Theme.P(0, 0, 0, 4) };
            var field = new IntField(min, max, value, suffix)
            {
                Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = col < 2 ? Theme.P(0, 0, 12, 0) : Padding.Empty,
            };
            tips.SetToolTip(lbl, tip);
            tips.SetToolTip(field.Box, tip);
            nums.Controls.Add(lbl, col, 0);
            nums.Controls.Add(field, col, 1);
            settingsControls.Add(field);
            return field;
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
            // fields stay readable (and copyable) while locked; everything else is disabled
            foreach (Control c in settingsControls)
            {
                var field = c as Field;
                if (field != null) field.Locked = running;
                else c.Enabled = !running;
            }
            btnStart.Enabled = !running;
            btnStop.Enabled = running;
            UpdateOutputUi();
            UpdateFramesUi();
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
            bool custom = chkCustomOutput.Checked;
            foreach (Control c in customOutputRows) c.Visible = custom;

            string effective = EffectiveOutputPath();
            if (!custom)
                lblEffectivePath.Text = "";
            else if (effective == null)
                lblEffectivePath.Text = "Кадры будут сохраняться в: (прочитайте сцену кнопкой ↻)";
            else
                lblEffectivePath.Text = "Кадры будут сохраняться в:  " + FramePathDisplay(effective);
            tips.SetToolTip(lblEffectivePath, effective != null && custom ? FramePathDisplay(effective) : null);

            // while rendering the preview follows the path reported by Blender itself
            if (btnStart.Enabled && effective != null && sceneExt != null)
                preview.SetSequence(effective, sceneExt);
        }

        // scene.render.filepath with the optional custom output applied; null while the scene is unknown
        string EffectiveOutputPath()
        {
            string dir = "", prefix = "";
            if (chkCustomOutput.Checked)
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

        // ─── custom frames ───────────────────────────────────────

        const int MaxFrame = 1048574; // Blender's MAXFRAME
        static readonly Regex FrameRangeRe = new Regex(@"^(\d+)(?:-(\d+))?$");

        /// <summary>"1-100, 120 150-200" -> sorted, merged ranges; null and a readable error for anything else.</summary>
        static List<KeyValuePair<int, int>> ParseFrames(string spec, out string error)
        {
            error = null;
            var ranges = new List<KeyValuePair<int, int>>();
            foreach (string part in Regex.Split(spec ?? "", @"[,;\s]+"))
            {
                if (part.Length == 0) continue;
                Match m = FrameRangeRe.Match(part);
                int first, last;
                if (!m.Success || !int.TryParse(m.Groups[1].Value, out first) ||
                    !int.TryParse(m.Groups[2].Success ? m.Groups[2].Value : m.Groups[1].Value, out last))
                {
                    error = "не понял «" + part + "»: пишите номера и диапазоны, например 1-100, 120";
                    return null;
                }
                if (last < first || last > MaxFrame)
                {
                    error = "неверный диапазон «" + part + "»";
                    return null;
                }
                ranges.Add(new KeyValuePair<int, int>(first, last));
            }
            if (ranges.Count == 0)
            {
                error = "укажите кадры, например 1-100, 120";
                return null;
            }
            ranges.Sort((a, b) => a.Key.CompareTo(b.Key));
            var merged = new List<KeyValuePair<int, int>>();
            foreach (var r in ranges)
            {
                int n = merged.Count;
                if (n > 0 && r.Key <= merged[n - 1].Value + 1)
                    merged[n - 1] = new KeyValuePair<int, int>(merged[n - 1].Key, Math.Max(merged[n - 1].Value, r.Value));
                else
                    merged.Add(r);
            }
            return merged;
        }

        static string FramesText(List<KeyValuePair<int, int>> ranges, string dash, string separator)
        {
            var parts = new List<string>();
            foreach (var r in ranges) parts.Add(r.Key == r.Value ? r.Key.ToString() : r.Key + dash + r.Value);
            return string.Join(separator, parts.ToArray());
        }

        static int FrameCount(List<KeyValuePair<int, int>> ranges)
        {
            int n = 0;
            foreach (var r in ranges) n += r.Value - r.Key + 1;
            return n;
        }

        /// <summary>The "--frames" value for resume_render.py ("1-100,120"), null if the list is not valid.</summary>
        static string NormalizeFrames(string spec)
        {
            string error;
            List<KeyValuePair<int, int>> ranges = ParseFrames(spec, out error);
            return ranges == null ? null : FramesText(ranges, "-", ",");
        }

        static string Plural(int n, string one, string few, string many)
        {
            int m10 = n % 10, m100 = n % 100;
            if (m10 == 1 && m100 != 11) return one;
            if (m10 >= 2 && m10 <= 4 && (m100 < 12 || m100 > 14)) return few;
            return many;
        }

        void UpdateFramesUi()
        {
            bool custom = chkCustomFrames.Checked;
            foreach (Control c in customFramesRows) c.Visible = custom;
            if (!custom) { lblFramesInfo.Text = ""; return; }
            string error;
            List<KeyValuePair<int, int>> ranges = ParseFrames(txtFrames.Text, out error);
            if (ranges == null)
            {
                lblFramesInfo.Text = error;
                lblFramesInfo.ForeColor = Theme.Orange;
                return;
            }
            int count = FrameCount(ranges);
            lblFramesInfo.Text = string.Format("{0} {1}: {2}", count, Plural(count, "кадр", "кадра", "кадров"), FramesText(ranges, "–", ", "));
            lblFramesInfo.ForeColor = Theme.TextDim;
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
                sceneTotal = sceneDone = 0;
                SetSceneText("—", "укажите blender.exe и .blend", Theme.TextDim);
                UpdateOutputUi();
                return;
            }

            int id = ++infoRequestId;
            SetSceneText(lblScenePath.Text, "читаю сцену в Blender…", Theme.TextDim);
            // with "Свои кадры" the status ("done N of M") counts the selected frames
            string frames = chkCustomFrames.Checked ? NormalizeFrames(txtFrames.Text) : null;
            infoFramesSelected = frames != null;
            var psi = new ProcessStartInfo(blender,
                string.Format("-b \"{0}\" --python-exit-code {1} -P \"{2}\" -- --info{3}", blend, ScriptErrorExitCode, ScriptPath,
                    frames != null ? " --frames \"" + frames + "\"" : ""))
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
                SetSceneText("—", "Не удалось запустить Blender: " + ex.Message, Theme.Red);
            }
        }

        // path line in the normal colour once the scene is known; the line under it carries frames / status / problems
        void SetSceneText(string path, string meta, Color metaColor)
        {
            lblScenePath.Text = path;
            lblScenePath.ForeColor = sceneFilepath != null ? Theme.Text : Theme.TextDim;
            tips.SetToolTip(lblScenePath, sceneFilepath != null ? path + "\nКлик — открыть папку" : null);
            lblSceneMeta.Text = meta;
            lblSceneMeta.ForeColor = metaColor;
        }

        void ApplySceneInfo(int id, Process p, List<string> lines)
        {
            if (infoProc == p) infoProc = null;
            p.Dispose();
            if (id != infoRequestId) return; // a newer request superseded this one

            string path = null, frames = null, status = null;
            string ext = null;
            int rangeTotal = 0, rangeDone = 0;
            lock (lines)
            {
                foreach (string msg in lines)
                {
                    if (msg.StartsWith("SCENE_PATH ")) path = msg.Substring(11).Trim();
                    else if (msg.StartsWith("SCENE_EXT ")) ext = msg.Substring(10).Trim();
                    else if (msg.StartsWith("SCENE_FRAMES "))
                    {
                        // SCENE_FRAMES <start> <end> [<step>] (the step was added in 0.5)
                        string[] parts = msg.Split(' ');
                        if (parts.Length >= 3 && int.TryParse(parts[1], out sceneFrameStart) && int.TryParse(parts[2], out sceneFrameEnd))
                        {
                            frames = string.Format(infoFramesSelected ? "сцена {0}–{1}" : "кадры {0}–{1}", sceneFrameStart, sceneFrameEnd);
                            if (parts.Length >= 4 && parts[3] != "1") frames += ", шаг " + parts[3];
                        }
                    }
                    else if (msg.StartsWith("STATUS "))
                    {
                        Match m = StatusRe.Match(msg);
                        if (m.Success)
                        {
                            rangeTotal = int.Parse(m.Groups[1].Value);
                            rangeDone = int.Parse(m.Groups[2].Value);
                            status = string.Format(infoFramesSelected ? "готово {0} из {1} выбранных" : "готово {0} из {1}", rangeDone, rangeTotal);
                        }
                    }
                }
            }

            if (path == null)
            {
                sceneFilepath = null;
                sceneTotal = sceneDone = 0;
                sceneFrameEnd = -1;
                SetSceneText("—", "Не удалось прочитать сцену (подробности — в журнале при запуске рендера)", Theme.Orange);
                UpdateOutputUi();
                return;
            }
            sceneFilepath = path;
            sceneExt = ext == "FFMPEG" ? null : ext;
            sceneTotal = rangeTotal;
            sceneDone = rangeDone;
            if (ext == "FFMPEG")
                SetSceneText(path, "Видео FFMPEG: продолжение по кадрам невозможно, переключите вывод на JPG/PNG/EXR", Theme.Orange);
            else
            {
                var meta = new List<string>();
                if (frames != null) meta.Add(frames);
                if (status != null) meta.Add(status);
                SetSceneText(FramePathDisplay(path), string.Join("  ·  ", meta.ToArray()), Theme.TextDim);
            }
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
            settings.CustomOutput = chkCustomOutput.Checked;
            settings.CustomFrames = chkCustomFrames.Checked;
            settings.Frames = txtFrames.Text.Trim();
            settings.Override = chkOverride.Checked;
            settings.LogDir = txtLogDir.Text.Trim().Trim('"');
            settings.MaxRetries = numRetries.Value;
            settings.RetryPauseSec = numPause.Value;
            settings.HangTimeoutMin = numHang.Value;
        }

        void StartSession()
        {
            ReadSettingsFromUi();

            string error = null, framesError = null;
            List<KeyValuePair<int, int>> frameRanges = settings.CustomFrames ? ParseFrames(settings.Frames, out framesError) : null;
            if (!File.Exists(settings.BlenderExe)) error = "Не найден blender.exe:\n" + settings.BlenderExe;
            else if (!File.Exists(settings.BlendFile)) error = "Не найден .blend файл:\n" + settings.BlendFile;
            else if (!File.Exists(ScriptPath))
                error = "Рядом с программой не найден resume_render.py:\n" + ScriptPath +
                        "\n\nЕсли программа запущена прямо из zip-архива — распакуйте архив целиком в обычную папку и запустите оттуда.";
            else if (settings.CustomOutput && settings.OutputDir.Length > 0 && !Path.IsPathRooted(settings.OutputDir))
                error = "Папка вывода должна быть полным путём, например E:\\Render\\shot01";
            else if (settings.CustomOutput && (settings.OutputPrefix.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || settings.OutputPrefix.Contains("\"")))
                error = "Имя файла содержит недопустимые символы: " + settings.OutputPrefix;
            else if (framesError != null)
                error = "Свои кадры: " + framesError;
            else
            {
                try { Directory.CreateDirectory(settings.LogDir); }
                catch (Exception ex) { error = "Не удалось создать папку логов:\n" + ex.Message; }
                if (error == null && settings.CustomOutput && settings.OutputDir.Length > 0)
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

            // Override: a new re-render needs confirmation; an unfinished one for the same scene/output just continues
            bool overrideContinues = false;
            if (settings.Override)
            {
                string key = OverrideKeyFor(settings);
                if (settings.OverrideSince > 0 && settings.OverrideKey == key)
                    overrideContinues = true;
                else
                {
                    var answer = MessageBox.Show(this,
                        (settings.CustomFrames ? "Override: выбранные кадры будут отрендерены заново.\n\n" : "Override: все кадры будут отрендерены заново.\n\n") +
                        "Уже существующие файлы кадров в папке вывода будут перезаписаны по мере рендера. " +
                        "Если рендер прервётся или вы его остановите, при следующем старте он продолжится с места остановки, " +
                        "а ещё не перезаписанные старые кадры останутся на месте.\n\nНачать?",
                        AppTitle, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                    if (answer != DialogResult.Yes) return;
                    settings.OverrideSince = ToUnixSeconds(DateTime.UtcNow);
                    settings.OverrideKey = key;
                    UpdateOverrideText();
                }
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
            AddUi("Старт: " + Path.GetFileName(settings.BlendFile), LogKind.Section);
            if (frameRanges != null)
            {
                int count = FrameCount(frameRanges);
                AddUi(string.Format("Свои кадры: {0} ({1} {2})", FramesText(frameRanges, "–", ", "), count, Plural(count, "кадр", "кадра", "кадров")));
            }
            if (settings.CustomOutput)
                AddUi(string.Format("Другая папка вывода: папка = {0}, имя = {1}",
                    settings.OutputDir.Length > 0 ? settings.OutputDir : "(из сцены)",
                    settings.OutputPrefix.Length > 0 ? settings.OutputPrefix : "(из сцены)"));
            if (settings.Override)
                AddUi(string.Format(overrideContinues
                        ? "Override: продолжаю перерендер, начатый {0:dd.MM HH:mm}. Кадры, сохранённые раньше, перезаписываются."
                        : "Override: перерендер всех кадров. Кадры, сохранённые раньше {0:dd.MM HH:mm}, перезаписываются.",
                    FromUnixSeconds(settings.OverrideSince)));
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
                    uiLines.Enqueue(Line("Не удалось открыть файл лога: " + ex.Message, LogKind.Error));
                }
            }
            AddUi(string.Format("── Попытка #{0} из {1} ──", attempt, settings.MaxRetries), LogKind.Section);

            // everything after "--" is ignored by Blender and read by resume_render.py
            var args = new StringBuilder();
            args.AppendFormat("-b \"{0}\" --python-exit-code {1} -P \"{2}\" --", settings.BlendFile, ScriptErrorExitCode, ScriptPath);
            if (settings.CustomOutput && settings.OutputDir.Length > 0) args.AppendFormat(" --output-dir \"{0}\"", settings.OutputDir);
            if (settings.CustomOutput && settings.OutputPrefix.Length > 0) args.AppendFormat(" --prefix \"{0}\"", settings.OutputPrefix);
            if (settings.Override && settings.OverrideSince > 0) args.AppendFormat(" --override-since {0}", settings.OverrideSince);
            string frames = settings.CustomFrames ? NormalizeFrames(settings.Frames) : null; // validated in StartSession
            if (frames != null) args.AppendFormat(" --frames \"{0}\"", frames);

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
                AddUi("Blender остановлен из-за зависания.", LogKind.Warning);
            else if (code != 0)
            {
                crashes++;
                if (code == RenderErrorExitCode)
                    AddUi("Рендер прервался с ошибкой внутри Blender (чаще всего сбой видеокарты / CUDA).", LogKind.Error);
                else
                    AddUi(string.Format("Blender упал: код {0} (0x{0:X8}).", code), LogKind.Error);
                if (settings.TgOnCrash && attempt < settings.MaxRetries)
                    NotifyTelegram(string.Format("⚠ Blender упал (код 0x{0:X8}), перезапуск через {1} с — попытка {2} из {3}.",
                        code, settings.RetryPauseSec, attempt + 1, settings.MaxRetries), false);
            }
            else
                AddUi("Blender завершился (код 0), но завершение рендера не подтверждено — перезапуск.", LogKind.Warning);

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
            AddUi("■ " + message, final == RunState.Done ? LogKind.Success : final == RunState.Failed ? LogKind.Error : LogKind.Event);
            SetRunningUi(false);
            SetThreadExecutionState(ES_CONTINUOUS);
            UpdateLabels();
            sceneInfoDueAt = DateTime.Now; // refresh "done N of M" for the scene path

            if (final == RunState.Done && settings.Override)
            {
                // re-render complete: untick (the handler clears OverrideSince) so the next Start resumes normally
                chkOverride.Checked = false;
                settings.Override = false;
                settings.Save();
                AddUi("Override завершён: все кадры отрендерены заново, галочка снята.", LogKind.Success);
            }

            if (final == RunState.Done)
            {
                SystemSounds.Asterisk.Play();
                tray.ShowBalloonTip(15000, "Рендер завершён", Path.GetFileName(settings.BlendFile) + ": " + message, ToolTipIcon.Info);
                Theme.Flash(this);
            }
            else if (final == RunState.Failed)
            {
                SystemSounds.Hand.Play();
                tray.ShowBalloonTip(15000, "Рендер остановлен с ошибкой", message, ToolTipIcon.Error);
                Theme.Flash(this);
            }

            if (final == RunState.Done && settings.TgOnDone)
                NotifyTelegram("✅ Рендер готов", true);
            else if (final == RunState.Failed && settings.TgOnFail)
                NotifyTelegram("❌ Рендер остановлен: " + message, true);
        }

        // ─── Override (re-render all frames) ─────────────────────

        // a pending re-render belongs to one scene + output; another scene/output starts a new one
        static string OverrideKeyFor(Settings s)
        {
            string output = s.CustomOutput ? s.OutputDir + "|" + s.OutputPrefix : "";
            return (s.BlendFile + "|" + output).ToLowerInvariant();
        }

        static readonly DateTime UnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        static long ToUnixSeconds(DateTime utc)
        {
            return (long)(utc - UnixEpoch).TotalSeconds;
        }

        static DateTime FromUnixSeconds(long seconds)
        {
            return UnixEpoch.AddSeconds(seconds).ToLocalTime();
        }

        void UpdateOverrideText()
        {
            chkOverride.Description = chkOverride.Checked && settings.OverrideSince > 0
                ? string.Format("Начат {0:dd.MM HH:mm}, продолжится с места остановки.", FromUnixSeconds(settings.OverrideSince))
                : "Старые кадры перезаписываются по ходу рендера.";
        }

        // ─── Telegram ────────────────────────────────────────────

        void UpdateTelegramButton()
        {
            int chats = TelegramApi.ParseChatIds(settings.TgChats).Count;
            lblTelegram.Text = settings.TgEnabled ? string.Format("включено, получателей: {0}", chats) : "выключено";
            lblTelegram.ForeColor = settings.TgEnabled ? Theme.Green : Theme.TextDim;
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
                    uiLines.Enqueue(errors.Count == 0
                        ? Line(string.Format("Telegram: уведомление отправлено ({0})", chats.Count), LogKind.Event)
                        : Line("Telegram: не удалось отправить — " + string.Join("; ", errors), LogKind.Warning));
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
                        uiLines.Enqueue(Line(ShortenWarning(line.Trim()), LogKind.Warning));
                }
                else if (line.IndexOf("Error", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    line.IndexOf("EXCEPTION", StringComparison.Ordinal) >= 0 ||
                    line.StartsWith("Traceback", StringComparison.Ordinal) ||
                    line.StartsWith("  File \"", StringComparison.Ordinal) ||
                    line.IndexOf(".crash.txt", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    uiLines.Enqueue(Line(line, LogKind.Error));
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
                uiLines.Enqueue(Line(string.Format("Кадр {0} готов за {1}", parts.Length >= 2 ? parts[1] : "?", FormatDuration(secs)), LogKind.Frame));
                return;
            }
            if (msg.StartsWith("STATUS "))
            {
                Match m = StatusRe.Match(msg);
                if (m.Success)
                {
                    total = int.Parse(m.Groups[1].Value);
                    done = int.Parse(m.Groups[2].Value);
                    uiLines.Enqueue(Line(string.Format("Кадров в диапазоне: {0}, готово: {1}, осталось: {2}", total, done, total - done), LogKind.Event));
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
                uiLines.Enqueue(Line("Путь вывода: " + pendingSequencePath + (renderExt != null ? "####." + renderExt : ""), LogKind.Event));
                return;
            }
            if (msg.StartsWith("OUTPUT_DIR "))
            {
                outputDir = msg.Substring(11).Trim();
                uiLines.Enqueue(Line("Папка рендера: " + outputDir, LogKind.Event));
                return;
            }
            LogKind kind = LogKind.Event;
            if (msg.StartsWith("ALL_FRAMES_DONE")) { allDoneSeen = true; kind = LogKind.Success; }
            else if (msg.StartsWith("RENDER_ERROR") || msg.StartsWith("ERROR")) kind = LogKind.Error;
            else if (msg.StartsWith("WARN")) kind = LogKind.Warning;
            uiLines.Enqueue(Line(msg, kind));
        }

        // "WARN (bke.modifier): C:\...\modifier.cc:424 BKE_modifier_set_error: Object: ..." -> "WARN (bke.modifier): Object: ..."
        static readonly Regex WarnSourceRe = new Regex(@"^(WARN \([^)]*\):)\s+\S+:\d+\s+\w+:\s*");

        static string ShortenWarning(string line)
        {
            return WarnSourceRe.Replace(line, "$1 ");
        }

        // an empty text is a blank separator line without a time stamp
        static LogLine Line(string text, LogKind kind)
        {
            return new LogLine { Time = text.Length == 0 ? "" : DateTime.Now.ToString("HH:mm:ss"), Text = text, Kind = kind };
        }

        void AddUi(string text, LogKind kind = LogKind.Event)
        {
            lock (sync) uiLines.Enqueue(Line(text, kind));
            DrainUi();
        }

        void DrainUi()
        {
            var lines = new List<LogLine>();
            string seqPath, seqExt;
            List<KeyValuePair<int, string>> newFrames = null;
            lock (sync)
            {
                while (uiLines.Count > 0) lines.Add(uiLines.Dequeue());
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

            txtLog.Append(lines);
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
                    AddUi(string.Format("Нет нового кадра {0} мин — считаю, что Blender завис, перезапускаю.", settings.HangTimeoutMin), LogKind.Warning);
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
            string dir = null;
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
                if (hasOutput) dir = outputDir;
            }
            btnOutput.Enabled = hasOutput;
            // before the first Start of this run of the program: what the scene's output folder already holds
            if (attempt == 0 && sceneTotal > 0) { t = sceneTotal; d = sceneDone; }

            int pct = t > 0 ? (int)(100L * d / t) : 0;
            bool active = state == RunState.Rendering || state == RunState.Waiting;
            Color bar = Theme.TextFaint;
            switch (state)
            {
                case RunState.Idle:
                    SetStatus("", "Готов к запуску", Theme.Text, Theme.Text); break;
                case RunState.Rendering:
                    SetStatus("РЕНДЕР", cur > 0 ? "Рендерится кадр " + cur : "Blender загружает сцену…", Theme.AccentText, Theme.Text);
                    bar = Theme.Accent; break;
                case RunState.Waiting:
                    int left = Math.Max(0, (int)Math.Ceiling((retryAt - DateTime.Now).TotalSeconds));
                    SetStatus("ПЕРЕЗАПУСК", string.Format("Новая попытка через {0} с…", left), Theme.Orange, Theme.Orange);
                    bar = Theme.Orange; break;
                case RunState.Done:
                    SetStatus("ГОТОВО", finishMessage, Theme.Green, Theme.Green);
                    bar = Theme.Green; break;
                case RunState.Stopped:
                    SetStatus("СТОП", finishMessage, Theme.TextDim, Theme.Text); break;
                case RunState.Failed:
                    SetStatus("ОШИБКА", finishMessage, Theme.Red, Theme.Red);
                    bar = Theme.Red; break;
            }
            progress.Set(d, t, bar);

            string blend = attempt > 0 ? settings.BlendFile : txtBlend.Text.Trim().Trim('"');
            string subtitle = blend.Length == 0 ? "Укажите Blender и сцену .blend, затем нажмите «Старт»" : Path.GetFileName(blend);
            if (blend.Length > 0 && chkCustomFrames.Checked)
            {
                string framesError;
                List<KeyValuePair<int, int>> ranges = ParseFrames(txtFrames.Text, out framesError);
                if (ranges != null) subtitle += "   ·   кадры " + FramesText(ranges, "–", ", ");
            }
            if (blend.Length > 0 && dir == null && sceneFilepath != null)
            {
                string effective = EffectiveOutputPath();
                if (effective != null) dir = Path.GetDirectoryName(effective);
            }
            if (blend.Length > 0 && dir != null) subtitle += "   →   " + dir;
            if (lblSubtitle.Text != subtitle) lblSubtitle.Text = subtitle;

            tileFrames.Set(t > 0 ? string.Format("{0} / {1}", d, t) : "—",
                t > 0 ? string.Format("{0}% · осталось {1}", pct, t - d) : "", Theme.Text);
            if (active && avg > 0 && t > d)
            {
                double eta = avg * (t - d);
                tileEta.Set("≈ " + FormatDuration(eta), string.Format("к {0:HH:mm, dd.MM}", DateTime.Now.AddSeconds(eta)), Theme.Text);
            }
            else
                tileEta.Set(state == RunState.Done ? "0:00" : "—", "", Theme.Text);
            tileAvg.Set(avg > 0 ? FormatDuration(avg) : "—", last > 0 ? "последний " + FormatDuration(last) : "", Theme.Text);
            if (attempt > 0)
            {
                TimeSpan elapsed = (active ? DateTime.Now : sessionEnd) - sessionStart;
                tileElapsed.Set(FormatDuration(elapsed.TotalSeconds), string.Format("старт в {0:HH:mm}", sessionStart), Theme.Text);
            }
            else
                tileElapsed.Set("—", "", Theme.Text);
            tileAttempts.Set(attempt > 0 ? string.Format("{0} / {1}", attempt, settings.MaxRetries) : "—",
                attempt > 0 ? string.Format("крашей {0} · зависаний {1}", crashes, hangs) : "", crashes + hangs > 0 ? Theme.Orange : Theme.Text);

            string title = active && t > 0 ? string.Format("{0}% — {1}", pct, AppTitle) : AppTitle;
            if (Text != title) Text = title;
            string tip = active && t > 0 ? string.Format("RenderCrashSolver — {0}% ({1} / {2})", pct, d, t) : "RenderCrashSolver";
            if (tray.Text != tip) tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
            UpdateTaskbar(t, d);
        }

        void SetStatus(string badgeText, string text, Color badgeColor, Color textColor)
        {
            badge.Set(badgeText, badgeColor);
            if (lblStatus.Text != text) lblStatus.Text = text;
            if (lblStatus.ForeColor != textColor) lblStatus.ForeColor = textColor;
        }

        // progress on the taskbar button: blue while rendering, yellow while waiting to restart, red after a failure
        void UpdateTaskbar(int t, int d)
        {
            if (!IsHandleCreated || !Visible) return;
            TaskbarState s = TaskbarState.None;
            if (state == RunState.Rendering) s = t > 0 ? TaskbarState.Normal : TaskbarState.Indeterminate;
            else if (state == RunState.Waiting) s = TaskbarState.Paused;
            else if (state == RunState.Failed) s = TaskbarState.Error;
            int value = t > 0 ? d : 1, max = Math.Max(1, t);
            if (s == taskbarState && value == taskbarValue && max == taskbarTotal) return;
            taskbarState = s;
            taskbarValue = value;
            taskbarTotal = max;
            Taskbar.Set(Handle, s, value, max);
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
