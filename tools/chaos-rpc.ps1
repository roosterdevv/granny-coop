<#
Thin client for the JSON-RPC control port of tools/chaos/chaos.py relay.
Kept in its own file so it can be exercised without launching the games:

  . .\chaos-rpc.ps1
  Invoke-Chaos 12345 "status" @{}

The relay speaks newline-delimited JSON-RPC 2.0 over TCP on loopback. One
connection is reused for every call in a run.
#>

$script:ChaosSock = $null
$script:ChaosReader = $null
$script:ChaosWriter = $null
$script:ChaosId = 0

function Close-Chaos {
    if ($script:ChaosSock) { try { $script:ChaosSock.Close() } catch { } }
    $script:ChaosSock = $null
    $script:ChaosReader = $null
    $script:ChaosWriter = $null
}

function Connect-Chaos([int]$Port) {
    Close-Chaos
    $c = New-Object System.Net.Sockets.TcpClient("127.0.0.1", $Port)
    $c.ReceiveTimeout = 5000
    $c.SendTimeout = 5000
    $s = $c.GetStream()
    $script:ChaosSock = $c
    $script:ChaosWriter = New-Object System.IO.StreamWriter($s, (New-Object System.Text.UTF8Encoding($false)))
    $script:ChaosWriter.NewLine = "`n"
    $script:ChaosReader = New-Object System.IO.StreamReader($s)
}

function Invoke-Chaos([int]$Port, [string]$Method, [hashtable]$Params) {
    # The JSON goes over a socket, never through a native command line: Windows
    # PowerShell 5.1 drops the double quotes from arguments passed to an exe,
    # which turns every relay call into a JSON parse error.
    if (-not $Params) { $Params = @{} }
    try {
        if (-not $script:ChaosSock) { Connect-Chaos $Port }
        $script:ChaosId++
        $req = @{ jsonrpc = "2.0"; id = $script:ChaosId; method = $Method; params = $Params } |
               ConvertTo-Json -Compress -Depth 6
        $script:ChaosWriter.WriteLine($req)
        $script:ChaosWriter.Flush()
        $line = $script:ChaosReader.ReadLine()
        if (-not $line) { throw "relay closed the control connection" }
        $resp = $line | ConvertFrom-Json
        if ($resp.PSObject.Properties.Name -contains "error") {
            Write-Output "chaos call $Method FAILED: $($resp.error.message)"
            return $null
        }
        Write-Output "chaos call $Method $(($Params | ConvertTo-Json -Compress)) -> $($resp.result | ConvertTo-Json -Compress -Depth 8)"
    } catch {
        Write-Output "chaos call $Method FAILED: $($_.Exception.Message)"
        Close-Chaos
    }
}
