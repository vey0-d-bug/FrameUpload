# On-headset smoke test: installs two tiny test apps on the real Steam Frame
# with FrameUpload --install, checks they landed and reached Steam, lists
# them, and removes them again.
#
#   powershell -ExecutionPolicy Bypass -File tests\smoke-headset.ps1 [-Exe PATH] [-Target frame] [-KeepApps]
#
# Needs: the headset on, Steam running on it (game mode is fine), and the
# `frame` entry in %USERPROFILE%\.ssh\config working. It touches only
# ~/Applications/FrameUpload-smoke-test.* on the headset. The Steam library
# keeps two "FrameUpload-smoke-test" entries until you remove them in Steam
# (Manage > Remove non-Steam game); FrameUpload doesn't edit Steam's list.
param([string]$Exe, [string]$Target = 'frame', [switch]$KeepApps)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$work = Join-Path ([IO.Path]::GetTempPath()) ('FrameUpload-smoke-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Force -Path $work | Out-Null
$script:failed = 0

function Check([bool]$ok, [string]$what) {
    if ($ok) { Write-Host "  ok    $what" } else { Write-Host "  FAIL  $what" -ForegroundColor Red; $script:failed++ }
}
function Compile([string]$out, [string[]]$cscArgs) {
    $o = & $csc /nologo /optimize+ "/out:$out" @cscArgs 2>&1
    if ($LASTEXITCODE -ne 0) { $o | Write-Host; throw "csc failed for $out" }
}

# The same ssh FrameUpload picks: Git for Windows' if there, else Windows' own.
$ssh = $null
foreach ($c in @("$env:ProgramFiles\Git\usr\bin\ssh.exe", "$env:LOCALAPPDATA\Programs\Git\usr\bin\ssh.exe", "$env:WINDIR\System32\OpenSSH\ssh.exe")) {
    if (Test-Path $c) { $ssh = $c; break }
}
if (-not $ssh) { $ssh = 'ssh.exe' }
if (-not $env:HOME) { $env:HOME = $env:USERPROFILE }
function Remote([string]$cmd) {
    $out = & $ssh -o BatchMode=yes -o ConnectTimeout=10 $Target $cmd 2>&1 | ForEach-Object { "$_" }
    return [pscustomobject]@{ Code = $LASTEXITCODE; Out = @($out) }
}

if ($Exe) { $Exe = (Resolve-Path $Exe).Path }
else {
    $Exe = Join-Path $work 'FrameUpload.exe'
    Compile $Exe @('/target:winexe', '/platform:x64', '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll', (Join-Path $repo 'FrameUpload.cs'))
}
Write-Host "Testing $Exe on $Target"

Write-Host 'Headset'
$r = Remote 'echo ok; { kill -0 "$(cat ~/.steam/steam.pid 2>/dev/null)" 2>/dev/null || pgrep -x steam >/dev/null; } && echo steam; command -v steamos-add-to-steam; df -h --output=avail ~ | tail -n 1'
Check ($r.Code -eq 0 -and $r.Out[0] -eq 'ok') "ssh $Target works ($($r.Out -join ' / '))"
if ($script:failed) { Write-Host 'Turn the headset on and check the frame entry in ~/.ssh/config, then run this again.'; exit 1 }
Check ($r.Out -contains 'steam') 'Steam is running on the headset'
Check (@($r.Out | Where-Object { $_ -like '*steamos-add-to-steam' }).Count -eq 1) 'steamos-add-to-steam is there'

# Two tiny apps: an AppImage (a shell script is enough for Steam's list)
# and a Windows .exe.
$img = Join-Path $work 'FrameUpload-smoke-test.AppImage'
[IO.File]::WriteAllText($img, "#!/bin/sh`necho 'FrameUpload smoke test'`nsleep 5`n", (New-Object Text.UTF8Encoding($false)))
$src = Join-Path $work 'smoke.cs'
[IO.File]::WriteAllText($src, 'class P { static void Main() { System.Console.WriteLine("FrameUpload smoke test"); System.Threading.Thread.Sleep(5000); } }')
$exeApp = Join-Path $work 'FrameUpload-smoke-test.exe'
Compile $exeApp @('/target:exe', $src)

Write-Host 'Install app'
$log = Join-Path $work 'frameupload-log.txt'
$p = Start-Process -FilePath $Exe -ArgumentList @("--host=$Target", "--log=`"$log`"", '--exit', '--install', "`"$img`"", "`"$exeApp`"") -PassThru
if (-not $p.WaitForExit(180000)) { try { $p.Kill() } catch { }; Check $false 'FrameUpload finished within 3 minutes' }
else { Check ($p.ExitCode -eq 0) "FrameUpload --install exit code 0 (got $($p.ExitCode))" }
$lines = @(); if (Test-Path $log) { $lines = @([IO.File]::ReadAllLines($log)) }
foreach ($n in 'FrameUpload-smoke-test.AppImage', 'FrameUpload-smoke-test.exe') {
    $l = @($lines | Where-Object { ($_ -split "`t")[1] -eq $n })[0]
    Check ($l -and $l.EndsWith("`tok")) "log: $n ok ($l)"
}

$r = Remote 'cd ~/Applications && stat -c "%n %s" FrameUpload-smoke-test.AppImage FrameUpload-smoke-test.exe; [ -x FrameUpload-smoke-test.AppImage ] && echo executable'
$sizes = @{ 'FrameUpload-smoke-test.AppImage' = (Get-Item $img).Length; 'FrameUpload-smoke-test.exe' = (Get-Item $exeApp).Length }
foreach ($n in $sizes.Keys) { Check ($r.Out -contains "$n $($sizes[$n])") "~/Applications/$n is there, $($sizes[$n]) bytes" }
Check ($r.Out -contains 'executable') 'the AppImage is executable'

# steamos-add-to-steam can report success when Steam didn't take the app,
# so look in Steam's own list. Steam writes shortcuts.vdf on its own
# schedule: give it up to 30 s.
$found = $false
for ($i = 0; $i -lt 10 -and -not $found; $i++) {
    $r = Remote 'for f in ~/.steam/steam/userdata/*/config/shortcuts.vdf ~/.local/share/Steam/userdata/*/config/shortcuts.vdf; do [ -f "$f" ] && grep -ao FrameUpload-smoke-test "$f" | wc -l; done 2>/dev/null | sort -n | tail -n 1'
    $n = 0; [void][int]::TryParse(($r.Out | Select-Object -Last 1), [ref]$n)
    if ($n -ge 2) { $found = $true } else { Start-Sleep 3 }
}
Check $found 'Steam''s shortcuts.vdf lists both FrameUpload-smoke-test apps (if not, look in the Steam library on the headset)'

Write-Host 'Apps... > Manage installed apps'
$wpf = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\WPF'
$uiDriver = Join-Path $work 'UiDriver.exe'
Compile $uiDriver @('/target:exe', "/r:$wpf\UIAutomationClient.dll", "/r:$wpf\UIAutomationTypes.dll", "/r:$wpf\WindowsBase.dll",
    '/r:System.Core.dll', (Join-Path $PSScriptRoot 'ui\UiDriver.cs'))
function Ui([int]$appPid, [string[]]$step) {
    $o = @(& $uiDriver $appPid @step 2>&1 | ForEach-Object { "$_" })
    foreach ($l in $o) { if ($l.StartsWith("ERR`t")) { Write-Host "        $l" } }
    return [pscustomobject]@{ Code = $LASTEXITCODE; Rows = @($o | Where-Object { $_.StartsWith("ROW`t") } | ForEach-Object { ($_ -split "`t")[1] }) }
}
$names = @('FrameUpload-smoke-test.AppImage', 'FrameUpload-smoke-test.exe')
$p = Start-Process -FilePath $Exe -ArgumentList @("--host=$Target") -PassThru
$uninstalled = $false
try {
    Check ((Ui $p.Id 'connected').Code -eq 0) 'the window connects'
    $ui = Ui $p.Id 'list'
    foreach ($n in $names) { Check ($ui.Rows -contains $n) "listed: $n" }
    if (-not $KeepApps) {
        $ui = Ui $p.Id (@('uninstall') + $names)
        Check ($ui.Code -eq 0) 'Uninstall... removes them (confirmed with OK)'
        foreach ($n in $names) { Check (-not ($ui.Rows -contains $n)) "no longer listed: $n" }
        $uninstalled = $ui.Code -eq 0
    }
} finally { [void](Ui $p.Id 'close'); if (-not $p.WaitForExit(10000)) { $p.Kill() } }

if (-not $KeepApps) {
    $r = Remote 'ls ~/Applications/FrameUpload-smoke-test.* 2>/dev/null; rm -f -- ~/Applications/FrameUpload-smoke-test.AppImage ~/Applications/FrameUpload-smoke-test.exe'
    Check ($r.Out.Count -eq 0 -or -not $uninstalled) 'the files are gone from ~/Applications'
    Write-Host '  Remove the two FrameUpload-smoke-test entries from the Steam library (Manage > Remove non-Steam game).'
}

Write-Host ''
if ($script:failed) { Write-Host "$($script:failed) check(s) failed. Files: $work" -ForegroundColor Red }
else { Write-Host 'Smoke test passed.'; Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue }
exit $script:failed
