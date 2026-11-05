// PreviewPanel - shows finished frames of the render sequence: latest frame, scrubbing, flipbook playback.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace RenderSolver
{
    class PreviewPanel : UserControl
    {
        PictureBox picture;
        Scrubber slider;
        Label lblInfo;
        UiButton btnPrev, btnNext, btnPlay, btnLatest, btnRescan;
        Toggle chkFollow;
        IntField numFps;
        Timer playTimer, resizeTimer;
        ToolTip tips;
        string emptyText = "";   // drawn in the middle of the viewer while there is no image

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
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Theme.Surface;
            ForeColor = Theme.Text;
            Font = Theme.Base;
            tips = Theme.MakeToolTip();

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

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Margin = Padding.Empty };
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(layout);

            picture = new PictureBox
            {
                Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Theme.Viewer, Cursor = Cursors.Hand, Margin = Padding.Empty,
            };
            picture.DoubleClick += delegate { OpenShownFile(); };
            picture.MouseDown += delegate { if (slider.Enabled) slider.Focus(); }; // arrow keys / space work after a click on the image
            picture.SizeChanged += delegate { resizeTimer.Stop(); resizeTimer.Start(); };
            picture.Paint += (s, e) =>
            {
                if (picture.Image != null || emptyText.Length == 0) return;
                TextRenderer.DrawText(e.Graphics, emptyText, Theme.Base, picture.ClientRectangle, Theme.TextFaint,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            };
            var menu = Theme.Menu();
            menu.Items.Add("Открыть кадр", null, delegate { OpenShownFile(); });
            menu.Items.Add("Показать в папке", null, delegate
            {
                string p = ShownPath;
                if (p != null) Process.Start("explorer.exe", "/select,\"" + p + "\"");
            });
            picture.ContextMenuStrip = menu;
            tips.SetToolTip(picture, "Двойной клик — открыть кадр в полном размере\n← / → — по кадру, пробел — проиграть");
            layout.Controls.Add(picture, 0, 0);

            slider = new Scrubber { Dock = DockStyle.Fill, Margin = Theme.P(0, 8, 0, 4) };
            slider.ValueChanged += delegate
            {
                if (updatingSlider) return;
                ShowIndex(slider.Value);
                // scrubbing back pauses following; returning to the end resumes it
                chkFollow.Checked = slider.Value == frames.Count - 1;
            };
            slider.KeyDown += (s, e) => { if (e.KeyCode == Keys.Space) { TogglePlay(); e.Handled = true; } };
            layout.Controls.Add(slider, 0, 1);

            // transport buttons | frame info | follow, fps, rescan
            var bar = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 5, RowCount = 1, Margin = Padding.Empty };
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var transport = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Anchor = AnchorStyles.Left, Margin = Padding.Empty };
            btnPrev = IconButton(Glyph.Prev, "Предыдущий кадр (←)", delegate { Step(-1); });
            btnPlay = IconButton(Glyph.Play, "Проиграть подряд (пробел)", delegate { TogglePlay(); });
            btnNext = IconButton(Glyph.Next, "Следующий кадр (→)", delegate { Step(1); });
            btnLatest = IconButton(Glyph.Latest, "Последний кадр (End)", delegate { chkFollow.Checked = true; ShowIndex(frames.Count - 1); });
            transport.Controls.AddRange(new Control[] { btnPrev, btnPlay, btnNext, btnLatest });
            bar.Controls.Add(transport, 0, 0);

            lblInfo = new Label
            {
                AutoSize = false, AutoEllipsis = true, UseMnemonic = false, Anchor = AnchorStyles.Left | AnchorStyles.Right,
                Height = Font.Height + Theme.S(4), ForeColor = Theme.TextDim, Margin = Theme.P(10, 0, 10, 0), TextAlign = ContentAlignment.MiddleLeft,
            };
            bar.Controls.Add(lblInfo, 1, 0);

            chkFollow = new Toggle("Показывать новые", null) { Checked = true, Anchor = AnchorStyles.Left, Margin = Theme.P(0, 0, 14, 0) };
            tips.SetToolTip(chkFollow, "Сразу показывать каждый новый готовый кадр");
            bar.Controls.Add(chkFollow, 2, 0);
            numFps = new IntField(1, 60, 24, "fps") { Width = Theme.S(76), Anchor = AnchorStyles.Left, Margin = Theme.P(0, 0, 6, 0) };
            numFps.Box.TextChanged += delegate { playTimer.Interval = Math.Max(1, 1000 / numFps.Value); };
            tips.SetToolTip(numFps.Box, "Скорость проигрывания");
            bar.Controls.Add(numFps, 3, 0);
            btnRescan = IconButton(Glyph.Refresh, "Перечитать папку с кадрами", delegate { Rescan(); });
            btnRescan.Margin = Padding.Empty;
            bar.Controls.Add(btnRescan, 4, 0);
            layout.Controls.Add(bar, 0, 2);

            // no sequence yet: disabled transport and the "path unknown" hint until SetSequence
            SyncSlider();
            UpdateInfo();
        }

        UiButton IconButton(Glyph glyph, string tip, EventHandler click)
        {
            var b = new UiButton("", glyph, ButtonKind.Ghost) { Margin = Theme.P(0, 0, 2, 0), AccessibleName = tip.Split('(')[0].Trim() };
            b.Click += click;
            tips.SetToolTip(b, tip);
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
                btnPlay.Glyph = Glyph.Play;
            }
            else if (frames.Count > 0)
            {
                chkFollow.Checked = false;
                playTimer.Start();
                btnPlay.Glyph = Glyph.Pause;
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
                lblInfo.Text = "";
                SetEmptyText(seqPattern == null ? "Кадров нет: путь вывода ещё не известен" : "Пока нет готовых кадров в\n" + seqDir);
                return;
            }
            SetEmptyText(loadError ?? "");
            if (shownIndex < 0) { lblInfo.Text = frames.Count + " кадров"; return; }
            string path = paths[frames[shownIndex]];
            string when = "";
            try { when = File.GetLastWriteTime(path).ToString("dd.MM HH:mm:ss"); } catch { }
            lblInfo.Text = string.Format("Кадр {0}   ·   {1} из {2} на диске   ·   {3}   ·   {4}{5}",
                frames[shownIndex], shownIndex + 1, frames.Count, Path.GetFileName(path), when,
                loadError != null ? "   ·   " + loadError : "");
        }

        void SetEmptyText(string text)
        {
            if (emptyText == text) return;
            emptyText = text;
            picture.Invalidate();
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
