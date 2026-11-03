// Theme.cs - dark look (Blender-like palette) and the owner-drawn controls the windows are built from.
// Sizes are 96-dpi pixels scaled with Theme.S(); fonts are in points and follow the system DPI by themselves.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace RenderCrashSolver
{
    static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(24, 24, 24);
        public static readonly Color Surface = Color.FromArgb(40, 40, 40);
        public static readonly Color Tile = Color.FromArgb(49, 49, 49);
        public static readonly Color Raised = Color.FromArgb(58, 58, 58);
        public static readonly Color RaisedHover = Color.FromArgb(72, 72, 72);
        public static readonly Color Input = Color.FromArgb(29, 29, 29);
        public static readonly Color Border = Color.FromArgb(64, 64, 64);
        public static readonly Color BorderHover = Color.FromArgb(92, 92, 92);
        public static readonly Color Text = Color.FromArgb(230, 230, 230);
        public static readonly Color TextDim = Color.FromArgb(158, 158, 158);
        public static readonly Color TextFaint = Color.FromArgb(112, 112, 112);
        public static readonly Color Accent = Color.FromArgb(71, 114, 179);      // Blender selection blue
        public static readonly Color AccentHover = Color.FromArgb(88, 133, 201);
        public static readonly Color AccentText = Color.FromArgb(132, 168, 224);
        public static readonly Color Green = Color.FromArgb(104, 190, 116);
        public static readonly Color Orange = Color.FromArgb(234, 166, 64);
        public static readonly Color Red = Color.FromArgb(232, 98, 88);
        public static readonly Color Viewer = Color.FromArgb(18, 18, 18);

        public static readonly Font Base = new Font("Segoe UI", 9F);
        public static readonly Font Small = new Font("Segoe UI", 8.25F);
        public static readonly Font Strong = new Font("Segoe UI Semibold", 9.75F);
        public static readonly Font Caption = new Font("Segoe UI Semibold", 7.5F);
        public static readonly Font Title = new Font("Segoe UI Semibold", 14F);
        public static readonly Font Value = new Font("Segoe UI Semibold", 13F);
        public static readonly Font Mono = new Font("Consolas", 9F);

        public static readonly float Scale = ReadScale();

        static float ReadScale()
        {
            try { using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) return g.DpiX / 96F; }
            catch { return 1F; }
        }

        public static int S(int px) { return (int)Math.Round(px * Scale); }

        public static Padding P(int left, int top, int right, int bottom)
        {
            return new Padding(S(left), S(top), S(right), S(bottom));
        }

        public static Color Mix(Color a, Color b, float t)
        {
            return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        public static Color ColorOf(LogKind kind)
        {
            switch (kind)
            {
                case LogKind.Frame: return TextDim;
                case LogKind.Section: return AccentText;
                case LogKind.Success: return Green;
                case LogKind.Warning: return Orange;
                case LogKind.Error: return Red;
                default: return Text;
            }
        }

        // ─── drawing ─────────────────────────────────────────────

        public static GraphicsPath RoundRect(RectangleF r, float radius)
        {
            var path = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d < 1) { path.AddRectangle(r); return path; }
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static void Fill(Graphics g, Color c, RectangleF r, float radius)
        {
            SmoothingMode old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath path = RoundRect(r, radius))
            using (var brush = new SolidBrush(c))
                g.FillPath(brush, path);
            g.SmoothingMode = old;
        }

        public static void Stroke(Graphics g, Color c, RectangleF r, float radius)
        {
            SmoothingMode old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath path = RoundRect(new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1), radius))
            using (var pen = new Pen(c))
                g.DrawPath(pen, path);
            g.SmoothingMode = old;
        }

        // owner-drawn controls paint their corners in the colour of whatever they sit on
        public static Color BackOf(Control c)
        {
            return c.Parent != null ? c.Parent.BackColor : Bg;
        }

        static PointF[] Pts(RectangleF b, params float[] f)
        {
            var p = new PointF[f.Length / 2];
            for (int i = 0; i < p.Length; i++) p[i] = new PointF(b.X + b.Width * f[2 * i], b.Y + b.Height * f[2 * i + 1]);
            return p;
        }

        public static void DrawGlyph(Graphics g, Glyph glyph, RectangleF b, Color c)
        {
            if (glyph == Glyph.None) return;
            SmoothingMode old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float x = b.X, y = b.Y, w = b.Width, h = b.Height;
            using (var brush = new SolidBrush(c))
            using (var pen = new Pen(c, Math.Max(1.4f, w * 0.12f)))
            {
                pen.StartCap = pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;
                switch (glyph)
                {
                    case Glyph.Play:
                        g.FillPolygon(brush, Pts(b, 0.22f, 0.1f, 0.9f, 0.5f, 0.22f, 0.9f));
                        break;
                    case Glyph.Pause:
                        g.FillRectangle(brush, x + w * 0.2f, y + h * 0.12f, w * 0.22f, h * 0.76f);
                        g.FillRectangle(brush, x + w * 0.58f, y + h * 0.12f, w * 0.22f, h * 0.76f);
                        break;
                    case Glyph.Stop:
                        Fill(g, c, new RectangleF(x + w * 0.14f, y + h * 0.14f, w * 0.72f, h * 0.72f), w * 0.1f);
                        break;
                    case Glyph.Prev:
                        g.FillRectangle(brush, x + w * 0.12f, y + h * 0.14f, w * 0.15f, h * 0.72f);
                        g.FillPolygon(brush, Pts(b, 0.88f, 0.14f, 0.33f, 0.5f, 0.88f, 0.86f));
                        break;
                    case Glyph.Next:
                        g.FillPolygon(brush, Pts(b, 0.12f, 0.14f, 0.67f, 0.5f, 0.12f, 0.86f));
                        g.FillRectangle(brush, x + w * 0.73f, y + h * 0.14f, w * 0.15f, h * 0.72f);
                        break;
                    case Glyph.Latest:
                        g.FillPolygon(brush, Pts(b, 0.04f, 0.16f, 0.42f, 0.5f, 0.04f, 0.84f));
                        g.FillPolygon(brush, Pts(b, 0.4f, 0.16f, 0.78f, 0.5f, 0.4f, 0.84f));
                        g.FillRectangle(brush, x + w * 0.8f, y + h * 0.16f, w * 0.14f, h * 0.68f);
                        break;
                    case Glyph.Refresh:
                    {
                        // clockwise arrow, open at the top right
                        float r = w * 0.36f, cx = x + w / 2, cy = y + h / 2, a = w * 0.2f;
                        g.DrawArc(pen, cx - r, cy - r, 2 * r, 2 * r, -30, 290);
                        g.FillPolygon(brush, new[]
                        {
                            new PointF(cx + a * 1.1f, cy - r), new PointF(cx - a * 0.4f, cy - r - a), new PointF(cx - a * 0.4f, cy - r + a),
                        });
                        break;
                    }
                    case Glyph.Folder:
                        pen.Width = Math.Max(1.2f, w * 0.1f);
                        g.DrawPolygon(pen, Pts(b, 0.08f, 0.2f, 0.4f, 0.2f, 0.5f, 0.32f, 0.92f, 0.32f, 0.92f, 0.82f, 0.08f, 0.82f));
                        break;
                    case Glyph.Dots:
                        for (int i = 0; i < 3; i++)
                            g.FillEllipse(brush, x + w * (0.14f + 0.31f * i), y + h * 0.42f, w * 0.16f, h * 0.16f);
                        break;
                }
            }
            g.SmoothingMode = old;
        }

        // ─── window chrome ───────────────────────────────────────

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        static extern int SetWindowTheme(IntPtr hwnd, string appName, string idList);

        [DllImport("user32.dll")]
        static extern bool FlashWindowEx(ref FLASHWINFO info);

        [StructLayout(LayoutKind.Sequential)]
        struct FLASHWINFO { public uint cbSize; public IntPtr hwnd; public uint dwFlags, uCount, dwTimeout; }

        /// <summary>Dark title bar on Windows 10 2004+ / 11; ignored elsewhere.</summary>
        public static void DarkTitleBar(IntPtr hwnd)
        {
            try
            {
                int on = 1;
                if (DwmSetWindowAttribute(hwnd, 20, ref on, 4) != 0) DwmSetWindowAttribute(hwnd, 19, ref on, 4); // 19 = older Windows 10 builds
                int caption = Bg.R | (Bg.G << 8) | (Bg.B << 16);
                DwmSetWindowAttribute(hwnd, 35, ref caption, 4); // Windows 11 caption colour
            }
            catch { }
        }

        public static void DarkScrollbars(Control c)
        {
            try { SetWindowTheme(c.Handle, "DarkMode_Explorer", null); } catch { }
        }

        /// <summary>Flashes the taskbar button until the window is activated (only when it is in the background).</summary>
        public static void Flash(Form form)
        {
            if (Form.ActiveForm == form || !form.IsHandleCreated) return;
            try
            {
                var info = new FLASHWINFO { hwnd = form.Handle, dwFlags = 3 | 12 }; // FLASHW_ALL | FLASHW_TIMERNOFG
                info.cbSize = (uint)Marshal.SizeOf(info);
                FlashWindowEx(ref info);
            }
            catch { }
        }

        /// <summary>App icon drawn in code: rounded blue square with a "resume" arrow around a play triangle.</summary>
        public static Icon AppIcon(int size)
        {
            using (var bmp = new Bitmap(size, size))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    float s = size;
                    var r = new RectangleF(s * 0.03f, s * 0.03f, s * 0.94f, s * 0.94f);
                    using (GraphicsPath path = RoundRect(r, s * 0.22f))
                    using (var brush = new LinearGradientBrush(r, Color.FromArgb(96, 140, 210), Color.FromArgb(52, 88, 150), 90f))
                        g.FillPath(brush, path);
                    float c = s / 2, rad = s * 0.29f, a = s * 0.12f;
                    using (var pen = new Pen(Color.White, Math.Max(1.5f, s * 0.085f)) { StartCap = LineCap.Round })
                        g.DrawArc(pen, c - rad, c - rad, rad * 2, rad * 2, -30, 280);
                    g.FillPolygon(Brushes.White, new[]
                    {
                        new PointF(c + a * 1.2f, c - rad), new PointF(c - a * 0.5f, c - rad - a), new PointF(c - a * 0.5f, c - rad + a),
                    });
                    g.FillPolygon(Brushes.White, new[]
                    {
                        new PointF(c - s * 0.08f, c - s * 0.12f), new PointF(c + s * 0.13f, c), new PointF(c - s * 0.08f, c + s * 0.12f),
                    });
                }
                return Icon.FromHandle(bmp.GetHicon());
            }
        }

        public static ToolTip MakeToolTip()
        {
            var tip = new ToolTip { OwnerDraw = true, InitialDelay = 400, AutoPopDelay = 20000, BackColor = Raised, ForeColor = Text };
            tip.Draw += (s, e) =>
            {
                e.Graphics.Clear(Raised);
                using (var pen = new Pen(BorderHover)) e.Graphics.DrawRectangle(pen, 0, 0, e.Bounds.Width - 1, e.Bounds.Height - 1);
                TextRenderer.DrawText(e.Graphics, e.ToolTipText, e.Font, e.Bounds, Text,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            };
            return tip;
        }

        public static ContextMenuStrip Menu()
        {
            return new ContextMenuStrip
            {
                Renderer = new ToolStripProfessionalRenderer(new DarkMenuColors()) { RoundedEdges = false },
                ShowImageMargin = false, BackColor = Raised, ForeColor = Text, Font = Base,
            };
        }

        class DarkMenuColors : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground { get { return Raised; } }
            public override Color ImageMarginGradientBegin { get { return Raised; } }
            public override Color ImageMarginGradientMiddle { get { return Raised; } }
            public override Color ImageMarginGradientEnd { get { return Raised; } }
            public override Color MenuBorder { get { return BorderHover; } }
            public override Color MenuItemBorder { get { return Accent; } }
            public override Color MenuItemSelected { get { return Accent; } }
            public override Color MenuItemSelectedGradientBegin { get { return Accent; } }
            public override Color MenuItemSelectedGradientEnd { get { return Accent; } }
            public override Color SeparatorDark { get { return Border; } }
            public override Color SeparatorLight { get { return Raised; } }
        }

        /// <summary>Dark colours for a dialog built from standard controls (Telegram settings).</summary>
        public static void ApplyDialog(Control root)
        {
            root.BackColor = Surface;
            root.ForeColor = Text;
            foreach (Control c in root.Controls) StyleStandard(c);
        }

        static void StyleStandard(Control c)
        {
            var button = c as Button;
            var check = c as CheckBox;
            if (c is TextBoxBase || c is ListBox)
            {
                c.BackColor = Input;
                c.ForeColor = Text;
                var box = c as TextBox;
                if (box != null) box.BorderStyle = BorderStyle.FixedSingle;
                var list = c as ListBox;
                if (list != null) list.BorderStyle = BorderStyle.FixedSingle;
            }
            else if (button != null)
            {
                button.FlatStyle = FlatStyle.Flat;
                button.UseVisualStyleBackColor = false;
                button.BackColor = Raised;
                button.ForeColor = Text;
                button.FlatAppearance.BorderColor = Border;
                button.FlatAppearance.MouseOverBackColor = RaisedHover;
                button.FlatAppearance.MouseDownBackColor = Border;
            }
            else if (check != null)
            {
                check.FlatStyle = FlatStyle.Flat;
                check.FlatAppearance.BorderColor = BorderHover;
                check.ForeColor = Text;
            }
            else if (c is Label)
                c.ForeColor = c.ForeColor == SystemColors.GrayText ? TextDim : Text;
            else if (c is GroupBox)
                c.ForeColor = TextDim;
            foreach (Control child in c.Controls) StyleStandard(child);
        }
    }

    enum Glyph { None, Play, Pause, Stop, Prev, Next, Latest, Refresh, Folder, Dots }

    enum LogKind { Event, Frame, Section, Success, Warning, Error }

    struct LogLine
    {
        public string Time, Text;
        public LogKind Kind;
    }

    enum TaskbarState { None = 0, Indeterminate = 1, Normal = 2, Error = 4, Paused = 8 }

    /// <summary>Progress on the taskbar button (ITaskbarList3, Windows 7+).</summary>
    static class Taskbar
    {
        [ComImport, Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface ITaskbarList3
        {
            void HrInit();
            void AddTab(IntPtr hwnd);
            void DeleteTab(IntPtr hwnd);
            void ActivateTab(IntPtr hwnd);
            void SetActiveAlt(IntPtr hwnd);
            void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
            void SetProgressValue(IntPtr hwnd, ulong completed, ulong total);
            void SetProgressState(IntPtr hwnd, int state);
        }

        [ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090"), ClassInterface(ClassInterfaceType.None)]
        class TaskbarList { }

        static ITaskbarList3 list;
        static bool unavailable;

        public static void Set(IntPtr hwnd, TaskbarState state, int value, int total)
        {
            if (unavailable) return;
            try
            {
                if (list == null)
                {
                    list = (ITaskbarList3)new TaskbarList();
                    list.HrInit();
                }
                list.SetProgressState(hwnd, (int)state);
                if (state == TaskbarState.Normal || state == TaskbarState.Error || state == TaskbarState.Paused)
                    list.SetProgressValue(hwnd, (ulong)Math.Max(0, value), (ulong)Math.Max(1, total));
            }
            catch { unavailable = true; }
        }
    }

    // ─── controls ────────────────────────────────────────────

    /// <summary>Rounded panel; children inherit its colour.</summary>
    class Card : Panel
    {
        public int Radius = Theme.S(6);

        public Card()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
            BackColor = Theme.Surface;
            ForeColor = Theme.Text;
            Padding = Theme.P(16, 14, 16, 14);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(Theme.BackOf(this));
            Theme.Fill(e.Graphics, BackColor, new RectangleF(0, 0, Width, Height), Radius);
        }
    }

    /// <summary>SplitContainer that never takes focus (no hatched focus rectangle on the splitter).</summary>
    class Split : SplitContainer
    {
        public Split()
        {
            SetStyle(ControlStyles.Selectable, false);
            TabStop = false;
            SplitterWidth = Theme.S(12);
            BackColor = Theme.Bg;
        }
    }

    enum ButtonKind { Normal, Primary, Danger, Ghost }

    class UiButton : Button
    {
        public ButtonKind Kind;
        public int ButtonHeight = Theme.S(28), MinWidth;
        Glyph glyph;
        bool hover, down;

        public UiButton(string text, Glyph glyph, ButtonKind kind)
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            this.glyph = glyph;
            Kind = kind;
            Text = text;
            Font = Theme.Base;
            ForeColor = Theme.Text;
            FlatStyle = FlatStyle.Flat;
            UseVisualStyleBackColor = false;
            Cursor = Cursors.Hand;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Margin = Theme.P(0, 0, 6, 0);
        }

        public Glyph Glyph
        {
            get { return glyph; }
            set { glyph = value; Invalidate(); }
        }

        public override Size GetPreferredSize(Size proposedSize)
        {
            if (Text.Length == 0) return new Size(ButtonHeight, ButtonHeight);
            int w = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPadding).Width + Theme.S(Kind == ButtonKind.Ghost ? 18 : 28);
            if (glyph != Glyph.None) w += Theme.S(12 + 7);
            return new Size(Math.Max(w, MinWidth), ButtonHeight);
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) { down = true; Invalidate(); }
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            down = false;
            Invalidate();
            base.OnMouseUp(e);
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            hover = down = false;
            Invalidate();
            base.OnEnabledChanged(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Color back = Theme.BackOf(this);
            g.Clear(back);
            bool filled = true;
            Color fill, fore = Theme.Text;
            switch (Kind)
            {
                case ButtonKind.Primary:
                    fill = down ? Theme.Mix(Theme.Accent, Color.Black, 0.15f) : hover ? Theme.AccentHover : Theme.Accent;
                    fore = Color.White;
                    break;
                case ButtonKind.Ghost:
                    fill = down ? Theme.RaisedHover : Theme.Raised;
                    filled = hover || down;
                    break;
                default:
                    fill = down ? Theme.Border : hover ? Theme.RaisedHover : Theme.Raised;
                    break;
            }
            Color icon = Kind == ButtonKind.Danger ? Theme.Red : fore;
            if (!Enabled)
            {
                fill = Theme.Mix(Theme.Raised, back, 0.55f);
                filled = Kind != ButtonKind.Ghost;
                fore = icon = Theme.TextFaint;
            }

            var r = new RectangleF(0, 0, Width, Height);
            float radius = Theme.S(4);
            if (filled) Theme.Fill(g, fill, r, radius);
            if (Focused && ShowFocusCues) Theme.Stroke(g, Theme.AccentText, r, radius);

            int gs = Theme.S(12), gap = Theme.S(7);
            Size ts = Text.Length > 0 ? TextRenderer.MeasureText(g, Text, Font, Size.Empty, TextFormatFlags.NoPadding) : Size.Empty;
            int total = ts.Width + (glyph == Glyph.None ? 0 : gs + (Text.Length > 0 ? gap : 0));
            int x = (Width - total) / 2;
            if (glyph != Glyph.None)
            {
                Theme.DrawGlyph(g, glyph, new RectangleF(x, (Height - gs) / 2f, gs, gs), icon);
                x += gs + gap;
            }
            if (Text.Length > 0)
                TextRenderer.DrawText(g, Text, Font, new Rectangle(x, 0, ts.Width + 2, Height), fore,
                    TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
    }

    /// <summary>Check box with an optional second line of dim description text.</summary>
    class Toggle : CheckBox
    {
        string description;
        bool hover;

        public Toggle(string text, string description)
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Text = text;
            this.description = description;
            Font = Theme.Base;
            ForeColor = Theme.Text;
            AutoSize = true;
            Cursor = Cursors.Hand;
            Margin = Theme.P(0, 4, 0, 4);
        }

        public string Description
        {
            get { return description; }
            set
            {
                if (value == description) return;
                description = value;
                if (Parent != null) Parent.PerformLayout();
                Invalidate();
            }
        }

        int BoxSize { get { return Theme.S(16); } }
        int Gap { get { return Theme.S(9); } }

        public override Size GetPreferredSize(Size proposedSize)
        {
            Size t = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPadding);
            int w = t.Width, h = Math.Max(BoxSize, t.Height);
            if (!string.IsNullOrEmpty(description))
            {
                Size d = TextRenderer.MeasureText(description, Theme.Small, Size.Empty, TextFormatFlags.NoPadding);
                w = Math.Max(w, d.Width);
                h = t.Height + Theme.S(2) + d.Height;
            }
            return new Size(BoxSize + Gap + w + Theme.S(4), h + Theme.S(2));
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnCheckedChanged(EventArgs e) { Invalidate(); base.OnCheckedChanged(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.BackOf(this));
            int lineH = TextRenderer.MeasureText(g, Text, Font, Size.Empty, TextFormatFlags.NoPadding).Height;
            int box = BoxSize;
            var r = new RectangleF(0, Math.Max(0, (lineH - box) / 2) + 1, box, box);
            float radius = Theme.S(3);
            if (Checked)
            {
                Theme.Fill(g, !Enabled ? Theme.Border : hover ? Theme.AccentHover : Theme.Accent, r, radius);
                SmoothingMode old = g.SmoothingMode;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var pen = new Pen(Enabled ? Color.White : Theme.TextDim, Math.Max(1.5f, Theme.S(2))))
                {
                    pen.StartCap = pen.EndCap = LineCap.Round;
                    pen.LineJoin = LineJoin.Round;
                    g.DrawLines(pen, new[]
                    {
                        new PointF(r.X + box * 0.25f, r.Y + box * 0.52f),
                        new PointF(r.X + box * 0.43f, r.Y + box * 0.7f),
                        new PointF(r.X + box * 0.76f, r.Y + box * 0.32f),
                    });
                }
                g.SmoothingMode = old;
            }
            else
            {
                Theme.Fill(g, Theme.Input, r, radius);
                Theme.Stroke(g, Enabled && hover ? Theme.TextFaint : Theme.BorderHover, r, radius);
            }
            if (Focused && ShowFocusCues)
                Theme.Stroke(g, Theme.AccentText, RectangleF.Inflate(r, Theme.S(2), Theme.S(2)), radius + Theme.S(2));

            int x = box + Gap;
            TextRenderer.DrawText(g, Text, Font, new Point(x, 0), Enabled ? Theme.Text : Theme.TextDim, TextFormatFlags.NoPadding);
            if (!string.IsNullOrEmpty(description))
                TextRenderer.DrawText(g, description, Theme.Small, new Point(x, lineH + Theme.S(2)),
                    Enabled ? Theme.TextDim : Theme.TextFaint, TextFormatFlags.NoPadding);
        }
    }

    /// <summary>Borderless TextBox in a rounded frame. Locked = read-only look while a render runs.</summary>
    class Field : Panel
    {
        public readonly TextBox Box;
        string suffix = "";
        bool locked;

        public Field(string text)
        {
            // the box first: setting Height below already runs OnLayout
            Box = new TextBox { BorderStyle = BorderStyle.None, BackColor = Theme.Input, ForeColor = Theme.Text, Font = Theme.Base, Text = text ?? "" };
            DoubleBuffered = true;
            ResizeRedraw = true;
            BackColor = Theme.Input;
            Cursor = Cursors.IBeam;
            Height = Theme.S(28);
            Margin = Theme.P(0, 3, 0, 3);
            Box.GotFocus += delegate { Invalidate(); };
            Box.LostFocus += delegate { Invalidate(); };
            Controls.Add(Box);
        }

        public string Suffix
        {
            get { return suffix; }
            set { suffix = value ?? ""; PerformLayout(); Invalidate(); }
        }

        public bool Locked
        {
            get { return locked; }
            set
            {
                locked = value;
                Box.ReadOnly = value;
                BackColor = Box.BackColor = value ? Theme.Surface : Theme.Input;
                Box.ForeColor = value ? Theme.TextDim : Theme.Text;
                Invalidate();
            }
        }

        int SuffixWidth
        {
            get { return suffix.Length == 0 ? 0 : TextRenderer.MeasureText(suffix, Theme.Small, Size.Empty, TextFormatFlags.NoPadding).Width + Theme.S(6); }
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int pad = Theme.S(8), h = Box.PreferredHeight;
            Box.SetBounds(pad, Math.Max(0, (Height - h) / 2), Math.Max(Theme.S(10), Width - 2 * pad - SuffixWidth), h);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.BackOf(this));
            var r = new RectangleF(0, 0, Width, Height);
            float radius = Theme.S(4);
            Theme.Fill(g, BackColor, r, radius);
            Theme.Stroke(g, !locked && Box.Focused ? Theme.Accent : Theme.Border, r, radius);
            if (suffix.Length > 0)
                TextRenderer.DrawText(g, suffix, Theme.Small, new Rectangle(Width - Theme.S(8) - SuffixWidth + Theme.S(6), 0, SuffixWidth, Height),
                    Theme.TextFaint, TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Box.Focus();
        }
    }

    /// <summary>Whole-number Field: digits only, Up/Down keys and the mouse wheel step the value.</summary>
    class IntField : Field
    {
        readonly int min, max;

        public IntField(int min, int max, int value, string suffix) : base("")
        {
            this.min = min;
            this.max = max;
            Suffix = suffix;
            Box.MaxLength = max.ToString().Length;
            Value = value;
            Box.KeyPress += (s, e) => { if (!char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar)) e.Handled = true; };
            Box.KeyDown += (s, e) =>
            {
                if (Locked) return;
                int step = e.KeyCode == Keys.Up ? 1 : e.KeyCode == Keys.Down ? -1 : e.KeyCode == Keys.PageUp ? 10 : e.KeyCode == Keys.PageDown ? -10 : 0;
                if (step == 0) return;
                Value += step;
                Box.SelectAll();
                e.Handled = e.SuppressKeyPress = true;
            };
            Box.MouseWheel += (s, e) => { if (!Locked && Box.Focused) Value += Math.Sign(e.Delta); };
            Box.Leave += delegate { Value = Value; }; // normalise empty / out-of-range text
        }

        public int Value
        {
            get
            {
                int v;
                if (!int.TryParse(Box.Text, out v)) v = min;
                return Math.Max(min, Math.Min(max, v));
            }
            set
            {
                string s = Math.Max(min, Math.Min(max, value)).ToString();
                if (Box.Text != s) Box.Text = s;
            }
        }
    }

    class ProgressStrip : Control
    {
        int value, maximum = 1;
        Color color = Theme.Accent;

        public ProgressStrip()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Height = Theme.S(6);
        }

        public void Set(int value, int maximum, Color color)
        {
            maximum = Math.Max(1, maximum);
            value = Math.Max(0, Math.Min(maximum, value));
            if (value == this.value && maximum == this.maximum && color == this.color) return;
            this.value = value;
            this.maximum = maximum;
            this.color = color;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.BackOf(this));
            float radius = Height / 2f;
            Theme.Fill(g, Theme.Tile, new RectangleF(0, 0, Width, Height), radius);
            if (value > 0)
                Theme.Fill(g, color, new RectangleF(0, 0, Math.Max(Height, (float)Width * value / maximum), Height), radius);
        }
    }

    /// <summary>Caption / big value / dim footnote.</summary>
    class StatTile : Control
    {
        readonly string caption;
        string value = "—", sub = "";
        Color valueColor = Theme.Text;

        public StatTile(string caption)
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            this.caption = caption.ToUpperInvariant();
            Height = PreferredHeight;
        }

        public static int PreferredHeight
        {
            get { return Theme.Caption.Height + Theme.Value.Height + Theme.Small.Height + Theme.S(18); }
        }

        public void Set(string value, string sub, Color color)
        {
            if (value == this.value && sub == this.sub && color == valueColor) return;
            this.value = value;
            this.sub = sub;
            valueColor = color;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.BackOf(this));
            Theme.Fill(g, Theme.Tile, new RectangleF(0, 0, Width, Height), Theme.S(5));
            int x = Theme.S(12), w = Width - 2 * x, y = Theme.S(8);
            const TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine;
            TextRenderer.DrawText(g, caption, Theme.Caption, new Rectangle(x, y, w, Theme.Caption.Height), Theme.TextFaint, flags);
            y += Theme.Caption.Height + Theme.S(2);
            TextRenderer.DrawText(g, value, Theme.Value, new Rectangle(x, y, w, Theme.Value.Height), valueColor, flags);
            y += Theme.Value.Height;
            TextRenderer.DrawText(g, sub, Theme.Small, new Rectangle(x, y, w, Theme.Small.Height), Theme.TextDim, flags);
        }
    }

    /// <summary>Small coloured pill ("РЕНДЕР", "ГОТОВО"...); hidden while its text is empty.</summary>
    class Badge : Control
    {
        Color color = Theme.TextDim;

        public Badge()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Font = Theme.Caption;
            Visible = false;
        }

        public void Set(string text, Color color)
        {
            bool show = text.Length > 0;
            if (Visible != show) Visible = show;
            if (Text == text && this.color == color) return;
            Text = text;
            this.color = color;
            Size = GetPreferredSize(Size.Empty);
            Invalidate();
        }

        public override Size GetPreferredSize(Size proposedSize)
        {
            Size t = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPadding);
            return new Size(t.Width + Theme.S(18), t.Height + Theme.S(9));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Color back = Theme.BackOf(this);
            g.Clear(back);
            var r = new RectangleF(0, 0, Width, Height);
            Theme.Fill(g, Theme.Mix(back, color, 0.2f), r, Height / 2f);
            TextRenderer.DrawText(g, Text, Font, new Rectangle(0, 0, Width, Height), color,
                TextFormatFlags.NoPadding | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
    }

    /// <summary>Timeline slider for the frame preview (0..Maximum), mouse and arrow keys.</summary>
    class Scrubber : Control
    {
        int maximum, pos;
        bool dragging, hover;
        public event EventHandler ValueChanged;

        public Scrubber()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true;
            Height = Theme.S(24);
            Cursor = Cursors.Hand;
        }

        public int Maximum
        {
            get { return maximum; }
            set
            {
                maximum = Math.Max(0, value);
                if (pos > maximum) Value = maximum;
                Invalidate();
            }
        }

        public int Value
        {
            get { return pos; }
            set
            {
                int v = Math.Max(0, Math.Min(maximum, value));
                if (v == pos) return;
                pos = v;
                Invalidate();
                if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
            }
        }

        int Pad { get { return Theme.S(9); } }

        float XFor(int v)
        {
            return Pad + (maximum > 0 ? (float)(Width - 2 * Pad) * v / maximum : 0);
        }

        int ValueAt(int x)
        {
            if (maximum == 0) return 0;
            return (int)Math.Round((double)(x - Pad) * maximum / Math.Max(1, Width - 2 * Pad));
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (!Enabled || e.Button != MouseButtons.Left) return;
            Focus();
            dragging = true;
            Capture = true;
            Value = ValueAt(e.X);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragging) Value = ValueAt(e.X);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            dragging = false;
            Capture = false;
            Invalidate();
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override bool IsInputKey(Keys keyData)
        {
            switch (keyData)
            {
                case Keys.Left: case Keys.Right: case Keys.Up: case Keys.Down: case Keys.Home: case Keys.End: return true;
            }
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            switch (e.KeyCode)
            {
                case Keys.Left: case Keys.Down: Value = pos - 1; e.Handled = true; break;
                case Keys.Right: case Keys.Up: Value = pos + 1; e.Handled = true; break;
                case Keys.PageDown: Value = pos - 10; e.Handled = true; break;
                case Keys.PageUp: Value = pos + 10; e.Handled = true; break;
                case Keys.Home: Value = 0; e.Handled = true; break;
                case Keys.End: Value = maximum; e.Handled = true; break;
            }
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.BackOf(this));
            float th = Theme.S(4), cy = Height / 2f, x = XFor(pos);
            Theme.Fill(g, Theme.Tile, new RectangleF(Pad, cy - th / 2, Width - 2 * Pad, th), th / 2);
            if (!Enabled) return;
            Theme.Fill(g, Theme.Accent, new RectangleF(Pad, cy - th / 2, x - Pad, th), th / 2);
            float r = Theme.S(hover || dragging ? 7 : 6);
            SmoothingMode old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (Focused)
                using (var ring = new SolidBrush(Theme.Mix(Theme.BackOf(this), Theme.Accent, 0.45f)))
                    g.FillEllipse(ring, x - r - Theme.S(3), cy - r - Theme.S(3), 2 * r + Theme.S(6), 2 * r + Theme.S(6));
            using (var thumb = new SolidBrush(Theme.Text)) g.FillEllipse(thumb, x - r, cy - r, 2 * r, 2 * r);
            g.SmoothingMode = old;
        }
    }

    /// <summary>Read-only coloured log. Follows new lines only while scrolled to the bottom.</summary>
    class LogView : RichTextBox
    {
        const int WM_SETREDRAW = 0x000B, WM_VSCROLL = 0x0115, SB_BOTTOM = 7;
        const int EM_GETSCROLLPOS = 0x0400 + 221, EM_SETSCROLLPOS = 0x0400 + 222;
        const int MaxChars = 400000, KeepChars = 200000;

        [StructLayout(LayoutKind.Sequential)]
        struct SCROLLINFO { public int cbSize, fMask, nMin, nMax, nPage, nPos, nTrackPos; }

        [StructLayout(LayoutKind.Sequential)]
        struct POINT { public int X, Y; }

        [DllImport("user32.dll")] static extern bool GetScrollInfo(IntPtr hwnd, int bar, ref SCROLLINFO info);
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, ref POINT lParam);

        public LogView()
        {
            ReadOnly = true;
            BorderStyle = BorderStyle.None;
            BackColor = Theme.Input;
            ForeColor = Theme.Text;
            Font = Theme.Mono;
            DetectUrls = false;
            WordWrap = true;
            ScrollBars = RichTextBoxScrollBars.Vertical;
            var menu = Theme.Menu();
            menu.Items.Add("Копировать", null, delegate { Copy(); });
            menu.Items.Add("Выделить всё", null, delegate { SelectAll(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Очистить", null, delegate { Clear(); });
            menu.Opening += delegate { menu.Items[0].Enabled = SelectionLength > 0; };
            ContextMenuStrip = menu;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.DarkScrollbars(this);
        }

        bool AtBottom()
        {
            var info = new SCROLLINFO { fMask = 0x17 }; // SIF_ALL
            info.cbSize = Marshal.SizeOf(info);
            if (!GetScrollInfo(Handle, 1, ref info)) return true; // no scroll bar yet
            return info.nPos + info.nPage + Font.Height >= info.nMax;
        }

        public void Append(List<LogLine> lines)
        {
            if (lines.Count == 0 || !IsHandleCreated) return;
            bool follow = AtBottom();
            var scroll = new POINT();
            SendMessage(Handle, EM_GETSCROLLPOS, IntPtr.Zero, ref scroll);
            int selStart = SelectionStart, selLength = SelectionLength;
            SendMessage(Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
            try
            {
                if (TextLength > MaxChars)
                {
                    int cut = GetFirstCharIndexFromLine(GetLineFromCharIndex(TextLength - KeepChars) + 1);
                    if (cut > 0)
                    {
                        Select(0, cut);
                        SelectedText = "";
                        selStart = selLength = 0;
                        follow = true;
                    }
                }
                foreach (LogLine line in lines)
                {
                    Select(TextLength, 0);
                    if (line.Time.Length > 0)
                    {
                        SelectionColor = Theme.TextFaint;
                        SelectedText = line.Time + "  ";
                    }
                    SelectionColor = Theme.ColorOf(line.Kind);
                    SelectedText = line.Text + "\n";
                }
                Select(selStart, selLength);
            }
            finally
            {
                SendMessage(Handle, WM_SETREDRAW, (IntPtr)1, IntPtr.Zero);
            }
            if (follow) SendMessage(Handle, WM_VSCROLL, (IntPtr)SB_BOTTOM, IntPtr.Zero);
            else SendMessage(Handle, EM_SETSCROLLPOS, IntPtr.Zero, ref scroll);
            Invalidate();
        }
    }
}
