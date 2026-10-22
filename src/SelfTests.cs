using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text;
using System.Diagnostics;

namespace CloudBackup {
    public static class SelfTests {
        static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
        public static int Run(string directory) {
            string root = Path.Combine(Path.GetFullPath(directory), "run-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            List<string> passed = new List<string>();
            try {
                string source = Path.Combine(root, "源目录 with spaces"), second = Path.Combine(root, "second"), remote = Path.Combine(root, "remote.git");
                Directory.CreateDirectory(source); Directory.CreateDirectory(second);
                File.WriteAllBytes(Path.Combine(source, "中文.txt"), Encoding.UTF8.GetBytes("first\r\nline\r\n"));
                File.WriteAllText(Path.Combine(second, "remove.txt"), "keep-original");
                Directory.CreateDirectory(Path.Combine(source, ".git")); File.WriteAllText(Path.Combine(source, ".git", "secret"), "must-not-upload");
                Directory.CreateDirectory(Path.Combine(source, "node_modules")); File.WriteAllText(Path.Combine(source, "node_modules", "skip.js"), "skip");
                File.WriteAllText(Path.Combine(source, ".gitignore"), "*.txt\n");
                Store store = new Store(Path.Combine(root, "state")); Git git = new Git(store.Root);
                git.Must(root, "init", "--bare", "--initial-branch=main", remote);
                Settings settings = new Settings { Repository = remote };
                settings.Folders.Add(new FolderJob { Name = "中文目录", Source = source });
                settings.Folders.Add(new FolderJob { Name = "second", Source = second });
                string empty = Path.Combine(root, "empty"); Directory.CreateDirectory(Path.Combine(empty, "nested-empty"));
                settings.Folders.Add(new FolderJob { Name = "empty", Source = empty });
                Engine engine = new Engine(store) { AllowLocalRemoteForTests = true };
                store.Save(settings); store.Save(settings);
                Assert(store.Load().Folders.Count == 3 && File.Exists(store.Config + ".previous"), "Settings atomic replacement failed."); passed.Add("atomic settings persistence");
                Settings emptyOnly = settings.Copy(); emptyOnly.Folders = emptyOnly.Folders.Where(j => j.Source == empty).ToList();
                Assert(engine.Backup(emptyOnly).Pushed, "First backup containing only empty directories failed."); passed.Add("empty remote with only empty source directories");
                RunResult first = engine.Backup(settings);
                Assert(first.Changed && first.Pushed, "Empty remote initial backup failed."); passed.Add("empty remote + multi-directory first push");
                string emptyManifest = git.Must(root, "--git-dir", remote, "show", "HEAD:backup-manifests/" + settings.Folders[2].Id + ".json");
                Assert(emptyManifest.Contains("nested-empty"), "Empty directory structure was lost."); passed.Add("empty and nested-empty directories preserved in manifest");
                string path = settings.Folders[0].Target + "/中文.txt";
                Assert(git.Must(root, "--git-dir", remote, "show", "HEAD:" + path) == "first\r\nline", "Byte/newline preservation failed.");
                string files = git.Must(root, "--git-dir", remote, "ls-tree", "-r", "--name-only", "HEAD");
                Assert(!files.Contains("secret") && !files.Contains("skip.js") && files.Contains("中文.txt"), "Exclusions or source .gitignore isolation failed.");
                Assert(!Directory.Exists(Path.Combine(second, ".git")) && File.ReadAllText(Path.Combine(second, "remove.txt")) == "keep-original", "Source was modified.");
                passed.Add("source read-only + .git exclusion + gitignore independence + Chinese paths");
                string initial = git.Must(root, "--git-dir", remote, "rev-parse", "HEAD");
                RunResult unchanged = engine.Backup(settings);
                Assert(!unchanged.Changed && !unchanged.Pushed && git.Must(root, "--git-dir", remote, "rev-parse", "HEAD") == initial, "Unchanged run created or pushed a commit.");
                File.SetLastWriteTimeUtc(Path.Combine(source, "中文.txt"), DateTime.UtcNow.AddMinutes(2));
                Assert(!engine.Backup(settings).Changed, "Timestamp-only change created commit."); passed.Add("no-change and timestamp-only skip");
                string junction = Path.Combine(source, "outside-link");
                using (Process link = Process.Start(new ProcessStartInfo("cmd.exe", "/c mklink /J " + Git.Quote(junction) + " " + Git.Quote(second)) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })) {
                    link.StandardOutput.ReadToEnd(); link.StandardError.ReadToEnd(); link.WaitForExit();
                    Assert(link.ExitCode == 0, "Test junction creation failed.");
                }
                Assert(!engine.Backup(settings).Changed, "Junction was followed into another directory."); passed.Add("directory junction skipped without following target");
                File.WriteAllText(Path.Combine(source, "中文.txt"), "updated"); File.Delete(Path.Combine(second, "remove.txt")); File.WriteAllText(Path.Combine(second, "new.txt"), "new");
                Assert(engine.Backup(settings).Pushed, "Changed backup failed.");
                files = git.Must(root, "--git-dir", remote, "ls-tree", "-r", "--name-only", "HEAD");
                Assert(files.Contains("new.txt") && !files.Contains("remove.txt"), "File deletion/addition was not mirrored."); passed.Add("addition modification deletion + retained Git history");
                File.WriteAllText(Path.Combine(source, "中文.txt"), "transaction-content");
                foreach (string failure in new[] { "before-add", "before-commit" }) {
                    engine.FaultForTests = delegate(string phase) { if (phase == failure) throw new IOException("injected failure"); };
                    bool failed = false; try { engine.Backup(settings); } catch (IOException) { failed = true; }
                    Assert(failed && git.Must(engine.CacheFor(remote), "status", "--porcelain") == "", "Failed snapshot left cache or index dirty.");
                }
                engine.FaultForTests = null; Assert(engine.Backup(settings).Pushed, "Backup did not recover after add/commit failure."); passed.Add("add and commit failure roll back cache and index");
                string cache = engine.CacheFor(remote), interrupted = Path.Combine(store.Root, "staging", "simulated-interruption"); Directory.CreateDirectory(interrupted);
                string originalTarget = Path.Combine(cache, settings.Folders[0].Target.Replace('/', Path.DirectorySeparatorChar)), oldTarget = Path.Combine(interrupted, "old");
                SnapshotJournal recovery = new SnapshotJournal { Repo = cache, Baseline = git.Must(cache, "rev-parse", "HEAD"), HadIndex = true, Changes = new List<SnapshotChange> {
                    new SnapshotChange { Relative = settings.Folders[0].Target, Incoming = Path.Combine(interrupted, "new"), Old = oldTarget, HadOld = true, Directory = true } } };
                File.Copy(Path.Combine(cache, ".git", "index"), Path.Combine(interrupted, "index.previous"));
                File.WriteAllText(Path.Combine(interrupted, "journal.json"), Json.Write(recovery));
                Paths.Child(originalTarget, store.Root); Paths.Child(oldTarget, store.Root); Directory.Move(originalTarget, oldTarget);
                Directory.CreateDirectory(originalTarget); File.WriteAllText(Path.Combine(originalTarget, "partial.txt"), "not-complete");
                File.WriteAllText(Path.Combine(cache, ".git", "index.lock"), "abandoned-test-lock");
                string orphan = Path.Combine(store.Root, "staging", "orphan-before-journal"); Directory.CreateDirectory(orphan); File.WriteAllText(Path.Combine(orphan, "partial-copy.txt"), "partial");
                engine.RecoverTransactions(cache);
                Assert(git.Must(cache, "status", "--porcelain") == "" && !Directory.Exists(interrupted), "Interrupted transaction was not recovered.");
                Assert(!Directory.Exists(orphan) && !File.Exists(Path.Combine(cache, ".git", "index.lock")), "Abandoned staging or stale lock was not cleaned."); passed.Add("abandoned staging and stale Git index lock recovered");
                Assert(!engine.Backup(settings).Changed, "Recovery created an unnecessary commit."); passed.Add("interrupted snapshot transaction recovered on next run");
                string cleanHead = git.Must(root, "--git-dir", remote, "rev-parse", "HEAD");
                File.WriteAllText(Path.Combine(source, ".env"), "fake=value");
                bool blocked = false; try { engine.Backup(settings); } catch (IOException) { blocked = true; }
                Assert(blocked && git.Must(root, "--git-dir", remote, "rev-parse", "HEAD") == cleanHead, "Sensitive filename leaked."); File.Delete(Path.Combine(source, ".env"));
                File.WriteAllText(Path.Combine(source, "fake-key.txt"), "sk-" + new string('a', 40));
                blocked = false; try { engine.Backup(settings); } catch (IOException) { blocked = true; }
                Assert(blocked, "Sensitive content was not blocked."); File.Delete(Path.Combine(source, "fake-key.txt")); passed.Add("sensitive filename and content blocking");
                using (FileStream big = File.Create(Path.Combine(source, "too-big.bin"))) big.SetLength(100L * 1024 * 1024);
                blocked = false; try { engine.Backup(settings); } catch (IOException) { blocked = true; }
                Assert(blocked, "Oversized file was not blocked."); File.Delete(Path.Combine(source, "too-big.bin")); passed.Add("100 MiB file blocking before commit");
                using (FileStream locked = new FileStream(Path.Combine(second, "new.txt"), FileMode.Open, FileAccess.Read, FileShare.None)) {
                    blocked = false; try { engine.Backup(settings); } catch (IOException) { blocked = true; }
                    Assert(blocked && git.Must(root, "--git-dir", remote, "rev-parse", "HEAD") == cleanHead, "Locked file produced a partial backup.");
                }
                passed.Add("locked file prevents partial snapshot commit");
                bool moved = false; string offline = Path.Combine(root, "remote-offline.git");
                File.WriteAllText(Path.Combine(second, "new.txt"), "pending-push");
                engine.Progress = delegate(string message) { if (!moved && message.Contains("正在推送")) { Paths.Child(remote, root); Paths.Child(offline, root); Directory.Move(remote, offline); moved = true; } };
                blocked = false; try { engine.Backup(settings); } catch (IOException) { blocked = true; }
                finally { if (moved) Directory.Move(offline, remote); engine.Progress = null; }
                Assert(blocked && moved, "Push failure simulation did not execute.");
                RunResult retry = engine.Backup(settings);
                Assert(retry.Pushed && !retry.Changed, "Pending commit was not retried without duplicate commit."); passed.Add("failed push preserved + automatic pending retry");
                string peer = Path.Combine(root, "peer"); git.Must(root, "clone", remote, peer);
                git.Must(peer, "config", "user.name", "Test"); git.Must(peer, "config", "user.email", "test@example.invalid");
                File.WriteAllText(Path.Combine(peer, "README.md"), "remote-managed-content"); git.Must(peer, "add", "README.md"); git.Must(peer, "commit", "-m", "remote change"); git.Must(peer, "push", "origin", "main");
                Assert(!engine.Backup(settings).Changed, "Remote-only change generated backup commit.");
                Assert(git.Must(root, "--git-dir", remote, "show", "HEAD:README.md") == "remote-managed-content", "Remote unmanaged content changed."); passed.Add("remote fast-forward + unrelated remote content preserved");
                Settings bad = settings.Copy(); bad.Folders[0].Source = store.Root;
                blocked = false; try { engine.Backup(bad); } catch (IOException) { blocked = true; }
                Assert(blocked, "Recursive cache source was accepted.");
                bad.Folders[0].Source = Path.Combine(root, "missing"); blocked = false; try { engine.Backup(bad); } catch (IOException) { blocked = true; }
                Assert(blocked, "Missing directory was accepted.");
                blocked = false; try { Engine.ValidateRemote("https://user:secret@github.com/a/b.git"); } catch (IOException) { blocked = true; }
                Assert(blocked, "Credential-bearing URL accepted."); passed.Add("missing/recursive paths + credential URL rejection");
                File.WriteAllText(Path.Combine(second, "new.txt"), "local-conflict"); moved = false;
                engine.Progress = delegate(string message) { if (!moved && message.Contains("正在推送")) { Directory.Move(remote, offline); moved = true; } };
                try { engine.Backup(settings); } catch (IOException) { }
                finally { if (moved) Directory.Move(offline, remote); engine.Progress = null; }
                string pendingHead = git.Must(engine.CacheFor(remote), "rev-parse", "HEAD");
                git.Must(peer, "pull", "--ff-only");
                File.WriteAllText(Path.Combine(peer, settings.Folders[1].Target.Replace('/', Path.DirectorySeparatorChar), "new.txt"), "remote-conflict");
                git.Must(peer, "add", "--all"); git.Must(peer, "commit", "-m", "remote conflict"); git.Must(peer, "push", "origin", "main");
                string remoteHead = git.Must(root, "--git-dir", remote, "rev-parse", "HEAD");
                blocked = false; try { engine.Backup(settings); } catch (IOException) { blocked = true; }
                Assert(blocked && git.Must(root, "--git-dir", remote, "rev-parse", "HEAD") == remoteHead && git.Must(engine.CacheFor(remote), "rev-parse", "HEAD") == pendingHead,
                    "Conflict overwrote remote or lost pending local commit."); passed.Add("divergent conflict preserves both sides without force push");
                engine.PreserveConflict(settings);
                Assert(git.Must(cache, "for-each-ref", "--format=%(objectname)", "refs/backup-recovery/").Contains(pendingHead), "Conflict recovery lost pending commit.");
                Assert(engine.Backup(settings).Pushed && git.Must(root, "--git-dir", remote, "show", "HEAD:" + settings.Folders[1].Target + "/new.txt") == "local-conflict", "Explicit conflict recovery failed.");
                passed.Add("explicit conflict recovery retains old commit and resumes backup");
                string report = Json.Write(new { success = true, passed = passed, root = root });
                File.WriteAllText(Path.Combine(root, "result.json"), report, Encoding.UTF8); Console.WriteLine(report); return 0;
            } catch (Exception error) {
                string report = Json.Write(new { success = false, passed = passed, error = error.ToString(), root = root });
                File.WriteAllText(Path.Combine(root, "result.json"), report, Encoding.UTF8); Console.WriteLine(report); return 1;
            }
        }
    }
    public static class TestRunner {
        public static int Main(string[] args) { return SelfTests.Run(args.Length > 0 ? args[0] : Environment.CurrentDirectory); }
    }
}
