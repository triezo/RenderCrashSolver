// QueueList.cs - the render queue: one row per scene, drag to reorder, drop .blend files or folders from Explorer.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace RenderSolver
{
    /// <summary>What one queue row shows; MainForm builds it from the job and the running render.</summary>
    struct JobView
    {
        public string Title, Detail, Chip;
        public Color DetailColor, ChipColor;
        public float Progress;    // 0..1, negative = no bar
        public bool Active;       // being rendered right now
    }

    class QueueList : Panel
    {
        public List<Job> Jobs = new List<Job>();
        public Func<Job, JobView> Describe;
        public event EventHandler SelectionChanged;
        public event Action<Job> EnabledToggled;
        public event Action<int, int> Moved;             // from index, insert position (0..Count) counted before the removal
        public event Action<string[], int> FilesDropped; // .blend paths, insert position
        public event EventHandler DeleteRequested;

        int selected = -1, hover = -1, dragFrom = -1, dropAt = -1;
        Point downAt;
        bool dragging;

        public QueueList()
        {
            SetStyle(ControlStyles.Selectable | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            AutoScroll = true;
            AllowDrop = true;
            TabStop = true;
            BackColor = Theme.Input;
            Font = Theme.Base;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.DarkScrollbars(this);
        }

        int RowHeight { get { return Theme.Strong.Height + Theme.Small.Height + Theme.S(26); } }
        int Pad { get { return Theme.S(6); } }

        public Job SelectedJob
        {
            get { return selected >= 0 && selected < Jobs.Count ? Jobs[selected] : null; }
        }

        public int SelectedIndex { get { return selected; } }

        /// <summary>Selects a row (-1 = none) and always raises SelectionChanged.</summary>
        public void SetSelected(int index)
        {
            selected = Jobs.Count == 0 ? -1 : Math.Max(-1, Math.Min(Jobs.Count - 1, index));
            EnsureVisible(selected);
            Invalidate();
            if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
        }

        /// <summary>Call after the Jobs list changed.</summary>
        public void RefreshList()
        {
            AutoScrollMinSize = new Size(0, Jobs.Count * RowHeight + 2 * Pad);
            if (selected >= Jobs.Count) selected = Jobs.Count - 1;
            Invalidate();
        }

        void EnsureVisible(int i)
        {
            if (i < 0) return;
            int top = Pad + i * RowHeight, scroll = -AutoScrollPosition.Y;
            if (top < scroll) AutoScrollPosition = new Point(0, top - Pad);
            else if (top + RowHeight > scroll + ClientSize.Height) AutoScrollPosition = new Point(0, top + RowHeight - ClientSize.Height + Pad);
        }

        Rectangle RowRect(int i)
        {
            return new Rectangle(Pad, Pad + i * RowHeight + AutoScrollPosition.Y, ClientSize.Width - 2 * Pad, RowHeight - Theme.S(4));
        }

        Rectangle CheckRect(int i)
        {
            Rectangle r = RowRect(i);
            int s = Theme.S(16);
            return new Rectangle(r.X + Theme.S(12), r.Y + Theme.S(8) + (Theme.Strong.Height - s) / 2, s, s);
        }

        int IndexAt(Point p)
        {
            int y = p.Y - AutoScrollPosition.Y - Pad;
            if (y < 0) return -1;
            int i = y / RowHeight;
            return i < Jobs.Count ? i : -1;
        }

        int InsertAt(Point p)
        {
            int y = p.Y - AutoScrollPosition.Y - Pad;
            return Math.Max(0, Math.Min(Jobs.Count, (int)Math.Round((double)y / RowHeight)));
        }

        // ─── painting ────────────────────────────────────────────

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            if (Jobs.Count == 0)
            {
                TextRenderer.DrawText(g, "Перетащите сюда файлы .blend или папку со сценами\nили нажмите «Добавить»", Font, ClientRectangle,
                    dropAt >= 0 ? Theme.AccentText : Theme.TextFaint,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
                if (dropAt >= 0) Theme.Stroke(g, Theme.Accent, new RectangleF(Pad, Pad, ClientSize.Width - 2 * Pad, ClientSize.Height - 2 * Pad), Theme.S(5));
                return;
            }
            for (int i = 0; i < Jobs.Count; i++)
            {
                Rectangle r = RowRect(i);
                if (r.Bottom < 0 || r.Top > ClientSize.Height) continue;
                JobView v = Describe != null ? Describe(Jobs[i]) : new JobView { Title = Jobs[i].Name, DetailColor = Theme.TextDim, Progress = -1 };
                DrawRow(g, i, r, v);
            }
            if (dropAt >= 0)
            {
                int y = Pad + dropAt * RowHeight + AutoScrollPosition.Y - Theme.S(3);
                Theme.Fill(g, Theme.Accent, new RectangleF(Pad, y, ClientSize.Width - 2 * Pad, Theme.S(2)), 1);
            }
        }

        void DrawRow(Graphics g, int i, Rectangle r, JobView v)
        {
            Job job = Jobs[i];
            const TextFormatFlags line = TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine;
            Color fill = i == selected ? Theme.Raised : i == hover ? Theme.Mix(Theme.Tile, Theme.Raised, 0.5f) : Theme.Tile;
            float radius = Theme.S(5);
            Theme.Fill(g, fill, r, radius);
            if (i == selected && Focused) Theme.Stroke(g, Theme.Mix(fill, Theme.Accent, 0.7f), r, radius);
            if (v.Active) Theme.Fill(g, Theme.Accent, new RectangleF(r.X, r.Y + Theme.S(8), Theme.S(3), r.Height - Theme.S(16)), 1);

            // ticked = rendered by Start
            Rectangle cb = CheckRect(i);
            if (job.Enabled)
            {
                Theme.Fill(g, Theme.Accent, cb, Theme.S(3));
                using (var pen = new Pen(Color.White, Math.Max(1.5f, Theme.S(2))))
                {
                    pen.StartCap = pen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                    var old = g.SmoothingMode;
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    g.DrawLines(pen, new[]
                    {
                        new PointF(cb.X + cb.Width * 0.25f, cb.Y + cb.Height * 0.52f),
                        new PointF(cb.X + cb.Width * 0.43f, cb.Y + cb.Height * 0.7f),
                        new PointF(cb.X + cb.Width * 0.76f, cb.Y + cb.Height * 0.32f),
                    });
                    g.SmoothingMode = old;
                }
            }
            else
            {
                Theme.Fill(g, Theme.Input, cb, Theme.S(3));
                Theme.Stroke(g, Theme.BorderHover, cb, Theme.S(3));
            }

            int x = cb.Right + Theme.S(10), right = r.Right - Theme.S(12), y = r.Y + Theme.S(8);
            if (!string.IsNullOrEmpty(v.Chip))
            {
                Size cs = TextRenderer.MeasureText(v.Chip, Theme.Caption, Size.Empty, TextFormatFlags.NoPadding);
                var chip = new Rectangle(right - cs.Width - Theme.S(16), y, cs.Width + Theme.S(16), cs.Height + Theme.S(7));
                Theme.Fill(g, Theme.Mix(fill, v.ChipColor, 0.2f), chip, chip.Height / 2f);
                TextRenderer.DrawText(g, v.Chip, Theme.Caption, chip, v.ChipColor,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                right = chip.Left - Theme.S(8);
            }
            string number = (i + 1) + ".";
            int nw = TextRenderer.MeasureText(g, number, Theme.Strong, Size.Empty, TextFormatFlags.NoPadding).Width;
            TextRenderer.DrawText(g, number, Theme.Strong, new Point(x, y), Theme.TextFaint, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, v.Title ?? "", Theme.Strong, new Rectangle(x + nw + Theme.S(6), y, Math.Max(0, right - x - nw - Theme.S(6)), Theme.Strong.Height),
                job.Enabled ? Theme.Text : Theme.TextFaint, line);

            int detailY = y + Theme.Strong.Height + Theme.S(2), textRight = r.Right - Theme.S(12);
            TextRenderer.DrawText(g, v.Detail ?? "", Theme.Small, new Rectangle(x, detailY, Math.Max(0, textRight - x), Theme.Small.Height),
                job.Enabled ? v.DetailColor : Theme.TextFaint, line);

            if (v.Progress >= 0)
            {
                var bar = new RectangleF(x, r.Bottom - Theme.S(7), Math.Max(0, textRight - x), Theme.S(3));
                Theme.Fill(g, Theme.Mix(fill, Color.Black, 0.3f), bar, bar.Height / 2);
                float p = Math.Min(1f, v.Progress);
                if (p > 0)
                    Theme.Fill(g, v.Active ? Theme.Accent : p >= 1 ? Theme.Green : Theme.TextFaint,
                        new RectangleF(bar.X, bar.Y, Math.Max(bar.Height, bar.Width * p), bar.Height), bar.Height / 2);
            }
        }

        // ─── mouse and keys ──────────────────────────────────────

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            int i = IndexAt(e.Location);
            if (i < 0) return;
            if (e.Button == MouseButtons.Left && CheckRect(i).Contains(e.Location))
            {
                if (EnabledToggled != null) EnabledToggled(Jobs[i]);
                Invalidate();
                return;
            }
            if (i != selected) SetSelected(i);
            if (e.Button == MouseButtons.Left)
            {
                dragFrom = i;
                downAt = e.Location;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragFrom >= 0 && e.Button == MouseButtons.Left)
            {
                if (!dragging && (Math.Abs(e.Y - downAt.Y) > Theme.S(6) || Math.Abs(e.X - downAt.X) > Theme.S(6))) dragging = true;
                if (dragging)
                {
                    int at = InsertAt(e.Location);
                    if (at != dropAt) { dropAt = at; Invalidate(); }
                }
                return;
            }
            int h = IndexAt(e.Location);
            if (h != hover) { hover = h; Invalidate(); }
            Cursor = h >= 0 && CheckRect(h).Contains(e.Location) ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (dragging && dropAt >= 0 && dropAt != dragFrom && dropAt != dragFrom + 1 && Moved != null) Moved(dragFrom, dropAt);
            dragging = false;
            dragFrom = dropAt = -1;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hover = -1;
            Invalidate();
        }

        protected override void OnScroll(ScrollEventArgs se) { base.OnScroll(se); Invalidate(); }
        protected override void OnMouseWheel(MouseEventArgs e) { base.OnMouseWheel(e); Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        protected override bool IsInputKey(Keys keyData)
        {
            if (keyData == Keys.Up || keyData == Keys.Down) return true;
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Up && selected > 0) SetSelected(selected - 1);
            else if (e.KeyCode == Keys.Down && selected < Jobs.Count - 1) SetSelected(selected + 1);
            else if (e.KeyCode == Keys.Delete && DeleteRequested != null) DeleteRequested(this, EventArgs.Empty);
            else if (e.KeyCode == Keys.Space && SelectedJob != null && EnabledToggled != null) { EnabledToggled(SelectedJob); Invalidate(); }
            else return;
            e.Handled = true;
        }

        // ─── files from Explorer ─────────────────────────────────

        /// <summary>.blend files of the drop; a dropped folder adds the .blend files directly inside it.</summary>
        static string[] BlendFiles(DragEventArgs e)
        {
            var files = e.Data.GetDataPresent(DataFormats.FileDrop) ? e.Data.GetData(DataFormats.FileDrop) as string[] : null;
            if (files == null) return null;
            var result = new List<string>();
            foreach (string f in files)
            {
                if (f.EndsWith(".blend", StringComparison.OrdinalIgnoreCase) && File.Exists(f)) result.Add(f);
                else if (Directory.Exists(f))
                {
                    try
                    {
                        string[] inside = Directory.GetFiles(f, "*.blend");
                        Array.Sort(inside, StringComparer.OrdinalIgnoreCase);
                        foreach (string b in inside) if (b.EndsWith(".blend", StringComparison.OrdinalIgnoreCase)) result.Add(b);
                    }
                    catch { }
                }
            }
            return result.Count > 0 ? result.ToArray() : null;
        }

        protected override void OnDragEnter(DragEventArgs e)
        {
            base.OnDragEnter(e);
            e.Effect = BlendFiles(e) != null ? DragDropEffects.Copy : DragDropEffects.None;
        }

        protected override void OnDragOver(DragEventArgs e)
        {
            base.OnDragOver(e);
            if (BlendFiles(e) == null) { e.Effect = DragDropEffects.None; return; }
            e.Effect = DragDropEffects.Copy;
            int at = InsertAt(PointToClient(new Point(e.X, e.Y)));
            if (at != dropAt) { dropAt = at; Invalidate(); }
        }

        protected override void OnDragLeave(EventArgs e)
        {
            base.OnDragLeave(e);
            dropAt = -1;
            Invalidate();
        }

        protected override void OnDragDrop(DragEventArgs e)
        {
            base.OnDragDrop(e);
            string[] files = BlendFiles(e);
            int at = dropAt < 0 ? Jobs.Count : dropAt;
            dropAt = -1;
            Invalidate();
            if (files != null && FilesDropped != null) FilesDropped(files, at);
        }
    }
}
