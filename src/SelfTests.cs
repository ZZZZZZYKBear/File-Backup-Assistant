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
                Store saveStore = new Store(Path.Combine(root, "save-regression"));
                Settings saveSettings = new Settings(); saveStore.Save(saveSettings); saveStore.Save(saveSettings);
                string previousConfig = saveStore.Config + ".previous";
                File.SetAttributes(previousConfig, FileAttributes.ReadOnly);
                saveSettings.Paused = true; saveStore.Save(saveSettings);
                Assert(saveStore.Load().Paused && Directory.GetFiles(saveStore.Root, "settings.json.previous-*").Length > 0, "Read-only previous backup prevented saving or lost recovery copy.");
                File.SetAttributes(previousConfig, FileAttributes.Normal);
                using (FileStream heldPrevious = new FileStream(previousConfig, FileMode.Open, FileAccess.Read, FileShare.None)) {
                    saveSettings.Paused = false; saveStore.Save(saveSettings);
                    Assert(!saveStore.Load().Paused, "Locked previous backup prevented saving.");
                }
                string liveConfig = File.ReadAllText(saveStore.Config);
                using (FileStream heldConfig = new FileStream(saveStore.Config, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                    saveSettings.Paused = true; bool saveBlocked = false;
                    try { saveStore.Save(saveSettings); } catch (IOException) { saveBlocked = true; }
                    Assert(saveBlocked && File.ReadAllText(saveStore.Config) == liveConfig, "Failed save damaged original settings.");
                }
                Assert(Directory.GetFiles(saveStore.Root, "*.tmp").Length == 0, "Failed save leaked a temporary config.");
                passed.Add("read-only and locked previous backups cannot block settings; live-file failure preserves original");
                saveStore.ForceJournalForTests = true; saveStore.Save(saveSettings);
                Assert(saveStore.Load().Paused && !File.Exists(saveStore.Config + ".recovery"), "Journaled save did not commit.");
                saveStore.SaveFaultForTests = delegate { throw new IOException("failure after live write"); };
                saveSettings.Paused = false; bool journalBlocked = false;
                try { saveStore.Save(saveSettings); } catch (IOException) { journalBlocked = true; }
                Assert(journalBlocked && saveStore.Load().Paused, "Failed journaled save did not restore the old config.");
                saveStore.SaveFaultForTests = null;
                byte[] beforeInterruptedSave = File.ReadAllBytes(saveStore.Config);
                File.WriteAllBytes(saveStore.Config + ".recovery", beforeInterruptedSave);
                File.WriteAllText(saveStore.Config, "{incomplete");
                Assert(new Store(saveStore.Root).Load().Paused && !File.Exists(saveStore.Config + ".recovery"), "Interrupted config save did not recover.");
                File.WriteAllBytes(saveStore.Config + ".recovery", beforeInterruptedSave);
                File.WriteAllText(saveStore.Config, Json.Write(saveSettings));
                Assert(new Store(saveStore.Root).Load().Paused, "Crash before marker deletion incorrectly committed a new config.");
                using (FileStream heldLive = new FileStream(saveStore.Config, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                    journalBlocked = false; try { saveStore.Save(saveSettings); } catch (IOException) { journalBlocked = true; }
                    Assert(journalBlocked && File.Exists(saveStore.Config + ".recovery") && File.ReadAllBytes(saveStore.Config).SequenceEqual(beforeInterruptedSave), "Locked journaled save damaged live config or lost undo record.");
                }
                Assert(new Store(saveStore.Root).Load().Paused && !File.Exists(saveStore.Config + ".recovery"), "Deferred config recovery failed after unlock.");
                passed.Add("redirected config saves commit safely, roll back failures and recover interrupted writes on restart");
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
                File.WriteAllText(Path.Combine(second, "healthy.txt"), "healthy-with-env");
                RunResult partial = engine.Backup(settings);
                files = git.Must(root, "--git-dir", remote, "ls-tree", "-r", "--name-only", "HEAD");
                Assert(partial.Pushed && partial.JobWarnings[settings.Folders[0].Id] == 1 && !files.Contains("/.env") && files.Contains("/healthy.txt"), "Sensitive file blocked healthy files or leaked.");
                Assert(partial.Message.Contains("跳过 1 项") && partial.Message.Contains("并非完整备份"), "Partial backup was not visibly reported.");
                Assert(!engine.Backup(settings).Changed, "Repeated skipped file generated a new commit."); File.Delete(Path.Combine(source, ".env"));
                File.WriteAllText(Path.Combine(source, "fake-key.txt"), "sk-" + new string('a', 40));
                File.WriteAllText(Path.Combine(second, "healthy.txt"), "healthy-with-key"); partial = engine.Backup(settings);
                files = git.Must(root, "--git-dir", remote, "ls-tree", "-r", "--name-only", "HEAD");
                Assert(partial.Pushed && partial.JobWarnings[settings.Folders[0].Id] == 1 && !files.Contains("/fake-key.txt") && git.Must(root, "--git-dir", remote, "show", "HEAD:" + settings.Folders[1].Target + "/healthy.txt") == "healthy-with-key", "Sensitive contents blocked other files or leaked.");
                File.Delete(Path.Combine(source, "fake-key.txt")); passed.Add("sensitive filenames and contents skipped while healthy files push; repeated skips do not commit");
                using (FileStream big = File.Create(Path.Combine(source, "too-big.bin"))) big.SetLength(100L * 1024 * 1024);
                File.WriteAllText(Path.Combine(second, "healthy.txt"), "healthy-with-big"); partial = engine.Backup(settings);
                files = git.Must(root, "--git-dir", remote, "ls-tree", "-r", "--name-only", "HEAD");
                Assert(partial.Pushed && partial.JobWarnings[settings.Folders[0].Id] == 1 && !files.Contains("/too-big.bin"), "Oversized file blocked backup or leaked."); File.Delete(Path.Combine(source, "too-big.bin")); passed.Add("100 MiB file skipped while other files push");
                File.WriteAllText(Path.Combine(second, "new.txt"), "locked-new-version");
                using (FileStream locked = new FileStream(Path.Combine(second, "new.txt"), FileMode.Open, FileAccess.Read, FileShare.None)) {
                    File.WriteAllText(Path.Combine(source, "中文.txt"), "healthy-with-lock"); partial = engine.Backup(settings);
                    Assert(partial.Pushed && partial.JobWarnings[settings.Folders[1].Id] == 1 && git.Must(root, "--git-dir", remote, "show", "HEAD:" + settings.Folders[1].Target + "/new.txt") == "new", "Locked file prevented healthy push or deleted previous backup.");
                }
                Assert(engine.Backup(settings).Pushed && git.Must(root, "--git-dir", remote, "show", "HEAD:" + settings.Folders[1].Target + "/new.txt") == "locked-new-version", "Unlocked file was not retried.");
                passed.Add("locked file skipped without deleting old backup; unlocked file retries next run");
                File.WriteAllText(Path.Combine(source, "changing.txt"), "stable-old"); engine.Backup(settings);
                engine.FaultForTests = delegate(string phase) { if (phase == "after-file-copy:changing.txt") File.SetLastWriteTimeUtc(Path.Combine(source, "changing.txt"), File.GetLastWriteTimeUtc(Path.Combine(source, "changing.txt")).AddSeconds(1)); };
                File.WriteAllText(Path.Combine(source, "changing.txt"), "unstable-new"); File.WriteAllText(Path.Combine(second, "healthy.txt"), "healthy-with-changing");
                partial = engine.Backup(settings); engine.FaultForTests = null;
                Assert(partial.Pushed && partial.JobWarnings[settings.Folders[0].Id] == 1 && git.Must(root, "--git-dir", remote, "show", "HEAD:" + settings.Folders[0].Target + "/changing.txt") == "stable-old", "Changing file stopped backup or lost previous snapshot.");
                engine.Backup(settings); File.Delete(Path.Combine(source, "changing.txt")); engine.Backup(settings);
                passed.Add("continuously changing file skipped with old version preserved");
                cleanHead = git.Must(root, "--git-dir", remote, "rev-parse", "HEAD");
                engine.FaultForTests = delegate(string phase) { if (phase == "before-file-write") throw new IOException("simulated stage disk failure"); };
                bool blocked = false; try { engine.Backup(settings); } catch (IOException) { blocked = true; } finally { engine.FaultForTests = null; }
                Assert(blocked && git.Must(root, "--git-dir", remote, "rev-parse", "HEAD") == cleanHead && git.Must(cache, "status", "--porcelain") == "", "Stage write failure was swallowed as a file skip.");
                passed.Add("staging disk failures stop safely rather than creating a false partial success");
                string unavailable = Path.Combine(source, "unavailable");
                Directory.CreateDirectory(Path.Combine(unavailable, "empty", "nested"));
                File.WriteAllText(Path.Combine(unavailable, "keep.txt"), "previous-safe");
                File.WriteAllText(Path.Combine(unavailable, "drop.txt"), "excluded-later"); engine.Backup(settings);
                string previousUnavailable = Path.Combine(cache, settings.Folders[0].Target.Replace('/', Path.DirectorySeparatorChar), "unavailable");
                File.WriteAllText(Path.Combine(previousUnavailable, ".env"), "fake=not-a-real-secret");
                git.Must(cache, "add", "-f", "--all"); git.Must(cache, "commit", "-m", "simulate existing sensitive remote file"); git.Must(cache, "push", "origin", "main");
                var deniedAcl = Directory.GetAccessControl(unavailable);
                var denyRule = new System.Security.AccessControl.FileSystemAccessRule(System.Security.Principal.WindowsIdentity.GetCurrent().User,
                    System.Security.AccessControl.FileSystemRights.ListDirectory, System.Security.AccessControl.AccessControlType.Deny);
                deniedAcl.AddAccessRule(denyRule);
                string originalExclusions = settings.Exclusions; settings.Exclusions += ";drop.txt";
                try {
                    Directory.SetAccessControl(unavailable, deniedAcl);
                    File.WriteAllText(Path.Combine(second, "healthy.txt"), "healthy-with-unreadable-directory"); partial = engine.Backup(settings);
                    files = git.Must(root, "--git-dir", remote, "ls-tree", "-r", "--name-only", "HEAD");
                    string preservedManifest = git.Must(root, "--git-dir", remote, "show", "HEAD:backup-manifests/" + settings.Folders[0].Id + ".json");
                    Assert(partial.Pushed && partial.JobWarnings[settings.Folders[0].Id] == 1 && files.Contains("/unavailable/keep.txt") &&
                        !files.Contains("/unavailable/drop.txt") && !files.Contains("/unavailable/.env") && preservedManifest.Contains("unavailable/empty/nested"),
                        "Unreadable directory lost safe old files/empty dirs, revived excluded secrets, or blocked other files.");
                } finally { deniedAcl.RemoveAccessRuleSpecific(denyRule); Directory.SetAccessControl(unavailable, deniedAcl); settings.Exclusions = originalExclusions; }
                Paths.DeleteTree(unavailable, root); engine.Backup(settings);
                passed.Add("unreadable directory preserves safe previous subtree and empty dirs while respecting current exclusions and credential checks");
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
                Settings legacy = Json.Read<Settings>(Json.Write(new { Repository = remote, Folders = new[] { new { Id = settings.Folders[0].Id, Name = "legacy", Source = source, Enabled = true } } }));
                Assert(legacy.Folders[0].GitHubName == null && legacy.Folders[0].Target == "backups/" + settings.Folders[0].Id, "Old settings changed destination on load.");
                passed.Add("legacy settings retain their original cloud directory");
                string oldPath = settings.Folders[0].Target, beforeRename = git.Must(root, "--git-dir", remote, "rev-parse", "HEAD");
                // Simulate an original format1 remote manifest before migration.
                string legacyManifest = Path.Combine(cache, "backup-manifests", settings.Folders[0].Id + ".json");
                File.WriteAllText(legacyManifest, Json.Write(new { format = 1, name = settings.Folders[0].Name, directories = new[] { "" } }), new UTF8Encoding(false));
                git.Must(cache, "add", "--all"); git.Must(cache, "commit", "-m", "test legacy manifest"); git.Must(cache, "push", "origin", "main");
                settings.Folders[0].GitHubName = "工作文档 v1";
                Assert(engine.Backup(settings).Pushed, "Custom Chinese directory rename failed.");
                files = git.Must(root, "--git-dir", remote, "ls-tree", "-r", "--name-only", "HEAD");
                Assert(!files.Contains(oldPath + "/") && files.Contains(settings.Folders[0].Target + "/中文.txt"), "Old directory was duplicated instead of migrated.");
                Assert(git.Must(root, "--git-dir", remote, "show", beforeRename + ":" + oldPath + "/中文.txt") == "healthy-with-lock", "Rename lost old Git history.");
                store.Save(settings); Assert(store.Load().Folders[0].GitHubName == "工作文档 v1", "Custom directory name was not persisted.");
                Assert(!engine.Backup(settings).Changed, "Repeated named backup created a commit.");
                passed.Add("Chinese and spaced directory names + format1 migration + history + persistence");
                settings.Folders[0].GitHubName = "工作文档 v2";
                engine.FaultForTests = delegate(string phase) { if (phase == "before-commit") throw new IOException("rename failure"); };
                blocked = false; try { engine.Backup(settings); } catch (IOException) { blocked = true; }
                Assert(blocked && git.Must(cache, "status", "--porcelain") == "", "Rename failure did not restore previous path and index.");
                engine.FaultForTests = null; Assert(engine.Backup(settings).Pushed, "Rename could not retry after rollback.");
                passed.Add("directory rename rollback and retry");
                settings.Folders[1].GitHubName = "Docs"; Assert(engine.Backup(settings).Pushed, "Initial ASCII name failed.");
                settings.Folders[1].GitHubName = "docs";
                engine.FaultForTests = delegate(string phase) { if (phase == "before-commit") throw new IOException("case rename failure"); };
                blocked = false; try { engine.Backup(settings); } catch (IOException) { blocked = true; }
                Assert(blocked && git.Must(cache, "status", "--porcelain") == "", "Case-only rename rollback left directory casing dirty.");
                engine.FaultForTests = null; Assert(engine.Backup(settings).Pushed, "Case-only rename failed.");
                files = git.Must(root, "--git-dir", remote, "ls-tree", "-r", "--name-only", "HEAD");
                Assert(files.Contains("backups/docs/new.txt") && !files.Contains("backups/Docs/new.txt"), "Case-only destination did not change on GitHub.");
                passed.Add("case-only cloud directory rename and rollback on Windows");
                string[] invalidNames = { "", " ", ".", "..", "../escape", "one/two", "one\\two", "NUL.txt", ".git", "x.", "x:invalid", new string('x', 81) };
                foreach (string invalid in invalidNames) { blocked = false; try { CloudNames.Normalize(invalid); } catch (IOException) { blocked = true; } Assert(blocked, "Invalid cloud name accepted: " + invalid); }
                settings.Folders[2].Enabled = false; settings.Folders[2].GitHubName = "DOCS";
                blocked = false; try { engine.Backup(settings); } catch (IOException) { blocked = true; }
                Assert(blocked, "Disabled task's duplicate name was accepted."); settings.Folders[2].Enabled = true; settings.Folders[2].GitHubName = null;
                passed.Add("unsafe cloud names and case-insensitive duplicate names rejected");
                git.Must(peer, "pull", "--ff-only"); Directory.CreateDirectory(Path.Combine(peer, "backups", "occupied")); File.WriteAllText(Path.Combine(peer, "backups", "occupied", "keep.txt"), "do-not-overwrite");
                git.Must(peer, "add", "--all"); git.Must(peer, "commit", "-m", "unmanaged cloud directory"); git.Must(peer, "push", "origin", "main");
                string desiredName = settings.Folders[0].GitHubName; settings.Folders[0].GitHubName = "occupied";
                blocked = false; try { engine.Backup(settings); } catch (IOException) { blocked = true; }
                Assert(blocked && git.Must(root, "--git-dir", remote, "show", "HEAD:backups/occupied/keep.txt") == "do-not-overwrite", "An unmanaged remote directory was overwritten.");
                settings.Folders[0].GitHubName = desiredName;
                passed.Add("remote directory ownership collision leaves other files unchanged");
                string existingManifest = File.ReadAllText(legacyManifest, Encoding.UTF8);
                File.WriteAllText(legacyManifest, Json.Write(new { format = 99, targetDirectory = desiredName }));
                git.Must(cache, "add", "--all"); git.Must(cache, "commit", "-m", "future manifest"); git.Must(cache, "push", "origin", "main");
                blocked = false; try { engine.Backup(settings); } catch (IOException) { blocked = true; } Assert(blocked, "Unknown manifest format was accepted.");
                File.WriteAllText(legacyManifest, existingManifest, new UTF8Encoding(false)); git.Must(cache, "add", "--all"); git.Must(cache, "commit", "-m", "restore manifest"); git.Must(cache, "push", "origin", "main");
                Assert(!engine.Backup(settings).Changed, "Future-manifest recovery created unnecessary changes.");
                passed.Add("unknown manifest format stops migration safely");
                Store uiStore = new Store(Path.Combine(root, "ui-preview"));
                using (MainForm form = new MainForm(uiStore, false, true)) {
                    form.ShowInTaskbar = false; form.Opacity = 0; form.Show(); System.Windows.Forms.Application.DoEvents();
                    System.Windows.Forms.DataGridView table = (System.Windows.Forms.DataGridView)typeof(MainForm).GetField("grid", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(form);
                    Assert(table.Columns[4].HeaderText == "GitHub 目录名", "Cloud name column is not in the requested position.");
                    table.CurrentCell = table.Rows[0].Cells[4]; table.BeginEdit(true);
                    ((System.Windows.Forms.TextBox)table.EditingControl).Text = "UI 中文目录";
                    Assert(table.EndEdit(), "Cloud name UI edit failed."); System.Windows.Forms.Application.DoEvents();
                    Assert(uiStore.Load().Folders[0].GitHubName == "UI 中文目录", "Edited cloud name did not save to settings.");
                    table.BeginEdit(true); ((System.Windows.Forms.TextBox)table.EditingControl).Text = "../invalid";
                    table.EndEdit(); System.Windows.Forms.Application.DoEvents();
                    Assert(uiStore.Load().Folders[0].GitHubName == "UI 中文目录" && Convert.ToString(table.Rows[0].Cells[4].Value) == "UI 中文目录", "UI accepted invalid destination.");
                    table.CancelEdit(); form.ForceExit();
                }
                passed.Add("editable cloud-name column saves valid input and blocks invalid input");
                Store removeStore = new Store(Path.Combine(root, "remove-preview"));
                using (MainForm form = new MainForm(removeStore, false, true)) {
                    form.ShowInTaskbar = false; form.Opacity = 0; form.Show(); System.Windows.Forms.Application.DoEvents();
                    var field = typeof(MainForm).GetField("settings", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    var table = (System.Windows.Forms.DataGridView)typeof(MainForm).GetField("grid", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(form);
                    Settings beforeRemove = (Settings)field.GetValue(form); removeStore.Save(beforeRemove);
                    string removeId = beforeRemove.Folders[0].Id;
                    using (FileStream heldConfig = new FileStream(removeStore.Config, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                        Assert(!form.RemoveJobs(new[] { removeId }), "Removal reported success when config was locked.");
                        Assert(((Settings)field.GetValue(form)).Folders.Count == 2 && table.Rows.Count == 2 && removeStore.Load().Folders.Count == 2, "Failed removal changed memory, grid or saved tasks.");
                    }
                    removeStore.Save(beforeRemove); File.SetAttributes(removeStore.Config + ".previous", FileAttributes.ReadOnly);
                    Assert(form.RemoveJobs(new[] { removeId }), "Read-only previous backup blocked task removal.");
                    Assert(((Settings)field.GetValue(form)).Folders.Count == 1 && table.Rows.Count == 1 && removeStore.Load().Folders.All(j => j.Id != removeId), "Successful removal did not persist across reload.");
                    File.SetAttributes(removeStore.Config + ".previous", FileAttributes.Normal);
                    Assert(form.RemoveJobs(((Settings)field.GetValue(form)).Folders.Select(j => j.Id).ToArray()) && table.Rows.Count == 0 && removeStore.Load().Folders.Count == 0, "Removing the last task failed.");
                    form.ForceExit();
                }
                passed.Add("remove selected tasks preserves UI on failed save, succeeds with blocked backup and persists an empty list");
                string report = Json.Write(new { success = true, passed = passed, root = root });
                File.WriteAllText(Path.Combine(root, "result.json"), report, Encoding.UTF8); Console.WriteLine(report); return 0;
            } catch (Exception error) {
                string report = Json.Write(new { success = false, passed = passed, error = error.ToString(), root = root });
                File.WriteAllText(Path.Combine(root, "result.json"), report, Encoding.UTF8); Console.WriteLine(report); return 1;
            }
        }
    }
    public static class TestRunner {
        [STAThread]
        public static int Main(string[] args) { return SelfTests.Run(args.Length > 0 ? args[0] : Environment.CurrentDirectory); }
    }
}
