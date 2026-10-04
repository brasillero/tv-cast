$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$base = $PSScriptRoot
$task = $null
$running = $null
$mutex = New-Object Threading.Mutex($false, ('Local\tv-cast-' + [Security.Principal.WindowsIdentity]::GetCurrent().User.Value))
$locked = $false
try {
    try { $locked = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $locked = $true }
    if (-not $locked) { [Console]::Error.WriteLine('Erro: outro tv-cast está em execução no Windows.'); exit 4 }
    $request = Get-Content -LiteralPath (Join-Path $base 'request.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $resultPath = Join-Path $base 'result.json'
    Remove-Item -LiteralPath $resultPath -Force -ErrorAction SilentlyContinue
    $service = New-Object -ComObject Schedule.Service
    $service.Connect()
    $folder = $service.GetFolder('\')
    $previous = @($folder.GetTasks(0) | Where-Object { $_.Name -eq 'tv-cast' })
    if ($previous.Count -gt 0 -and ($previous[0].State -eq 4 -or $previous[0].State -eq 2)) {
        throw 'A tarefa tv-cast ainda está em execução; aguarde o limite de 85 s.'
    }
    $exe = Join-Path $base 'CastHelper.exe'
    $source = Join-Path $base 'CastHelper.cs'
    $hashPath = Join-Path $base 'build.sha256'
    $hash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
    $oldHash = if (Test-Path -LiteralPath $hashPath) { (Get-Content -LiteralPath $hashPath -Raw).Trim() } else { '' }
    if (-not (Test-Path -LiteralPath $exe) -or $hash -ne $oldHash) {
        Write-Output 'Compilando o auxiliar C# do Windows...'
        $candidate = Join-Path $base 'CastHelper.build.exe'
        Remove-Item -LiteralPath $candidate -Force -ErrorAction SilentlyContinue
        try {
            $refs = @('UIAutomationClient', 'UIAutomationTypes', 'System.Windows.Forms', 'System.Drawing', 'WindowsBase', 'System.Web.Extensions')
            $framework = [Runtime.InteropServices.RuntimeEnvironment]::GetRuntimeDirectory()
            $env:TEMP = $base
            $env:TMP = $base
            $refs = @($refs | ForEach-Object {
                $path = Join-Path $framework ($_ + '.dll')
                if (-not (Test-Path -LiteralPath $path)) { $path = Join-Path (Join-Path $framework 'WPF') ($_ + '.dll') }
                $path
            })
            Add-Type -Path $source -OutputAssembly $candidate -OutputType WindowsApplication -ReferencedAssemblies $refs
            Move-Item -LiteralPath $candidate -Destination $exe -Force
            Set-Content -LiteralPath $hashPath -Value $hash -Encoding ASCII
        } finally {
            Remove-Item -LiteralPath $candidate -Force -ErrorAction SilentlyContinue
        }
    }
    $user = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    $definition = $service.NewTask(0)
    $definition.RegistrationInfo.Description = 'tv-cast: automação Miracast interativa iniciada pelo WSL.'
    $definition.Principal.UserId = $user
    $definition.Principal.LogonType = 3
    $definition.Principal.RunLevel = 0
    $definition.Settings.Enabled = $true
    $definition.Settings.AllowDemandStart = $true
    $definition.Settings.DisallowStartIfOnBatteries = $false
    $definition.Settings.StopIfGoingOnBatteries = $false
    $definition.Settings.ExecutionTimeLimit = 'PT85S'
    $definition.Settings.MultipleInstances = 2
    $action = $definition.Actions.Create(0)
    $action.Path = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $action.Arguments = '-NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "' + (Join-Path $base 'worker.ps1') + '"'
    $action.WorkingDirectory = $base
    $task = $folder.RegisterTaskDefinition('tv-cast', $definition, 6, $user, $null, 3, $null)
    $running = $task.Run($null)
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ($watch.Elapsed.TotalSeconds -lt 78) {
        if (Test-Path -LiteralPath $resultPath) {
            $result = Get-Content -LiteralPath $resultPath -Raw -Encoding UTF8 | ConvertFrom-Json
            if ($result.requestId -eq $request.requestId) {
                foreach ($line in $result.messages) { Write-Output $line }
                exit [int]$result.code
            }
        }
        Start-Sleep -Milliseconds 250
        if ($task.State -ne 4 -and $task.State -ne 2 -and $watch.Elapsed.TotalSeconds -gt 3) {
            # O arquivo pode ter sido gravado entre a leitura e Refresh.
            if (Test-Path -LiteralPath $resultPath) { continue }
            throw ('A tarefa terminou sem resultado (código Windows: {0}). Verifique se o usuário está conectado.' -f $task.LastTaskResult)
        }
    }
    $running.Stop()
    [Console]::Error.WriteLine('Erro: timeout da tarefa interativa. Verifique se o Windows está desbloqueado.')
    exit 124
} catch {
    [Console]::Error.WriteLine('Erro: ' + $_.Exception.Message)
    exit 3
} finally {
    if ($locked) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
