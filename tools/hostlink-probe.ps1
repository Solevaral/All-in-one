# Отладка модуля без каркаса: подключается к каналу и отправляет методы протокола HostLink по очереди.
#   hostlink-probe.ps1 -Pipe AllInOne.fdimmer -Methods hello,getStatus,shutdown
param(
    [Parameter(Mandatory)][string]$Pipe,
    [string[]]$Methods = @("hello", "getStatus"),
    [int]$TimeoutMs = 5000
)

$client = New-Object System.IO.Pipes.NamedPipeClientStream(".", $Pipe, [System.IO.Pipes.PipeDirection]::InOut)
$client.Connect($TimeoutMs)
$utf8 = New-Object System.Text.UTF8Encoding($false)
$reader = New-Object System.IO.StreamReader($client, $utf8)
$writer = New-Object System.IO.StreamWriter($client, $utf8)
$writer.AutoFlush = $true
$writer.NewLine = "`n"

$id = 0
foreach ($m in $Methods) {
    $id++
    $name, $params = $m -split ':', 2
    $request = if ($params) { "{""id"":$id,""method"":""$name"",""params"":$params}" } else { "{""id"":$id,""method"":""$name""}" }
    Write-Output ">> $request"
    $writer.WriteLine($request)
    # Пропускаем события (statusChanged/notify), пока не придёт ответ на наш id.
    while ($true) {
        $task = $reader.ReadLineAsync()
        if (-not $task.Wait($TimeoutMs)) { Write-Output "<< (нет ответа за $TimeoutMs мс)"; break }
        $line = $task.Result
        if ($null -eq $line) { Write-Output "<< (канал закрыт)"; break }
        Write-Output "<< $line"
        if ($line -match """id"":$id\b") { break }
    }
}
$client.Dispose()
