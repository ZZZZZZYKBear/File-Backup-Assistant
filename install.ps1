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
if (-not $NoLaunch) { Start-Process -FilePath $executable -WorkingDirectory $destination }
