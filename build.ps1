$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework 4.8 compiler is required.' }
$output = Join-Path $projectRoot 'dist'
New-Item -ItemType Directory -Path $output -Force | Out-Null
Add-Type -AssemblyName System.Drawing
$bitmap = New-Object System.Drawing.Bitmap 64,64
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.Clear([System.Drawing.Color]::Transparent)
$blue = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(49,99,233))
$white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
$graphics.FillEllipse($blue,2,2,60,60)
$graphics.FillEllipse($white,14,27,24,20)
$graphics.FillEllipse($white,25,17,25,28)
$graphics.FillRectangle($white,22,30,29,17)
$pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(49,99,233)),4
$graphics.DrawLine($pen,33,43,33,27)
$graphics.DrawLine($pen,33,27,26,34)
$graphics.DrawLine($pen,33,27,40,34)
$icon = [System.Drawing.Icon]::FromHandle($bitmap.GetHicon())
$iconPath = Join-Path $output 'app.ico'
$iconFile = [System.IO.File]::Create($iconPath)
$icon.Save($iconFile)
$iconFile.Dispose(); $icon.Dispose(); $graphics.Dispose(); $bitmap.Dispose(); $blue.Dispose(); $white.Dispose(); $pen.Dispose()
$sources = @('Core.cs','App.cs','SelfTests.cs') | ForEach-Object { Join-Path $projectRoot ('src\' + $_) }
$references = @('/reference:System.Windows.Forms.dll','/reference:System.Drawing.dll','/reference:System.Web.Extensions.dll')
& $compiler /nologo /utf8output /codepage:65001 /target:winexe /main:CloudBackup.Program /platform:anycpu /optimize+ @references (('/win32icon:' + $iconPath)) (('/win32manifest:' + (Join-Path $projectRoot 'src\app.manifest'))) (('/out:' + (Join-Path $output 'GitHubBackupAssistant.exe'))) @sources
if ($LASTEXITCODE -ne 0) { throw 'Application build failed.' }
& $compiler /nologo /utf8output /codepage:65001 /target:exe /main:CloudBackup.TestRunner /platform:anycpu /optimize+ @references (('/out:' + (Join-Path $output 'BackupTests.exe'))) @sources
if ($LASTEXITCODE -ne 0) { throw 'Test harness build failed.' }
Copy-Item -LiteralPath (Join-Path $projectRoot 'src\App.config') -Destination (Join-Path $output 'GitHubBackupAssistant.exe.config') -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'src\App.config') -Destination (Join-Path $output 'BackupTests.exe.config') -Force
Write-Output ('Built: ' + (Join-Path $output 'GitHubBackupAssistant.exe'))
