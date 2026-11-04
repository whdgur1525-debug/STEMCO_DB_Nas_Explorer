param(
    [string]$WorkDir='C:\Users\msi\Documents\Codex\2026-11-04\c\work\isolated-small',
    [string]$Executable='C:\Users\msi\Documents\Codex\2026-11-04\c\outputs\AOI-NasExplorer_0.3.0\AOI-NasExplorer.exe',
    [string]$PgBin='C:\Program Files\PostgreSQL\18\bin'
)
$ErrorActionPreference='Stop'
$dbPath=Join-Path ([IO.Path]::GetFullPath($WorkDir)) 'pgdata'
$marker=Join-Path $WorkDir '.nas-explorer-test-owner'
if(!(Test-Path -LiteralPath $marker) -or (Get-Content -LiteralPath $marker -Raw).Trim() -ne $dbPath){throw 'Local review cluster ownership marker mismatch.'}
if(!(Test-Path -LiteralPath $Executable)){throw 'The prepared NAS Explorer executable is missing.'}
& (Join-Path $PgBin 'pg_ctl.exe') status -D $dbPath
$running=$LASTEXITCODE -eq 0
if(!$running){
    if(Get-NetTCPConnection -LocalPort 55741 -State Listen -ErrorAction SilentlyContinue){throw 'Port 55741 belongs to another process. No process was stopped.'}
    & (Join-Path $PgBin 'pg_ctl.exe') start -D $dbPath -l (Join-Path $WorkDir 'postgres.log') -o '-h 127.0.0.1 -p 55741 -c shared_buffers=32MB -c work_mem=2MB -c maintenance_work_mem=32MB -c max_connections=12 -c max_parallel_workers_per_gather=0 -c max_worker_processes=2' -w -t 30
    if($LASTEXITCODE -ne 0){throw 'Local review database start failed.'}
}
$prior=$env:PGPASSWORD
try{
    $env:PGPASSWORD=(Get-Content -LiteralPath (Join-Path $WorkDir 'viewer-password.local') -Raw).TrimEnd()
    & (Join-Path $PgBin 'psql.exe') -X -h 127.0.0.1 -p 55741 -U nas_viewer -d nas_recovered_20261104_220842 -v ON_ERROR_STOP=1 -c 'SELECT current_database();'
    if($LASTEXITCODE -ne 0){throw 'The read-only local review account could not connect.'}
}finally{$env:PGPASSWORD=$prior}
$version=(Get-Item -LiteralPath $Executable).VersionInfo.ProductVersion.Split('+')[0]
if([version]$version -ge [version]'0.1.2'){
    Start-Process -FilePath $Executable -ArgumentList '--local' -WindowStyle Normal
}else{
    Start-Process -FilePath $Executable -WindowStyle Normal
    Write-Output 'In NAS Explorer, click NAS connection then Connect. The local profile is already saved.'
}
Write-Output 'Local review DB is running at 127.0.0.1:55741. Close NAS Explorer, then use Stop-LocalReview.ps1 when finished.'
