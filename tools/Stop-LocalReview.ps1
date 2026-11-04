param(
    [string]$WorkDir='C:\Users\msi\Documents\Codex\2026-11-04\c\work\isolated-small',
    [string]$PgBin='C:\Program Files\PostgreSQL\18\bin'
)
$ErrorActionPreference='Stop'
$dbPath=Join-Path ([IO.Path]::GetFullPath($WorkDir)) 'pgdata'
$marker=Join-Path $WorkDir '.nas-explorer-test-owner'
if(!(Test-Path -LiteralPath $marker) -or (Get-Content -LiteralPath $marker -Raw).Trim() -ne $dbPath){throw 'Local review cluster ownership marker mismatch.'}
& (Join-Path $PgBin 'pg_ctl.exe') status -D $dbPath
if($LASTEXITCODE -eq 3){Write-Output 'Local review DB is already stopped.';exit 0}
if($LASTEXITCODE -ne 0){throw 'Could not determine local review cluster status.'}
& (Join-Path $PgBin 'pg_ctl.exe') stop -D $dbPath -m fast -w -t 30
if($LASTEXITCODE -ne 0){throw 'Local review DB stop failed.'}
& (Join-Path $PgBin 'pg_ctl.exe') status -D $dbPath
if($LASTEXITCODE -ne 3){throw 'Local review DB shutdown was not confirmed.'}
Write-Output 'Local review DB stopped. No database files were deleted.'
exit 0
