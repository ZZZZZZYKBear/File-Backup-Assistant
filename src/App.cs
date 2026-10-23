using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CloudBackup {
    static class Program {
        public const string Title = "云笺备份 · GitHub Backup Assistant";
        [STAThread]
        static int Main(string[] args) {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            try {
                if (args.Length > 0 && args[0] == "--self-test") return SelfTests.Run(args.Length > 1 ? args[1] : Environment.CurrentDirectory);
                if (args.Length > 1 && args[0] == "--render-preview") {
                    Store preview = new Store(Path.Combine(args[1], "preview-state"));
                    using (MainForm form = new MainForm(preview, false, true)) {
                        form.Show(); Application.DoEvents();
                        using (Bitmap bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height)); bitmap.Save(Path.Combine(args[1], "app-preview.png")); }
                        form.ForceExit();
                    }
                    return 0;
                }
                string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GitHubBackupAssistant");
                if (DesktopContext.RelaunchIfRedirected(root, args.Contains("--tray"))) return 0;
                bool created;
                using (Mutex single = new Mutex(true, @"Local\GitHubBackupAssistant-" + Engine.Key(Environment.UserName), out created)) {
                    if (!created) {
                        IntPtr window = FindWindow(null, Title);
                        if (window != IntPtr.Zero) { ShowWindow(window, 9); SetForegroundWindow(window); }
                        else MessageBox.Show("助手已经在运行，请在系统托盘中打开。", "云笺备份");
                        return 0;
                    }
                    Application.Run(new MainForm(new Store(root), args.Contains("--tray"), false));
                    single.ReleaseMutex();
                }
                return 0;
            } catch (Exception error) {
                MessageBox.Show(Store.Sanitize(error.Message), "云笺备份 · 无法启动", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr window, int command);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
    }
    internal static class DesktopContext {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern uint GetFinalPathNameByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle file, System.Text.StringBuilder path, uint size, uint flags);
        internal static string PhysicalPath(string path) {
            using (var handle = CreateFile(path, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero)) {
                if (handle.IsInvalid) throw new IOException("无法确认应用数据位置：" + new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message);
                var result = new System.Text.StringBuilder(32768);
                uint length = GetFinalPathNameByHandle(handle, result, (uint)result.Capacity, 0);
                if (length == 0 || length >= result.Capacity) throw new IOException("无法确认应用数据的真实位置。");
                string physical = result.ToString();
                if (physical.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + physical.Substring(8);
                return physical.StartsWith(@"\\?\", StringComparison.Ordinal) ? physical.Substring(4) : physical;
            }
        }
        internal static bool IsRedirected(string requested, string physical) {
            return !Paths.Equal(requested, physical) &&
                physical.IndexOf(@"\Packages\", StringComparison.OrdinalIgnoreCase) >= 0 &&
                physical.IndexOf(@"\LocalCache\", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        internal static bool RelaunchIfRedirected(string root, bool tray) {
            Directory.CreateDirectory(root);
            string config = Path.Combine(root, "settings.json");
            bool redirected = File.Exists(config) && IsRedirected(config, PhysicalPath(config));
            // A package may read the real file until its first write (copy on
            // write), so checking only the existing configuration is insufficient.
            if (!redirected) {
                string probe = Path.Combine(root, "context-" + Guid.NewGuid().ToString("N") + ".tmp");
                try {
                    using (FileStream file = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
                    redirected = IsRedirected(probe, PhysicalPath(probe));
                } finally { if (File.Exists(probe)) File.Delete(probe); }
            }
            if (!redirected) return false;
            string executable = PhysicalPath(Application.ExecutablePath), target = executable;
            if (IsRedirected(Application.ExecutablePath, executable))
                throw new IOException("安装文件也被启动器重定向，请从 Windows 桌面运行安装脚本后启动，避免使用临时包缓存作为自启路径。");
            if (tray) {
                // The physical shortcut remains accessible to Explorer even
                // when the caller's logical AppData path is virtualized.
                string link = Path.Combine(root, "desktop-launch.lnk");
                object shell = null, shortcut = null;
                try {
                    shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
                    shortcut = shell.GetType().InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { link });
                    Type type = shortcut.GetType();
                    type.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { executable });
                    type.InvokeMember("Arguments", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { "--tray" });
                    type.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, shortcut, null);
                    target = PhysicalPath(link);
                } finally {
                    if (shortcut != null) Marshal.FinalReleaseComObject(shortcut);
                    if (shell != null) Marshal.FinalReleaseComObject(shell);
                }
            }
            Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), Git.Quote(target)) { UseShellExecute = false, CreateNoWindow = true });
            return true;
        }
    }
    public sealed class MainForm : Form {
        readonly Store store; Settings settings; Engine engine;
        readonly bool preview, startHidden;
        readonly Color ink = Color.FromArgb(28, 39, 60), muted = Color.FromArgb(106, 119, 139), blue = Color.FromArgb(49, 99, 233);
        readonly DataGridView grid = new DataGridView();
        readonly Label state = new Label(), count = new Label(), last = new Label(), next = new Label(), repository = new Label();
        readonly TextBox logs = new TextBox();
        readonly ProgressBar progress = new ProgressBar();
        readonly List<Button> actions = new List<Button>();
        readonly Button pause = new Button();
        Button backupButton;
        ToolStripMenuItem stopBackupMenu;
        CancellationTokenSource backupCancellation;
        readonly NotifyIcon tray = new NotifyIcon();
        readonly System.Windows.Forms.Timer clock = new System.Windows.Forms.Timer();
        bool busy, exiting, exitWhenDone, loadingGrid, hideTip;
        public MainForm(Store store, bool startHidden, bool preview) {
            this.store = store; this.preview = preview; this.startHidden = startHidden;
            settings = store.Load();
            Text = Program.Title; ClientSize = new Size(1080, 760); MinimumSize = new Size(940, 680);
            StartPosition = FormStartPosition.CenterScreen; Font = new Font("Microsoft YaHei UI", 9F);
            BackColor = Color.FromArgb(245, 247, 251); ForeColor = ink; AutoScaleMode = AutoScaleMode.Dpi;
            DoubleBuffered = true; Icon = MakeIcon();
            BuildInterface(); SetupTray();
            try { engine = new Engine(store); engine.Progress = Report; }
            catch (Exception error) { state.Text = error.Message; }
            if (!preview) { try { Startup.Set(settings.AutoStart); } catch (Exception error) { Report("开机自启设置未完成：" + error.Message); } }
            if (preview) {
                settings.Folders.Add(new FolderJob { Name = "我的文档", Source = @"D:\资料\工作文档", GitHubName = "工作文档", Status = "内容未变化", LastCheck = "今天 12:00" });
                settings.Folders.Add(new FolderJob { Name = "项目笔记", Source = @"D:\资料\项目笔记", GitHubName = "项目笔记备份", Status = "备份已推送", LastCheck = "今天 12:00" });
                state.Text = "一切就绪，等待下一次检查";
            }
            RefreshGrid(); RefreshHeader();
            if (File.Exists(store.LogFile)) logs.Text = String.Join(Environment.NewLine, File.ReadAllLines(store.LogFile).Reverse().Take(80).Reverse());
            clock.Interval = 15000; clock.Tick += async delegate { try { RefreshHeader(); if (!preview && !busy && !settings.Paused && settings.Folders.Any(j => j.Enabled) && DateTime.UtcNow >= NextTime()) await Backup(); } catch (Exception error) { Report("计划检查未完成：" + error.Message); } };
            if (!preview) clock.Start();
            Shown += delegate { if (startHidden) Hide(); };
            FormClosing += OnClosing;
        }
        static Icon MakeIcon() {
            using (Bitmap bitmap = new Bitmap(64, 64)) {
                using (Graphics graphics = Graphics.FromImage(bitmap)) {
                    graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    graphics.Clear(Color.Transparent);
                    using (Brush blue = new SolidBrush(Color.FromArgb(49, 99, 233))) graphics.FillEllipse(blue, 2, 2, 60, 60);
                    using (Brush white = new SolidBrush(Color.White)) { graphics.FillEllipse(white, 14, 27, 24, 20); graphics.FillEllipse(white, 25, 17, 25, 28); graphics.FillRectangle(white, 22, 30, 29, 17); }
                    using (Pen pen = new Pen(Color.FromArgb(49, 99, 233), 4)) { graphics.DrawLine(pen, 33, 43, 33, 27); graphics.DrawLine(pen, 33, 27, 26, 34); graphics.DrawLine(pen, 33, 27, 40, 34); }
                }
                IntPtr handle = bitmap.GetHicon();
                try { using (Icon icon = Icon.FromHandle(handle)) return (Icon)icon.Clone(); }
                finally { DestroyIcon(handle); }
            }
        }
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr handle);
        Label LabelText(string text, float size, Color color) { return new Label { Text = text, AutoSize = true, Font = new Font(Font.FontFamily, size), ForeColor = color, Margin = new Padding(0, 0, 0, 5) }; }
        Button ButtonText(string text, EventHandler click, bool primary) {
            Button button = new Button { Text = text, AutoSize = true, Height = 35, MinimumSize = new Size(92, 35), FlatStyle = FlatStyle.Flat,
                BackColor = primary ? blue : Color.White, ForeColor = primary ? Color.White : ink, Padding = new Padding(12, 3, 12, 3), Margin = new Padding(0, 0, 9, 0), Cursor = Cursors.Hand };
            button.FlatAppearance.BorderColor = primary ? blue : Color.FromArgb(218, 224, 234);
            button.Click += click; actions.Add(button); return button;
        }
        Panel Metric(string title, Label value) {
            Panel panel = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Margin = new Padding(0, 0, 12, 0), Padding = new Padding(16, 10, 10, 8) };
            Label caption = LabelText(title, 9, muted); caption.Dock = DockStyle.Top;
            value.Font = new Font(Font.FontFamily, 13, FontStyle.Bold); value.Dock = DockStyle.Fill; value.Padding = new Padding(0, 5, 0, 0);
            panel.Controls.Add(value); panel.Controls.Add(caption); return panel;
        }
        void BuildInterface() {
            TableLayoutPanel layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(26, 20, 26, 18), ColumnCount = 1, RowCount = 8 };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 92)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 84));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 62)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 47));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 126)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
            Panel heading = new Panel { Dock = DockStyle.Fill };
            heading.Controls.Add(LabelText("云笺备份", 23, ink));
            Label subtitle = LabelText("GitHub 文件夹备份助手  /  每次改动，留一份记录", 9, muted); subtitle.Location = new Point(2, 55); heading.Controls.Add(subtitle);
            Panel preferenceArea = new Panel { Dock = DockStyle.Right, Width = 112 };
            Button preferences = ButtonText("设置", delegate { Preferences(); }, false); preferences.Dock = DockStyle.Top; preferenceArea.Controls.Add(preferences); heading.Controls.Add(preferenceArea); preferenceArea.BringToFront();
            layout.Controls.Add(heading, 0, 0);
            TableLayoutPanel metrics = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Margin = new Padding(0, 0, 0, 10) };
            for (int i = 0; i < 3; i++) metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333F));
            metrics.Controls.Add(Metric("备份目录", count), 0, 0); metrics.Controls.Add(Metric("最近成功推送", last), 1, 0); metrics.Controls.Add(Metric("下一次检查", next), 2, 0); layout.Controls.Add(metrics, 0, 1);
            Panel repo = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 8, 12, 8), BackColor = Color.FromArgb(232, 239, 255), Margin = new Padding(0, 3, 0, 10) };
            repository.Dock = DockStyle.Fill; repository.AutoEllipsis = true; repository.TextAlign = ContentAlignment.MiddleLeft; repository.ForeColor = blue;
            Button open = ButtonText("打开仓库 ↗", delegate { Open(settings.Repository.Replace(".git", "")); }, false); open.Dock = DockStyle.Right;
            repo.Controls.Add(repository); repo.Controls.Add(open); layout.Controls.Add(repo, 0, 2);
            FlowLayoutPanel toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
            toolbar.Controls.Add(ButtonText("＋ 添加目录", delegate { AddFolder(); }, true));
            toolbar.Controls.Add(ButtonText("移除选中", delegate { RemoveFolder(); }, false));
            backupButton = ButtonText("立即备份", async delegate { if (backupCancellation != null) RequestStop(); else await Backup(); }, false);
            toolbar.Controls.Add(backupButton);
            pause.Text = "暂停计划"; pause.AutoSize = true; pause.MinimumSize = new Size(98, 35); pause.FlatStyle = FlatStyle.Flat; pause.BackColor = Color.White; pause.FlatAppearance.BorderColor = Color.FromArgb(218, 224, 234);
            pause.Click += delegate { TogglePause(); }; actions.Add(pause); toolbar.Controls.Add(pause);
            toolbar.Controls.Add(ButtonText("登录 GitHub", async delegate { await Background("请在浏览器中完成 GitHub 登录…", delegate { engine.Login(); return "GitHub 登录已完成，可点击立即备份。"; }); }, false));
            toolbar.Controls.Add(ButtonText("检查连接", async delegate { await Background("正在检查仓库连接…", delegate { engine.TestConnection(settings.Repository); return "仓库可访问。首次推送若需要认证，请点击“登录 GitHub”。"; }); }, false));
            layout.Controls.Add(toolbar, 0, 3);
            grid.Dock = DockStyle.Fill; grid.BackgroundColor = Color.White; grid.BorderStyle = BorderStyle.None; grid.AllowUserToAddRows = false; grid.AllowUserToDeleteRows = false;
            grid.AllowUserToResizeRows = false; grid.RowHeadersVisible = false; grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect; grid.MultiSelect = true;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill; grid.EnableHeadersVisualStyles = false; grid.ColumnHeadersHeight = 39;
            grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(236, 240, 247); grid.ColumnHeadersDefaultCellStyle.ForeColor = muted; grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
            grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(230, 237, 255); grid.DefaultCellStyle.SelectionForeColor = ink; grid.DefaultCellStyle.Padding = new Padding(5, 0, 5, 0);
            grid.GridColor = Color.FromArgb(236, 240, 247); grid.RowTemplate.Height = 43; grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Enabled", HeaderText = "启用", FillWeight = 10, MinimumWidth = 60 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "目录名称", ReadOnly = true, FillWeight = 23 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "本地路径", ReadOnly = true, FillWeight = 50 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "状态", ReadOnly = true, FillWeight = 30 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "GitHubName", HeaderText = "GitHub 目录名", ReadOnly = false, FillWeight = 36,
                ToolTipText = "双击编辑，回车保存；下次备份时会迁移到新名称。支持中文。" });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "最近检查", ReadOnly = true, FillWeight = 30 });
            grid.CurrentCellDirtyStateChanged += delegate { if (grid.CurrentCell is DataGridViewCheckBoxCell && grid.IsCurrentCellDirty) grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            grid.CellValidating += delegate(object sender, DataGridViewCellValidatingEventArgs e) {
                if (loadingGrid || busy || e.RowIndex < 0 || e.ColumnIndex != 4) return;
                try {
                    string name = CloudNames.Normalize(Convert.ToString(e.FormattedValue));
                    FolderJob current = grid.Rows[e.RowIndex].Tag as FolderJob;
                    if (settings.Folders.Any(j => j != current && String.Equals(j.RemoteDirectory, name, StringComparison.OrdinalIgnoreCase))) throw new IOException("GitHub 目录名与其他任务重复，请换一个名称。");
                    grid.Rows[e.RowIndex].ErrorText = "";
                } catch (Exception error) { e.Cancel = true; grid.Rows[e.RowIndex].ErrorText = error.Message; Report(error.Message + "（Esc 可取消编辑）"); }
            };
            grid.CellEndEdit += delegate(object sender, DataGridViewCellEventArgs e) { if (e.RowIndex >= 0) grid.Rows[e.RowIndex].ErrorText = ""; };
            grid.CellValueChanged += delegate(object sender, DataGridViewCellEventArgs e) {
                if (loadingGrid || busy || e.RowIndex < 0 || e.ColumnIndex != 0 && e.ColumnIndex != 4) return;
                FolderJob job = grid.Rows[e.RowIndex].Tag as FolderJob; if (job == null) return;
                string previousName = job.GitHubName; bool previousEnabled = job.Enabled;
                try {
                    if (e.ColumnIndex == 0) job.Enabled = Convert.ToBoolean(grid.Rows[e.RowIndex].Cells[0].Value);
                    else {
                        string name = CloudNames.Normalize(Convert.ToString(grid.Rows[e.RowIndex].Cells[4].Value));
                        if (name == job.RemoteDirectory) return;
                        job.GitHubName = name; CloudNames.ValidateJobs(settings.Folders);
                    }
                    store.Save(settings);
                    if (e.ColumnIndex == 4) {
                        loadingGrid = true; grid.Rows[e.RowIndex].Cells[4].Value = job.RemoteDirectory; loadingGrid = false;
                        job.Status = "目录名待同步"; grid.Rows[e.RowIndex].Cells[3].Value = job.Status;
                        grid.Rows[e.RowIndex].Cells[4].ToolTipText = "云端位置：" + job.Target;
                        Report("GitHub 目录名已保存：" + job.RemoteDirectory + "。点击立即备份或等待计划检查生效。");
                    }
                    RefreshHeader();
                } catch (Exception error) {
                    job.GitHubName = previousName; job.Enabled = previousEnabled; loadingGrid = true;
                    grid.Rows[e.RowIndex].Cells[0].Value = previousEnabled; grid.Rows[e.RowIndex].Cells[4].Value = job.RemoteDirectory;
                    loadingGrid = false; Report("未保存：" + error.Message);
                }
            };
            grid.CellDoubleClick += delegate(object sender, DataGridViewCellEventArgs e) {
                if (e.RowIndex >= 0 && e.ColumnIndex != 0 && e.ColumnIndex != 4) { FolderJob job = grid.Rows[e.RowIndex].Tag as FolderJob; if (job != null && Directory.Exists(job.Source)) Open(job.Source); }
            };
            layout.Controls.Add(grid, 0, 4);
            state.Dock = DockStyle.Fill; state.TextAlign = ContentAlignment.MiddleLeft; state.AutoEllipsis = true; state.Text = "添加一个或多个目录，即可开始备份。"; state.ForeColor = muted; layout.Controls.Add(state, 0, 5);
            logs.Dock = DockStyle.Fill; logs.Multiline = true; logs.ReadOnly = true; logs.ScrollBars = ScrollBars.Vertical; logs.BorderStyle = BorderStyle.FixedSingle;
            logs.BackColor = Color.FromArgb(28, 39, 60); logs.ForeColor = Color.FromArgb(202, 216, 240); logs.Font = new Font("Microsoft YaHei UI", 8.5F); layout.Controls.Add(logs, 0, 6);
            Panel footer = new Panel { Dock = DockStyle.Fill };
            Label hint = LabelText("默认每小时检查 · 无变化不推送 · 关闭窗口后继续在托盘运行", 8, muted); hint.Dock = DockStyle.Fill; hint.TextAlign = ContentAlignment.MiddleLeft; footer.Controls.Add(hint);
            progress.Dock = DockStyle.Bottom; progress.Height = 3; progress.Style = ProgressBarStyle.Marquee; progress.Visible = false; footer.Controls.Add(progress); layout.Controls.Add(footer, 0, 7);
            Controls.Add(layout);
        }
        void SetupTray() {
            tray.Icon = Icon; tray.Text = "云笺备份 · GitHub 文件夹备份"; tray.Visible = !preview;
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("打开助手", null, delegate { Restore(); });
            menu.Items.Add("立即备份", null, async delegate { await Backup(); });
            stopBackupMenu = new ToolStripMenuItem("停止本轮备份", null, delegate { RequestStop(); }); stopBackupMenu.Enabled = false;
            menu.Items.Add(stopBackupMenu);
            menu.Items.Add("暂停 / 继续计划", null, delegate { TogglePause(); });
            menu.Items.Add("打开日志", null, delegate { Directory.CreateDirectory(Path.GetDirectoryName(store.LogFile)); Open(Path.GetDirectoryName(store.LogFile)); });
            menu.Items.Add(new ToolStripSeparator()); menu.Items.Add("退出助手", null, delegate { Exit(); });
            tray.ContextMenuStrip = menu; tray.DoubleClick += delegate { Restore(); };
        }
        public void ForceExit() { exiting = true; clock.Stop(); tray.Visible = false; Close(); }
        void Exit() { if (busy) { exitWhenDone = true; Report("当前操作完成后退出助手。"); } else ForceExit(); }
        void OnClosing(object sender, FormClosingEventArgs e) {
            if (!exiting && !preview && e.CloseReason == CloseReason.UserClosing) {
                e.Cancel = true; Hide();
                if (!hideTip) { tray.ShowBalloonTip(2500, "云笺备份仍在运行", "助手已收起到系统托盘，右键图标可以退出。", ToolTipIcon.Info); hideTip = true; }
            } else { clock.Stop(); tray.Visible = false; tray.Dispose(); }
        }
        void Restore() { Show(); WindowState = FormWindowState.Normal; Activate(); }
        void Open(string target) { try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch (Exception error) { MessageBox.Show(error.Message, "无法打开"); } }
        void RefreshGrid() {
            loadingGrid = true; grid.Rows.Clear();
            foreach (FolderJob job in settings.Folders) {
                int i = grid.Rows.Add(job.Enabled, job.Name, job.Source, job.Status, job.RemoteDirectory, job.LastCheck ?? "尚未检查");
                grid.Rows[i].Tag = job; grid.Rows[i].Cells[1].ToolTipText = "云端位置：" + job.Target;
                grid.Rows[i].Cells[4].ToolTipText = "双击编辑，回车保存。云端位置：" + job.Target;
            }
            loadingGrid = false;
        }
        DateTime NextTime() { DateTime value; return DateTime.TryParse(settings.NextRunUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out value) ? value.ToUniversalTime() : DateTime.UtcNow; }
        void RefreshHeader() {
            count.Text = settings.Folders.Count + " 个 / 启用 " + settings.Folders.Count(j => j.Enabled);
            last.Text = settings.LastPush ?? "尚未推送";
            next.Text = settings.Paused ? "已暂停" : (!settings.Folders.Any(j => j.Enabled) ? "等待添加目录" : NextTime().ToLocalTime().ToString("MM-dd HH:mm"));
            pause.Text = settings.Paused ? "继续计划" : "暂停计划";
            repository.Text = "目标仓库   " + settings.Repository.Replace("https://github.com/", "").Replace(".git", "");
        }
        void Report(string message) {
            if (IsDisposed) return;
            if (InvokeRequired) { BeginInvoke(new Action<string>(Report), message); return; }
            state.Text = Store.Sanitize(message);
            logs.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + Store.Sanitize(message) + Environment.NewLine);
            if (logs.TextLength > 60000) logs.Text = logs.Text.Substring(logs.TextLength - 40000);
            logs.SelectionStart = logs.TextLength; logs.ScrollToCaret();
        }
        void SetBusy(bool value) {
            busy = value; foreach (Button button in actions) button.Enabled = !value; grid.ReadOnly = value; progress.Visible = value;
            bool canStop = backupCancellation != null && !backupCancellation.IsCancellationRequested;
            backupButton.Text = backupCancellation == null ? "立即备份" : canStop ? "停止备份" : "正在停止…";
            if (backupCancellation != null) backupButton.Enabled = canStop;
            if (stopBackupMenu != null) stopBackupMenu.Enabled = canStop;
        }
        void RequestStop() {
            if (backupCancellation == null || backupCancellation.IsCancellationRequested) return;
            backupCancellation.Cancel(); SetBusy(true);
            const string message = "正在停止本轮备份…若正在创建提交或整理历史，会在该步骤完成后停止。";
            Report(message); store.Log(message);
        }
        async Task Background(string message, Func<string> operation) {
            if (busy) return; if (engine == null) { MessageBox.Show("未找到 Git for Windows，请安装后重启助手。"); return; }
            SetBusy(true); Report(message);
            try { Report(await Task.Run(operation)); }
            catch (Exception error) { Report(error.Message); store.Log(error.Message); if (!Visible) tray.ShowBalloonTip(3000, "操作未完成", Store.Sanitize(error.Message).Substring(0, Math.Min(220, Store.Sanitize(error.Message).Length)), ToolTipIcon.Warning); }
            finally { SetBusy(false); if (exitWhenDone) ForceExit(); }
        }
        async Task Backup() {
            if (busy) return;
            if (!grid.EndEdit()) return;
            if (engine == null || !settings.Folders.Any(j => j.Enabled)) { Report("请先添加并启用目录，并确认已安装 Git。"); return; }
            Settings snapshot = settings.Copy(); backupCancellation = new CancellationTokenSource(); SetBusy(true);
            foreach (FolderJob job in settings.Folders.Where(j => j.Enabled)) job.Status = "检查中…";
            RefreshGrid();
            try {
                CancellationToken token = backupCancellation.Token;
                RunResult result = await Task.Run(delegate { return engine.Backup(snapshot, token); });
                foreach (FolderJob job in settings.Folders.Where(j => j.Enabled)) {
                    int warnings; result.JobWarnings.TryGetValue(job.Id, out warnings);
                    job.Status = result.Changed || result.Pushed ? "备份已推送" : "内容未变化";
                    if (warnings > 0) job.Status += " · 跳过 " + warnings + " 项";
                    job.LastCheck = DateTime.Now.ToString("MM-dd HH:mm:ss");
                }
                if (result.Pushed) settings.LastPush = DateTime.Now.ToString("MM-dd HH:mm");
                Report(result.Message);
            } catch (OperationCanceledException) {
                const string message = "本轮备份已停止。已完成的提交保留，未推送的提交可在下次备份补传；自动计划保持原设置。";
                Report(message); store.Log(message);
                foreach (FolderJob job in settings.Folders.Where(j => j.Enabled)) { job.Status = "本轮已停止"; job.LastCheck = DateTime.Now.ToString("MM-dd HH:mm:ss"); }
            } catch (Exception error) {
                string message = Store.Sanitize(error.Message); Report(message); store.Log(message);
                foreach (FolderJob job in settings.Folders.Where(j => j.Enabled)) { job.Status = "本轮失败 · 查看日志"; job.LastCheck = DateTime.Now.ToString("MM-dd HH:mm:ss"); }
                if (!Visible) tray.ShowBalloonTip(4000, "备份未完成", message.Substring(0, Math.Min(220, message.Length)), ToolTipIcon.Warning);
            } finally {
                backupCancellation.Dispose(); backupCancellation = null;
                settings.NextRunUtc = DateTime.UtcNow.AddMinutes(settings.IntervalMinutes).ToString("o");
                try { store.Save(settings); }
                catch (Exception error) { Report("备份状态未能保存，下次启动将重新检查：" + error.Message); }
                finally { SetBusy(false); RefreshGrid(); RefreshHeader(); if (exitWhenDone) ForceExit(); }
            }
        }
        void AddFolder() {
            if (busy) return;
            if (!grid.EndEdit()) return;
            using (FolderBrowserDialog dialog = new FolderBrowserDialog { Description = "选择要备份到 GitHub 的文件夹（之后可继续添加）", ShowNewFolderButton = false }) {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try {
                    Paths.Source(dialog.SelectedPath, store.Root);
                    if (settings.Folders.Any(j => Paths.Equal(j.Source, dialog.SelectedPath))) { MessageBox.Show("这个目录已在备份列表中。"); return; }
                    FolderJob added = new FolderJob { Name = Path.GetFileName(Paths.Full(dialog.SelectedPath)), Source = Paths.Full(dialog.SelectedPath) };
                    added.GitHubName = CloudNames.Suggest(added.Name, added.Id, settings.Folders);
                    Settings candidate = settings.Copy(); candidate.Folders.Add(added);
                    if (SaveTaskSettings(candidate, "添加目录")) Report("目录已添加，可点击“立即备份”，或等待下一次每小时检查。");
                } catch (Exception error) { MessageBox.Show(error.Message, "无法添加目录"); }
            }
        }
        void RemoveFolder() {
            if (busy) return;
            if (!grid.EndEdit()) return;
            List<FolderJob> selected = grid.SelectedRows.Cast<DataGridViewRow>().Select(r => r.Tag as FolderJob).Where(j => j != null).ToList();
            if (selected.Count == 0) return;
            if (MessageBox.Show("移除选中的 " + selected.Count + " 个备份任务？\n本地源文件、云端现有备份和历史均保留。", "移除备份任务", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            RemoveJobs(selected.Select(j => j.Id));
        }
        bool SaveTaskSettings(Settings candidate, string action) {
            try {
                store.Save(candidate); settings = candidate; RefreshGrid(); RefreshHeader(); return true;
            } catch (Exception error) {
                string message = action + "未保存，原任务和计划保持不变：" + Store.Sanitize(error.Message);
                Report(message); store.Log(message); return false;
            }
        }
        internal bool RemoveJobs(IEnumerable<string> ids) {
            if (busy) return false;
            HashSet<string> selected = new HashSet<string>(ids);
            Settings candidate = settings.Copy(); candidate.Folders.RemoveAll(j => selected.Contains(j.Id));
            if (!SaveTaskSettings(candidate, "移除任务")) return false;
            Report("任务已移除，本地文件和云端备份均保留。"); return true;
        }
        void TogglePause() {
            if (busy || !grid.EndEdit()) return;
            Settings candidate = settings.Copy(); candidate.Paused = !candidate.Paused;
            SaveTaskSettings(candidate, "更改计划");
        }
        async void Preferences() {
            if (busy) return;
            if (!grid.EndEdit()) return;
            using (SettingsForm form = new SettingsForm(settings.Copy(), store.Root)) {
                DialogResult result = form.ShowDialog(this);
                if (result == DialogResult.Retry) {
                    if (MessageBox.Show("这会先保留未推送提交到本地恢复引用，再以远端最新版本重建缓存。\n源文件不变；之后可以点击立即备份，生成当前源目录的新快照。", "处理远端备份冲突", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK)
                        await Background("正在保留本地提交并恢复缓存…", delegate { engine.PreserveConflict(settings.Copy()); return "缓存已恢复，可点击立即备份。旧提交保留在本地恢复引用中。"; });
                    return;
                }
                if (result != DialogResult.OK) return;
                try {
                    Startup.Set(form.Value.AutoStart); settings = form.Value;
                    settings.NextRunUtc = DateTime.UtcNow.AddMinutes(settings.IntervalMinutes).ToString("o");
                    store.Save(settings); RefreshHeader(); RefreshGrid(); Report("设置已保存。");
                } catch (Exception error) { MessageBox.Show(error.Message, "设置未保存"); }
            }
        }
    }
    sealed class SettingsForm : Form {
        public Settings Value; readonly TextBox remote = new TextBox(), excludes = new TextBox();
        readonly NumericUpDown interval = new NumericUpDown(); readonly CheckBox startup = new CheckBox();
        public SettingsForm(Settings value, string root) {
            Value = value; Text = "设置 · 云笺备份"; ClientSize = new Size(660, 470); Font = new Font("Microsoft YaHei UI", 9);
            StartPosition = FormStartPosition.CenterParent; FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
            TableLayoutPanel panel = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 1, RowCount = 10 };
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 26)); panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 39)); panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 40)); panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 38)); panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 88)); panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 40)); panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            panel.Controls.Add(new Label { Text = "GitHub 仓库地址", AutoSize = true }, 0, 0);
            remote.Text = value.Repository; remote.Dock = DockStyle.Top; panel.Controls.Add(remote, 0, 1);
            panel.Controls.Add(new Label { Text = "自动检查间隔（分钟）", AutoSize = true }, 0, 2);
            interval.Minimum = 5; interval.Maximum = 10080; interval.Value = value.IntervalMinutes; interval.Width = 130; panel.Controls.Add(interval, 0, 3);
            startup.Text = "登录 Windows 后自动启动（收起到托盘）"; startup.AutoSize = true; startup.Checked = value.AutoStart; panel.Controls.Add(startup, 0, 4);
            panel.Controls.Add(new Label { Text = "排除名称或通配符（用分号分隔，应用于所有层级）", AutoSize = true }, 0, 5);
            excludes.Text = value.Exclusions; excludes.Multiline = true; excludes.Dock = DockStyle.Fill; panel.Controls.Add(excludes, 0, 6);
            panel.Controls.Add(new Label { Text = ".git 始终排除；链接目录不跟随；疑似密钥和 ≥100 MiB 文件会阻止本轮备份。", Dock = DockStyle.Fill, ForeColor = Color.DimGray }, 0, 7);
            FlowLayoutPanel tools = new FlowLayoutPanel { Dock = DockStyle.Fill };
            LinkLabel log = new LinkLabel { Text = "打开数据与日志", AutoSize = true, Margin = new Padding(0, 6, 20, 0) }; log.LinkClicked += delegate { Process.Start("explorer.exe", Git.Quote(root)); }; tools.Controls.Add(log);
            LinkLabel recover = new LinkLabel { Text = "处理远端备份冲突", AutoSize = true, Margin = new Padding(0, 6, 0, 0) }; recover.LinkClicked += delegate { DialogResult = DialogResult.Retry; }; tools.Controls.Add(recover); panel.Controls.Add(tools, 0, 8);
            FlowLayoutPanel buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
            Button save = new Button { Text = "保存设置", Width = 110, Height = 32, BackColor = Color.FromArgb(49, 99, 233), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
            save.Click += delegate { try { Value.Repository = Engine.ValidateRemote(remote.Text.Trim()); Value.IntervalMinutes = (int)interval.Value; Value.AutoStart = startup.Checked; Value.Exclusions = excludes.Text; DialogResult = DialogResult.OK; } catch (Exception error) { MessageBox.Show(error.Message); } };
            buttons.Controls.Add(save); buttons.Controls.Add(new Button { Text = "取消", Width = 90, Height = 32, DialogResult = DialogResult.Cancel }); panel.Controls.Add(buttons, 0, 9);
            Controls.Add(panel); AcceptButton = save;
        }
    }
}
