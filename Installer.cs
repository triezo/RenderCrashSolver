// Installer.cs - per-user install (no admin rights) shared by RenderSolver_Setup.exe and "RenderSolver.exe /uninstall".
// The program goes to %LOCALAPPDATA%\Programs\RenderSolver with a Start menu shortcut, an optional desktop shortcut and an
// entry in Settings > Apps (HKCU). settings.ini and logs stay next to the exe, as in the portable zip.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace RenderSolver
{
    static class Installer
    {
        public const string ExeName = "RenderSolver.exe";
        public const string ShortcutDescription = "Очередь рендера для Blender";
        const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + AppInfo.Name;

        public static string DefaultDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppInfo.Name); }
        }

        public static string StartMenuLink
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppInfo.Name + ".lnk"); }
        }

        public static string DesktopLink
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppInfo.Name + ".lnk"); }
        }

        // ─── registry entry in Settings > Apps ───────────────────

        /// <summary>Folder and version of the current installation, or null.</summary>
        public static string InstalledDir(out string version)
        {
            version = null;
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(UninstallKey))
                {
                    if (k == null) return null;
                    string dir = k.GetValue("InstallLocation") as string;
                    version = k.GetValue("DisplayVersion") as string;
                    return !string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, ExeName)) ? dir : null;
                }
            }
            catch { return null; }
        }

        static void Register(string dir, long bytes)
        {
            string exe = Path.Combine(dir, ExeName);
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                k.SetValue("DisplayName", AppInfo.Name);
                k.SetValue("DisplayVersion", AppInfo.Version);
                k.SetValue("Publisher", AppInfo.Publisher);
                k.SetValue("DisplayIcon", exe + ",0");
                k.SetValue("InstallLocation", dir);
                k.SetValue("UninstallString", "\"" + exe + "\" /uninstall");
                k.SetValue("URLInfoAbout", AppInfo.Url);
                k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
                k.SetValue("EstimatedSize", (int)Math.Max(1, bytes / 1024), RegistryValueKind.DWord);
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
        }

        static bool SamePath(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            try
            {
                return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        // ─── shortcuts ───────────────────────────────────────────

        [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
        class ShellLink { }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
        interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int max, IntPtr findData, int flags);
            void GetIDList(out IntPtr idList);
            void SetIDList(IntPtr idList);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int max);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int max);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int max);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
            void GetHotkey(out short hotkey);
            void SetHotkey(short hotkey);
            void GetShowCmd(out int showCmd);
            void SetShowCmd(int showCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int max, out int index);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, int reserved);
            void Resolve(IntPtr hwnd, int flags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
        }

        static void CreateShortcut(string lnk, string target)
        {
            var link = (IShellLinkW)new ShellLink();
            try
            {
                link.SetPath(target);
                link.SetWorkingDirectory(Path.GetDirectoryName(target));
                link.SetDescription(ShortcutDescription);
                link.SetIconLocation(target, 0);
                Directory.CreateDirectory(Path.GetDirectoryName(lnk));
                ((System.Runtime.InteropServices.ComTypes.IPersistFile)link).Save(lnk, true);
            }
            finally { Marshal.ReleaseComObject(link); }
        }

        static string ShortcutTarget(string lnk)
        {
            if (!File.Exists(lnk)) return null;
            var link = (IShellLinkW)new ShellLink();
            try
            {
                ((System.Runtime.InteropServices.ComTypes.IPersistFile)link).Load(lnk, 0);
                var sb = new StringBuilder(1024);
                link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
                return sb.ToString();
            }
            catch { return null; }
            finally { Marshal.ReleaseComObject(link); }
        }

        /// <summary>Deletes the shortcut only if it starts this exe (a link the user made to another copy stays).</summary>
        static void DeleteShortcut(string lnk, string exe)
        {
            try { if (SamePath(ShortcutTarget(lnk), exe)) File.Delete(lnk); } catch { }
        }

        // ─── install ─────────────────────────────────────────────

        /// <summary>True while a running RenderSolver keeps the exe open (it cannot be replaced then).</summary>
        public static bool InUse(string file)
        {
            if (!File.Exists(file)) return false;
            try
            {
                using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) return false;
            }
            catch (IOException) { return true; }
            catch (UnauthorizedAccessException) { return false; } // read-only file: the copy below reports it
        }

        public static string Install(string dir, byte[] exe, byte[] readme, bool desktopShortcut)
        {
            Directory.CreateDirectory(dir);
            string exePath = Path.Combine(dir, ExeName), tmp = exePath + ".new";
            File.WriteAllBytes(tmp, exe);
            if (File.Exists(exePath)) File.Delete(exePath);
            File.Move(tmp, exePath);
            if (readme != null) File.WriteAllBytes(Path.Combine(dir, "README.txt"), readme);

            CreateShortcut(StartMenuLink, exePath);
            if (desktopShortcut) CreateShortcut(DesktopLink, exePath);
            else DeleteShortcut(DesktopLink, exePath);
            Register(dir, exe.Length + (readme != null ? readme.Length : 0));
            return exePath;
        }

        // ─── uninstall ("RenderSolver.exe /uninstall") ───────────

        public static void Uninstall()
        {
            string exe = Application.ExecutablePath, dir = Path.GetDirectoryName(exe);
            var form = new SetupWindow("Удаление " + AppInfo.Name);
            form.SetHeader("Удаление " + AppInfo.Name, "Версия " + AppInfo.Version + " · " + dir);
            form.AddText("Будут удалены программа, её ярлыки и запись в списке установленных приложений.", Theme.Text, Theme.Base);
            var chkData = new Toggle("Удалить также очередь сцен, настройки и журналы",
                "settings.ini и папка logs в папке программы. Отрендеренные кадры не удаляются.");
            chkData.Margin = Theme.P(0, 12, 0, 4);
            form.Body.Controls.Add(chkData);

            bool removed = false;
            UiButton btnRemove = form.AddButton("Удалить", ButtonKind.Primary), btnCancel = form.AddButton("Отмена", ButtonKind.Normal);
            btnCancel.Click += delegate { form.Close(); };
            btnRemove.Click += delegate
            {
                string busy = RunningCopy(exe);
                while (busy != null)
                {
                    if (MessageBox.Show(form, "RenderSolver ещё открыт. Закройте его окно (рендер при этом остановится) и нажмите «Повтор».",
                            form.Text, MessageBoxButtons.RetryCancel, MessageBoxIcon.Warning) != DialogResult.Retry) return;
                    busy = RunningCopy(exe);
                }
                try
                {
                    RemoveInstallation(exe, dir, chkData.Checked);
                    removed = true;
                }
                catch (Exception ex)
                {
                    MessageBox.Show(form, "Не удалось удалить RenderSolver:\n" + ex.Message, form.Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                form.ClearPage();
                form.SetHeader(AppInfo.Name + " удалён", dir);
                form.AddText(chkData.Checked
                    ? "Программа и её настройки удалены."
                    : "Программа удалена. Очередь и настройки остались в папке программы: если установить RenderSolver туда же, они вернутся.",
                    Theme.Text, Theme.Base);
                UiButton btnClose = form.AddButton("Закрыть", ButtonKind.Primary);
                btnClose.Click += delegate { form.Close(); };
                form.AcceptButton = form.CancelButton = btnClose;
                form.FitHeight();
                btnClose.Focus();
            };
            form.FitHeight();
            form.CancelButton = btnCancel;
            Application.Run(form);
            if (removed) DeleteAfterExit(exe, dir);
        }

        /// <summary>Path of another RenderSolver started from this exe, or null.</summary>
        static string RunningCopy(string exe)
        {
            int me = Process.GetCurrentProcess().Id;
            foreach (Process p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExeName)))
            {
                try
                {
                    if (p.Id != me && SamePath(p.MainModule.FileName, exe)) return exe;
                }
                catch { } // a process of another user / elevated: not ours to wait for
                finally { p.Dispose(); }
            }
            return null;
        }

        static void RemoveInstallation(string exe, string dir, bool removeData)
        {
            DeleteShortcut(StartMenuLink, exe);
            DeleteShortcut(DesktopLink, exe);

            string ignored;
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(UninstallKey))
            {
                string registered = k == null ? null : k.GetValue("InstallLocation") as string;
                if (k != null && (registered == null || SamePath(registered, dir) || InstalledDir(out ignored) == null))
                {
                    k.Close();
                    Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
                }
            }

            // only files the program made: the folder may have been chosen by hand and hold other things
            TryDelete(Path.Combine(dir, "README.txt"));
            TryDelete(Path.Combine(dir, ExeName + ".new"));
            if (removeData)
            {
                TryDelete(Path.Combine(dir, "settings.ini"));
                string logs = Path.Combine(dir, "logs");
                if (Directory.Exists(logs))
                {
                    foreach (string f in Directory.GetFiles(logs, "render_log_*.txt")) TryDelete(f);
                    try { Directory.Delete(logs); } catch { } // only if now empty
                }
            }

            // the copy of resume_render.py made for Blender
            string scriptDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppInfo.Name);
            TryDelete(Path.Combine(scriptDir, "resume_render.py"));
            try { Directory.Delete(scriptDir); } catch { }
        }

        static void TryDelete(string file)
        {
            try { if (File.Exists(file)) File.Delete(file); } catch { }
        }

        /// <summary>A running exe cannot delete itself: a hidden cmd waits for this process to end, then removes the exe and the folder if it is empty.</summary>
        static void DeleteAfterExit(string exe, string dir)
        {
            string cmd = string.Format("/c ping -n 3 127.0.0.1 >nul & del /f /q \"{0}\" & rd \"{1}\"", exe, dir);
            try
            {
                Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), cmd)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = Path.GetTempPath(), // not the folder being removed
                });
            }
            catch { }
        }
    }

    /// <summary>Dark window of the installer and the uninstaller: header with the app icon, a top-down body, buttons at the bottom right.</summary>
    class SetupWindow : Form
    {
        public readonly FlowLayoutPanel Body;
        readonly FlowLayoutPanel buttons;
        readonly Panel header;
        readonly Image icon = Theme.AppIconBitmap(Theme.S(48));
        string title = "", subtitle = "";

        public SetupWindow(string caption)
        {
            Text = caption;
            Font = Theme.Base;
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Theme.Surface;
            ForeColor = Theme.Text;
            Icon = Theme.AppIcon(Theme.S(32));
            ClientSize = new Size(Theme.S(540), Theme.S(300));

            // docking runs from the last added control: Body (Fill) goes first
            Body = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false,
                Padding = Theme.P(24, 14, 24, 8), BackColor = Theme.Surface,
            };
            buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, WrapContents = false,
                Height = Theme.S(60), Padding = Theme.P(24, 16, 18, 16), BackColor = Theme.Bg,
            };
            header = new Panel { Dock = DockStyle.Top, Height = Theme.S(84), BackColor = Theme.Bg };
            header.Paint += PaintHeader;
            Controls.Add(Body);
            Controls.Add(buttons);
            Controls.Add(header);
        }

        public int InnerWidth { get { return ClientSize.Width - Body.Padding.Horizontal; } }

        public void SetHeader(string title, string subtitle)
        {
            this.title = title;
            this.subtitle = subtitle;
            header.Invalidate();
        }

        void PaintHeader(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            int pad = Theme.S(24), size = Theme.S(48), y = (header.Height - size) / 2;
            g.DrawImage(icon, pad, y, size, size);
            int x = pad + size + Theme.S(16), w = header.Width - x - pad;
            Size t = TextRenderer.MeasureText(title, Theme.Title);
            Size s = TextRenderer.MeasureText(subtitle, Theme.Small);
            int top = (header.Height - t.Height - s.Height) / 2;
            TextRenderer.DrawText(g, title, Theme.Title, new Rectangle(x, top, w, t.Height), Theme.Text,
                TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, subtitle, Theme.Small, new Rectangle(x, top + t.Height, w, s.Height), Theme.TextDim,
                TextFormatFlags.NoPadding | TextFormatFlags.PathEllipsis);
            using (var pen = new Pen(Theme.Border)) g.DrawLine(pen, 0, header.Height - 1, header.Width, header.Height - 1);
        }

        public Label AddText(string text, Color color, Font font)
        {
            var label = new Label
            {
                Text = text, ForeColor = color, Font = font, AutoSize = true,
                MaximumSize = new Size(InnerWidth, 0), Margin = Theme.P(0, 2, 0, 6),
            };
            Body.Controls.Add(label);
            return label;
        }

        /// <summary>Buttons are placed right to left: the first one added is the rightmost.</summary>
        public UiButton AddButton(string text, ButtonKind kind)
        {
            var button = new UiButton(text, Glyph.None, kind) { MinWidth = Theme.S(96), Margin = Theme.P(6, 0, 0, 0) };
            buttons.Controls.Add(button);
            return button;
        }

        /// <summary>Removes the page's controls. Called from a button's Click, so that button is disposed only afterwards.</summary>
        public void ClearPage()
        {
            var old = new List<Control>();
            foreach (Control c in Body.Controls) old.Add(c);
            foreach (Control c in buttons.Controls) old.Add(c);
            Body.Controls.Clear();
            buttons.Controls.Clear();
            BeginInvoke((MethodInvoker)delegate { foreach (Control c in old) c.Dispose(); });
        }

        /// <summary>Window height to fit the body.</summary>
        public void FitHeight()
        {
            int body = Body.GetPreferredSize(new Size(ClientSize.Width, 0)).Height;
            ClientSize = new Size(ClientSize.Width, header.Height + Math.Max(Theme.S(90), body) + buttons.Height);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.DarkTitleBar(Handle);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) icon.Dispose();
            base.Dispose(disposing);
        }
    }
}
