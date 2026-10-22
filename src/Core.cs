using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace CloudBackup {
    public sealed class FolderJob {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Source { get; set; }
        public bool Enabled { get; set; }
        public string Status { get; set; }
        public string LastCheck { get; set; }
        public FolderJob() { Id = Guid.NewGuid().ToString("N"); Enabled = true; Status = "等待备份"; }
        public string Target { get { return "backups/" + Id; } }
    }
    public sealed class Settings {
        public string Repository { get; set; }
        public int IntervalMinutes { get; set; }
        public bool AutoStart { get; set; }
        public bool Paused { get; set; }
        public string Exclusions { get; set; }
        public string NextRunUtc { get; set; }
        public string LastPush { get; set; }
        public List<FolderJob> Folders { get; set; }
        public Settings() {
            Repository = "https://github.com/ZZZZZZYKBear/my-record.git";
            IntervalMinutes = 60; AutoStart = true; Paused = false;
            Exclusions = "node_modules;.venv;venv;__pycache__;.DS_Store;Thumbs.db;*.tmp;~$*";
            NextRunUtc = DateTime.UtcNow.AddHours(1).ToString("o");
            Folders = new List<FolderJob>();
        }
        public Settings Copy() { return Json.Read<Settings>(Json.Write(this)); }
    }
    public static class Json {
        public static string Write(object value) { return new JavaScriptSerializer { MaxJsonLength = 8388608 }.Serialize(value); }
        public static T Read<T>(string value) { return new JavaScriptSerializer { MaxJsonLength = 8388608 }.Deserialize<T>(value); }
    }
    public sealed class Store {
        public readonly string Root;
        public string Config { get { return Path.Combine(Root, "settings.json"); } }
        public string LogFile { get { return Path.Combine(Root, "logs", "app.log"); } }
        readonly object gate = new object();
        public Store(string root) { Root = Path.GetFullPath(root); Directory.CreateDirectory(Root); }
        public Settings Load() {
            if (!File.Exists(Config)) return new Settings();
            Settings value = Json.Read<Settings>(File.ReadAllText(Config, Encoding.UTF8));
            if (value == null || value.Folders == null || value.IntervalMinutes < 5 || value.IntervalMinutes > 10080)
                throw new InvalidDataException("设置文件无效，已保留原文件：" + Config);
            foreach (FolderJob job in value.Folders) {
                if (!Regex.IsMatch(job.Id ?? "", "^[a-f0-9]{32}$")) throw new InvalidDataException("备份目录编号无效。");
            }
            return value;
        }
        public void Save(Settings settings) {
            lock (gate) {
                string temp = Config + ".tmp";
                File.WriteAllText(temp, Json.Write(settings), new UTF8Encoding(false));
                if (File.Exists(Config)) File.Replace(temp, Config, Config + ".previous", true);
                else File.Move(temp, Config);
            }
        }
        public void Log(string message) {
            try { lock (gate) {
                Directory.CreateDirectory(Path.GetDirectoryName(LogFile));
                if (File.Exists(LogFile) && new FileInfo(LogFile).Length > 2097152) {
                    string previous = LogFile + ".previous";
                    if (File.Exists(previous)) File.Delete(previous);
                    File.Move(LogFile, previous);
                }
                File.AppendAllText(LogFile, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + Sanitize(message) + Environment.NewLine, Encoding.UTF8);
            } } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        public static string Sanitize(string text) {
            text = Regex.Replace(text ?? "", @"https://[^\s/@]+:[^\s/@]+@", "https://[已隐藏]@");
            return Regex.Replace(text, @"\b(?:gh[pousr]_[A-Za-z0-9_]{15,}|github_pat_[A-Za-z0-9_]+|sk-[A-Za-z0-9_-]{20,})", "[凭据已隐藏]");
        }
    }
    public static class Paths {
        public static string Full(string path) { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        public static bool Inside(string path, string parent) { return Full(path).StartsWith(Full(parent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase); }
        public static bool Equal(string a, string b) { return String.Equals(Full(a), Full(b), StringComparison.OrdinalIgnoreCase); }
        public static void Child(string path, string root) {
            if (!Inside(path, root)) throw new IOException("拒绝操作应用工作目录以外的路径。");
            string current = Full(path);
            while (Inside(current, root)) {
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("工作路径包含链接，已停止：" + current);
                current = Path.GetDirectoryName(current);
            }
        }
        public static void DeleteTree(string path, string root) {
            Child(path, root);
            if (!Directory.Exists(path)) return;
            // Never follow a junction, including one introduced into a cache.
            foreach (string file in Directory.GetFiles(path)) { if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0) File.SetAttributes(file, FileAttributes.Normal); File.Delete(file); }
            foreach (string directory in Directory.GetDirectories(path)) {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) Directory.Delete(directory);
                else DeleteTree(directory, root);
            }
            Directory.Delete(path);
        }
        public static void Source(string source, string state) {
            string full = Full(source);
            if (!Directory.Exists(full)) throw new IOException("目录不可用（可能已移动或磁盘离线）：" + full);
            if (Equal(full, Path.GetPathRoot(full))) throw new IOException("请选择具体文件夹，不支持整个磁盘。");
            if (Equal(full, state) || Inside(full, state) || Inside(state, full)) throw new IOException("备份目录不能与助手的数据目录重叠。");
            if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) throw new IOException("请选择实际目录，不能选择符号链接或目录联接。");
        }
    }
    public sealed class GitResult {
        public int Code; public string Text;
    }
    public sealed class Git {
        public readonly string Executable;
        readonly string hooks;
        public Git(string root) {
            hooks = Path.Combine(root, "empty-hooks"); Directory.CreateDirectory(hooks);
            string[] guesses = {
                Environment.GetEnvironmentVariable("BACKUP_GIT_EXE"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "cmd", "git.exe"),
                @"D:\Mysoftware\Git\cmd\git.exe"
            };
            Executable = guesses.FirstOrDefault(p => !String.IsNullOrEmpty(p) && File.Exists(p));
            if (Executable == null) {
                foreach (string folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';')) {
                    try { string path = Path.Combine(folder.Trim('"'), "git.exe"); if (File.Exists(path)) { Executable = path; break; } } catch { }
                }
            }
            if (Executable == null) throw new IOException("未找到 Git for Windows。请安装 Git 后重启助手。");
        }
        public static string Quote(string value) {
            StringBuilder result = new StringBuilder("\""); int slashes = 0;
            foreach (char c in value) {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') result.Append('\\', slashes * 2 + 1).Append('"');
                else result.Append('\\', slashes).Append(c);
                slashes = 0;
            }
            return result.Append('\\', slashes * 2).Append('"').ToString();
        }
        public GitResult Run(string cwd, params string[] args) { return Run(cwd, false, args); }
        public GitResult Run(string cwd, bool interactive, params string[] args) {
            List<string> options = new List<string> { "-c", "core.hooksPath=" + hooks, "-c", "core.quotePath=false" };
            if (!interactive) options.AddRange(new[] { "-c", "credential.interactive=never" });
            options.AddRange(args);
            ProcessStartInfo info = new ProcessStartInfo(Executable, String.Join(" ", options.Select(Quote))) {
                WorkingDirectory = cwd, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            // Git plumbing always runs in our cache, with no ambient index/dir overrides.
            foreach (string key in new[] { "GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_OBJECT_DIRECTORY", "GIT_ALTERNATE_OBJECT_DIRECTORIES" }) info.EnvironmentVariables.Remove(key);
            info.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
            info.EnvironmentVariables["GCM_INTERACTIVE"] = interactive ? "Always" : "Never";
            using (Process process = Process.Start(info)) {
                Task<string> stdout = process.StandardOutput.ReadToEndAsync();
                Task<string> stderr = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(interactive ? 300000 : 180000)) {
                    try {
                        string killer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "taskkill.exe");
                        using (Process kill = Process.Start(new ProcessStartInfo(killer, "/PID " + process.Id + " /T /F") { UseShellExecute = false, CreateNoWindow = true })) kill.WaitForExit(10000);
                        process.WaitForExit(10000);
                    } catch { try { process.Kill(); process.WaitForExit(10000); } catch { } }
                    throw new IOException("Git 操作超时。网络恢复后可重试，已有提交会保留。");
                }
                Task.WaitAll(stdout, stderr);
                return new GitResult { Code = process.ExitCode, Text = Store.Sanitize(stdout.Result + stderr.Result) };
            }
        }
        public string Must(string cwd, params string[] args) {
            GitResult result = Run(cwd, args);
            if (result.Code != 0) throw new IOException("Git 操作失败：" + result.Text.Trim());
            return result.Text.Trim();
        }
    }
    public sealed class Snapshot {
        public string Path; public long Bytes; public int Files; public int Skipped;
        public List<string> Directories = new List<string>();
    }
    public sealed class SnapshotChange {
        public string Relative { get; set; }
        public string Incoming { get; set; }
        public string Old { get; set; }
        public bool HadOld { get; set; }
        public bool Directory { get; set; }
    }
    public sealed class SnapshotJournal {
        public string Repo { get; set; }
        public string Baseline { get; set; }
        public bool HadIndex { get; set; }
        public List<SnapshotChange> Changes { get; set; }
    }
    public sealed class RunResult {
        public bool Pushed; public bool Changed; public int Files; public int Skipped; public string Message;
    }
    public sealed class Engine {
        readonly Store store; readonly Git git;
        public bool AllowLocalRemoteForTests;
        public Action<string> FaultForTests;
        public Action<string> Progress;
        public Engine(Store store) { this.store = store; git = new Git(store.Root); }
        void Report(string message) { store.Log(message); if (Progress != null) Progress(message); }
        public static string ValidateRemote(string url) {
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri) || uri.Scheme != "https" || uri.Host != "github.com" ||
                uri.Port != 443 || uri.UserInfo != "" || uri.Query != "" || uri.Fragment != "" ||
                !Regex.IsMatch(uri.AbsolutePath, @"^/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+(?:\.git)?/?$"))
                throw new IOException("请输入不含令牌的 GitHub HTTPS 仓库地址，例如 https://github.com/owner/repo.git。");
            return url.TrimEnd('/');
        }
        public static string Key(string value) {
            using (SHA256 hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "").Substring(0, 20).ToLowerInvariant();
        }
        bool Excluded(string name, string patterns) {
            if (name.Equals(".git", StringComparison.OrdinalIgnoreCase) || name.Equals(".svn", StringComparison.OrdinalIgnoreCase) || name.Equals(".hg", StringComparison.OrdinalIgnoreCase)) return true;
            foreach (string pattern in (patterns ?? "").Split(new[] { ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)) {
                string regex = "^" + Regex.Escape(pattern.Trim()).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
                if (Regex.IsMatch(name, regex, RegexOptions.IgnoreCase)) return true;
            }
            return false;
        }
        void CheckSensitive(string path, string relative) {
            string name = Path.GetFileName(path).ToLowerInvariant();
            bool example = name.EndsWith(".example") || name.EndsWith(".sample") || name.EndsWith(".template");
            if ((!example && (name == ".env" || name.StartsWith(".env."))) ||
                new[] { "id_rsa", "id_ed25519", "id_ecdsa", "credentials", "credentials.json" }.Contains(name) ||
                new[] { ".pfx", ".p12", ".key", ".pem" }.Contains(Path.GetExtension(name)))
                throw new IOException("发现可能包含凭据的文件，本轮未提交：" + relative + "。可在设置中将不需要备份的文件加入排除规则。");
            FileInfo file = new FileInfo(path);
            if (file.Length >= 100L * 1024 * 1024) throw new IOException("单文件达到 100 MiB，GitHub 普通仓库不接收：" + relative);
            if (file.Length <= 2 * 1024 * 1024) {
                byte[] bytes = File.ReadAllBytes(path);
                if (!bytes.Contains((byte)0)) {
                    string text = Encoding.UTF8.GetString(bytes);
                    if (Regex.IsMatch(text, @"-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----|\bgh[pousr]_[A-Za-z0-9_]{20,}|\bgithub_pat_[A-Za-z0-9_]{25,}|\bsk-[A-Za-z0-9_-]{24,}"))
                        throw new IOException("检测到疑似密钥内容，本轮未提交：" + relative);
                }
            }
        }
        public Snapshot Capture(FolderJob job, string stage, string exclusions) {
            Paths.Source(job.Source, store.Root);
            Paths.Child(stage, store.Root);
            Snapshot snapshot = new Snapshot { Path = stage };
            Directory.CreateDirectory(stage);
            CaptureDirectory(Paths.Full(job.Source), stage, "", exclusions, snapshot);
            return snapshot;
        }
        void CaptureDirectory(string source, string target, string relative, string exclusions, Snapshot snapshot) {
            if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) { snapshot.Skipped++; return; }
            snapshot.Directories.Add(relative);
            foreach (string entry in Directory.GetFileSystemEntries(source)) {
                string name = Path.GetFileName(entry), rel = relative == "" ? name : relative + "/" + name;
                FileAttributes attributes = File.GetAttributes(entry);
                if (Excluded(name, exclusions) || (attributes & FileAttributes.ReparsePoint) != 0) { snapshot.Skipped++; continue; }
                string dest = Path.Combine(target, name);
                if ((attributes & FileAttributes.Directory) != 0) {
                    Directory.CreateDirectory(dest); CaptureDirectory(entry, dest, rel, exclusions, snapshot);
                } else {
                    bool stable = false;
                    for (int retry = 0; retry < 3; retry++) {
                        FileInfo before = new FileInfo(entry); long size = before.Length; DateTime time = before.LastWriteTimeUtc;
                        if (size >= 100L * 1024 * 1024) throw new IOException("单文件达到 100 MiB：" + rel);
                        using (FileStream input = new FileStream(entry, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        using (FileStream output = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None)) input.CopyTo(output);
                        FileInfo after = new FileInfo(entry);
                        if (size == after.Length && time == after.LastWriteTimeUtc && new FileInfo(dest).Length == size) { stable = true; break; }
                        Thread.Sleep(100);
                    }
                    if (!stable) throw new IOException("文件持续变化，无法完成一致复制：" + rel);
                    CheckSensitive(dest, rel);
                    snapshot.Files++; snapshot.Bytes += new FileInfo(dest).Length;
                }
            }
        }
        public string CacheFor(string remote) { return Path.Combine(store.Root, "repositories", Key(remote), "repo"); }
        void ConfigureCache(string repo) {
            git.Must(repo, "config", "core.autocrlf", "false");
            git.Must(repo, "config", "core.longpaths", "true");
            git.Must(repo, "config", "user.name", "GitHub Backup Assistant");
            git.Must(repo, "config", "user.email", "backup-assistant@users.noreply.github.com");
            string info = Path.Combine(repo, ".git", "info"); Directory.CreateDirectory(info);
            File.WriteAllText(Path.Combine(info, "attributes"), "* -text -filter -ident -working-tree-encoding\n", new UTF8Encoding(false));
        }
        string EnsureRepo(string remote) {
            string repo = CacheFor(remote); Paths.Child(repo, store.Root);
            if (!Directory.Exists(Path.Combine(repo, ".git"))) {
                string parent = Path.GetDirectoryName(repo); Directory.CreateDirectory(parent);
                string pending = Path.Combine(parent, "clone-" + Guid.NewGuid().ToString("N"));
                Paths.Child(pending, store.Root);
                try {
                    git.Must(parent, "clone", "--no-checkout", "--no-hardlinks", "--", remote, pending);
                    ConfigureCache(pending);
                    if (git.Run(pending, "rev-parse", "--verify", "HEAD").Code == 0) git.Must(pending, "reset", "--hard", "HEAD");
                    else git.Must(pending, "symbolic-ref", "HEAD", "refs/heads/main");
                    if (Directory.Exists(repo)) throw new IOException("缓存目录已存在但不是有效仓库，已保留供检查：" + repo);
                    Directory.Move(pending, repo);
                } finally { if (Directory.Exists(pending)) Paths.DeleteTree(pending, store.Root); }
            }
            Paths.Child(Path.Combine(repo, ".git"), store.Root);
            ConfigureCache(repo);
            string actual = git.Must(repo, "remote", "get-url", "origin");
            if (!String.Equals(actual, remote, StringComparison.Ordinal)) throw new IOException("缓存仓库的远端地址不匹配，已停止。");
            return repo;
        }
        bool HasHead(string repo) { return git.Run(repo, "rev-parse", "--verify", "HEAD").Code == 0; }
        string Head(string repo) { return HasHead(repo) ? git.Must(repo, "rev-parse", "HEAD") : ""; }
        public void RecoverTransactions(string repo) {
            ClearAbandonedLock(repo);
            string staging = Path.Combine(store.Root, "staging");
            if (!Directory.Exists(staging)) return;
            foreach (string directory in Directory.GetDirectories(staging)) {
                Paths.Child(directory, store.Root);
                string journalFile = Path.Combine(directory, "journal.json");
                if (!File.Exists(journalFile)) { Paths.DeleteTree(directory, store.Root); continue; }
                SnapshotJournal journal = Json.Read<SnapshotJournal>(File.ReadAllText(journalFile, Encoding.UTF8));
                if (journal == null || !Paths.Equal(journal.Repo, repo)) continue;
                RecoverJournal(directory, journal);
            }
        }
        void ClearAbandonedLock(string repo) {
            string indexLock = Path.Combine(repo, ".git", "index.lock"); Paths.Child(indexLock, store.Root);
            if (!File.Exists(indexLock)) return;
            // The app serializes operations. Be conservative about external or
            // orphaned Git processes before removing a stale lock in our cache.
            foreach (Process process in Process.GetProcesses()) {
                try {
                    string name = process.ProcessName;
                    if (name.Equals("git", StringComparison.OrdinalIgnoreCase) || name.StartsWith("git-", StringComparison.OrdinalIgnoreCase))
                        throw new IOException("发现 Git 仍在运行，暂未清理缓存索引锁；操作结束后可重试。");
                } catch (System.ComponentModel.Win32Exception) { }
                finally { process.Dispose(); }
            }
            using (FileStream exclusive = new FileStream(indexLock, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            File.Delete(indexLock);
            Report("已清理异常中断留下的缓存索引锁。");
        }
        void RemoveEntry(string path, bool directory) {
            Paths.Child(path, store.Root);
            if (directory) Paths.DeleteTree(path, store.Root);
            else if (File.Exists(path)) File.Delete(path);
        }
        void RecoverJournal(string stage, SnapshotJournal journal) {
            Paths.Child(stage, store.Root); Paths.Child(journal.Repo, store.Root);
            ClearAbandonedLock(journal.Repo);
            if (Head(journal.Repo) != journal.Baseline) { Paths.DeleteTree(stage, store.Root); return; }
            string index = Path.Combine(journal.Repo, ".git", "index"); Paths.Child(index, store.Root);
            if (File.Exists(index + ".lock")) throw new IOException("Git 索引仍被占用，事务记录已保留；请稍后重试。");
            foreach (SnapshotChange change in journal.Changes) {
                if (!Regex.IsMatch(change.Relative, @"^(?:backups/[a-f0-9]{32}|backup-manifests/[a-f0-9]{32}\.json)$")) throw new IOException("事务目标路径无效。");
                string target = Path.Combine(journal.Repo, change.Relative.Replace('/', Path.DirectorySeparatorChar));
                Paths.Child(target, store.Root); Paths.Child(change.Old, stage);
                bool oldExists = change.Directory ? Directory.Exists(change.Old) : File.Exists(change.Old);
                if (oldExists) {
                    RemoveEntry(target, change.Directory);
                    if (change.Directory) Directory.Move(change.Old, target); else File.Move(change.Old, target);
                } else if (!change.HadOld) RemoveEntry(target, change.Directory);
            }
            if (journal.HadIndex) File.Copy(Path.Combine(stage, "index.previous"), index, true);
            else if (File.Exists(index)) File.Delete(index);
            Paths.DeleteTree(stage, store.Root);
        }
        SnapshotJournal PrepareJournal(string repo, string stage, List<FolderJob> jobs, List<Snapshot> snapshots) {
            SnapshotJournal journal = new SnapshotJournal { Repo = repo, Baseline = Head(repo), Changes = new List<SnapshotChange>() };
            string index = Path.Combine(repo, ".git", "index"); Paths.Child(index, store.Root);
            if (File.Exists(index + ".lock")) throw new IOException("Git 索引被占用，请稍后重试。");
            journal.HadIndex = File.Exists(index); if (journal.HadIndex) File.Copy(index, Path.Combine(stage, "index.previous"));
            for (int i = 0; i < jobs.Count; i++) {
                string manifest = Path.Combine(stage, "manifest-" + jobs[i].Id + ".json");
                File.WriteAllText(manifest, Json.Write(new { format = 1, name = jobs[i].Name, directories = snapshots[i].Directories.OrderBy(p => p, StringComparer.Ordinal).ToArray() }), new UTF8Encoding(false));
                foreach (bool directory in new[] { true, false }) {
                    string relative = directory ? jobs[i].Target : "backup-manifests/" + jobs[i].Id + ".json";
                    string target = Path.Combine(repo, relative.Replace('/', Path.DirectorySeparatorChar)); Paths.Child(target, store.Root);
                    if (directory && File.Exists(target) || !directory && Directory.Exists(target)) throw new IOException("远端备份路径类型不匹配：" + relative);
                    journal.Changes.Add(new SnapshotChange { Relative = relative, Directory = directory,
                        Incoming = directory ? snapshots[i].Path : manifest, Old = Path.Combine(stage, "old-" + journal.Changes.Count),
                        HadOld = directory ? Directory.Exists(target) : File.Exists(target) });
                }
            }
            string journalFile = Path.Combine(stage, "journal.json");
            File.WriteAllText(journalFile + ".tmp", Json.Write(journal), new UTF8Encoding(false));
            File.Move(journalFile + ".tmp", journalFile);
            return journal;
        }
        void ApplyJournal(SnapshotJournal journal) {
            foreach (SnapshotChange change in journal.Changes) {
                string target = Path.Combine(journal.Repo, change.Relative.Replace('/', Path.DirectorySeparatorChar));
                Paths.Child(target, store.Root); Paths.Child(change.Old, store.Root); Paths.Child(change.Incoming, store.Root);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                if (change.HadOld) { if (change.Directory) Directory.Move(target, change.Old); else File.Move(target, change.Old); }
                if (change.Directory) Directory.Move(change.Incoming, target); else File.Move(change.Incoming, target);
            }
        }
        void Reconcile(string repo, string branch) {
            if (git.Must(repo, "status", "--porcelain").Trim() != "") throw new IOException("缓存中有尚未提交的内容，已保留；请打开数据目录检查，避免覆盖。");
            git.Must(repo, "fetch", "origin");
            string remoteRef = "refs/remotes/origin/" + branch;
            if (git.Run(repo, "rev-parse", "--verify", remoteRef).Code != 0) return;
            if (!HasHead(repo)) { git.Must(repo, "reset", "--hard", remoteRef); return; }
            GitResult clean = git.Run(repo, "status", "--porcelain");
            if (clean.Code != 0 || clean.Text.Trim() != "") throw new IOException("缓存中有尚未提交的快照，已保留；请打开日志检查，避免覆盖。");
            GitResult rebase = git.Run(repo, "rebase", "origin/" + branch);
            if (rebase.Code != 0) {
                GitResult abort = git.Run(repo, "rebase", "--abort");
                if (abort.Code != 0) throw new IOException("远端冲突且 Git 无法退出变基，已有数据均已保留；请检查缓存：" + repo);
                throw new IOException("远端与本地备份发生冲突，本地提交已保留，没有强制推送。请检查缓存仓库：" + repo);
            }
        }
        bool PushPending(string repo, string branch) {
            if (!HasHead(repo)) return false;
            string remoteRef = "refs/remotes/origin/" + branch;
            string ahead = git.Run(repo, "rev-parse", "--verify", remoteRef).Code == 0 ? git.Must(repo, "rev-list", "--count", remoteRef + "..HEAD") : "1";
            if (ahead == "0") return false;
            Report("正在推送备份提交…");
            git.Must(repo, "push", "--porcelain", "origin", "HEAD:refs/heads/" + branch);
            return true;
        }
        public RunResult Backup(Settings settings) {
            string remote = AllowLocalRemoteForTests ? settings.Repository : ValidateRemote(settings.Repository);
            List<FolderJob> jobs = settings.Folders.Where(j => j.Enabled).ToList();
            if (jobs.Count == 0) return new RunResult { Message = "请先添加并启用至少一个备份目录。" };
            foreach (FolderJob job in jobs) {
                if (!Regex.IsMatch(job.Id ?? "", "^[a-f0-9]{32}$")) throw new IOException("目录编号无效。");
                Paths.Source(job.Source, store.Root);
            }
            Report("检查仓库和待上传提交…");
            string repo = EnsureRepo(remote);
            RecoverTransactions(repo);
            string branch = git.Must(repo, "symbolic-ref", "--short", "HEAD");
            Reconcile(repo, branch);
            bool pushed = PushPending(repo, branch);
            string stageRoot = Path.Combine(store.Root, "staging", Guid.NewGuid().ToString("N"));
            List<Snapshot> snapshots = new List<Snapshot>();
            try {
                foreach (FolderJob job in jobs) {
                    Report("扫描目录：" + job.Name);
                    snapshots.Add(Capture(job, Path.Combine(stageRoot, job.Id), settings.Exclusions));
                }
                SnapshotJournal journal = PrepareJournal(repo, stageRoot, jobs, snapshots);
                bool changed;
                try {
                    ApplyJournal(journal);
                    if (FaultForTests != null) FaultForTests("before-add");
                    List<string> add = new List<string> { "add", "-f", "-A", "--", "backups", "backup-manifests" };
                    git.Must(repo, add.ToArray());
                    GitResult diff = git.Run(repo, "diff", "--cached", "--quiet", "--exit-code");
                    if (diff.Code != 0 && diff.Code != 1) throw new IOException("无法检查快照变化：" + diff.Text);
                    changed = diff.Code == 1;
                    if (changed) {
                        Report("发现内容变化，正在创建备份提交…");
                        if (FaultForTests != null) FaultForTests("before-commit");
                        git.Must(repo, "commit", "-m", "Backup " + DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz") + " (" + jobs.Count + " folders)");
                    }
                } catch {
                    RecoverJournal(stageRoot, journal);
                    throw;
                }
                // The commit is durable (or the snapshot was unchanged). A later
                // push failure must keep this commit, never roll it back.
                Paths.DeleteTree(stageRoot, store.Root);
                if (changed) pushed = PushPending(repo, branch) || pushed;
                int skipped = snapshots.Sum(s => s.Skipped);
                string message = changed ? "备份已推送" : (pushed ? "已补传之前的备份" : "内容未变化，本次未提交、未推送");
                if (skipped > 0) message += "（排除/链接 " + skipped + " 项）";
                Report(message);
                return new RunResult { Changed = changed, Pushed = pushed, Files = snapshots.Sum(s => s.Files), Skipped = skipped, Message = message };
            } finally { if (Directory.Exists(stageRoot) && !File.Exists(Path.Combine(stageRoot, "journal.json"))) Paths.DeleteTree(stageRoot, store.Root); }
        }
        public void PreserveConflict(Settings settings) {
            string remote = AllowLocalRemoteForTests ? settings.Repository : ValidateRemote(settings.Repository);
            string repo = EnsureRepo(remote); RecoverTransactions(repo);
            if (git.Must(repo, "status", "--porcelain").Trim() != "") throw new IOException("缓存有未提交内容，不能执行冲突恢复，请先检查日志。");
            string branch = git.Must(repo, "symbolic-ref", "--short", "HEAD");
            git.Must(repo, "fetch", "origin");
            git.Must(repo, "rev-parse", "--verify", "refs/remotes/origin/" + branch);
            string recovery = "refs/backup-recovery/" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-ffff");
            git.Must(repo, "update-ref", recovery, "HEAD");
            // Only an app-owned cache is reset, after retaining a named ref.
            Paths.Child(repo, store.Root); git.Must(repo, "reset", "--hard", "origin/" + branch);
            Report("已保留冲突前的本地提交：" + recovery + "。下次将从当前源目录生成新快照。");
        }
        public string TestConnection(string remote) { return git.Must(store.Root, "ls-remote", "--symref", ValidateRemote(remote), "HEAD"); }
        public void Login() {
            GitResult result = git.Run(store.Root, true, "credential-manager", "github", "login", "--url", "https://github.com", "--browser");
            if (result.Code != 0) throw new IOException("GitHub 登录未完成：" + result.Text);
        }
    }
    public static class Startup {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        public static void Set(bool enabled) {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey)) {
                if (enabled) key.SetValue("GitHubBackupAssistant", Git.Quote(System.Windows.Forms.Application.ExecutablePath) + " --tray");
                else key.DeleteValue("GitHubBackupAssistant", false);
            }
        }
        public static bool Enabled() {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey)) return key != null && key.GetValue("GitHubBackupAssistant") != null;
        }
    }
}
