param([switch]$NoLaunch)
$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot 'dist'
if (-not (Test-Path -LiteralPath (Join-Path $source 'GitHubBackupAssistant.exe'))) { $source = $PSScriptRoot }
$programsRoot = Join-Path $env:LOCALAPPDATA 'Programs'
$destination = [System.IO.Path]::GetFullPath((Join-Path $programsRoot 'GitHubBackupAssistant'))
if (-not $destination.StartsWith(([System.IO.Path]::GetFullPath($programsRoot).TrimEnd('\') + '\'), [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid installation path.' }
$executable = Join-Path $destination 'GitHubBackupAssistant.exe'
$running = Get-Process -Name GitHubBackupAssistant -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $executable }
if ($running) { throw 'Please exit the assistant from its tray menu before reinstalling.' }
New-Item -ItemType Directory -Path $destination -Force | Out-Null
if (-not ('BackupInstallNative' -as [type])) {
    Add-Type 'using System; using System.Text; using System.Runtime.InteropServices; using Microsoft.Win32.SafeHandles; public static class BackupInstallNative { [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] public static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint size, uint flags); }'
}
function Get-PhysicalInstallPath([string]$filePath) {
    $stream = [IO.File]::Open($filePath, [IO.FileMode]::Open, [IO.FileAccess]::Read, ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    try {
        $buffer = New-Object Text.StringBuilder 32768
        $length = [BackupInstallNative]::GetFinalPathNameByHandle($stream.SafeFileHandle, $buffer, $buffer.Capacity, 0)
        if ($length -eq 0 -or $length -ge $buffer.Capacity) { throw 'Cannot resolve the installation path.' }
        $path = $buffer.ToString()
        if ($path.StartsWith('\\?\UNC\')) { return ('\\' + $path.Substring(8)) }
        if ($path.StartsWith('\\?\')) { return $path.Substring(4) }
        return $path
    } finally { $stream.Dispose() }
}
$contextProbe = Join-Path $destination ('install-context-' + [Guid]::NewGuid().ToString('N') + '.tmp')
try {
    [IO.File]::WriteAllBytes($contextProbe, [byte[]]@())
    $physicalProbe = Get-PhysicalInstallPath $contextProbe
    if ($physicalProbe -match '\\Packages\\.*\\LocalCache\\' -and -not $physicalProbe.Equals($contextProbe, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Windows redirected this installation to a launcher cache. Run install.ps1 from the Windows desktop instead.'
    }
} finally { if (Test-Path -LiteralPath $contextProbe) { Remove-Item -LiteralPath $contextProbe } }
foreach ($name in @('GitHubBackupAssistant.exe','GitHubBackupAssistant.exe.config')) {
    $file = Join-Path $source $name
    if (-not (Test-Path -LiteralPath $file)) { throw ('Missing application file: ' + $file) }
    $target = Join-Path $destination $name
    if (Test-Path -LiteralPath $target) { Copy-Item -LiteralPath $target -Destination ($target + '.previous') -Force }
    Copy-Item -LiteralPath $file -Destination $target -Force
}
$instructions = Join-Path $PSScriptRoot 'README.md'
if (Test-Path -LiteralPath $instructions) { Copy-Item -LiteralPath $instructions -Destination (Join-Path $destination 'README.md') -Force }
$shell = New-Object -ComObject WScript.Shell
$shortcutPaths = @((Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) '云笺备份.lnk'), (Join-Path ([Environment]::GetFolderPath('Programs')) '云笺备份.lnk'))
foreach ($shortcutPath in $shortcutPaths) {
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $executable
    $shortcut.WorkingDirectory = $destination
    $shortcut.IconLocation = ($executable + ',0')
    $shortcut.Description = 'GitHub folder backup assistant'
    $shortcut.Save()
}
$autoStart = $true
$settingsPath = Join-Path $env:LOCALAPPDATA 'GitHubBackupAssistant\settings.json'
if (Test-Path -LiteralPath $settingsPath) { $autoStart = [bool]((Get-Content -LiteralPath $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json).AutoStart) }
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
if ($autoStart) { New-ItemProperty -LiteralPath $runKey -Name GitHubBackupAssistant -Value ('"' + $executable + '" --tray') -PropertyType String -Force | Out-Null }
else { Remove-ItemProperty -LiteralPath $runKey -Name GitHubBackupAssistant -ErrorAction SilentlyContinue }
Write-Output ('Installed: ' + $executable)
Write-Output ('Startup enabled: ' + $autoStart)
if (-not $NoLaunch) { Start-Process -FilePath (Join-Path $env:WINDIR 'explorer.exe') -ArgumentList ('"' + (Get-PhysicalInstallPath $executable) + '"') -WindowStyle Hidden }
