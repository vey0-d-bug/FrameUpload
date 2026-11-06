# FrameUpload's automated tests: runs the real FrameUpload.exe against a fake
# headset (a folder on this PC, reached through a fake ssh.exe that runs the
# commands with Git for Windows' bash), so no headset is needed.
#
#   powershell -ExecutionPolicy Bypass -File tests\run-tests.ps1 [-Exe PATH] [-Only NAME] [-NoUi] [-Keep] [-WorkDir DIR]
#
#   -Exe PATH   test this FrameUpload.exe (default: build one from the source)
#   -Only NAME  run only the tests whose name contains NAME
#   -NoUi       skip the tests that click through the window (Manage installed apps)
#   -Keep       keep the work folder (fake headsets, logs) even when all pass
#   -WorkDir    where the work folder goes (default: %TEMP%)
#
# Needs Windows with Git for Windows. Exit code: the number of failed tests.
param([string]$Exe, [string]$Only, [switch]$NoUi, [switch]$Keep, [string]$WorkDir = [IO.Path]::GetTempPath())

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$repo = Split-Path -Parent $here
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$wpf = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\WPF'
$work = Join-Path $WorkDir ('FrameUpload-tests-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Force -Path $work | Out-Null
$bin = Join-Path $work 'bin'
New-Item -ItemType Directory -Force -Path (Join-Path $bin 'fakessh') | Out-Null

# ---- Tools ----

$gitUsr = $null
foreach ($c in @("$env:ProgramFiles\Git\usr\bin", "$env:LOCALAPPDATA\Programs\Git\usr\bin")) {
    if (Test-Path (Join-Path $c 'bash.exe')) { $gitUsr = $c; break }
}
if (-not $gitUsr) {
    $git = Get-Command git -ErrorAction SilentlyContinue
    if ($git) {
        $c = Join-Path (Split-Path -Parent (Split-Path -Parent $git.Source)) 'usr\bin'
        if (Test-Path (Join-Path $c 'bash.exe')) { $gitUsr = $c }
    }
}
if (-not $gitUsr) { Write-Host 'Git for Windows (its usr\bin\bash.exe) is needed for the fake headset.'; exit 100 }

function Compile([string]$out, [string[]]$cscArgs) {
    $o = & $csc /nologo /optimize+ "/out:$out" @cscArgs 2>&1
    if ($LASTEXITCODE -ne 0) { $o | Write-Host; throw "csc failed for $out" }
}

if ($Exe) {
    $Exe = (Resolve-Path $Exe).Path
    # (PowerShell names ignore case: tests must not use a variable called $exe.)
    Write-Host "Testing $Exe"
} else {
    $Exe = Join-Path $bin 'FrameUpload.exe'
    Compile $Exe @('/target:winexe', '/platform:x64', '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll', (Join-Path $repo 'FrameUpload.cs'))
    Write-Host "Built $Exe from FrameUpload.cs"
}
$fakeSsh = Join-Path $bin 'fakessh\ssh.exe'
Compile $fakeSsh @('/target:exe', (Join-Path $here 'fake-headset\FakeSsh.cs'))
$uiDriver = Join-Path $bin 'UiDriver.exe'
if (-not $NoUi) {
    Compile $uiDriver @('/target:exe', "/r:$wpf\UIAutomationClient.dll", "/r:$wpf\UIAutomationTypes.dll", "/r:$wpf\WindowsBase.dll",
        '/r:System.Core.dll', (Join-Path $here 'ui\UiDriver.cs'))
}

# The stubs must have LF line ends for bash, whatever git's autocrlf did.
$stubs = Join-Path $work 'stubs'
foreach ($d in 'bin', 'flatpak') {
    New-Item -ItemType Directory -Force -Path (Join-Path $stubs $d) | Out-Null
    foreach ($f in Get-ChildItem (Join-Path $here "fake-headset\$d") -File) {
        $text = [IO.File]::ReadAllText($f.FullName) -replace "`r`n", "`n"
        [IO.File]::WriteAllText((Join-Path $stubs "$d\$($f.Name)"), $text, (New-Object Text.UTF8Encoding($false)))
    }
}
$env:FAKE_STUBS = $stubs
$env:FAKE_GIT_USR = $gitUsr
# FrameUpload saves the last IP here; keep the tests away from the real one.
$env:APPDATA = Join-Path $work 'appdata'
New-Item -ItemType Directory -Force -Path $env:APPDATA | Out-Null

# ---- Helpers ----

$results = New-Object System.Collections.ArrayList
$script:current = $null

function Check([bool]$ok, [string]$what) {
    if ($ok) { Write-Host "    ok    $what" }
    else { Write-Host "    FAIL  $what" -ForegroundColor Red; $script:current.Failed++ }
}

function Test([string]$name, [scriptblock]$body) {
    if ($Only -and $name -notlike "*$Only*") { return }
    Write-Host ''
    Write-Host $name
    $script:current = [pscustomobject]@{ Name = $name; Failed = 0 }
    try { & $body } catch {
        Write-Host "    FAIL  $($_.Exception.Message)" -ForegroundColor Red
        Write-Host "          at $($_.InvocationInfo.PositionMessage -replace "`n", "`n          ")"
        $script:current.Failed++
    }
    [void]$results.Add($script:current)
}

# A fresh fake headset: home\ is its ~, state\ its switches and records.
function New-Headset([string]$name, [string[]]$switches) {
    $short = ($name -replace '[^A-Za-z0-9]+', '-')
    if ($short.Length -gt 40) { $short = $short.Substring(0, 40) }
    $root = Join-Path $work $short
    foreach ($d in 'home', 'state', 'tmp') { New-Item -ItemType Directory -Force -Path (Join-Path $root $d) | Out-Null }
    foreach ($s in $switches) { New-Item -ItemType File -Force -Path (Join-Path $root "state\$s") | Out-Null }
    $env:FAKE_HEADSET = $root
    return $root
}

# Test files on the PC, filled with bytes that differ per file.
function New-PcFile([string]$name, [int]$size) {
    $dir = Join-Path $work 'pc'
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $p = Join-Path $dir $name
    $bytes = New-Object byte[] $size
    (New-Object Random($name.GetHashCode())).NextBytes($bytes)
    [IO.File]::WriteAllBytes($p, $bytes)
    return $p
}

# Quotes one argument the way the C runtime reads it back.
function Quote([string]$s) {
    if ($s -notmatch '[\s"]' -and $s.Length -gt 0) { return $s }
    $sb = New-Object Text.StringBuilder '"'
    $slashes = 0
    foreach ($ch in $s.ToCharArray()) {
        if ($ch -eq '\') { $slashes++; continue }
        if ($ch -eq '"') { [void]$sb.Append('\', $slashes * 2 + 1) } else { [void]$sb.Append('\', $slashes) }
        [void]$sb.Append($ch); $slashes = 0
    }
    [void]$sb.Append('\', $slashes * 2).Append('"')
    return $sb.ToString()
}

function Start-App([string[]]$appArgs) {
    $psi = New-Object Diagnostics.ProcessStartInfo $Exe
    $psi.Arguments = (@("--ssh=$fakeSsh") + $appArgs | ForEach-Object { Quote $_ }) -join ' '
    $psi.UseShellExecute = $false
    return [Diagnostics.Process]::Start($psi)
}

# Runs FrameUpload with --exit and a log; returns its exit code and log lines.
function Run-App([string[]]$appArgs, [int]$timeout = 120) {
    $log = Join-Path $env:FAKE_HEADSET 'frameupload-log.txt'
    $p = Start-App (@("--log=$log", '--exit') + $appArgs)
    if (-not $p.WaitForExit($timeout * 1000)) {
        try { $p.Kill() } catch { }
        throw "FrameUpload didn't finish in $timeout s (a message box may be waiting for an answer)"
    }
    $lines = @()
    if (Test-Path $log) { $lines = @([IO.File]::ReadAllLines($log)) }
    return [pscustomobject]@{ Code = $p.ExitCode; Log = $lines }
}

function LogLine($r, [string]$name) { return @($r.Log | Where-Object { ($_ -split "`t")[1] -eq $name })[0] }
function LogOk($r, [string]$name) { $l = LogLine $r $name; return $l -and $l.EndsWith("`tok") }
function LogFailed($r, [string]$name, [string]$text) { $l = LogLine $r $name; return $l -and $l.Contains("`tFAILED ") -and $l.Contains($text) }

function Same([string]$a, [string]$b) {
    if (-not (Test-Path -LiteralPath $a) -or -not (Test-Path -LiteralPath $b)) { return $false }
    return (Get-FileHash -LiteralPath $a).Hash -eq (Get-FileHash -LiteralPath $b).Hash
}

function Lines([string]$root, [string]$file) {
    $p = Join-Path $root "state\$file"
    if (Test-Path $p) { return @([IO.File]::ReadAllLines($p, [Text.Encoding]::UTF8)) } else { return @() }
}

function NoParts([string]$dir) {
    if (-not (Test-Path -LiteralPath $dir)) { return $true }
    return @(Get-ChildItem -LiteralPath $dir -Force -Filter '*.part').Count -eq 0
}

function Ui([int]$appPid, [string[]]$step) {
    $o = & $uiDriver $appPid @step 2>&1 | ForEach-Object { "$_" }
    $code = $LASTEXITCODE
    foreach ($l in $o) { if ($l.StartsWith("ERR`t")) { Write-Host "          $l" } }
    return [pscustomobject]@{ Code = $code; Out = @($o) }
}

function Rows($ui) { return @($ui.Out | Where-Object { $_.StartsWith("ROW`t") } | ForEach-Object { ($_ -split "`t")[1] }) }

# ---- Tests: Install app (--install) ----

Test 'Install: .apk, .exe and AppImage go to ~/Applications and into Steam; Flatpaks install with Flatpak' {
    $h = New-Headset $script:current.Name @('steam-running')
    $apk = New-PcFile 'game.apk' 300000
    $winApp = New-PcFile 'Tool.exe' 50000
    $img = New-PcFile 'Viewer.AppImage' 200000
    $fp = New-PcFile 'bundle.flatpak' 40000
    $ref = New-PcFile 'remote.flatpakref' 500
    $r = Run-App @('--install', $apk, $winApp, $img, $fp, $ref)
    $apps = Join-Path $h 'home\Applications'
    Check ($r.Code -eq 0) "exit code 0 (got $($r.Code))"
    foreach ($n in 'game.apk', 'Tool.exe', 'Viewer.AppImage', 'bundle.flatpak', 'remote.flatpakref') { Check (LogOk $r $n) "log says ok for $n" }
    foreach ($p in $apk, $winApp, $img) { Check (Same $p (Join-Path $apps (Split-Path -Leaf $p))) "$(Split-Path -Leaf $p) is in ~/Applications, byte for byte" }
    $added = Lines $h 'added-to-steam.txt'
    Check ($added.Count -eq 3) "3 apps added to Steam (got $($added.Count))"
    foreach ($n in 'game.apk', 'Tool.exe', 'Viewer.AppImage') { Check (@($added | Where-Object { $_.EndsWith("/Applications/$n") }).Count -eq 1) "steamos-add-to-steam got ~/Applications/$n" }
    $fl = Lines $h 'flatpak.txt'
    Check (@($fl | Where-Object { $_ -like 'remote-add --user --if-not-exists flathub *' }).Count -ge 1) 'Flathub added for the user'
    Check (@($fl | Where-Object { $_ -like 'install --user -y --noninteractive --bundle */Applications/bundle.flatpak' }).Count -eq 1) '.flatpak installed with --bundle'
    Check (@($fl | Where-Object { $_ -like 'install --user -y --noninteractive --from */Applications/remote.flatpakref' }).Count -eq 1) '.flatpakref installed with --from'
    $firstInstall = [array]::FindIndex([string[]]$fl, [Predicate[string]]{ param($l) $l.StartsWith('install ') })
    $firstRemote = [array]::FindIndex([string[]]$fl, [Predicate[string]]{ param($l) $l.StartsWith('remote-add ') })
    Check ($firstRemote -ge 0 -and $firstRemote -lt $firstInstall) 'Flathub is added before the first install'
    Check (-not (Test-Path (Join-Path $apps 'bundle.flatpak')) -and -not (Test-Path (Join-Path $apps 'remote.flatpakref'))) 'the Flatpak files are removed after installing'
    Check (NoParts $apps) 'no .part files left behind'
}

Test 'Install: with Steam not running, apps stay in ~/Applications and say why' {
    $h = New-Headset $script:current.Name @()
    $apk = New-PcFile 'game.apk' 1000
    $img = New-PcFile 'Viewer.AppImage' 1000
    $r = Run-App @('--install', $apk, $img)
    Check ($r.Code -eq 2) "exit code 2, one per app (got $($r.Code))"
    foreach ($n in 'game.apk', 'Viewer.AppImage') { Check (LogFailed $r $n 'Steam is not running') "log for $n says Steam is not running" }
    Check (Same $apk (Join-Path $h 'home\Applications\game.apk')) 'the .apk is still copied'
    Check ((Lines $h 'added-to-steam.txt').Count -eq 0) 'nothing was added to Steam'
}

Test 'Install: a failing steamos-add-to-steam is reported' {
    $h = New-Headset $script:current.Name @('steam-running', 'add-fails')
    $winApp = New-PcFile 'Tool.exe' 1000
    $r = Run-App @('--install', $winApp)
    Check ($r.Code -eq 1) "exit code 1 (got $($r.Code))"
    Check (LogFailed $r 'Tool.exe' 'could not reach Steam') 'log has the headset''s error'
}

Test 'Install: a Flatpak without flatpak on the headset says so' {
    $h = New-Headset $script:current.Name @('steam-running', 'no-flatpak')
    $fp = New-PcFile 'bundle.flatpak' 1000
    $r = Run-App @('--install', $fp)
    Check ($r.Code -eq 1) "exit code 1 (got $($r.Code))"
    Check (LogFailed $r 'bundle.flatpak' 'Flatpak is not on the headset') 'log says Flatpak is missing'
}

Test 'Install: a failing Flatpak install shows flatpak''s error and removes the file' {
    $h = New-Headset $script:current.Name @('steam-running', 'flatpak-fails')
    $fp = New-PcFile 'bundle.flatpak' 1000
    $r = Run-App @('--install', $fp)
    Check ($r.Code -eq 1) "exit code 1 (got $($r.Code))"
    Check (LogFailed $r 'bundle.flatpak' 'No space left on device') 'log has flatpak''s last lines'
    Check (-not (Test-Path (Join-Path $h 'home\Applications\bundle.flatpak'))) 'the bundle is removed'
}

Test 'Install: names with spaces, quotes, $ and accents reach the headset as they are' {
    $h = New-Headset $script:current.Name @('steam-running')
    $names = @("Bob's Game (v2).AppImage", 'Pay $HOME `x`.apk', ('Caf' + [char]0xE9 + ' & Co.exe'))
    $files = @($names | ForEach-Object { New-PcFile $_ 2000 })
    $r = Run-App (@('--install') + $files)
    Check ($r.Code -eq 0) "exit code 0 (got $($r.Code))"
    $added = Lines $h 'added-to-steam.txt'
    foreach ($i in 0..($names.Count - 1)) {
        Check (Same $files[$i] (Join-Path $h "home\Applications\$($names[$i])")) "$($names[$i]) copied under its own name"
        Check (@($added | Where-Object { $_.EndsWith("/Applications/$($names[$i])") }).Count -eq 1) "$($names[$i]) added to Steam under its own name"
    }
}

Test 'Install: with the headset unreachable, every app fails with ssh''s error' {
    $h = New-Headset $script:current.Name @('offline')
    $apk = New-PcFile 'game.apk' 1000
    $winApp = New-PcFile 'Tool.exe' 1000
    $r = Run-App @('--install', $apk, $winApp)
    Check ($r.Code -eq 2) "exit code 2 (got $($r.Code))"
    foreach ($n in 'game.apk', 'Tool.exe') { Check (LogFailed $r $n 'Connection timed out') "log for $n has ssh's error" }
}

# ---- Tests: copying, as before v7 ----

Test 'Copy: without --install, files (apps too) are copied to the folder and not installed' {
    $h = New-Headset $script:current.Name @('steam-running')
    $mp4 = New-PcFile 'clip.mp4' 500000
    $apk = New-PcFile 'game.apk' 1000
    $r = Run-App @('--dir=~/Videos/Test', $mp4, $apk)
    Check ($r.Code -eq 0) "exit code 0 (got $($r.Code))"
    Check (Same $mp4 (Join-Path $h 'home\Videos\Test\clip.mp4')) 'clip.mp4 is in ~/Videos/Test'
    Check (Same $apk (Join-Path $h 'home\Videos\Test\game.apk')) 'game.apk is copied there too'
    Check ((Lines $h 'added-to-steam.txt').Count -eq 0) 'nothing was added to Steam'
    Check (-not (Test-Path (Join-Path $h 'home\Applications'))) '~/Applications was not touched'
}

Test 'Copy: a big file goes as parallel ranges and arrives whole' {
    $h = New-Headset $script:current.Name @()
    $big = New-PcFile 'big.mkv' (130 * 1048576)
    $r = Run-App @('--streams=4', $big) 300
    Check ($r.Code -eq 0) "exit code 0 (got $($r.Code))"
    $l = LogLine $r 'big.mkv'
    Check ($l -and $l.Contains("`t4 stream(s)`t")) "sent as 4 ranges ($l)"
    Check (Same $big (Join-Path $h 'home\Videos\big.mkv')) 'big.mkv is in ~/Videos, byte for byte'
    Check (NoParts (Join-Path $h 'home\Videos')) 'no .part left behind'
    Remove-Item -Force -ErrorAction SilentlyContinue $big, (Join-Path $h 'home\Videos\big.mkv')
}

# ---- Tests: Manage installed apps (clicks through the window) ----

if (-not $NoUi) {
    Test 'Manage apps: lists ~/Applications and the user''s Flatpaks' {
        $h = New-Headset $script:current.Name @('steam-running')
        $apps = Join-Path $h 'home\Applications'
        New-Item -ItemType Directory -Force -Path $apps | Out-Null
        foreach ($n in 'game.apk', 'Tool.exe', 'Viewer.AppImage', '.hidden.part') { [IO.File]::WriteAllText((Join-Path $apps $n), 'x') }
        [IO.File]::WriteAllText((Join-Path $h 'state\flatpak-apps.txt'), "org.example.Paint`tPaint`n")
        $p = Start-App @()
        try {
            Check ((Ui $p.Id 'connected').Code -eq 0) 'the window connects to the fake headset'
            $ui = Ui $p.Id 'list'
            Check ($ui.Code -eq 0) 'Manage installed apps opens and lists'
            $rows = Rows $ui
            foreach ($n in 'game.apk', 'Tool.exe', 'Viewer.AppImage', 'Paint') { Check ($rows -contains $n) "lists $n" }
            Check (-not ($rows -contains '.hidden.part')) 'hidden files are not listed'
            Check ($rows.Count -eq 4) "4 rows (got: $($rows -join ', '))"
        } finally { [void](Ui $p.Id 'close'); if (-not $p.WaitForExit(10000)) { $p.Kill() } }
    }

    Test 'Manage apps: uninstall removes the file and the Flatpak, after a confirmation' {
        $h = New-Headset $script:current.Name @('steam-running')
        $apps = Join-Path $h 'home\Applications'
        New-Item -ItemType Directory -Force -Path $apps | Out-Null
        foreach ($n in "Bob's Game.AppImage", 'Tool.exe') { [IO.File]::WriteAllText((Join-Path $apps $n), 'x') }
        [IO.File]::WriteAllText((Join-Path $h 'state\flatpak-apps.txt'), "org.example.Paint`tPaint`norg.example.Keep`tKeep`n")
        $p = Start-App @()
        try {
            [void](Ui $p.Id 'connected')
            $ui = Ui $p.Id @('uninstall', "Bob's Game.AppImage", 'Paint')
            Check ($ui.Code -eq 0) 'selects, confirms and uninstalls'
            $msg = ($ui.Out | Where-Object { $_.StartsWith("MSG`t") }) -join ' '
            Check ($msg.Contains("Bob's Game.AppImage") -and $msg.Contains('Paint')) "the confirmation names both ($msg)"
            Check (-not ($msg.Contains('not everything') -or $msg.Contains('Not everything'))) 'no error box'
            Check (-not (Test-Path -LiteralPath (Join-Path $apps "Bob's Game.AppImage"))) "Bob's Game.AppImage is removed"
            Check (Test-Path (Join-Path $apps 'Tool.exe')) 'Tool.exe is kept'
            $fl = Lines $h 'flatpak.txt'
            Check (@($fl | Where-Object { $_ -eq 'uninstall --user -y --noninteractive org.example.Paint' }).Count -eq 1) 'flatpak uninstall --user org.example.Paint ran'
            $rows = Rows $ui
            Check (($rows -contains 'Tool.exe') -and ($rows -contains 'Keep') -and $rows.Count -eq 2) "the list then shows what is left (got: $($rows -join ', '))"
        } finally { [void](Ui $p.Id 'close'); if (-not $p.WaitForExit(10000)) { $p.Kill() } }
    }

    Test 'Manage apps: with nothing installed it says so' {
        $h = New-Headset $script:current.Name @('steam-running')
        $p = Start-App @()
        try {
            [void](Ui $p.Id 'connected')
            $ui = Ui $p.Id 'list'
            Check ($ui.Code -eq 0) 'Manage installed apps opens'
            Check ((Rows $ui) -contains 'No sideloaded apps yet.') 'says "No sideloaded apps yet."'
        } finally { [void](Ui $p.Id 'close'); if (-not $p.WaitForExit(10000)) { $p.Kill() } }
    }
}

# ---- Summary ----

$failed = @($results | Where-Object { $_.Failed -gt 0 })
Write-Host ''
Write-Host ("{0} of {1} tests passed." -f ($results.Count - $failed.Count), $results.Count)
foreach ($t in $failed) { Write-Host "  FAILED: $($t.Name)" -ForegroundColor Red }
if ($failed.Count -eq 0 -and -not $Keep) { Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue }
else { Write-Host "Fake headsets and logs: $work" }
exit $failed.Count
