<#
.SYNOPSIS
  Publish TeamsRecorder to a stable location outside the repo and (re)launch it.

  Output: %LOCALAPPDATA%\TeamsRecorder\app\TeamsRecorder.exe (self-contained win-x64,
  no .NET runtime needed on the machine). Writes repo.path next to the exe so the
  app can find sidecar\ in this checkout. Re-points the Startup shortcut if present;
  the Start Menu shortcut re-points itself on first launch.

  Usage:  .\publish.ps1            # publish + relaunch
          .\publish.ps1 -NoLaunch  # publish only
#>
param([switch]$NoLaunch)

$ErrorActionPreference = 'Stop'
$repo    = $PSScriptRoot
$project = Join-Path $repo 'src\TeamsRecorder\TeamsRecorder.csproj'
$outDir  = Join-Path $env:LOCALAPPDATA 'TeamsRecorder\app'
$exe     = Join-Path $outDir 'TeamsRecorder.exe'

Write-Host "[publish] Stopping running instance (if any)..."
Get-Process TeamsRecorder -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800

Write-Host "[publish] dotnet publish -> $outDir"
dotnet publish $project -c Release -r win-x64 --self-contained true -o $outDir -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

Set-Content -Path (Join-Path $outDir 'repo.path') -Value $repo -Encoding utf8 -NoNewline

# Re-point the per-user Startup shortcut if the user has "Start with Windows" on.
$startupLnk = Join-Path ([Environment]::GetFolderPath('Startup')) 'TeamsRecorder.lnk'
if (Test-Path $startupLnk) {
    $s = (New-Object -ComObject WScript.Shell).CreateShortcut($startupLnk)
    $s.TargetPath = $exe; $s.WorkingDirectory = $outDir; $s.IconLocation = "$exe,0"; $s.Save()
    Write-Host "[publish] Startup shortcut -> $exe"
}

if (-not $NoLaunch) {
    Write-Host "[publish] Launching $exe"
    Start-Process -FilePath $exe -WorkingDirectory $outDir
}
Write-Host "[publish] Done."
