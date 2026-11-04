param(
    [Parameter(Mandatory=$true)][string]$WorkDir,
    [Parameter(Mandatory=$true)][string]$SchemaFile,
    [Parameter(Mandatory=$true)][string]$DumpFile,
    [Parameter(Mandatory=$true)][string]$Dotnet,
    [string]$PgBin='C:\Program Files\PostgreSQL\18\bin',
    [int]$Port=55741
)
$ErrorActionPreference='Stop'
$k=[DateTimeOffset]::UtcNow.ToOffset([TimeSpan]::FromHours(9))
if($k.Date -eq [datetime]'2026-11-04' -and $k.Hour -lt 22){throw 'Do not run before 22:00 KST on 2026-11-04.'}
$os=Get-CimInstance Win32_OperatingSystem
if($os.FreePhysicalMemory -lt 8GB/1KB){throw 'Less than 8GB RAM available. Defer large tests.'}
$drive=Get-PSDrive -Name ([IO.Path]::GetPathRoot([IO.Path]::GetFullPath($WorkDir)).Substring(0,1))
if($drive.Free -lt 20GB){throw 'At least 20GB free disk is required.'}
$root=Split-Path $PSScriptRoot -Parent
$workPath=[IO.Path]::GetFullPath($WorkDir)
$dbPath=Join-Path $workPath 'pgdata'
$priorPgPassword=$env:PGPASSWORD
try{
    & (Join-Path $PSScriptRoot 'Test-Isolated.ps1') -WorkDir $workPath -SchemaFile $SchemaFile -PgBin $PgBin -Port $Port -Dotnet $Dotnet -Framework net10.0 -Stress -KeepServer
    $uiDll=Join-Path $root 'src\Aoi.NasExplorer.App\bin\Release\net10.0-windows\AOI-NasExplorer.dll'
    $uiStress=Join-Path $workPath ('ui-stress-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
    $uiProcess=Start-Process -FilePath $Dotnet -ArgumentList @($uiDll,'--stress-ui',$uiStress) -WindowStyle Hidden -Wait -PassThru
    if($uiProcess.ExitCode -ne 0){throw 'Large UI verification failed'}
    $passwordFile=Join-Path $workPath 'password.local'
    $env:PGPASSWORD=(Get-Content -LiteralPath $passwordFile -Raw).TrimEnd()
    $database='nas_recovered_'+(Get-Date -Format 'yyyyMMdd_HHmmss')
    & (Join-Path $PgBin 'psql.exe') -h 127.0.0.1 -p $Port -U nas_test -d postgres -v ON_ERROR_STOP=1 -c "CREATE DATABASE $database"
    if($LASTEXITCODE -ne 0){throw 'Private restored DB creation failed'}
    $recovery=Join-Path $workPath ('recovered-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
    New-Item -ItemType Directory -Path $recovery -Force | Out-Null
    & (Join-Path $PgBin 'pg_restore.exe') -h 127.0.0.1 -p $Port -U nas_test -d $database --no-owner --no-privileges --no-subscriptions --no-publications --no-tablespaces --exit-on-error --single-transaction $DumpFile 2> (Join-Path $recovery 'restore.log')
    if($LASTEXITCODE -ne 0){throw 'Recovered dump restore failed; see restore.log'}
    [pscustomobject]@{Database=$database;Dump=[IO.Path]::GetFullPath($DumpFile);SHA256=(Get-FileHash -LiteralPath $DumpFile -Algorithm SHA256).Hash;RestoredAt=[DateTimeOffset]::Now.ToString('O');ExternalReplicationExcluded=$true} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $recovery 'restore-receipt.json') -Encoding utf8
    $env:NAS_RECOVERED_DATABASE=$database
    $env:NAS_TEST_PORT=$Port
    $env:NAS_TEST_PASSWORD_FILE=$passwordFile
    $env:NAS_TEST_OUTPUT=$recovery
    & $Dotnet (Join-Path $root 'tests\Aoi.NasExplorer.Tests\bin\Release\net10.0\Aoi.NasExplorer.Tests.dll') --recovered
    if($LASTEXITCODE -ne 0){throw 'Recovered data verification failed'}
    $uiDll=Join-Path $root 'src\Aoi.NasExplorer.App\bin\Release\net10.0-windows\AOI-NasExplorer.dll'
    $uiProcess=Start-Process -FilePath $Dotnet -ArgumentList @($uiDll,'--recovered-ui',$recovery) -WindowStyle Hidden -Wait -PassThru
    if($uiProcess.ExitCode -ne 0){throw 'Recovered UI verification failed'}
    Write-Output ('RECOVERED RESULTS: '+$recovery)
}finally{
    $env:PGPASSWORD=$priorPgPassword
    $ownerFile=Join-Path $workPath '.nas-explorer-test-owner'
    if((Test-Path -LiteralPath $ownerFile) -and (Get-Content -LiteralPath $ownerFile -Raw).Trim() -eq $dbPath){
        & (Join-Path $PgBin 'pg_ctl.exe') stop -D $dbPath -m fast -w -t 30
        if($LASTEXITCODE -ne 0){Write-Warning 'Inspect the owned test cluster; shutdown was not confirmed.'}
    }
}
