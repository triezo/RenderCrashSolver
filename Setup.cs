// Setup.cs - RenderSolver_Setup_<ver>.exe. package.bat builds it with RenderSolver.exe and README.txt embedded as resources;
// it installs them for the current user only, so no administrator rights or UAC prompt are needed (see Installer.cs).

using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

[assembly: AssemblyTitle(RenderSolver.AppInfo.Name + " Setup")]
[assembly: AssemblyProduct(RenderSolver.AppInfo.Name)]
[assembly: AssemblyDescription("Installer of RenderSolver, render queue for Blender")]
[assembly: AssemblyCompany(RenderSolver.AppInfo.Publisher)]
[assembly: AssemblyCopyright("© " + RenderSolver.AppInfo.Publisher)]
[assembly: AssemblyVersion(RenderSolver.AppInfo.Version + ".0.0")]
[assembly: AssemblyFileVersion(RenderSolver.AppInfo.Version + ".0.0")]
[assembly: AssemblyInformationalVersion(RenderSolver.AppInfo.Version)]

namespace RenderSolver
{
    static class SetupProgram
    {
        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        [STAThread]
        static void Main()
        {
            try { SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new SetupForm());
        }
    }

    class SetupForm : SetupWindow
    {
        readonly byte[] exe = Resource(Installer.ExeName), readme = Resource("README.txt");
        Field txtDir;
        Toggle chkDesktop;

        public SetupForm() : base("Установка " + AppInfo.Name + " " + AppInfo.Version)
        {
            if (exe == null)
            {
                SetHeader(AppInfo.Name + " " + AppInfo.Version, "Установщик собран без программы");
                AddText("В этот установщик не встроен RenderSolver.exe. Соберите его через package.bat.", Theme.Red, Theme.Base);
                AddButton("Закрыть", ButtonKind.Primary).Click += delegate { Close(); };
                FitHeight();
                return;
            }
            OptionsPage();
        }

        static byte[] Resource(string name)
        {
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            {
                if (s == null) return null;
                var ms = new MemoryStream();
                s.CopyTo(ms);
                return ms.ToArray();
            }
        }

        void OptionsPage()
        {
            string version;
            string installed = Installer.InstalledDir(out version);
            bool update = installed != null, same = update && version == AppInfo.Version;
            SetHeader(AppInfo.Name + " " + AppInfo.Version,
                same ? "Эта версия уже установлена — можно установить её заново"
                : update ? string.Format("Обновление: сейчас установлена версия {0}", version ?? "?")
                : "Очередь рендера для Blender с перезапуском после крашей");

            AddText("Папка программы", Theme.TextDim, Theme.Caption).Margin = Theme.P(0, 0, 0, 4);
            var row = new Panel { Width = InnerWidth, Height = Theme.S(28), Margin = Theme.P(0, 0, 0, 4), BackColor = Theme.Surface };
            var btnBrowse = new UiButton("Обзор…", Glyph.Folder, ButtonKind.Normal) { Margin = Padding.Empty };
            txtDir = new Field(installed ?? Installer.DefaultDir);
            row.Controls.Add(txtDir);
            row.Controls.Add(btnBrowse);
            Size b = btnBrowse.GetPreferredSize(Size.Empty);
            btnBrowse.AutoSize = false;
            btnBrowse.SetBounds(row.Width - b.Width, 0, b.Width, b.Height);
            txtDir.SetBounds(0, 0, row.Width - b.Width - Theme.S(8), Theme.S(28));
            btnBrowse.Click += delegate { Browse(); };
            Body.Controls.Add(row);
            AddText("Права администратора не нужны: программа ставится только для вас. Очередь и настройки хранятся в этой же папке.",
                Theme.TextFaint, Theme.Small);

            chkDesktop = new Toggle("Ярлык на рабочем столе", "Ярлык в меню «Пуск» создаётся всегда") { Margin = Theme.P(0, 10, 0, 4) };
            chkDesktop.Checked = !update || File.Exists(Installer.DesktopLink);
            Body.Controls.Add(chkDesktop);

            UiButton btnInstall = AddButton(same ? "Переустановить" : update ? "Обновить" : "Установить", ButtonKind.Primary);
            UiButton btnCancel = AddButton("Отмена", ButtonKind.Normal);
            btnInstall.Click += delegate { DoInstall(); };
            btnCancel.Click += delegate { Close(); };
            AcceptButton = btnInstall;
            CancelButton = btnCancel;
            FitHeight();
            ActiveControl = btnInstall;
        }

        void Browse()
        {
            using (var dlg = new FolderBrowserDialog { Description = "Куда установить RenderSolver (внутри будет создана папка RenderSolver)", ShowNewFolderButton = true })
            {
                string current = txtDir.Box.Text.Trim();
                try { if (Directory.Exists(current)) dlg.SelectedPath = current; } catch { }
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                string dir = dlg.SelectedPath;
                // like other installers: a picked parent folder gets its own RenderSolver subfolder
                if (!string.Equals(Path.GetFileName(dir.TrimEnd('\\')), AppInfo.Name, StringComparison.OrdinalIgnoreCase)
                    && !File.Exists(Path.Combine(dir, Installer.ExeName)))
                    dir = Path.Combine(dir, AppInfo.Name);
                txtDir.Box.Text = dir;
            }
        }

        void DoInstall()
        {
            string dir = txtDir.Box.Text.Trim().Trim('"');
            bool rooted = false;
            try
            {
                rooted = Path.IsPathRooted(dir) && (dir.StartsWith(@"\\") || (dir.Length > 2 && dir[1] == ':'));
                if (rooted) dir = Path.GetFullPath(dir).TrimEnd('\\');
                rooted = rooted && dir.Length > 3; // not a bare drive like D:\
            }
            catch { rooted = false; }
            if (!rooted)
            {
                MessageBox.Show(this, "Укажите полный путь к папке, например:\n" + Installer.DefaultDir, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            string exePath = Path.Combine(dir, Installer.ExeName);
            while (Installer.InUse(exePath))
            {
                if (MessageBox.Show(this, "RenderSolver сейчас открыт. Закройте его и нажмите «Повтор».\n\n" +
                        "Если идёт рендер, он остановится; готовые кадры останутся, и после обновления «Старт» продолжит с того же места.",
                        Text, MessageBoxButtons.RetryCancel, MessageBoxIcon.Warning) != DialogResult.Retry) return;
            }

            Cursor = Cursors.WaitCursor;
            try
            {
                exePath = Installer.Install(dir, exe, readme, chkDesktop.Checked);
            }
            catch (UnauthorizedAccessException)
            {
                Cursor = Cursors.Default;
                MessageBox.Show(this, "Нет прав на запись в папку:\n" + dir + "\n\nВыберите папку в своём профиле, например:\n" + Installer.DefaultDir,
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            catch (Exception ex)
            {
                Cursor = Cursors.Default;
                MessageBox.Show(this, "Не удалось установить RenderSolver:\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            Cursor = Cursors.Default;
            DonePage(exePath);
        }

        void DonePage(string exePath)
        {
            ClearPage();
            SetHeader(AppInfo.Name + " " + AppInfo.Version + " установлен", Path.GetDirectoryName(exePath));
            AddText("RenderSolver есть в меню «Пуск»" + (chkDesktop.Checked ? " и на рабочем столе." : "."), Theme.Text, Theme.Base);
            AddText("Удалить его можно в «Параметры → Приложения → Установленные приложения».", Theme.TextFaint, Theme.Small);
            var chkRun = new Toggle("Запустить RenderSolver", null) { Checked = true, Margin = Theme.P(0, 10, 0, 4) };
            Body.Controls.Add(chkRun);
            UiButton btnDone = AddButton("Готово", ButtonKind.Primary);
            btnDone.Click += delegate
            {
                if (chkRun.Checked)
                {
                    try { Process.Start(new ProcessStartInfo(exePath) { WorkingDirectory = Path.GetDirectoryName(exePath), UseShellExecute = true }); }
                    catch (Exception ex) { MessageBox.Show(this, "Не удалось запустить RenderSolver:\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
                }
                Close();
            };
            AcceptButton = CancelButton = btnDone;
            FitHeight();
            ActiveControl = btnDone;
        }
    }
}
