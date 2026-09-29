// Telegram.cs - render notifications through a Telegram bot (Bot API over HTTPS, no extra libraries).

using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace RenderCrashSolver
{
    static class TelegramApi
    {
        const long MaxPhotoBytes = 10L * 1024 * 1024; // Bot API limit for sendPhoto

        static TelegramApi()
        {
            // .NET Framework defaults to TLS 1.0 on older setups; api.telegram.org requires TLS 1.2
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch { }
        }

        static string Api(string token, string method)
        {
            return "https://api.telegram.org/bot" + token.Trim() + "/" + method;
        }

        public static List<string> ParseChatIds(string text)
        {
            return Regex.Split(text ?? "", @"[\s,;]+").Where(s => s.Length > 0).Distinct().ToList();
        }

        /// <summary>Returns null on success, otherwise a readable error.</summary>
        public static string SendMessage(string token, string chatId, string text)
        {
            try
            {
                string body = "chat_id=" + Uri.EscapeDataString(chatId) + "&text=" + Uri.EscapeDataString(text)
                    + "&disable_web_page_preview=true";
                using (var wc = new WebClient())
                {
                    wc.Encoding = Encoding.UTF8;
                    wc.Headers[HttpRequestHeader.ContentType] = "application/x-www-form-urlencoded";
                    wc.UploadString(Api(token, "sendMessage"), body);
                }
                return null;
            }
            catch (Exception ex) { return Describe(ex); }
        }

        /// <summary>Sends the image with a caption; falls back to a plain message if the photo can't be sent.</summary>
        public static string SendPhotoOrMessage(string token, string chatId, string photoPath, string text)
        {
            if (!CanSendPhoto(photoPath)) return SendMessage(token, chatId, text);
            string caption = text.Length > 1024 ? text.Substring(0, 1021) + "..." : text;
            try
            {
                string boundary = "----rcs" + DateTime.Now.Ticks.ToString("x");
                var req = (HttpWebRequest)WebRequest.Create(Api(token, "sendPhoto"));
                req.Method = "POST";
                req.ContentType = "multipart/form-data; boundary=" + boundary;
                req.Timeout = 120000;
                using (Stream body = req.GetRequestStream())
                {
                    WriteField(body, boundary, "chat_id", chatId);
                    WriteField(body, boundary, "caption", caption);
                    string ext = Path.GetExtension(photoPath).ToLowerInvariant();
                    WriteText(body, "--" + boundary + "\r\nContent-Disposition: form-data; name=\"photo\"; filename=\"frame" + ext
                        + "\"\r\nContent-Type: " + (ext == ".png" ? "image/png" : "image/jpeg") + "\r\n\r\n");
                    byte[] bytes = File.ReadAllBytes(photoPath);
                    body.Write(bytes, 0, bytes.Length);
                    WriteText(body, "\r\n--" + boundary + "--\r\n");
                }
                using (req.GetResponse()) { }
                return null;
            }
            catch (Exception ex)
            {
                string photoError = Describe(ex);
                string textError = SendMessage(token, chatId, text);
                return textError == null ? null : photoError;
            }
        }

        static bool CanSendPhoto(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext != ".jpg" && ext != ".jpeg" && ext != ".png") return false;
            try { return new FileInfo(path).Length < MaxPhotoBytes; } catch { return false; }
        }

        static void WriteField(Stream s, string boundary, string name, string value)
        {
            WriteText(s, "--" + boundary + "\r\nContent-Disposition: form-data; name=\"" + name + "\"\r\n\r\n" + value + "\r\n");
        }

        static void WriteText(Stream s, string text)
        {
            byte[] b = Encoding.UTF8.GetBytes(text);
            s.Write(b, 0, b.Length);
        }

        /// <summary>"@botname" or null + error.</summary>
        public static string GetBotName(string token, out string error)
        {
            error = null;
            try
            {
                var root = GetJson(token, "getMe");
                var result = root["result"] as IDictionary<string, object>;
                return result != null && result.ContainsKey("username") ? "@" + result["username"] : "?";
            }
            catch (Exception ex) { error = Describe(ex); return null; }
        }

        public class Chat
        {
            public string Id, Title, Kind;
            public override string ToString() { return Title + "   (" + Kind + ", id " + Id + ")"; }
        }

        /// <summary>Chats that recently wrote to the bot or added it to a group (Telegram keeps them ~24 h).</summary>
        public static List<Chat> GetRecentChats(string token, out string error)
        {
            error = null;
            var chats = new List<Chat>();
            try
            {
                var root = GetJson(token, "getUpdates");
                var updates = root["result"] as IEnumerable;
                if (updates == null) return chats;
                foreach (object u in updates)
                {
                    var update = u as IDictionary<string, object>;
                    if (update == null) continue;
                    foreach (string key in new[] { "message", "edited_message", "channel_post", "my_chat_member", "chat_member" })
                    {
                        object part;
                        if (!update.TryGetValue(key, out part)) continue;
                        var chat = (part as IDictionary<string, object>) == null ? null
                            : Get((IDictionary<string, object>)part, "chat") as IDictionary<string, object>;
                        if (chat == null) continue;
                        string id = Convert.ToString(Get(chat, "id"), System.Globalization.CultureInfo.InvariantCulture);
                        if (string.IsNullOrEmpty(id) || chats.Any(c => c.Id == id)) continue;
                        string kind = Convert.ToString(Get(chat, "type"));
                        string title = Convert.ToString(Get(chat, "title"));
                        if (string.IsNullOrEmpty(title))
                        {
                            title = (Convert.ToString(Get(chat, "first_name")) + " " + Convert.ToString(Get(chat, "last_name"))).Trim();
                            string user = Convert.ToString(Get(chat, "username"));
                            if (!string.IsNullOrEmpty(user)) title += " @" + user;
                        }
                        chats.Add(new Chat
                        {
                            Id = id,
                            Title = title,
                            Kind = kind == "private" ? "личный" : kind == "channel" ? "канал" : "группа",
                        });
                    }
                }
            }
            catch (Exception ex) { error = Describe(ex); }
            return chats;
        }

        static object Get(IDictionary<string, object> d, string key)
        {
            object v;
            return d.TryGetValue(key, out v) ? v : null;
        }

        static IDictionary<string, object> GetJson(string token, string method)
        {
            using (var wc = new WebClient())
            {
                wc.Encoding = Encoding.UTF8;
                string json = wc.DownloadString(Api(token, method));
                return new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
            }
        }

        static string Describe(Exception ex)
        {
            string description = null;
            var web = ex as WebException;
            if (web != null && web.Response != null)
            {
                try
                {
                    using (var reader = new StreamReader(web.Response.GetResponseStream(), Encoding.UTF8))
                    {
                        var root = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(reader.ReadToEnd());
                        object d;
                        if (root != null && root.TryGetValue("description", out d)) description = Convert.ToString(d);
                    }
                }
                catch { }
            }
            if (description == null) return ex.Message;
            if (description.Contains("Unauthorized") || description.Contains("Not Found"))
                return "неверный токен бота";
            if (description.Contains("chat not found"))
                return "чат не найден — человек должен сначала написать боту /start";
            if (description.Contains("blocked by the user") || description.Contains("can't initiate"))
                return "человек не нажал /start у бота или заблокировал его";
            if (description.Contains("kicked") || description.Contains("not a member"))
                return "бота удалили из группы";
            return description;
        }
    }

    /// <summary>Settings dialog for Telegram notifications.</summary>
    class TelegramForm : Form
    {
        readonly Settings settings;
        CheckBox chkEnabled, chkShowToken, chkDone, chkFail, chkCrash, chkPhoto;
        TextBox txtToken, txtChats;
        CheckedListBox lstChats;
        Label lblBot;
        Button btnFind, btnTest;
        bool syncingList;

        public TelegramForm(Settings settings)
        {
            this.settings = settings;
            Text = "Уведомления в Telegram";
            Font = new Font("Segoe UI", 9F);
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(640, 540);

            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Padding = new Padding(12) };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            Controls.Add(t);

            chkEnabled = new CheckBox { Text = "Отправлять уведомления в Telegram", AutoSize = true, Checked = settings.TgEnabled, Font = new Font("Segoe UI", 9F, FontStyle.Bold) };
            AddFull(t, chkEnabled);

            var help = new Label
            {
                AutoSize = true, MaximumSize = new Size(600, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(3, 6, 3, 10),
                Text = "1. В Telegram откройте @BotFather → /newbot → скопируйте токен сюда.\n" +
                       "2. Каждый, кто должен получать уведомления, пишет вашему боту /start.\n" +
                       "    Или добавьте бота в общий чат и напишите там любое сообщение.\n" +
                       "3. Нажмите «Найти чаты» и отметьте получателей. Затем «Тест».",
            };
            AddFull(t, help);

            t.Controls.Add(new Label { Text = "Токен бота", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 8, 3) });
            txtToken = new TextBox { Text = settings.TgToken, Dock = DockStyle.Fill, UseSystemPasswordChar = true };
            t.Controls.Add(txtToken);
            chkShowToken = new CheckBox { Text = "показать", AutoSize = true, Margin = new Padding(6, 5, 3, 3) };
            chkShowToken.CheckedChanged += delegate { txtToken.UseSystemPasswordChar = !chkShowToken.Checked; };
            t.Controls.Add(chkShowToken);

            t.Controls.Add(new Label { Text = "Получатели", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 8, 3) });
            txtChats = new TextBox { Text = settings.TgChats, Dock = DockStyle.Fill };
            txtChats.TextChanged += delegate { SyncChecks(); };
            t.Controls.Add(txtChats);
            btnFind = new Button { Text = "Найти чаты", AutoSize = true, Margin = new Padding(6, 2, 3, 2) };
            btnFind.Click += delegate { FindChats(); };
            t.Controls.Add(btnFind);

            lblBot = new Label { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 2, 3, 2), Text = "chat ID через запятую — или отметьте ниже" };
            t.Controls.Add(new Label());
            t.Controls.Add(lblBot);
            t.SetColumnSpan(lblBot, 2);

            lstChats = new CheckedListBox { Dock = DockStyle.Fill, Height = 120, CheckOnClick = true, IntegralHeight = false };
            lstChats.ItemCheck += OnChatChecked;
            AddFull(t, lstChats);

            var grpWhen = new GroupBox { Text = "Когда отправлять", Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(8), Margin = new Padding(3, 10, 3, 3) };
            var flowWhen = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.TopDown };
            chkDone = new CheckBox { Text = "✅ Рендер готов", AutoSize = true, Checked = settings.TgOnDone };
            chkFail = new CheckBox { Text = "❌ Рендер остановился с ошибкой (лимит попыток, ошибка скрипта)", AutoSize = true, Checked = settings.TgOnFail };
            chkCrash = new CheckBox { Text = "⚠ Каждый краш Blender (перед автоматическим перезапуском)", AutoSize = true, Checked = settings.TgOnCrash };
            chkPhoto = new CheckBox { Text = "Прикреплять последний отрендеренный кадр", AutoSize = true, Checked = settings.TgPhoto };
            flowWhen.Controls.AddRange(new Control[] { chkDone, chkFail, chkCrash, chkPhoto });
            grpWhen.Controls.Add(flowWhen);
            AddFull(t, grpWhen);

            var bottom = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Margin = new Padding(0, 10, 0, 0) };
            var btnCancel = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel, AutoSize = true };
            var btnOk = new Button { Text = "Сохранить", DialogResult = DialogResult.OK, AutoSize = true };
            btnOk.Click += delegate { Save(); };
            btnTest = new Button { Text = "Тест", AutoSize = true, Margin = new Padding(3, 3, 40, 3) };
            btnTest.Click += delegate { SendTest(); };
            bottom.Controls.AddRange(new Control[] { btnCancel, btnOk, btnTest });
            AddFull(t, bottom);
            AcceptButton = btnOk;
            CancelButton = btnCancel;
        }

        static void AddFull(TableLayoutPanel t, Control c)
        {
            t.Controls.Add(c);
            t.SetColumnSpan(c, 3);
        }

        void Save()
        {
            settings.TgEnabled = chkEnabled.Checked;
            settings.TgToken = txtToken.Text.Trim();
            settings.TgChats = string.Join(", ", TelegramApi.ParseChatIds(txtChats.Text));
            settings.TgOnDone = chkDone.Checked;
            settings.TgOnFail = chkFail.Checked;
            settings.TgOnCrash = chkCrash.Checked;
            settings.TgPhoto = chkPhoto.Checked;
            settings.Save();
        }

        void FindChats()
        {
            string token = txtToken.Text.Trim();
            if (token.Length == 0) { MessageBox.Show(this, "Сначала вставьте токен бота.", Text); return; }
            btnFind.Enabled = false;
            lblBot.Text = "Запрашиваю Telegram…";
            ThreadPool.QueueUserWorkItem(delegate
            {
                string err1, err2 = null;
                string bot = TelegramApi.GetBotName(token, out err1);
                var chats = new List<TelegramApi.Chat>();
                if (err1 == null) chats = TelegramApi.GetRecentChats(token, out err2);
                SafeInvoke(() =>
                {
                    btnFind.Enabled = true;
                    if (err1 != null || err2 != null) { lblBot.Text = "Ошибка: " + (err1 ?? err2); return; }
                    lblBot.Text = chats.Count > 0
                        ? string.Format("Бот {0}: найдено чатов — {1}. Отметьте получателей.", bot, chats.Count)
                        : string.Format("Бот {0}: чатов не найдено. Напишите боту /start и нажмите ещё раз.", bot);
                    syncingList = true;
                    foreach (var chat in chats)
                    {
                        bool exists = false;
                        foreach (TelegramApi.Chat item in lstChats.Items) if (item.Id == chat.Id) exists = true;
                        if (!exists) lstChats.Items.Add(chat);
                    }
                    syncingList = false;
                    SyncChecks();
                });
            });
        }

        void OnChatChecked(object sender, ItemCheckEventArgs e)
        {
            if (syncingList) return;
            var chat = (TelegramApi.Chat)lstChats.Items[e.Index];
            List<string> ids = TelegramApi.ParseChatIds(txtChats.Text);
            if (e.NewValue == CheckState.Checked) { if (!ids.Contains(chat.Id)) ids.Add(chat.Id); }
            else ids.Remove(chat.Id);
            syncingList = true;
            txtChats.Text = string.Join(", ", ids);
            syncingList = false;
        }

        void SyncChecks()
        {
            if (syncingList) return;
            syncingList = true;
            List<string> ids = TelegramApi.ParseChatIds(txtChats.Text);
            for (int i = 0; i < lstChats.Items.Count; i++)
                lstChats.SetItemChecked(i, ids.Contains(((TelegramApi.Chat)lstChats.Items[i]).Id));
            syncingList = false;
        }

        void SendTest()
        {
            string token = txtToken.Text.Trim();
            List<string> ids = TelegramApi.ParseChatIds(txtChats.Text);
            if (token.Length == 0 || ids.Count == 0)
            {
                MessageBox.Show(this, "Укажите токен и хотя бы одного получателя.", Text);
                return;
            }
            btnTest.Enabled = false;
            string text = "🔔 Тест уведомлений RenderCrashSolver\nКомпьютер: " + Environment.MachineName;
            ThreadPool.QueueUserWorkItem(delegate
            {
                var errors = new List<string>();
                foreach (string id in ids)
                {
                    string err = TelegramApi.SendMessage(token, id, text);
                    if (err != null) errors.Add(id + ": " + err);
                }
                SafeInvoke(() =>
                {
                    btnTest.Enabled = true;
                    if (errors.Count == 0)
                        MessageBox.Show(this, "Отправлено получателям: " + ids.Count, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    else
                        MessageBox.Show(this, "Не удалось отправить:\n" + string.Join("\n", errors), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                });
            });
        }

        void SafeInvoke(Action a)
        {
            try { if (!IsDisposed && IsHandleCreated) BeginInvoke(a); } catch { }
        }
    }
}
