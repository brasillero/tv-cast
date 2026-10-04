$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$base = $PSScriptRoot
$request = $null
$messages = New-Object 'Collections.Generic.List[string]'
$child = $null
function Save-Result([int]$Code) {
    $result = [ordered]@{
        requestId = $request.requestId
        code = $Code
        sessionId = (Get-Process -Id $PID).SessionId
        messages = @($messages.ToArray())
    }
    $part = Join-Path $base 'result.partial.json'
    $result | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $part -Encoding UTF8
    Move-Item -LiteralPath $part -Destination (Join-Path $base 'result.json') -Force
}
function Read-Receivers {
    Add-Type -AssemblyName System.Runtime.WindowsRuntime
    $null = [Windows.Devices.Enumeration.DeviceInformation, Windows.Devices.Enumeration, ContentType=WindowsRuntime]
    $null = [Windows.Devices.Enumeration.DeviceInformationKind, Windows.Devices.Enumeration, ContentType=WindowsRuntime]
    $null = [Windows.Devices.Enumeration.DeviceInformationCollection, Windows.Devices.Enumeration, ContentType=WindowsRuntime]
    $selector = 'System.Devices.Aep.ProtocolId:="{0407d24e-53de-4c9a-9ba1-9ced54641188}"'
    $properties = [string[]]@('System.Devices.Aep.IsConnected', 'System.Devices.Aep.IsPaired')
    $op = [Windows.Devices.Enumeration.DeviceInformation]::FindAllAsync($selector, $properties, [Windows.Devices.Enumeration.DeviceInformationKind]::AssociationEndpoint)
    $method = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
        $_.Name -eq 'AsTask' -and $_.IsGenericMethod -and $_.GetGenericArguments().Count -eq 1 -and
        $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
    } | Select-Object -First 1
    $future = $method.MakeGenericMethod([Windows.Devices.Enumeration.DeviceInformationCollection]).Invoke($null, @($op))
    if (-not $future.Wait(18000)) {
        try { $op.Cancel() } catch { }
        throw [TimeoutException]::new('Timeout de 18 s ao descobrir receptores Wi-Fi Direct.')
    }
    foreach ($item in $future.Result) {
        [pscustomobject]@{
            name = $item.Name
            id = $item.Id
            connected = [bool]$item.Properties['System.Devices.Aep.IsConnected']
            paired = [bool]$item.Properties['System.Devices.Aep.IsPaired']
        }
    }
}
function Run-Helper([string]$Command) {
    $script:child = Start-Process -FilePath (Join-Path $base 'CastHelper.exe') -ArgumentList $Command -WorkingDirectory $base -PassThru
    if (-not $script:child.WaitForExit(55000)) {
        $script:child.Kill()
        throw [TimeoutException]::new('Timeout de 55 s no auxiliar de UI Automation.')
    }
    if (-not (Test-Path -LiteralPath (Join-Path $base 'helper-result.json'))) {
        throw ('O auxiliar não produziu resultado (código {0}).' -f $script:child.ExitCode)
    }
    $result = Get-Content -LiteralPath (Join-Path $base 'helper-result.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($result.requestId -ne $request.requestId) { throw 'O auxiliar retornou um resultado de outra execução.' }
    $result
}
try {
    $request = Get-Content -LiteralPath (Join-Path $base 'request.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if ((Get-Process -Id $PID).SessionId -eq 0) { throw 'A tarefa iniciou na sessão 0; é necessário um usuário conectado na sessão interativa.' }
    Remove-Item -LiteralPath (Join-Path $base 'helper-result.json') -Force -ErrorAction SilentlyContinue
    if ($request.action -eq 'list') {
        $devices = @(Read-Receivers)
        if ($devices.Count -eq 0) { $messages.Add('Nenhum receptor/candidato Wi-Fi Direct encontrado agora. Ative o modo de espelhamento da TV e tente novamente.') }
        else {
            $messages.Add('Receptores/candidatos Wi-Fi Direct visíveis (o protocolo também inclui dispositivos que não são TVs):')
            foreach ($item in $devices | Sort-Object name) {
                $state = if ($item.connected) { 'conectado' } elseif ($item.paired) { 'pareado' } else { 'disponível' }
                $messages.Add(('  {0} [{1}]' -f $item.name, $state))
            }
        }
        Save-Result 0
        exit 0
    }
    if ($request.action -eq 'status') {
        $probe = Run-Helper 'probe'
        foreach ($line in $probe.messages) { $messages.Add($line) }
        $up = $null
        $monitors = $null
        try {
            $adapters = @(Get-NetAdapter -IncludeHidden | Where-Object { $_.InterfaceDescription -like '*Wi-Fi Direct*' })
            $up = @($adapters | Where-Object { $_.Status -eq 'Up' }).Count -gt 0
            $messages.Add(('Adaptador Wi-Fi Direct Up: {0}.' -f $(if ($up) { 'sim' } else { 'não' })))
        } catch { $messages.Add('Não foi possível consultar os adaptadores: ' + $_.Exception.Message) }
        try {
            $monitors = @(Get-CimInstance Win32_PnPEntity -Filter "PNPClass='Monitor' AND Present=True")
            if ($monitors.Count -eq 0) { $messages.Add('Monitores PnP presentes: nenhum.') }
            else { $messages.Add('Monitores PnP presentes: ' + (($monitors | ForEach-Object { $_.Name }) -join ', ') + '. (PnP sozinho não identifica Miracast.)') }
        } catch { $messages.Add('Não foi possível consultar os monitores PnP: ' + $_.Exception.Message) }
        if (-not $probe.connected -and $up) { $messages.Add('Wi-Fi Direct está ativo, mas nenhuma projeção Miracast foi confirmada; o adaptador também pode ter outros usos.') }
        Save-Result ([int]$probe.code)
        exit ([int]$probe.code)
    }
    $helper = Run-Helper 'ui'
    foreach ($line in $helper.messages) { $messages.Add($line) }
    Save-Result ([int]$helper.code)
    exit ([int]$helper.code)
} catch {
    $messages.Add('Erro: ' + $_.Exception.Message)
    $code = if ($_.Exception -is [TimeoutException]) { 124 } else { 3 }
    if ($null -ne $request) { Save-Result $code }
    exit $code
} finally {
    if ($null -ne $child -and -not $child.HasExited) { $child.Kill() }
}
