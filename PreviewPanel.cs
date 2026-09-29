// PreviewPanel - shows finished frames of the render sequence: latest frame, scrubbing, flipbook playback.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace RenderCrashSolver
{
    class PreviewPanel : UserControl
    {
        PictureBox picture;
        TrackBar slider;
        Label lblInfo;
        Button btnPrev, btnNext, btnPlay, btnLatest, btnRescan;
        CheckBox chkFollow;
        NumericUpDown numFps;
        Timer playTimer, resizeTimer;

        // current sequence: <dir>\<prefix>####<suffix>.<ext>
        string seqDir, seqExt;
        Regex seqPattern;
        readonly List<int> frames = new List<int>();          // sorted frame numbers on disk
        readonly Dictionary<int, string> paths = new Dictionary<int, string>();
        int shownIndex = -1;
        bool updatingSlider;

        public PreviewPanel()
        {
            Dock = DockStyle.Fill;

            // timers first: control events below may fire while the layout is being built
            playTimer = new Timer { Interval = 1000 / 24 };
            playTimer.Tick += delegate
            {
                if (frames.Count == 0) { TogglePlay(); return; }
                ShowIndex(shownIndex >= frames.Count - 1 ? 0 : shownIndex + 1);
            };
            resizeTimer = new Timer { Interval = 300 };
            resizeTimer.Tick += delegate
            {
                resizeTimer.Stop();
                if (!playTimer.Enabled && shownIndex >= 0) LoadImage(shownIndex);
            };

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1 };
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(layout);

            picture = new PictureBox
            {
                Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.FromArgb(32, 32, 32), Cursor = Cursors.Hand, Margin = new Padding(3),
            };
            picture.DoubleClick += delegate { OpenShownFile(); };
            picture.SizeChanged += delegate { resizeTimer.Stop(); resizeTimer.Start(); };
            var menu = new ContextMenuStrip();
            menu.Items.Add("Открыть кадр", null, delegate { OpenShownFile(); });
            menu.Items.Add("Показать в папке", null, delegate
            {
                string p = ShownPath;
                if (p != null) Process.Start("explorer.exe", "/select,\"" + p + "\"");
            });
            picture.ContextMenuStrip = menu;
            new ToolTip().SetToolTip(picture, "Двойной клик — открыть кадр в полном размере");
            layout.Controls.Add(picture, 0, 0);

            slider = new TrackBar { Dock = DockStyle.Fill, Minimum = 0, Maximum = 0, TickStyle = TickStyle.None, Margin = new Padding(3, 3, 3, 0) };
            slider.ValueChanged += delegate
            {
                if (updatingSlider) return;
                ShowIndex(slider.Value);
                // scrubbing back pauses following; returning to the end resumes it
                chkFollow.Checked = slider.Value == frames.Count - 1;
            };
            layout.Controls.Add(slider, 0, 1);

            lblInfo = new Label { AutoSize = false, Dock = DockStyle.Fill, AutoEllipsis = true, Height = 20, Margin = new Padding(3, 0, 3, 3), Text = "Нет кадров" };
            layout.Controls.Add(lblInfo, 0, 2);

            var bar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = new Padding(0) };
            btnPrev = SmallButton("◀", delegate { Step(-1); });
            btnNext = SmallButton("▶", delegate { Step(1); });
            btnPlay = SmallButton("Играть", delegate { TogglePlay(); });
            btnLatest = SmallButton("Последний", delegate { chkFollow.Checked = true; ShowIndex(frames.Count - 1); });
            btnRescan = SmallButton("↻", delegate { Rescan(); });
            new ToolTip().SetToolTip(btnRescan, "Перечитать папку с кадрами");
            chkFollow = new CheckBox { Text = "Показывать новые кадры", Checked = true, AutoSize = true, Margin = new Padding(8, 6, 3, 3) };
            var lblFps = new Label { Text = "FPS", AutoSize = true, Margin = new Padding(8, 6, 0, 3) };
            numFps = new NumericUpDown { Minimum = 1, Maximum = 60, Value = 24, Width = 48, Margin = new Padding(3, 3, 3, 3) };
            numFps.ValueChanged += delegate { playTimer.Interval = 1000 / (int)numFps.Value; };
            bar.Controls.AddRange(new Control[] { btnPrev, btnPlay, btnNext, btnLatest, btnRescan, chkFollow, lblFps, numFps });
            layout.Controls.Add(bar, 0, 3);
        }

        Button SmallButton(string text, EventHandler click)
        {
            var b = new Button
            {
                Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                MinimumSize = new Size(32, 0), Padding = new Padding(6, 1, 6, 1), Margin = new Padding(0, 0, 4, 0),
            };
            b.Click += click;
            return b;
        }

        /// <summary>Newest frame of the sequence on disk, or null.</summary>
        public string LatestFramePath
        {
            get { return frames.Count > 0 ? paths[frames[frames.Count - 1]] : null; }
        }

        string ShownPath
        {
            get { return shownIndex >= 0 && shownIndex < frames.Count ? paths[frames[shownIndex]] : null; }
        }

        // ─── sequence ────────────────────────────────────────────

        /// <summary>filepath = scene.render.filepath made absolute (no extension), ext = jpg/png/...</summary>
        public void SetSequence(string filepath, string ext)
        {
            string dir = null;
            Regex pattern = null;
            if (!string.IsNullOrEmpty(filepath) && !string.IsNullOrEmpty(ext))
            {
                try
                {
                    dir = Path.GetDirectoryName(filepath);
                    string name = Path.GetFileName(filepath);
                    string prefix = name, suffix = "";
                    if (name.Contains("#"))
                    {
                        prefix = name.Substring(0, name.IndexOf('#'));
                        suffix = name.Substring(name.LastIndexOf('#') + 1);
                    }
                    pattern = new Regex("^" + Regex.Escape(prefix) + @"(\d+)" + Regex.Escape(suffix) + @"\." + Regex.Escape(ext) + "$",
                        RegexOptions.IgnoreCase);
                }
                catch { dir = null; pattern = null; }
            }
            bool same = dir == seqDir && ext == seqExt && (pattern == null ? seqPattern == null : seqPattern != null && pattern.ToString() == seqPattern.ToString());
            if (same) return;
            seqDir = dir;
            seqExt = ext;
            seqPattern = pattern;
            Rescan();
        }

        public void Rescan()
        {
            int keepFrame = shownIndex >= 0 && shownIndex < frames.Count ? frames[shownIndex] : -1;
            frames.Clear();
            paths.Clear();
            if (seqPattern != null && Directory.Exists(seqDir))
            {
                try
                {
                    foreach (string file in Directory.GetFiles(seqDir, "*." + seqExt))
                    {
                        Match m = seqPattern.Match(Path.GetFileName(file));
                        int n;
                        if (m.Success && int.TryParse(m.Groups[1].Value, out n) && !paths.ContainsKey(n))
                        {
                            paths[n] = file;
                            frames.Add(n);
                        }
                    }
                }
                catch { }
            }
            frames.Sort();

            int index = frames.Count - 1;
            if (!chkFollow.Checked && keepFrame >= 0)
            {
                int k = frames.IndexOf(keepFrame);
                if (k >= 0) index = k;
            }
            SyncSlider();
            shownIndex = -1;
            ShowIndex(index);
        }

        /// <summary>A frame was just written by Blender.</summary>
        public void AddFrame(int frame, string path)
        {
            if (!paths.ContainsKey(frame))
            {
                int pos = frames.BinarySearch(frame);
                frames.Insert(pos < 0 ? ~pos : pos, frame);
            }
            paths[frame] = path;
            SyncSlider();
            if (chkFollow.Checked && !playTimer.Enabled)
                ShowIndex(frames.IndexOf(frame));
            else
                UpdateInfo();
        }

        void SyncSlider()
        {
            updatingSlider = true;
            slider.Maximum = Math.Max(0, frames.Count - 1);
            slider.Enabled = frames.Count > 1;
            if (shownIndex >= 0 && shownIndex <= slider.Maximum) slider.Value = shownIndex;
            updatingSlider = false;
            bool any = frames.Count > 0;
            btnPrev.Enabled = btnNext.Enabled = btnPlay.Enabled = btnLatest.Enabled = any;
        }

        // ─── display ─────────────────────────────────────────────

        void Step(int delta)
        {
            if (frames.Count == 0) return;
            chkFollow.Checked = false;
            ShowIndex(Math.Max(0, Math.Min(frames.Count - 1, shownIndex + delta)));
        }

        void TogglePlay()
        {
            if (playTimer.Enabled)
            {
                playTimer.Stop();
                btnPlay.Text = "Играть";
            }
            else if (frames.Count > 0)
            {
                chkFollow.Checked = false;
                playTimer.Start();
                btnPlay.Text = "Пауза";
            }
        }

        void ShowIndex(int index)
        {
            if (frames.Count == 0 || index < 0)
            {
                shownIndex = -1;
                SetImage(null);
                UpdateInfo();
                return;
            }
            index = Math.Min(index, frames.Count - 1);
            if (index == shownIndex && picture.Image != null) return;
            shownIndex = index;
            updatingSlider = true;
            slider.Value = index;
            updatingSlider = false;
            LoadImage(index);
        }

        string loadError;

        void LoadImage(int index)
        {
            loadError = null;
            string path = paths[frames[index]];
            try
            {
                // read into memory so the file is never locked while Blender may overwrite it
                byte[] bytes = File.ReadAllBytes(path);
                using (var ms = new MemoryStream(bytes))
                using (var img = Image.FromStream(ms))
                {
                    Size box = picture.ClientSize;
                    double scale = Math.Min(1.0, Math.Min((double)box.Width / img.Width, (double)box.Height / img.Height));
                    int w = Math.Max(1, (int)(img.Width * scale)), h = Math.Max(1, (int)(img.Height * scale));
                    var bmp = new Bitmap(w, h);
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.InterpolationMode = playTimer.Enabled ? InterpolationMode.Bilinear : InterpolationMode.HighQualityBicubic;
                        g.DrawImage(img, 0, 0, w, h);
                    }
                    SetImage(bmp);
                }
            }
            catch (Exception ex)
            {
                SetImage(null);
                loadError = seqExt == "exr" ? "EXR нельзя показать в превью" : "не удалось открыть: " + ex.Message;
            }
            UpdateInfo();
        }

        void SetImage(Image img)
        {
            Image old = picture.Image;
            picture.Image = img;
            if (old != null) old.Dispose();
        }

        void UpdateInfo()
        {
            if (frames.Count == 0)
            {
                lblInfo.Text = seqPattern == null ? "Нет кадров (путь вывода ещё не известен)" : "Нет готовых кадров в " + seqDir;
                return;
            }
            if (shownIndex < 0) { lblInfo.Text = frames.Count + " кадров"; return; }
            string path = paths[frames[shownIndex]];
            string when = "";
            try { when = File.GetLastWriteTime(path).ToString("dd.MM HH:mm:ss"); } catch { }
            lblInfo.Text = string.Format("Кадр {0}   ({1} из {2} на диске)   ·   {3}   ·   {4}{5}",
                frames[shownIndex], shownIndex + 1, frames.Count, Path.GetFileName(path), when,
                loadError != null ? "   ·   " + loadError : "");
        }

        void OpenShownFile()
        {
            string p = ShownPath;
            if (p != null && File.Exists(p))
            {
                try { Process.Start(p); } catch { }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                playTimer.Dispose();
                resizeTimer.Dispose();
                SetImage(null);
            }
            base.Dispose(disposing);
        }
    }
}
