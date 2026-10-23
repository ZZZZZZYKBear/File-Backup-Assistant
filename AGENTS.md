# Windows maintenance

- Codex may redirect AppData reads and writes into its MSIX LocalCache, even when `GetCurrentPackageFullName` reports no package. The same logical path can contain different settings in Codex and Explorer.
- Inspect physical file paths and verify the running desktop UI's task count, source paths and GitHub directory names. A hash of the logical settings path alone cannot prove configuration preservation.
- Update only when the assistant is idle. Launch the installed application through Windows Explorer; keep `DesktopContext` protection before settings loading and mutex acquisition.
- Preserve both configuration views and pending Git commits when repairing this issue. Never replace desktop settings with a launcher-cache copy or blindly restore an old backup. Verify remote manifest ownership before changing an internal task ID.
- Build with `build.ps1` and run `dist/BackupTests.exe` against isolated local repositories. Keep PowerShell scripts UTF-8 with BOM for Windows PowerShell 5.
- Release archives contain only the application executable, its config, `README.md` and `install.ps1`; exclude user settings, caches, diagnostics and credentials.
