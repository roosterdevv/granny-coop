<#
Run a host + client pair from two isolated copies of the game and collect the
MelonLoader logs. Every process is recorded with its creation time and only
those pids are ever stopped. Refuses to start if a Granny.exe is already
running (it may be the owner's own session).

Usage:
  powershell -ExecutionPolicy Bypass -File run-pair.ps1 [-Seconds 150] [-Port 47777] [-Probe]
    [-DoorTest] [-ClientDoorTest] [-ItemTest] [-PlaceTest] [-PauseTest] [-ClientDelay 12]
    [-KillClientAfter 60] [-RelaunchClientAfter 90] [-KillHostAfter 60]
    [-RelaunchHostAfter 90]
    [-Relay [-RelayProfile bad_wifi] [-RelaySeed N] [-ChaosPlan "60:profile:hell;90:stats"]]
    [-MemSample 30] [-MaxLogLines 1000]

  The -Kill* / -Relaunch* switches take seconds measured from that process's own
  launch; -1 (the default) means "never". Every process this script starts is
  tracked and stopped at the end, including a relaunched client or host.

  -Relay puts tools/chaos/chaos.py (from a coop-anything checkout, passed as
  -ChaosRoot) between the two copies: the host keeps -Port, the relay listens
  for the client on a free port and forwards to the host.
  -ChaosPlan is a ';'-separated list of "secondsFromStart:method:arg..." entries:
    profile:<name>          set_profile
    partition:<dir>:<ms>    one-way blackout (dir = c2s or s2c), heals by itself
    disconnect:<ms>         both directions down, heals by itself
    stats                   print the relay counters into the run log
  Relay output, info and packet log go to <ArtifactDir>/chaos-<stamp>/.
  -MemSample N logs each tracked process's working set and private bytes every N
  seconds, for soak runs (memory growth). -MaxLogLines caps the filtered log dump
  in the run file; the full logs stay in each test install.
#>
param(
    [int]$Seconds = 150,
    [int]$Port = 47777,
    [switch]$Probe,
    [switch]$DoorTest,
    [switch]$ClientDoorTest,
    [switch]$ItemTest,
    [switch]$PlaceTest,
    [switch]$PauseTest,
    [int]$ClientDelay = 12,
    [int]$KillClientAfter = -1,
    [int]$RelaunchClientAfter = -1,
    [int]$KillHostAfter = -1,
    [int]$RelaunchHostAfter = -1,
    [switch]$Relay,
    [string]$RelayProfile = "bad_wifi",
    [int]$RelaySeed = -1,
    [string]$ChaosPlan = "",
    [int]$MemSample = 0,
    [int]$MaxLogLines = 1000,
    [string]$ArtifactDir = (Join-Path $PSScriptRoot "runs"),
    [string]$Test1 = "C:\Users\Public\granny-test",
    [string]$Test2 = "C:\Users\Public\granny-test2",
    [string]$ChaosRoot = ""
)

$ErrorActionPreference = "Stop"

$running = Get-Process -Name Granny -ErrorAction SilentlyContinue
if ($running) {
    Write-Output "REFUSING: Granny.exe already running (pid $($running.Id -join ',')); it may be the owner's session."
    exit 2
}

foreach ($dir in @($Test1, $Test2)) {
    if (-not (Test-Path (Join-Path $dir "Granny.exe"))) { Write-Output "missing install: $dir"; exit 2 }
}

function Stop-Tracked($proc, [string]$label) {
    $p = Get-Process -Id $proc.Id -ErrorAction SilentlyContinue
    if ($p) {
        # Print the process start time, and say so: earlier logs read this
        # parenthesised time as the moment of stopping and made every run
        # look like it ended minutes before its last log line.
        Write-Output "stopping pid $($proc.Id) (started $($p.StartTime.ToString('o'))) [$label]"
        Stop-Process -Id $proc.Id -Force
    } else {
        Write-Output "pid $($proc.Id) already gone (exit $($proc.ExitCode)) [$label]"
    }
}

# Fresh Unity logs per instance so the two processes never share Player.log.
$hostLog = Join-Path $Test1 "unity-host.log"
$clientLog = Join-Path $Test2 "unity-client.log"
Remove-Item $hostLog, $clientLog -Force -ErrorAction SilentlyContinue

$extra = @()
if ($Probe) { $extra = @("--coop-probe") }
# The flip is driven by the host only; the client must see it arrive.
$hostExtra = $extra
if ($DoorTest) { $hostExtra = $hostExtra + @("--coop-doortest") }
if ($ItemTest) { $hostExtra = $hostExtra + @("--coop-itemtest") }
if ($PauseTest) { $hostExtra = $hostExtra + @("--coop-pausetest") }
# -ClientDoorTest flips a door on the client instead, which is the only way to
# exercise the join -> host Request path (Poll sees a local change, sends it).
$clientExtra = $extra
if ($ClientDoorTest) { $clientExtra = $clientExtra + @("--coop-doortest") }
# -PlaceTest moves one item on the guest only; the host must put it back.
if ($PlaceTest) { $clientExtra = $clientExtra + @("--coop-placetest") }

$started = Get-Date

Write-Output "launching host from $Test1"
$hostProc = Start-Process -FilePath (Join-Path $Test1 "Granny.exe") `
    -WorkingDirectory $Test1 `
    -ArgumentList (@("--coop-host", "--coop-name=Host", "--coop-autoplay", "--coop-testmove", "--coop-port=$Port") + $hostExtra + @("-logFile", $hostLog)) `
    -WindowStyle Minimized -PassThru

# Optional bad link between the two copies. The host keeps $Port; the client
# dials the relay instead, so every datagram crosses the impairment model.
$repoRoot = $ChaosRoot
$joinPort = $Port
$relayProc = $null
$rpcPort = 0
$chaosDir = ""

if ($Relay) {
    $chaosScript = Join-Path $repoRoot "tools\chaos\chaos.py"
    if (-not $repoRoot -or -not (Test-Path $chaosScript)) {
        Write-Output "REFUSING: -Relay needs -ChaosRoot pointing at a coop-anything checkout (tools\chaos\chaos.py)"
        Stop-Tracked $hostProc "no chaos relay"
        exit 5
    }
    $pythonExe = (Get-Command python -ErrorAction SilentlyContinue).Source
    if (-not $pythonExe) {
        Write-Output "REFUSING: -Relay needs python 3.8+ on PATH (tools/chaos)"
        Stop-Tracked $hostProc "no python"
        exit 5
    }
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $chaosDir = Join-Path $ArtifactDir "chaos-$stamp"
    New-Item -ItemType Directory -Force -Path $chaosDir | Out-Null
    $infoFile = Join-Path $chaosDir "info.json"
    $relayLog = Join-Path $chaosDir "packets.jsonl"
    $relayArgs = @("-I", (Join-Path $repoRoot "tools\chaos\chaos.py"), "relay",
                   "--target", "127.0.0.1:$Port", "--profile", $RelayProfile,
                   "--log", $relayLog, "--info-file", $infoFile,
                   "--seconds", ($Seconds + 30))
    if ($RelaySeed -ge 0) { $relayArgs += @("--seed", $RelaySeed) }

    Write-Output "starting relay -> 127.0.0.1:$Port profile=$RelayProfile (stamp $stamp)"
    $relayProc = Start-Process -FilePath $pythonExe -ArgumentList $relayArgs `
        -WorkingDirectory $repoRoot -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $chaosDir "relay.out.txt") `
        -RedirectStandardError (Join-Path $chaosDir "relay.err.txt")

    for ($i = 0; $i -lt 50 -and -not (Test-Path $infoFile); $i++) { Start-Sleep -Milliseconds 200 }
    if (-not (Test-Path $infoFile)) {
        Write-Output "relay did not come up; see $chaosDir\relay.err.txt"
        Stop-Tracked $hostProc "relay failed"
        Stop-Tracked $relayProc "relay failed"
        exit 4
    }

    $info = Get-Content $infoFile -Raw | ConvertFrom-Json
    $joinPort = [int]$info.listen
    $rpcPort = [int]$info.rpc
    Write-Output "relay listening on 127.0.0.1:$joinPort rpc=$rpcPort artifacts=$chaosDir"
}

. (Join-Path $PSScriptRoot "chaos-rpc.ps1")

function Invoke-ChaosCall([string]$method, [hashtable]$params) {
    Invoke-Chaos $rpcPort $method $params
}

# "secondsFromStart:method:arg..." entries, executed in the event loop below.
$plan = @()
if ($ChaosPlan) {
    foreach ($entry in ($ChaosPlan -split ';')) {
        if (-not $entry.Trim()) { continue }
        $f = $entry.Split(':')
        # @() on the way in: an if-statement unrolls a one-element array to a
        # bare string, and then Args[0] would index a character ('8' -> 56 ms).
        $rest = @()
        if ($f.Count -gt 2) { $rest = @($f[2..($f.Count - 1)]) }
        $plan += , @{ At = [double]$f[0]; Method = $f[1]; Args = @($rest); Done = $false }
    }
}

Start-Sleep -Seconds $ClientDelay

if ($hostProc.HasExited) {
    Write-Output "host exited early with code $($hostProc.ExitCode)"
    if ($relayProc) { Stop-Tracked $relayProc "host exited early" }
    exit 3
}

Write-Output "launching client from $Test2"
$clientProc = Start-Process -FilePath (Join-Path $Test2 "Granny.exe") `
    -WorkingDirectory $Test2 `
    -ArgumentList (@("--coop-join=127.0.0.1", "--coop-name=Guest", "--coop-autoplay", "--coop-testmove", "--coop-port=$joinPort") + $clientExtra + @("-logFile", $clientLog)) `
    -WindowStyle Minimized -PassThru

$pids = @($hostProc.Id, $clientProc.Id)
Write-Output "pids: $($pids -join ',') - running for $Seconds s"
$clientStart = Get-Date

$tracked = @($hostProc, $clientProc)
if ($relayProc) { $tracked += $relayProc }
$client2Log = Join-Path $Test2 "unity-client2.log"
$host2Log = Join-Path $Test1 "unity-host2.log"
Remove-Item $client2Log, $host2Log -Force -ErrorAction SilentlyContinue
$clientDown = $false
$hostDown = $false
$relaunched = $false
$hostRelaunched = $false
$lastMem = $started

# Event loop instead of one long sleep: the chaos runs need to stop or start a
# process at a known offset without losing track of any pid.
while (((Get-Date) - $started).TotalSeconds -lt $Seconds) {
    $elapsed = ((Get-Date) - $started).TotalSeconds
    foreach ($step in $plan) {
        if ($step.Done -or $elapsed -lt $step.At) { continue }
        $step.Done = $true
        switch ($step.Method) {
            "profile"    { Invoke-ChaosCall "set_profile" @{ profile = $step.Args[0] } }
            "partition"  { Invoke-ChaosCall "partition" @{ dir = $step.Args[0]; on = $true; duration_ms = [int]$step.Args[1] } }
            "disconnect" { Invoke-ChaosCall "disconnect" @{ duration_ms = [int]$step.Args[0] } }
            "stats"      { Invoke-ChaosCall "stats" @{} }
            default      { Write-Output "chaos plan: unknown method '$($step.Method)'" }
        }
    }

    if ($MemSample -gt 0 -and ((Get-Date) - $lastMem).TotalSeconds -ge $MemSample) {
        $lastMem = Get-Date
        $samples = foreach ($p in $tracked) {
            $q = Get-Process -Id $p.Id -ErrorAction SilentlyContinue
            if ($q) {
                "{0}:ws={1}MB priv={2}MB" -f $p.Id, [int]($q.WorkingSet64 / 1MB), [int]($q.PrivateMemorySize64 / 1MB)
            } else { "{0}:gone" -f $p.Id }
        }
        Write-Output ("mem t={0:N0}s {1}" -f $elapsed, ($samples -join " "))
    }

    if (-not $hostDown -and $KillHostAfter -ge 0 -and
        ((Get-Date) - $started).TotalSeconds -ge $KillHostAfter) {
        Stop-Tracked $hostProc "kill-host test"
        $hostDown = $true
    }

    if ($hostDown -and -not $hostRelaunched -and $RelaunchHostAfter -ge 0 -and
        ((Get-Date) - $started).TotalSeconds -ge $RelaunchHostAfter) {
        Write-Output "relaunching host from $Test1 (port $Port, log $host2Log)"
        $host2Proc = Start-Process -FilePath (Join-Path $Test1 "Granny.exe") -WorkingDirectory $Test1 `
            -ArgumentList (@("--coop-host", "--coop-name=Host2", "--coop-autoplay", "--coop-testmove", "--coop-port=$Port") + $hostExtra + @("-logFile", $host2Log)) `
            -WindowStyle Minimized -PassThru
        $tracked += $host2Proc
        $hostRelaunched = $true
    }

    $clientElapsed = ((Get-Date) - $clientStart).TotalSeconds

    if (-not $clientDown -and $KillClientAfter -ge 0 -and $clientElapsed -ge $KillClientAfter) {
        Stop-Tracked $clientProc "kill-client test"
        $clientDown = $true
    }

    if ($clientDown -and -not $relaunched -and $RelaunchClientAfter -ge 0 -and
        $clientElapsed -ge $RelaunchClientAfter) {
        Write-Output "relaunching client from $Test2 (port $Port, log $client2Log)"
        $client2Proc = Start-Process -FilePath (Join-Path $Test2 "Granny.exe") `
            -WorkingDirectory $Test2 `
            -ArgumentList (@("--coop-join=127.0.0.1", "--coop-name=Guest2", "--coop-autoplay", "--coop-testmove", "--coop-port=$joinPort") + $clientExtra + @("-logFile", $client2Log)) `
            -WindowStyle Minimized -PassThru
        $tracked += $client2Proc
        $relaunched = $true
    }

    Start-Sleep -Milliseconds 500
}

if ($relayProc) {
    # Final counters before anything is stopped: a "pass" through a profile
    # that forwarded everything proves nothing, so keep seen/dropped in the log.
    Invoke-ChaosCall "stats" @{}
    Invoke-ChaosCall "shutdown" @{}
    Start-Sleep -Milliseconds 500
    Write-Output "chaos artifacts: $chaosDir"
}

foreach ($p in $tracked) {
    Stop-Tracked $p "end of run"
}
Start-Sleep -Seconds 3

# Report the logs written by this run only.
foreach ($dir in @($Test1, $Test2)) {
    $logs = Get-ChildItem (Join-Path $dir "MelonLoader\Logs") -Filter "*.log" -ErrorAction SilentlyContinue |
            Where-Object { $_.LastWriteTime -ge $started } | Sort-Object LastWriteTime
    foreach ($l in $logs) {
        Write-Output ""
        Write-Output "===== $($l.FullName) ($($l.Length) bytes) ====="
        Select-String -Path $l.FullName -Pattern "COOP|ERROR|Exception|CRITICAL|Failed" |
            Select-Object -First $MaxLogLines |
            ForEach-Object { $_.Line }
    }
}

