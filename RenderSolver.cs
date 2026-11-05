// RenderSolver - render queue for Blender: renders scenes one after another and resumes each one after crashes.
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

namespace RenderSolver
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

    enum JobStatus { Waiting, Rendering, Done, Failed, Stopped }

    /// <summary>One scene of the render queue. Output path and frame range come from the .blend unless overridden here.</summary>
    class Job
    {
        // saved in settings.ini, one [job] section per scene
        public string BlendFile = "";
        public bool Enabled = true;          // unticked scenes stay in the list but Start skips them
        public bool CustomFrames = false;    // render Frames instead of the scene range
        public string Frames = "";           // "1-100, 120, 150-200"
        public bool CustomOutput = false;
        public string OutputDir = "";        // empty = keep scene.render.filepath directory
        public string OutputPrefix = "";     // empty = keep scene file name prefix
        // Override: re-render every frame, overwriting existing files. Frames saved before OverrideSince
        // (unix seconds, set at the first Start) count as stale, so crashes / Stop / reboot resume the re-render.
        public bool Override = false;
        public long OverrideSince = 0;
        public string OverrideKey = "";      // blend + output the OverrideSince belongs to

        // this run of the program only
        public JobStatus Status = JobStatus.Waiting;
        public string Message = "";
        // read from the .blend with "resume_render.py -- --info"; InfoValid = false means (re)read it
        public bool InfoValid;
        public string InfoError;             // why the scene could not be read
        public string SceneFilepath, SceneExt;
        public int SceneStart, SceneEnd = -1, SceneStep = 1;
        public int Total, Done;              // selected frames / of them already on disk
        public bool InfoFramesSelected;      // Total and Done count the custom frames

        public string Name
        {
            get { return BlendFile.Length == 0 ? "Новая сцена" : Path.GetFileNameWithoutExtension(BlendFile); }
        }

        public bool ReadKey(string key, string val)
        {
            long since;
            switch (key)
            {
                case "BlendFile": BlendFile = val; return true;
                case "Enabled": Enabled = val != "0"; return true;
                case "CustomFrames": CustomFrames = val == "1"; return true;
                case "Frames": Frames = val; return true;
                case "CustomOutput":
                case "OverrideOutput": /* name used up to 0.3 */ CustomOutput = val == "1"; return true;
                case "OutputDir": OutputDir = val; return true;
                case "OutputPrefix": OutputPrefix = val; return true;
                case "Override": Override = val == "1"; return true;
                case "OverrideSince": if (long.TryParse(val, out since)) OverrideSince = since; return true;
                case "OverrideKey": OverrideKey = val; return true;
            }
            return false;
        }

        public string[] SaveLines()
        {
            return new[]
            {
                "[job]",
                "BlendFile=" + BlendFile,
                "Enabled=" + (Enabled ? "1" : "0"),
                "CustomFrames=" + (CustomFrames ? "1" : "0"),
                "Frames=" + Frames,
                "CustomOutput=" + (CustomOutput ? "1" : "0"),
                "OutputDir=" + OutputDir,
                "OutputPrefix=" + OutputPrefix,
                "Override=" + (Override ? "1" : "0"),
                "OverrideSince=" + OverrideSince,
                "OverrideKey=" + OverrideKey,
            };
        }

        /// <summary>Same scene and options; a pending Override re-render is not copied.</summary>
        public Job Duplicate()
        {
            return new Job
            {
                BlendFile = BlendFile, Enabled = Enabled, CustomFrames = CustomFrames, Frames = Frames,
                CustomOutput = CustomOutput, OutputDir = OutputDir, OutputPrefix = OutputPrefix, Override = Override,
            };
        }
    }

    class Settings
    {
        public string BlenderExe = FindBlender();
        public string LogDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
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
        public readonly List<Job> Jobs = new List<Job>();

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
            var legacy = new Job(); // up to 0.5 settings.ini held a single scene at the top level
            Job job = null;
            try
            {
                foreach (string raw in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line == "[job]")
                    {
                        job = new Job();
                        s.Jobs.Add(job);
                        continue;
                    }
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim();
                    string val = line.Substring(eq + 1).Trim();
                    if (job != null) { job.ReadKey(key, val); continue; }
                    int n;
                    switch (key)
                    {
                        case "BlenderExe": s.BlenderExe = val; break;
                        case "LogDir": s.LogDir = val; break;
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
                        default: legacy.ReadKey(key, val); break;
                    }
                }
            }
            catch { }
            if (s.Jobs.Count == 0 && legacy.BlendFile.Length > 0) s.Jobs.Add(legacy);
            return s;
        }

        public void Save()
        {
            var lines = new List<string>
            {
                "BlenderExe=" + BlenderExe,
                "LogDir=" + LogDir,
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
            };
            foreach (Job job in Jobs)
            {
                lines.Add("");
                lines.AddRange(job.SaveLines());
            }
            try { File.WriteAllLines(FilePath, lines.ToArray(), new UTF8Encoding(false)); }
            catch { }
        }
    }

    enum RunState { Idle, Rendering, Waiting, Done, Stopped, Failed }

    class MainForm : Form
    {
        const string AppName = "RenderSolver";
        const string AppTitle = AppName + " 0.6";
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
        List<Job> jobs;       // settings.Jobs, in render order
        Job selected;         // shown in the scene editor
        Job current;          // being rendered
        bool loadingEditor;   // the editor is being filled from a job: its change events are not edits

        // the TextBoxes inside the Fields: the code reads and writes them directly
        TextBox txtBlender, txtBlend, txtOutputDir, txtOutputPrefix, txtFrames, txtLogDir;
        LogView txtLog;
        Toggle chkCustomOutput, chkCustomFrames, chkOverride;
        Label lblScenePath, lblSceneMeta, lblEffectivePath, lblFramesInfo, lblStatus, lblSubtitle, lblTelegram;
        Label lblQueueTitle, lblJobTitle, lblEditorEmpty;
        UiButton btnSceneRefresh, btnAdd, btnUp, btnDown, btnDuplicate, btnRemove;
        QueueList queueList;
        Segmented tabs;
        Control queuePage, settingsPage, editorPanel;
        readonly List<Control> customOutputRows = new List<Control>(); // shown only with "Другая папка вывода"
        readonly List<Control> customFramesRows = new List<Control>(); // shown only with "Свои кадры"
        readonly List<Control> globalControls = new List<Control>();   // locked while the queue runs
        readonly List<Control> editorControls = new List<Control>();   // locked while the edited scene renders
        IntField numRetries, numPause, numHang;
        UiButton btnStart, btnStop, btnLogs, btnOutput, btnTelegram;
        Badge badge;
        ProgressStrip progress;
        StatTile tileFrames, tileEta, tileAvg, tileQueue, tileAttempts;
        ToolTip tips;
        NotifyIcon tray;
        Timer timer;
        PreviewPanel preview;
        TaskbarState taskbarState = TaskbarState.None;
        int taskbarValue = -1, taskbarTotal = -1;

        // scene reads ("resume_render.py -- --info"): one Blender at a time, only while nothing renders
        Process infoProc;
        Job infoJob;
        int infoRequestId;
        DateTime infoDueAt = DateTime.MinValue;

        // queue state, UI thread only
        RunState state = RunState.Idle;
        bool queueRunning, ranOnce;
        string finishMessage = "";
        DateTime queueStart, queueEnd, jobStart;
        int attempt, crashes, hangs;          // of the current scene
        DateTime retryAt;
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
            jobs = settings.Jobs;
            BuildUi();
            queueList.Jobs = jobs;
            queueList.RefreshList();
            queueList.SetSelected(jobs.Count > 0 ? 0 : -1);
            SetRunningUi(false);
            UpdateLabels();
            UpdateTelegramButton();
            infoDueAt = DateTime.Now; // read the scenes on startup
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.DarkTitleBar(Handle);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.F5 && btnStart.Enabled) { StartQueue(); return true; }
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
            ClientSize = new Size(Theme.S(1440), Theme.S(900));
            MinimumSize = new Size(Theme.S(1040), Theme.S(680));
            StartPosition = FormStartPosition.CenterScreen;
            Icon = Theme.AppIcon(Theme.S(32));
            tips = Theme.MakeToolTip();
            Load += delegate
            {
                // laptops / 125-150% scaling: the default size may not fit the screen
                Rectangle area = Screen.FromControl(this).WorkingArea;
                if (Width > area.Width || Height > area.Height) WindowState = FormWindowState.Maximized;
            };

            // header with status and numbers; below it the queue on the left, frames over the log on the right
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = Theme.P(12, 12, 12, 12), Margin = Padding.Empty };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(root);
            root.Controls.Add(BuildHeader(), 0, 0);

            var body = new Split { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1, Margin = Theme.P(0, 12, 0, 0) };
            var right = new Split { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
            body.Panel1.Controls.Add(BuildLeft());
            right.Panel1.Controls.Add(BuildPreview());
            right.Panel2.Controls.Add(BuildLog());
            body.Panel2.Controls.Add(right);
            root.Controls.Add(body, 0, 1);
            // the left column keeps its width (default or dragged by the user), but on a narrow window
            // it gives way so the preview toolbar still fits, and comes back when the window grows
            int leftWidth = 0;
            bool fitting = false;
            Action fitLeft = delegate
            {
                if (leftWidth == 0) return;
                int target = Math.Max(body.Panel1MinSize, Math.Min(leftWidth, body.Width - body.SplitterWidth - Theme.S(600)));
                if (body.SplitterDistance == target) return;
                fitting = true;
                try { body.SplitterDistance = target; } catch { }
                fitting = false;
            };
            body.SizeChanged += delegate { fitLeft(); };
            body.SplitterMoved += delegate { if (!fitting && leftWidth > 0) leftWidth = body.SplitterDistance; };
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
                leftWidth = Theme.S(560);
                fitLeft();
                ActiveControl = btnStart;
            };

            tray = new NotifyIcon { Icon = Theme.AppIcon(Theme.S(16)), Text = AppName, Visible = true };
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
            btnStop.Click += delegate { StopQueue(); };
            btnStart = new UiButton("Старт", Glyph.Play, ButtonKind.Primary) { ButtonHeight = Theme.S(38), MinWidth = Theme.S(132), Font = Theme.Strong, Margin = Padding.Empty };
            btnStart.Click += delegate { StartQueue(); };
            tips.SetToolTip(btnStart, "Рендер всех отмеченных сцен по очереди (F5)");
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
            tileFrames = AddTile(tiles, 0, "Кадры", "Во время рендера — текущая сцена, до старта — все отмеченные сцены");
            tileEta = AddTile(tiles, 1, "Осталось", "Текущая сцена: среднее время кадра × оставшиеся кадры");
            tileAvg = AddTile(tiles, 2, "Среднее время кадра", "По последним 20 кадрам");
            tileQueue = AddTile(tiles, 3, "Очередь", null);
            tileAttempts = AddTile(tiles, 4, "Попытка", "Запуски Blender для текущей сцены");
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

        // queue and scene editor on one tab, settings shared by all scenes on the other
        Control BuildLeft()
        {
            var card = new Card { Dock = DockStyle.Fill, Padding = Theme.P(16, 14, 16, 14) };
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            card.Controls.Add(t);
            tabs = new Segmented("Очередь", "Настройки") { Dock = DockStyle.Fill, Margin = Theme.P(0, 0, 0, 12) };
            t.Controls.Add(tabs, 0, 0);
            var host = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            queuePage = BuildQueuePage();
            settingsPage = BuildSettingsPage();
            settingsPage.Visible = false;
            host.Controls.Add(queuePage);
            host.Controls.Add(settingsPage);
            tabs.SelectedIndexChanged += delegate
            {
                queuePage.Visible = tabs.SelectedIndex == 0;
                settingsPage.Visible = tabs.SelectedIndex == 1;
            };
            t.Controls.Add(host, 0, 1);
            return card;
        }

        Control BuildQueuePage()
        {
            var page = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Margin = Padding.Empty };
            page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            page.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            page.RowStyles.Add(new RowStyle(SizeType.Percent, 50));

            var head = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, RowCount = 1, Margin = Theme.P(0, 0, 0, 8) };
            head.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            head.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            lblQueueTitle = new Label
            {
                AutoSize = false, AutoEllipsis = true, UseMnemonic = false, Anchor = AnchorStyles.Left | AnchorStyles.Right,
                Font = Theme.Caption, ForeColor = Theme.TextFaint, Height = Theme.Caption.Height + Theme.S(4), Margin = Padding.Empty,
            };
            btnAdd = new UiButton("Добавить", Glyph.Plus, ButtonKind.Normal) { Anchor = AnchorStyles.Right, Margin = Padding.Empty };
            btnAdd.Click += delegate { AddScenesDialog(); };
            tips.SetToolTip(btnAdd, "Добавить сцены .blend в конец очереди.\nМожно и перетащить файлы или папку в список.");
            head.Controls.Add(lblQueueTitle, 0, 0);
            head.Controls.Add(btnAdd, 1, 0);
            page.Controls.Add(head, 0, 0);

            queueList = new QueueList { Dock = DockStyle.Fill, Margin = Padding.Empty, Describe = DescribeJob };
            queueList.SelectionChanged += delegate { selected = queueList.SelectedJob; LoadEditor(); };
            queueList.EnabledToggled += job =>
            {
                job.Enabled = !job.Enabled;
                settings.Save();
                UpdateLabels();
            };
            queueList.Moved += (from, to) =>
            {
                Job job = jobs[from];
                jobs.RemoveAt(from);
                if (to > from) to--;
                jobs.Insert(to, job);
                queueList.RefreshList();
                queueList.SetSelected(to);
                settings.Save();
            };
            queueList.FilesDropped += (files, at) => AddScenes(files, at);
            queueList.DeleteRequested += delegate { RemoveSelected(); };
            var menu = Theme.Menu();
            menu.Items.Add("Открыть папку рендера", null, delegate { OpenFolder(OutputFolderOf(selected)); });
            menu.Items.Add("Открыть сцену в Blender", null, delegate { OpenInBlender(selected); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Выше", null, delegate { MoveSelected(-1); });
            menu.Items.Add("Ниже", null, delegate { MoveSelected(1); });
            menu.Items.Add("Дублировать", null, delegate { DuplicateSelected(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Убрать из очереди", null, delegate { RemoveSelected(); });
            menu.Opening += (s, e) =>
            {
                if (selected == null) { e.Cancel = true; return; }
                menu.Items[menu.Items.Count - 1].Enabled = !(queueRunning && selected == current);
            };
            queueList.ContextMenuStrip = menu;
            page.Controls.Add(queueList, 0, 1);

            var bar = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, RowCount = 1, Margin = Theme.P(0, 6, 0, 0) };
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var tools = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Anchor = AnchorStyles.Left, Margin = Padding.Empty };
            btnUp = ListButton(Glyph.Up, "Выше", delegate { MoveSelected(-1); });
            btnDown = ListButton(Glyph.Down, "Ниже", delegate { MoveSelected(1); });
            btnDuplicate = ListButton(Glyph.Duplicate, "Дублировать сцену (например, чтобы отрендерить другие кадры)", delegate { DuplicateSelected(); });
            btnRemove = ListButton(Glyph.Cross, "Убрать из очереди (Delete). Файлы не удаляются", delegate { RemoveSelected(); });
            tools.Controls.AddRange(new Control[] { btnUp, btnDown, btnDuplicate, btnRemove });
            bar.Controls.Add(tools, 0, 0);
            bar.Controls.Add(new Label
            {
                Text = "галочка — рендерить · перетаскивайте, чтобы поменять порядок", AutoSize = false, AutoEllipsis = true,
                Anchor = AnchorStyles.Left | AnchorStyles.Right, Font = Theme.Small, ForeColor = Theme.TextFaint,
                Height = Theme.Small.Height + Theme.S(2), TextAlign = ContentAlignment.MiddleRight, Margin = Theme.P(8, 0, 0, 0),
            }, 1, 0);
            page.Controls.Add(bar, 0, 2);

            var editorHost = new Panel { Dock = DockStyle.Fill, Margin = Theme.P(0, 14, 0, 0) };
            editorPanel = BuildEditor();
            lblEditorEmpty = new Label
            {
                Dock = DockStyle.Fill, Text = "Выберите сцену в очереди, чтобы настроить её кадры и вывод",
                ForeColor = Theme.TextFaint, TextAlign = ContentAlignment.MiddleCenter, Visible = false,
            };
            editorHost.Controls.Add(editorPanel);
            editorHost.Controls.Add(lblEditorEmpty);
            page.Controls.Add(editorHost, 0, 3);
            return page;
        }

        UiButton ListButton(Glyph glyph, string tip, EventHandler click)
        {
            var b = new UiButton("", glyph, ButtonKind.Ghost) { Margin = Theme.P(0, 0, 2, 0), AccessibleName = tip.Split('(', '.')[0].Trim() };
            b.Click += click;
            tips.SetToolTip(b, tip);
            return b;
        }

        // the selected scene: file, frames, output, Override
        Control BuildEditor()
        {
            var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Margin = Padding.Empty };
            scroll.HandleCreated += delegate { Theme.DarkScrollbars(scroll); };
            var t = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 3, Margin = Padding.Empty };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            scroll.Controls.Add(t);

            int row = NextRow(t);
            lblJobTitle = new Label { AutoSize = true, Font = Theme.Caption, ForeColor = Theme.TextFaint, Margin = Theme.P(0, 0, 0, 6) };
            t.Controls.Add(lblJobTitle, 0, row);
            t.SetColumnSpan(lblJobTitle, 3);

            txtBlend = AddPathRow(t, "Сцена", "", "файл сцены .blend", delegate
            {
                BrowseFile(txtBlend, "Blender scene (*.blend)|*.blend");
            }, null, editorControls);

            // output: the scene's own path is primary, a different output folder is optional
            row = NextRow(t);
            t.Controls.Add(RowLabel("Вывод"), 0, row);
            lblScenePath = new Label
            {
                AutoSize = false, AutoEllipsis = true, UseMnemonic = false, Anchor = AnchorStyles.Left | AnchorStyles.Right,
                Height = Font.Height + Theme.S(4), Margin = Theme.P(0, 7, 8, 0), Cursor = Cursors.Hand, Text = "—",
            };
            lblScenePath.Click += delegate { if (selected != null && selected.SceneFilepath != null) OpenFolder(Path.GetDirectoryName(selected.SceneFilepath)); };
            t.Controls.Add(lblScenePath, 1, row);
            btnSceneRefresh = new UiButton("", Glyph.Refresh, ButtonKind.Ghost) { Anchor = AnchorStyles.Top, Margin = Theme.P(0, 3, 0, 3), AccessibleName = "Перечитать сцену" };
            btnSceneRefresh.Click += delegate { if (selected != null) MarkInfoDirty(selected, 0); };
            tips.SetToolTip(btnSceneRefresh, "Перечитать путь вывода и готовые кадры из .blend");
            t.Controls.Add(btnSceneRefresh, 2, row);
            t.SetRowSpan(btnSceneRefresh, 2);
            editorControls.Add(btnSceneRefresh);
            row = NextRow(t);
            lblSceneMeta = new Label
            {
                AutoSize = false, AutoEllipsis = true, UseMnemonic = false, Anchor = AnchorStyles.Left | AnchorStyles.Right,
                Font = Theme.Small, ForeColor = Theme.TextDim, Height = Theme.Small.Height + Theme.S(4), Margin = Theme.P(0, 0, 8, 4),
            };
            t.Controls.Add(lblSceneMeta, 1, row);

            // frames: the scene range by default, or a list of frames / ranges typed by the animator
            row = NextRow(t);
            chkCustomFrames = new Toggle("Свои кадры", "Только выбранные кадры вместо диапазона сцены.") { Margin = Theme.P(0, 12, 0, 4) };
            t.Controls.Add(chkCustomFrames, 0, row);
            t.SetColumnSpan(chkCustomFrames, 3);
            editorControls.Add(chkCustomFrames);
            txtFrames = AddTextRow(t, "Кадры", "", "например 1-100, 120, 150-200", customFramesRows, editorControls);
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

            row = NextRow(t);
            chkCustomOutput = new Toggle("Другая папка вывода", "Своя папка или имя файла; сам .blend не меняется.") { Margin = Theme.P(0, 12, 0, 4) };
            t.Controls.Add(chkCustomOutput, 0, row);
            t.SetColumnSpan(chkCustomOutput, 3);
            editorControls.Add(chkCustomOutput);
            txtOutputDir = AddPathRow(t, "Папка", "", "пусто — папка из сцены", delegate
            {
                BrowseFolder(txtOutputDir);
            }, customOutputRows, editorControls);
            txtOutputPrefix = AddTextRow(t, "Имя файла", "", "пусто — из сцены; shot_ → shot_0001.jpg", customOutputRows, editorControls);
            row = NextRow(t);
            lblEffectivePath = new Label
            {
                AutoSize = false, AutoEllipsis = true, UseMnemonic = false, Anchor = AnchorStyles.Left | AnchorStyles.Right,
                Font = Theme.Small, ForeColor = Theme.TextDim, Height = Theme.Small.Height + Theme.S(4), Margin = Theme.P(0, 2, 0, 2),
            };
            t.Controls.Add(lblEffectivePath, 1, row);
            t.SetColumnSpan(lblEffectivePath, 2);
            customOutputRows.Add(lblEffectivePath);

            row = NextRow(t);
            chkOverride = new Toggle("Override — перерендерить все кадры заново", "") { Margin = Theme.P(0, 12, 0, 0) };
            t.Controls.Add(chkOverride, 0, row);
            t.SetColumnSpan(chkOverride, 3);
            editorControls.Add(chkOverride);

            // every change goes straight into the selected job
            txtBlend.TextChanged += delegate
            {
                if (!Editing) return;
                selected.BlendFile = txtBlend.Text.Trim().Trim('"');
                JobEdited(800);
            };
            chkCustomFrames.CheckedChanged += delegate
            {
                if (Editing)
                {
                    // start from the scene range so the animator edits what Blender would render
                    if (chkCustomFrames.Checked && txtFrames.Text.Trim().Length == 0 && selected.SceneEnd >= selected.SceneStart)
                        txtFrames.Text = selected.SceneStart == selected.SceneEnd
                            ? selected.SceneStart.ToString() : string.Format("{0}-{1}", selected.SceneStart, selected.SceneEnd);
                    selected.CustomFrames = chkCustomFrames.Checked;
                    JobEdited(800);
                }
                UpdateFramesUi();
            };
            txtFrames.TextChanged += delegate
            {
                if (Editing)
                {
                    selected.Frames = txtFrames.Text.Trim();
                    if (selected.CustomFrames) JobEdited(1500);
                }
                UpdateFramesUi();
            };
            chkCustomOutput.CheckedChanged += delegate
            {
                if (Editing)
                {
                    // prefill with the scene values so the user edits from what Blender uses
                    if (chkCustomOutput.Checked && txtOutputDir.Text.Length == 0 && txtOutputPrefix.Text.Length == 0 && selected.SceneFilepath != null)
                    {
                        txtOutputDir.Text = Path.GetDirectoryName(selected.SceneFilepath);
                        txtOutputPrefix.Text = Path.GetFileName(selected.SceneFilepath);
                    }
                    selected.CustomOutput = chkCustomOutput.Checked;
                    JobEdited(800);
                }
                UpdateOutputUi();
            };
            txtOutputDir.TextChanged += delegate
            {
                if (Editing)
                {
                    selected.OutputDir = txtOutputDir.Text.Trim().Trim('"');
                    if (selected.CustomOutput) JobEdited(1500);
                }
                UpdateOutputUi();
            };
            txtOutputPrefix.TextChanged += delegate
            {
                if (Editing)
                {
                    selected.OutputPrefix = txtOutputPrefix.Text.Trim();
                    if (selected.CustomOutput) JobEdited(1500);
                }
                UpdateOutputUi();
            };
            chkOverride.CheckedChanged += delegate
            {
                if (Editing)
                {
                    selected.Override = chkOverride.Checked;
                    // unticking cancels a re-render in progress: from then on existing frames count as done again
                    if (!chkOverride.Checked) { selected.OverrideSince = 0; selected.OverrideKey = ""; }
                    JobEdited(-1);
                }
                UpdateOverrideText();
            };
            return scroll;
        }

        Control BuildSettingsPage()
        {
            var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Margin = Padding.Empty };
            scroll.HandleCreated += delegate { Theme.DarkScrollbars(scroll); };
            var t = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 3, Margin = Padding.Empty };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            scroll.Controls.Add(t);

            AddSection(t, "Blender", true);
            txtBlender = AddPathRow(t, "Blender", settings.BlenderExe, "путь к blender.exe", delegate
            {
                BrowseFile(txtBlender, "Blender|blender.exe|Программы (*.exe)|*.exe");
            }, null, globalControls);
            txtBlender.TextChanged += delegate
            {
                settings.BlenderExe = txtBlender.Text.Trim().Trim('"');
                foreach (Job job in jobs) job.InfoValid = false;
                CancelSceneInfo();
                infoDueAt = DateTime.Now.AddMilliseconds(800);
                RefreshJobViews();
            };
            int row = NextRow(t);
            t.Controls.Add(new Label
            {
                Text = "Один Blender для всех сцен очереди.", AutoSize = true, Font = Theme.Small, ForeColor = Theme.TextFaint, Margin = Theme.P(0, 0, 0, 4),
            }, 1, row);

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
                "Сколько раз запускать Blender для одной сцены,\nпрежде чем отметить её ошибкой и перейти к следующей");
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
            }, null, globalControls);

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
            return scroll;
        }

        Control BuildPreview()
        {
            var card = new Card { Dock = DockStyle.Fill, Padding = Theme.P(16, 12, 16, 14) };
            TableLayoutPanel t = CardLayout(card);
            btnOutput = new UiButton("Папка рендера", Glyph.Folder, ButtonKind.Ghost);
            btnOutput.Click += delegate { OpenFolder(PreviewFolder()); };
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

        TextBox AddPathRow(TableLayoutPanel t, string label, string value, string hint, EventHandler browse, List<Control> group, List<Control> lockGroup)
        {
            int row = NextRow(t);
            var lbl = RowLabel(label);
            var field = new Field(value) { Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = Theme.P(0, 3, 6, 3) };
            var btn = new UiButton("", Glyph.Dots, ButtonKind.Normal) { Margin = Theme.P(0, 3, 0, 3), AccessibleName = "Выбрать: " + label };
            btn.Click += browse;
            tips.SetToolTip(btn, "Выбрать…");
            if (hint != null) SetCueBanner(field.Box, hint);
            t.Controls.Add(lbl, 0, row);
            t.Controls.Add(field, 1, row);
            t.Controls.Add(btn, 2, row);
            lockGroup.Add(field);
            lockGroup.Add(btn);
            if (group != null) group.AddRange(new Control[] { lbl, field, btn });
            return field.Box;
        }

        TextBox AddTextRow(TableLayoutPanel t, string label, string value, string hint, List<Control> group, List<Control> lockGroup)
        {
            int row = NextRow(t);
            var lbl = RowLabel(label);
            var field = new Field(value) { Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = Theme.P(0, 3, 6, 3) };
            if (hint != null) SetCueBanner(field.Box, hint);
            t.Controls.Add(lbl, 0, row);
            t.Controls.Add(field, 1, row);
            lockGroup.Add(field);
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
            globalControls.Add(field);
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
                MessageBox.Show(this, "Папка не найдена:\n" + dir, AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            Process.Start("explorer.exe", "\"" + dir + "\"");
        }

        void OpenInBlender(Job job)
        {
            if (job == null || !File.Exists(job.BlendFile) || !File.Exists(settings.BlenderExe)) return;
            try { Process.Start(new ProcessStartInfo(settings.BlenderExe, "\"" + job.BlendFile + "\"") { WorkingDirectory = Path.GetDirectoryName(job.BlendFile) }); }
            catch (Exception ex) { MessageBox.Show(this, "Не удалось открыть Blender:\n" + ex.Message, AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        static void Lock(Control c, bool locked)
        {
            var field = c as Field;
            if (field != null) field.Locked = locked;
            else c.Enabled = !locked;
        }

        void SetRunningUi(bool running)
        {
            // fields stay readable (and copyable) while locked; everything else is disabled
            foreach (Control c in globalControls) Lock(c, running);
            btnStart.Enabled = !running;
            btnStop.Enabled = running;
            UpdateEditorLock();
        }

        // ─── queue editing ───────────────────────────────────────

        bool Editing
        {
            get { return !loadingEditor && selected != null; }
        }

        /// <summary>The selected job's options changed: re-read the scene after infoDelayMs (negative = no re-read).</summary>
        void JobEdited(int infoDelayMs)
        {
            // an edited scene that already finished in this run renders again if the queue is still running
            if (selected.Status != JobStatus.Rendering) { selected.Status = JobStatus.Waiting; selected.Message = ""; }
            if (infoDelayMs >= 0) MarkInfoDirty(selected, infoDelayMs);
            UpdateJobTitle();
            queueList.Invalidate();
        }

        /// <summary>Fills the scene editor from the selected job.</summary>
        void LoadEditor()
        {
            loadingEditor = true;
            Job j = selected;
            editorPanel.Visible = j != null;
            lblEditorEmpty.Visible = j == null && jobs.Count > 0;
            if (j != null)
            {
                txtBlend.Text = j.BlendFile;
                chkCustomFrames.Checked = j.CustomFrames;
                txtFrames.Text = j.Frames;
                chkCustomOutput.Checked = j.CustomOutput;
                txtOutputDir.Text = j.OutputDir;
                txtOutputPrefix.Text = j.OutputPrefix;
                chkOverride.Checked = j.Override;
            }
            loadingEditor = false;
            UpdateJobTitle();
            UpdateFramesUi();
            UpdateOverrideText();
            UpdateSceneText();
            UpdateEditorLock();
        }

        void UpdateJobTitle()
        {
            int i = selected != null ? jobs.IndexOf(selected) : -1;
            lblJobTitle.Text = i < 0 ? "" : string.Format("СЦЕНА {0} · {1}", i + 1, selected.Name.ToUpperInvariant());
            int enabled = 0;
            foreach (Job j in jobs) if (j.Enabled) enabled++;
            lblQueueTitle.Text = jobs.Count == 0 ? "ОЧЕРЕДЬ ПУСТА"
                : string.Format("ОЧЕРЕДЬ · {0} {1}{2}", jobs.Count, Plural(jobs.Count, "СЦЕНА", "СЦЕНЫ", "СЦЕН"),
                    enabled < jobs.Count ? string.Format(" · ОТМЕЧЕНО {0}", enabled) : "");
        }

        // the scene being rendered is read-only; every other scene can be edited, even while the queue runs
        void UpdateEditorLock()
        {
            bool locked = queueRunning && selected != null && selected == current;
            foreach (Control c in editorControls) Lock(c, locked);
            bool has = selected != null;
            int i = has ? jobs.IndexOf(selected) : -1;
            btnUp.Enabled = has && i > 0;
            btnDown.Enabled = has && i < jobs.Count - 1;
            btnDuplicate.Enabled = has;
            btnRemove.Enabled = has && !locked;
            UpdateOutputUi();
            UpdateFramesUi();
        }

        void AddScenesDialog()
        {
            using (var dlg = new OpenFileDialog { Filter = "Blender scene (*.blend)|*.blend", Multiselect = true, Title = "Добавить сцены в очередь" })
            {
                try
                {
                    Job last = jobs.Count > 0 ? jobs[jobs.Count - 1] : null;
                    string dir = last != null ? Path.GetDirectoryName(last.BlendFile) : null;
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) dlg.InitialDirectory = dir;
                }
                catch { }
                if (dlg.ShowDialog(this) == DialogResult.OK) AddScenes(dlg.FileNames, jobs.Count);
            }
        }

        void AddScenes(string[] files, int at)
        {
            at = Math.Max(0, Math.Min(jobs.Count, at));
            int added = 0;
            foreach (string f in files)
            {
                if (!f.EndsWith(".blend", StringComparison.OrdinalIgnoreCase)) continue; // .blend1 backups are not scenes to render
                jobs.Insert(at + added, new Job { BlendFile = f });
                added++;
            }
            if (added == 0) return;
            queueList.RefreshList();
            queueList.SetSelected(at);
            infoDueAt = DateTime.Now;
            settings.Save();
            AddUi(string.Format("В очередь {0} {1}", added == 1 ? "добавлена" : "добавлено", added == 1
                ? Path.GetFileName(files[0]) : added + " " + Plural(added, "сцена", "сцены", "сцен")));
        }

        void MoveSelected(int delta)
        {
            int i = selected != null ? jobs.IndexOf(selected) : -1;
            int j = i + delta;
            if (i < 0 || j < 0 || j >= jobs.Count) return;
            jobs[i] = jobs[j];
            jobs[j] = selected;
            queueList.RefreshList();
            queueList.SetSelected(j);
            settings.Save();
        }

        void DuplicateSelected()
        {
            if (selected == null) return;
            int i = jobs.IndexOf(selected);
            jobs.Insert(i + 1, selected.Duplicate());
            queueList.RefreshList();
            queueList.SetSelected(i + 1);
            infoDueAt = DateTime.Now;
            settings.Save();
        }

        void RemoveSelected()
        {
            if (selected == null || (queueRunning && selected == current)) return;
            if (infoJob == selected) CancelSceneInfo();
            int i = jobs.IndexOf(selected);
            jobs.RemoveAt(i);
            queueList.RefreshList();
            queueList.SetSelected(Math.Min(i, jobs.Count - 1));
            settings.Save();
        }

        // ─── queue rows ──────────────────────────────────────────

        JobView DescribeJob(Job j)
        {
            var v = new JobView { Title = j.Name, DetailColor = Theme.TextDim, Progress = -1, Active = queueRunning && j == current };
            if (v.Active)
            {
                int t, d, cur;
                lock (sync) { t = total; d = done; cur = currentFrame; }
                v.Detail = state == RunState.Waiting ? "ждёт перезапуска Blender…"
                    : cur > 0 ? string.Format("рендерится кадр {0} · готово {1} из {2}", cur, d, t) : "Blender загружает сцену…";
                v.Progress = t > 0 ? (float)d / t : 0;
                v.Chip = t > 0 ? (int)(100L * d / t) + "%" : "РЕНДЕР";
                v.ChipColor = state == RunState.Waiting ? Theme.Orange : Theme.AccentText;
                return v;
            }

            if (infoJob == j) v.Detail = "читаю сцену…";
            else if (j.InfoValid && j.SceneFilepath == null && j.InfoError != null) { v.Detail = j.InfoError; v.DetailColor = Theme.Orange; }
            else if (j.SceneFilepath != null && j.SceneExt == null) { v.Detail = "видео FFMPEG — нужен вывод кадрами (JPG/PNG/EXR)"; v.DetailColor = Theme.Orange; }
            else if (j.SceneFilepath != null)
            {
                string frames = FramesSummary(j);
                v.Detail = frames + (j.Total > 0 ? string.Format("{0}готово {1} из {2}", frames.Length > 0 ? " · " : "", j.Done, j.Total) : "");
                if (j.Total > 0) v.Progress = (float)j.Done / j.Total;
            }
            else v.Detail = "сцена ещё не прочитана";

            bool complete = j.InfoValid && j.Total > 0 && j.Done >= j.Total;
            switch (j.Status)
            {
                case JobStatus.Done: v.Chip = "ГОТОВО"; v.ChipColor = Theme.Green; break;
                case JobStatus.Failed:
                    v.Chip = "ОШИБКА";
                    v.ChipColor = Theme.Red;
                    v.Detail = j.Message;
                    v.DetailColor = Theme.Red;
                    break;
                case JobStatus.Stopped: v.Chip = "СТОП"; v.ChipColor = Theme.TextDim; break;
                default:
                    if (j.Override) { v.Chip = "ЗАНОВО"; v.ChipColor = Theme.Orange; }
                    else if (complete) { v.Chip = "ГОТОВО"; v.ChipColor = Theme.Green; }
                    else if (queueRunning && j.Enabled) { v.Chip = "ЖДЁТ"; v.ChipColor = Theme.TextDim; }
                    break;
            }
            return v;
        }

        string FramesSummary(Job j)
        {
            if (j.CustomFrames)
            {
                string error;
                List<KeyValuePair<int, int>> ranges = ParseFrames(j.Frames, out error);
                return ranges == null ? "неверный список кадров" : "кадры " + FramesText(ranges, "–", ", ");
            }
            if (j.SceneEnd < j.SceneStart) return "";
            return string.Format("кадры {0}–{1}", j.SceneStart, j.SceneEnd) + (j.SceneStep > 1 ? ", шаг " + j.SceneStep : "");
        }

        // something about the jobs changed outside the editor (scene read, status): redraw what shows it
        void RefreshJobViews()
        {
            queueList.Invalidate();
            UpdateJobTitle();
            UpdateSceneText();
            UpdateOutputUi();
        }

        // ─── scene output path ───────────────────────────────────

        // Blender appends the frame number (4 digits) unless the name already has #### in it
        static string FramePathDisplay(string pathNoExt, string ext)
        {
            string name = Path.GetFileName(pathNoExt);
            return pathNoExt + (name.Contains("#") ? "" : "####") + "." + (ext ?? "ext");
        }

        void UpdateOutputUi()
        {
            bool custom = chkCustomOutput.Checked && selected != null;
            foreach (Control c in customOutputRows) c.Visible = custom;

            string effective = EffectiveOutputPath(selected);
            string ext = selected != null ? selected.SceneExt : null;
            if (!custom)
                lblEffectivePath.Text = "";
            else if (effective == null)
                lblEffectivePath.Text = "Кадры будут сохраняться в: (сцена ещё не прочитана)";
            else
                lblEffectivePath.Text = "Кадры будут сохраняться в:  " + FramePathDisplay(effective, ext);
            tips.SetToolTip(lblEffectivePath, effective != null && custom ? FramePathDisplay(effective, ext) : null);

            // while the queue runs the preview follows the scene Blender renders
            if (!queueRunning) preview.SetSequence(effective, ext);
        }

        /// <summary>scene.render.filepath with the job's custom output applied; null while the scene is unknown.</summary>
        static string EffectiveOutputPath(Job j)
        {
            if (j == null) return null;
            string dir = "", prefix = "";
            if (j.CustomOutput)
            {
                dir = j.OutputDir.Trim().Trim('"').TrimEnd('\\', '/');
                prefix = j.OutputPrefix.Trim();
            }
            if (j.SceneFilepath == null && (dir.Length == 0 || prefix.Length == 0)) return null;
            if (dir.Length == 0) dir = Path.GetDirectoryName(j.SceneFilepath);
            if (prefix.Length == 0) prefix = Path.GetFileName(j.SceneFilepath);
            try { return Path.Combine(dir, prefix); }
            catch { return null; } // invalid characters while typing
        }

        static string OutputFolderOf(Job j)
        {
            string path = EffectiveOutputPath(j);
            return path != null ? Path.GetDirectoryName(path) : null;
        }

        string PreviewFolder()
        {
            if (queueRunning)
            {
                lock (sync) if (!string.IsNullOrEmpty(outputDir)) return outputDir;
                return OutputFolderOf(current);
            }
            return OutputFolderOf(selected);
        }

        /// <summary>resume_render.py options for the job's custom output ("" without one).</summary>
        static string OutputArgs(Job j)
        {
            if (!j.CustomOutput) return "";
            string dir = j.OutputDir.Trim().Trim('"').TrimEnd('\\', '/'); // a trailing backslash would escape the closing quote
            string prefix = j.OutputPrefix.Trim();
            var args = new StringBuilder();
            if (dir.Length > 0) args.AppendFormat(" --output-dir \"{0}\"", dir);
            if (prefix.Length > 0 && !prefix.Contains("\"")) args.AppendFormat(" --prefix \"{0}\"", prefix);
            return args.ToString();
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
            bool custom = chkCustomFrames.Checked && selected != null;
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

        // ─── reading the scenes ──────────────────────────────────

        /// <summary>Re-reads the job's scene after delayMs (debounces typing).</summary>
        void MarkInfoDirty(Job j, int delayMs)
        {
            j.InfoValid = false;
            if (infoJob == j) CancelSceneInfo();
            infoDueAt = DateTime.Now.AddMilliseconds(delayMs);
            RefreshJobViews();
        }

        void CancelSceneInfo()
        {
            infoRequestId++;
            if (infoProc != null) { try { infoProc.Kill(); } catch { } }
            infoProc = null;
            infoJob = null;
        }

        /// <summary>Starts reading the next scene that needs it; called from the timer while the queue is idle.</summary>
        void PumpSceneInfo()
        {
            if (infoProc != null || DateTime.Now < infoDueAt) return;
            bool changed = false;
            foreach (Job j in jobs)
            {
                if (j.InfoValid) continue;
                string problem = !File.Exists(settings.BlenderExe) ? "укажите blender.exe на вкладке «Настройки»"
                    : j.BlendFile.Length == 0 ? "выберите файл сцены .blend"
                    : !File.Exists(j.BlendFile) ? "файл сцены не найден"
                    : !File.Exists(ScriptPath) ? "рядом с программой нет resume_render.py" : null;
                if (problem != null)
                {
                    j.InfoValid = true;
                    j.InfoError = problem;
                    j.SceneFilepath = null;
                    j.Total = j.Done = 0;
                    j.SceneEnd = -1;
                    changed = true;
                    continue;
                }
                StartSceneInfo(j);
                changed = true;
                break;
            }
            if (changed) RefreshJobViews();
        }

        void StartSceneInfo(Job j)
        {
            int id = ++infoRequestId;
            // with "Свои кадры" the status ("done N of M") counts the selected frames
            string frames = j.CustomFrames ? NormalizeFrames(j.Frames) : null;
            bool framesSelected = frames != null;
            var psi = new ProcessStartInfo(settings.BlenderExe,
                string.Format("-b \"{0}\" --python-exit-code {1} -P \"{2}\" -- --info{3}{4}", j.BlendFile, ScriptErrorExitCode, ScriptPath,
                    frames != null ? " --frames \"" + frames + "\"" : "", OutputArgs(j)))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = Path.GetDirectoryName(j.BlendFile),
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
                try { if (!IsDisposed) BeginInvoke(new Action(() => ApplySceneInfo(j, id, p, lines, framesSelected))); } catch { }
            };
            try
            {
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                infoProc = p;
                infoJob = j;
            }
            catch (Exception ex)
            {
                j.InfoValid = true;
                j.InfoError = "не удалось запустить Blender: " + ex.Message;
                j.SceneFilepath = null;
            }
        }

        void ApplySceneInfo(Job j, int id, Process p, List<string> lines, bool framesSelected)
        {
            if (infoProc == p) { infoProc = null; infoJob = null; }
            p.Dispose();
            if (id != infoRequestId) return; // cancelled or superseded

            string path = null, ext = null;
            int start = 0, end = -1, step = 1, rangeTotal = 0, rangeDone = 0;
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
                        if (parts.Length >= 3 && int.TryParse(parts[1], out start) && int.TryParse(parts[2], out end))
                            if (parts.Length < 4 || !int.TryParse(parts[3], out step)) step = 1;
                    }
                    else if (msg.StartsWith("STATUS "))
                    {
                        Match m = StatusRe.Match(msg);
                        if (m.Success)
                        {
                            rangeTotal = int.Parse(m.Groups[1].Value);
                            rangeDone = int.Parse(m.Groups[2].Value);
                        }
                    }
                }
            }

            j.InfoValid = true;
            j.SceneFilepath = path;
            j.InfoError = path == null ? "не удалось прочитать сцену (подробности — в журнале при запуске рендера)" : null;
            j.SceneExt = ext == "FFMPEG" ? null : ext;
            j.SceneStart = start;
            j.SceneEnd = path != null ? end : -1;
            j.SceneStep = Math.Max(1, step);
            j.Total = rangeTotal;
            j.Done = rangeDone;
            j.InfoFramesSelected = framesSelected;
            infoDueAt = DateTime.Now; // go on with the next scene
            RefreshJobViews();
        }

        // path line of the editor; the line under it carries frames / status / problems
        void UpdateSceneText()
        {
            Job j = selected;
            string path = "—", meta = "", tip = null;
            Color metaColor = Theme.TextDim;
            if (j != null)
            {
                if (j.SceneFilepath != null)
                {
                    path = j.SceneExt != null ? FramePathDisplay(j.SceneFilepath, j.SceneExt) : j.SceneFilepath;
                    tip = path + "\nКлик — открыть папку";
                }
                if (infoJob == j) meta = "читаю сцену в Blender…";
                else if (!j.InfoValid) meta = j.SceneFilepath != null ? "обновлю, когда Blender освободится…" : "сцена ещё не прочитана";
                else if (j.SceneFilepath == null) { meta = j.InfoError ?? ""; metaColor = Theme.Orange; }
                else if (j.SceneExt == null) { meta = "Видео FFMPEG: продолжение по кадрам невозможно, переключите вывод на JPG/PNG/EXR"; metaColor = Theme.Orange; }
                else
                {
                    var parts = new List<string>();
                    if (j.SceneEnd >= j.SceneStart)
                        parts.Add(string.Format(j.InfoFramesSelected ? "сцена {0}–{1}" : "кадры {0}–{1}", j.SceneStart, j.SceneEnd)
                            + (j.SceneStep > 1 ? ", шаг " + j.SceneStep : ""));
                    if (j.Total > 0)
                        parts.Add(string.Format(j.InfoFramesSelected ? "готово {0} из {1} выбранных" : "готово {0} из {1}", j.Done, j.Total));
                    meta = string.Join("  ·  ", parts.ToArray());
                }
            }
            lblScenePath.Text = path;
            lblScenePath.ForeColor = j != null && j.SceneFilepath != null ? Theme.Text : Theme.TextDim;
            tips.SetToolTip(lblScenePath, tip);
            lblSceneMeta.Text = meta;
            lblSceneMeta.ForeColor = metaColor;
        }

        // ─── queue control ───────────────────────────────────────

        string ScriptPath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "resume_render.py"); }
        }

        void ReadSettingsFromUi()
        {
            settings.BlenderExe = txtBlender.Text.Trim().Trim('"');
            settings.LogDir = txtLogDir.Text.Trim().Trim('"');
            settings.MaxRetries = numRetries.Value;
            settings.RetryPauseSec = numPause.Value;
            settings.HangTimeoutMin = numHang.Value;
        }

        /// <summary>What is wrong with a job's options, or null.</summary>
        static string JobProblem(Job j)
        {
            string framesError = null;
            if (j.BlendFile.Length == 0) return "не выбран файл .blend";
            if (!File.Exists(j.BlendFile)) return "не найден файл " + j.BlendFile;
            if (j.CustomFrames && ParseFrames(j.Frames, out framesError) == null) return "свои кадры: " + framesError;
            string dir = j.OutputDir.Trim().Trim('"').TrimEnd('\\', '/'), prefix = j.OutputPrefix.Trim();
            if (j.CustomOutput && dir.Length > 0 && !Path.IsPathRooted(dir)) return "папка вывода должна быть полным путём, например E:\\Render\\shot01";
            if (j.CustomOutput && (prefix.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || prefix.Contains("\"")))
                return "имя файла содержит недопустимые символы: " + prefix;
            return null;
        }

        void StartQueue()
        {
            ReadSettingsFromUi();

            var toRender = new List<Job>();
            foreach (Job j in jobs) if (j.Enabled) toRender.Add(j);
            string error = null;
            if (!File.Exists(settings.BlenderExe)) error = "Не найден blender.exe:\n" + settings.BlenderExe + "\n\nУкажите его на вкладке «Настройки».";
            else if (!File.Exists(ScriptPath))
                error = "Рядом с программой не найден resume_render.py:\n" + ScriptPath +
                        "\n\nЕсли программа запущена прямо из zip-архива — распакуйте архив целиком в обычную папку и запустите оттуда.";
            else if (jobs.Count == 0) error = "Очередь пуста. Добавьте сцены .blend кнопкой «Добавить» или перетащите их в список.";
            else if (toRender.Count == 0) error = "Ни одна сцена не отмечена галочкой.";
            else
            {
                var problems = new List<string>();
                foreach (Job j in toRender)
                {
                    string problem = JobProblem(j);
                    if (problem != null) problems.Add(string.Format("• {0}. {1}: {2}", jobs.IndexOf(j) + 1, j.Name, problem));
                }
                if (problems.Count > 0) error = "Не все сцены готовы к рендеру:\n\n" + string.Join("\n", problems.ToArray());
                else
                {
                    try { Directory.CreateDirectory(settings.LogDir); }
                    catch (Exception ex) { error = "Не удалось создать папку логов:\n" + ex.Message; }
                    foreach (Job j in toRender)
                    {
                        string dir = j.OutputDir.Trim().Trim('"').TrimEnd('\\', '/');
                        if (error != null || !j.CustomOutput || dir.Length == 0) continue;
                        try { Directory.CreateDirectory(dir); }
                        catch (Exception ex) { error = "Не удалось создать папку вывода " + dir + ":\n" + ex.Message; }
                    }
                }
            }
            if (error != null)
            {
                MessageBox.Show(this, error, AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // Override: a new re-render needs confirmation; an unfinished one for the same scene/output just continues
            var newOverrides = new List<Job>();
            foreach (Job j in toRender)
                if (j.Override && !(j.OverrideSince > 0 && j.OverrideKey == OverrideKeyFor(j))) newOverrides.Add(j);
            if (newOverrides.Count > 0)
            {
                var names = new List<string>();
                foreach (Job j in newOverrides) names.Add("• " + j.Name + (j.CustomFrames ? " (выбранные кадры)" : " (все кадры)"));
                var answer = MessageBox.Show(this,
                    "Override: эти сцены будут отрендерены заново:\n\n" + string.Join("\n", names.ToArray()) + "\n\n" +
                    "Уже существующие файлы кадров будут перезаписаны по мере рендера. " +
                    "Если рендер прервётся или вы его остановите, при следующем старте он продолжится с места остановки, " +
                    "а ещё не перезаписанные старые кадры останутся на месте.\n\nНачать?",
                    AppTitle, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                if (answer != DialogResult.Yes) return;
                long now = ToUnixSeconds(DateTime.UtcNow);
                foreach (Job j in newOverrides)
                {
                    j.OverrideSince = now;
                    j.OverrideKey = OverrideKeyFor(j);
                }
                UpdateOverrideText();
            }
            settings.Save();

            // do not load a scene twice in RAM: no scene reads while rendering
            CancelSceneInfo();

            foreach (Job j in jobs) { j.Status = JobStatus.Waiting; j.Message = ""; }
            queueRunning = ranOnce = true;
            stopRequested = false;
            queueStart = DateTime.Now;
            if (txtLog.TextLength > 0) AddUi("");
            AddUi(string.Format("Старт очереди: {0} {1}", toRender.Count, Plural(toRender.Count, "сцена", "сцены", "сцен")), LogKind.Section);
            SetRunningUi(true);
            SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED); // keep PC awake during the render
            RunNextJob();
        }

        /// <summary>Starts the first ticked scene that has not been rendered in this run, or ends the queue.</summary>
        void RunNextJob()
        {
            Job next = null;
            foreach (Job j in jobs) if (j.Enabled && j.Status == JobStatus.Waiting) { next = j; break; }
            if (next == null) { FinishQueue(false); return; }

            // checked again: the job may have been edited while the queue ran
            string problem = JobProblem(next);
            if (problem != null)
            {
                current = next;
                AddUi(string.Format("── {0} ──", next.Name), LogKind.Section);
                FinishJob(JobStatus.Failed, problem);
                return;
            }
            // already complete according to the last scene read: no need to load it in Blender
            if (!next.Override && next.InfoValid && next.SceneFilepath != null && next.Total > 0 && next.Done >= next.Total)
            {
                next.Status = JobStatus.Done;
                next.Message = "все кадры уже на диске";
                AddUi(string.Format("{0}: все кадры уже на диске — пропускаю", next.Name), LogKind.Success);
                queueList.Invalidate();
                RunNextJob();
                return;
            }

            current = next;
            next.Status = JobStatus.Rendering;
            attempt = crashes = hangs = 0;
            killedByWatchdog = false;
            jobStart = DateTime.Now;
            lock (sync)
            {
                total = done = currentFrame = 0;
                durations.Clear();
                seenWarnings.Clear();
                outputDir = null;
            }
            int position, count;
            QueuePosition(out position, out count);
            AddUi(string.Format("── Сцена {0} из {1}: {2} ──", position, count, next.Name), LogKind.Section);
            if (next.CustomFrames)
            {
                string framesError;
                List<KeyValuePair<int, int>> ranges = ParseFrames(next.Frames, out framesError);
                int n = FrameCount(ranges);
                AddUi(string.Format("Свои кадры: {0} ({1} {2})", FramesText(ranges, "–", ", "), n, Plural(n, "кадр", "кадра", "кадров")));
            }
            if (next.CustomOutput)
                AddUi(string.Format("Другая папка вывода: папка = {0}, имя = {1}",
                    next.OutputDir.Length > 0 ? next.OutputDir : "(из сцены)",
                    next.OutputPrefix.Length > 0 ? next.OutputPrefix : "(из сцены)"));
            if (next.Override)
                AddUi(string.Format("Override: перерендер, начатый {0:dd.MM HH:mm}. Кадры, сохранённые раньше, перезаписываются.",
                    FromUnixSeconds(next.OverrideSince)));
            string expected = EffectiveOutputPath(next);
            preview.SetSequence(expected, next.SceneExt);
            UpdateEditorLock();
            queueList.Invalidate();
            Launch();
        }

        /// <summary>1-based place of the current scene among the ticked ones, and how many are ticked.</summary>
        void QueuePosition(out int position, out int count)
        {
            position = count = 0;
            foreach (Job j in jobs)
            {
                if (!j.Enabled) continue;
                count++;
                if (j == current) position = count;
            }
        }

        void Launch()
        {
            Job job = current;
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
                    logWriter.WriteLine("[{0}] {1} - launch attempt #{2} - {3:yyyy-MM-dd HH:mm:ss}", AppName, job.BlendFile, attempt, DateTime.Now);
                    logWriter.WriteLine("===================================================");
                }
                catch (Exception ex)
                {
                    logWriter = null;
                    uiLines.Enqueue(Line("Не удалось открыть файл лога: " + ex.Message, LogKind.Error));
                }
            }
            AddUi(string.Format("Попытка #{0} из {1}", attempt, settings.MaxRetries), LogKind.Section);

            // everything after "--" is ignored by Blender and read by resume_render.py
            var args = new StringBuilder();
            args.AppendFormat("-b \"{0}\" --python-exit-code {1} -P \"{2}\" --", job.BlendFile, ScriptErrorExitCode, ScriptPath);
            args.Append(OutputArgs(job));
            if (job.Override && job.OverrideSince > 0) args.AppendFormat(" --override-since {0}", job.OverrideSince);
            string frames = job.CustomFrames ? NormalizeFrames(job.Frames) : null; // validated in RunNextJob
            if (frames != null) args.AppendFormat(" --frames \"{0}\"", frames);

            var psi = new ProcessStartInfo(settings.BlenderExe, args.ToString())
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = Path.GetDirectoryName(job.BlendFile),
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
                FinishJob(JobStatus.Failed, "не удалось запустить Blender: " + ex.Message);
                return;
            }
            proc = p;
            state = RunState.Rendering;
        }

        void OnExited(int code)
        {
            CloseLog(string.Format("[{0}] Blender exited with code {1}", AppName, code));
            if (proc != null) { proc.Dispose(); proc = null; }

            bool allDone;
            lock (sync) allDone = allDoneSeen;

            if (stopRequested) { FinishJob(JobStatus.Stopped, "остановлено вручную"); return; }
            if (allDone) { FinishJob(JobStatus.Done, "все кадры отрендерены"); return; }
            if (code == ScriptErrorExitCode)
            {
                FinishJob(JobStatus.Failed, "ошибка в resume_render.py (код 2) — подробности в журнале");
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
                        code, settings.RetryPauseSec, attempt + 1, settings.MaxRetries), false, current);
            }
            else
                AddUi("Blender завершился (код 0), но завершение рендера не подтверждено — перезапуск.", LogKind.Warning);

            if (attempt >= settings.MaxRetries)
            {
                FinishJob(JobStatus.Failed, string.Format("достигнут лимит попыток ({0})", settings.MaxRetries));
                return;
            }
            retryAt = DateTime.Now.AddSeconds(settings.RetryPauseSec);
            state = RunState.Waiting;
        }

        void StopQueue()
        {
            if (state == RunState.Waiting)
            {
                stopRequested = true;
                FinishJob(JobStatus.Stopped, "остановлено вручную");
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

        /// <summary>The current scene ended; the queue goes on unless it was stopped.</summary>
        void FinishJob(JobStatus status, string message)
        {
            Job j = current;
            j.Status = status;
            j.Message = message;
            AddUi(string.Format("■ {0}: {1}", j.Name, message),
                status == JobStatus.Done ? LogKind.Success : status == JobStatus.Failed ? LogKind.Error : LogKind.Event);

            if (status == JobStatus.Done && j.Override)
            {
                // re-render complete: the next Start resumes this scene normally
                j.Override = false;
                j.OverrideSince = 0;
                j.OverrideKey = "";
                if (selected == j) { loadingEditor = true; chkOverride.Checked = false; loadingEditor = false; UpdateOverrideText(); }
                AddUi(j.Name + ": Override завершён, все кадры отрендерены заново, галочка снята.", LogKind.Success);
            }
            // the row shows what this render reached until the scene is re-read once the queue is idle
            lock (sync)
            {
                if (total > 0) { j.Total = total; j.Done = status == JobStatus.Done ? total : done; }
            }
            j.InfoValid = false;
            settings.Save();

            if (status == JobStatus.Done && settings.TgOnDone) NotifyTelegram("✅ Сцена готова", true, j);
            else if (status == JobStatus.Failed)
            {
                tray.ShowBalloonTip(15000, "Сцена остановлена с ошибкой", j.Name + ": " + message, ToolTipIcon.Error);
                if (settings.TgOnFail) NotifyTelegram("❌ Сцена остановлена: " + message, true, j);
            }

            queueList.Invalidate();
            if (status == JobStatus.Stopped) FinishQueue(true);
            else RunNextJob();
        }

        void FinishQueue(bool stopped)
        {
            queueRunning = false;
            int ok = 0, failed = 0, count = 0;
            foreach (Job j in jobs)
            {
                if (!j.Enabled) continue;
                count++;
                if (j.Status == JobStatus.Done) ok++;
                else if (j.Status == JobStatus.Failed) failed++;
            }
            if (stopped)
            {
                state = RunState.Stopped;
                finishMessage = "Остановлено вручную";
            }
            else if (failed > 0)
            {
                state = RunState.Failed;
                finishMessage = string.Format("Очередь закончена: готово {0} из {1}, с ошибкой {2}", ok, count, failed);
            }
            else
            {
                state = RunState.Done;
                finishMessage = count == 1 ? "Все кадры отрендерены" : string.Format("Все {0} {1} отрендерены", count, Plural(count, "сцена", "сцены", "сцен"));
            }
            current = null;
            queueEnd = DateTime.Now;
            AddUi("■ " + finishMessage, state == RunState.Done ? LogKind.Success : state == RunState.Failed ? LogKind.Error : LogKind.Event);
            SetRunningUi(false);
            SetThreadExecutionState(ES_CONTINUOUS);
            infoDueAt = DateTime.Now; // refresh "done N of M" of the scenes
            UpdateLabels();
            queueList.Invalidate();

            if (state == RunState.Done)
            {
                SystemSounds.Asterisk.Play();
                tray.ShowBalloonTip(15000, "Рендер завершён", finishMessage, ToolTipIcon.Info);
                Theme.Flash(this);
            }
            else if (state == RunState.Failed)
            {
                SystemSounds.Hand.Play();
                tray.ShowBalloonTip(15000, "Очередь закончена с ошибками", finishMessage, ToolTipIcon.Warning);
                Theme.Flash(this);
            }
            if (!stopped && count > 1 && settings.TgOnDone) NotifyTelegram("🏁 " + finishMessage, false, null);
        }

        // ─── Override (re-render all frames) ─────────────────────

        // a pending re-render belongs to one scene + output; another scene/output starts a new one
        static string OverrideKeyFor(Job j)
        {
            string output = j.CustomOutput ? j.OutputDir + "|" + j.OutputPrefix : "";
            return (j.BlendFile + "|" + output).ToLowerInvariant();
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
            chkOverride.Description = selected != null && chkOverride.Checked && selected.OverrideSince > 0
                ? string.Format("Начат {0:dd.MM HH:mm}, продолжится с места остановки.", FromUnixSeconds(selected.OverrideSince))
                : "Старые кадры перезаписываются по ходу рендера.";
        }

        // ─── Telegram ────────────────────────────────────────────

        void UpdateTelegramButton()
        {
            int chats = TelegramApi.ParseChatIds(settings.TgChats).Count;
            lblTelegram.Text = settings.TgEnabled ? string.Format("включено, получателей: {0}", chats) : "выключено";
            lblTelegram.ForeColor = settings.TgEnabled ? Theme.Green : Theme.TextDim;
        }

        /// <summary>Sends headline + details of the scene (or of the queue when job is null) on a background thread.</summary>
        void NotifyTelegram(string headline, bool withPhoto, Job job)
        {
            string token = settings.TgToken;
            List<string> chats = TelegramApi.ParseChatIds(settings.TgChats);
            if (!TelegramFeature || !settings.TgEnabled || token.Length == 0 || chats.Count == 0) return;

            var text = new StringBuilder(headline);
            if (job != null)
            {
                int t, d;
                string dir;
                lock (sync) { t = total; d = done; dir = outputDir; }
                text.Append("\nСцена: ").Append(Path.GetFileName(job.BlendFile));
                if (t > 0) text.AppendFormat("\nКадры: {0} из {1}", d, t);
                text.AppendFormat("\nВремя: {0} · крашей: {1}", FormatDuration((DateTime.Now - jobStart).TotalSeconds), crashes);
                if (!string.IsNullOrEmpty(dir)) text.Append("\nПапка: ").Append(dir);
            }
            else
            {
                foreach (Job j in jobs)
                    if (j.Enabled)
                        text.AppendFormat("\n{0} {1}", j.Status == JobStatus.Done ? "✅" : j.Status == JobStatus.Failed ? "❌" : "·", j.Name);
                text.AppendFormat("\nВремя: {0}", FormatDuration((queueEnd - queueStart).TotalSeconds));
            }
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

            if (state == RunState.Waiting && queueRunning && DateTime.Now >= retryAt) Launch();

            // scenes are read only while idle, so a read never competes with the render for RAM
            if (!queueRunning) PumpSceneInfo();

            if (++flushCounter >= 10)
            {
                flushCounter = 0;
                lock (sync)
                {
                    if (logWriter != null) try { logWriter.Flush(); } catch { }
                }
            }

            if (queueRunning) queueList.Invalidate(); // live progress of the current row
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
            bool active = state == RunState.Rendering || state == RunState.Waiting;
            int enabled = 0, finished = 0, failed = 0, position, count;
            foreach (Job j in jobs)
            {
                if (!j.Enabled) continue;
                enabled++;
                if (j.Status == JobStatus.Done || j.Status == JobStatus.Failed) finished++;
                if (j.Status == JobStatus.Failed) failed++;
            }
            QueuePosition(out position, out count);
            if (!queueRunning)
            {
                // frames of all ticked scenes, as far as they have been read
                int qt = 0, qd = 0;
                foreach (Job j in jobs)
                    if (j.Enabled && j.SceneFilepath != null) { qt += j.Total; qd += Math.Min(j.Done, j.Total); }
                t = qt;
                d = qd;
            }
            btnOutput.Enabled = PreviewFolder() != null;

            int pct = t > 0 ? (int)(100L * d / t) : 0;
            Color bar = Theme.TextFaint;
            switch (state)
            {
                case RunState.Idle:
                    SetStatus("", jobs.Count == 0 ? "Очередь пуста" : "Готов к запуску", Theme.Text, Theme.Text); break;
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
                    SetStatus("ОШИБКИ", finishMessage, Theme.Red, Theme.Red);
                    bar = Theme.Red; break;
            }
            progress.Set(d, t, bar);

            string subtitle;
            if (queueRunning && current != null)
            {
                subtitle = string.Format("Сцена {0} из {1}  ·  {2}", position, count, current.Name);
                if (current.CustomFrames)
                {
                    string framesError;
                    List<KeyValuePair<int, int>> ranges = ParseFrames(current.Frames, out framesError);
                    if (ranges != null) subtitle += "  ·  кадры " + FramesText(ranges, "–", ", ");
                }
                if (dir == null) dir = OutputFolderOf(current);
                if (dir != null) subtitle += "   →   " + dir;
            }
            else if (jobs.Count == 0)
                subtitle = "Добавьте сцены .blend: кнопка «Добавить» или перетащите файлы в очередь";
            else
                subtitle = string.Format("{0} {1} в очереди", enabled, Plural(enabled, "сцена", "сцены", "сцен"))
                    + (enabled < jobs.Count ? string.Format(", ещё {0} без галочки", jobs.Count - enabled) : "");
            if (lblSubtitle.Text != subtitle) lblSubtitle.Text = subtitle;

            tileFrames.Set(t > 0 ? string.Format("{0} / {1}", d, t) : "—",
                t > 0 ? string.Format("{0}% · осталось {1}", pct, t - d) : "", Theme.Text);
            if (active && avg > 0 && t > d)
            {
                double eta = avg * (t - d);
                tileEta.Set("≈ " + FormatDuration(eta), string.Format("сцена · к {0:HH:mm, dd.MM}", DateTime.Now.AddSeconds(eta)), Theme.Text);
            }
            else
                tileEta.Set("—", "", Theme.Text);
            tileAvg.Set(avg > 0 ? FormatDuration(avg) : "—", last > 0 ? "последний " + FormatDuration(last) : "", Theme.Text);
            if (queueRunning)
                tileQueue.Set(string.Format("{0} / {1}", position, count), "прошло " + FormatDuration((DateTime.Now - queueStart).TotalSeconds), Theme.Text);
            else if (ranOnce)
                tileQueue.Set(string.Format("{0} / {1}", finished - failed, enabled),
                    (failed > 0 ? string.Format("ошибок {0} · ", failed) : "") + "за " + FormatDuration((queueEnd - queueStart).TotalSeconds),
                    failed > 0 ? Theme.Orange : Theme.Text);
            else
                tileQueue.Set(enabled > 0 ? enabled.ToString() : "—", enabled > 0 ? Plural(enabled, "сцена", "сцены", "сцен") + " к рендеру" : "", Theme.Text);
            tileAttempts.Set(attempt > 0 ? string.Format("{0} / {1}", attempt, settings.MaxRetries) : "—",
                attempt > 0 ? string.Format("крашей {0} · зависаний {1}", crashes, hangs) : "", crashes + hangs > 0 ? Theme.Orange : Theme.Text);

            string title = active && t > 0
                ? (count > 1 ? string.Format("{0}% · сцена {1}/{2} — {3}", pct, position, count, AppTitle) : string.Format("{0}% — {1}", pct, AppTitle))
                : AppTitle;
            if (Text != title) Text = title;
            string tip = active && t > 0 ? string.Format("{0} — {1}% ({2} / {3})", AppName, pct, d, t) : AppName;
            if (tray.Text != tip) tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
            UpdateTaskbar(active ? t : 0, active ? d : 0);
            UpdateJobTitle();
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
            if (queueRunning)
            {
                var answer = MessageBox.Show(this, "Рендер ещё идёт. Остановить Blender и выйти?",
                    AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (answer != DialogResult.Yes) { e.Cancel = true; return; }
                stopRequested = true;
                if (proc != null)
                {
                    try { proc.Kill(); proc.WaitForExit(5000); } catch { }
                }
                CloseLog(string.Format("[{0}] Stopped: window closed", AppName));
                SetThreadExecutionState(ES_CONTINUOUS);
            }
            else
                ReadSettingsFromUi();
            settings.Save();
            timer.Stop();
            if (infoProc != null) { try { infoProc.Kill(); } catch { } }
            tray.Visible = false;
            tray.Dispose();
            base.OnFormClosing(e);
        }
    }
}
