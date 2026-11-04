param(
    [Parameter(Mandatory=$true)][string]$WorkDir,
    [Parameter(Mandatory=$true)][string]$SchemaFile,
    [string]$PgBin='C:\Program Files\PostgreSQL\18\bin',
    [int]$Port=55741,
    [string]$Dotnet='dotnet',
    [string]$Framework='net9.0',
    [switch]$Stress,
    [switch]$KeepServer
)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$workPath=[IO.Path]::GetFullPath($WorkDir)
$dbPath=Join-Path $workPath 'pgdata'
$ownerFile=Join-Path $workPath '.nas-explorer-test-owner'
$passwordFile=Join-Path $workPath 'password.local'
if($Port -lt 55000 -or $Port -gt 55999){throw 'Only a private test port in 55000..55999 is permitted.'}
if($Stress){
    $k=[DateTimeOffset]::UtcNow.ToOffset([TimeSpan]::FromHours(9))
    if($k.Date -eq [datetime]'2026-11-04' -and $k.Hour -lt 22){throw 'Large tests are prohibited before 22:00 KST on 2026-11-04.'}
    $os=Get-CimInstance Win32_OperatingSystem
    if($os.FreePhysicalMemory -lt 8GB/1KB){throw 'At least 8GB available RAM is required for large tests.'}
}
New-Item -ItemType Directory -Path $workPath -Force | Out-Null
if(Test-Path -LiteralPath $dbPath){
    if(!(Test-Path -LiteralPath $ownerFile) -or (Get-Content -LiteralPath $ownerFile -Raw).Trim() -ne $dbPath){throw 'The existing cluster is not owned by this test harness.'}
}else{
    if(Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue){throw 'Test port is already in use.'}
    $password=[Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(24))
    Set-Content -LiteralPath $passwordFile -Value $password -NoNewline -Encoding ascii
    & (Join-Path $PgBin 'initdb.exe') -D $dbPath -U nas_test --pwfile=$passwordFile --auth-host=scram-sha-256 --auth-local=trust --encoding=UTF8 --locale=C
    if($LASTEXITCODE -ne 0){throw 'initdb failed'}
    Set-Content -LiteralPath $ownerFile -Value $dbPath -Encoding utf8
}
& (Join-Path $PgBin 'pg_ctl.exe') status -D $dbPath *> $null
$wasRunning=$LASTEXITCODE -eq 0
if(!$wasRunning){
    & (Join-Path $PgBin 'pg_ctl.exe') start -D $dbPath -l (Join-Path $workPath 'postgres.log') -o "-h 127.0.0.1 -p $Port -c shared_buffers=32MB -c work_mem=2MB -c maintenance_work_mem=32MB -c max_connections=12 -c max_parallel_workers_per_gather=0 -c max_worker_processes=2" -w -t 30
    if($LASTEXITCODE -ne 0){throw 'Private PostgreSQL start failed'}
}
$env:NAS_TEST_PORT=$Port
$env:NAS_TEST_PASSWORD_FILE=$passwordFile
$env:NAS_TEST_SCHEMA=[IO.Path]::GetFullPath($SchemaFile)
$env:NAS_TEST_OUTPUT=Join-Path $workPath ('results-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
try{
    & $Dotnet build (Join-Path $root 'tests\Aoi.NasExplorer.Tests\Aoi.NasExplorer.Tests.csproj') -c Release -m:1 -p:NasTargetFramework=$Framework -p:UseSharedCompilation=false --disable-build-servers -v:minimal
    if($LASTEXITCODE -ne 0){throw 'Test build failed'}
    $dll=Join-Path $root "tests\Aoi.NasExplorer.Tests\bin\Release\$Framework\Aoi.NasExplorer.Tests.dll"
    $testArgs=@($dll,'--integration')
    if($Stress){$testArgs+='--stress'}
    & $Dotnet @testArgs
    if($LASTEXITCODE -ne 0){throw 'Tests failed; see test-results.json'}
    Write-Output ('RESULTS: '+$env:NAS_TEST_OUTPUT)
}finally{
    if(!$wasRunning -and !$KeepServer){
        & (Join-Path $PgBin 'pg_ctl.exe') stop -D $dbPath -m fast -w -t 30
        if($LASTEXITCODE -ne 0){Write-Warning 'Private test cluster did not stop. Inspect the owned work directory.'}
    }
}
