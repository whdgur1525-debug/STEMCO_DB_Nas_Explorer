param(
    [string]$Dotnet='dotnet',
    [string]$Framework='net9.0',
    [switch]$Publish,
    [string]$OutputDir
)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
if($Publish -and $Framework -ne 'net10.0'){throw 'Portable release builds require net10.0. net9.0 is a VS2022 development compatibility target only.'}
& $Dotnet build (Join-Path $root 'AOI-NasExplorer.sln') -c Release -m:1 -p:NasTargetFramework=$Framework -p:UseSharedCompilation=false --disable-build-servers -v:minimal
if($LASTEXITCODE -ne 0){throw 'Build failed'}
if($Publish){
    if([string]::IsNullOrWhiteSpace($OutputDir)){$OutputDir=Join-Path $root 'artifacts\portable-win-x64'}
    & $Dotnet publish (Join-Path $root 'src\Aoi.NasExplorer.App\Aoi.NasExplorer.App.csproj') -c Release -r win-x64 --self-contained true -m:1 -p:NasTargetFramework=$Framework -p:UseSharedCompilation=false -p:PublishSingleFile=false -p:DebugType=None -o $OutputDir --disable-build-servers -v:minimal
    if($LASTEXITCODE -ne 0){throw 'Publish failed'}
    $commit=git -C $root rev-parse HEAD 2>$null
    [pscustomobject]@{BuiltAt=[DateTimeOffset]::Now.ToString('O');Framework=$Framework;Runtime='win-x64';SelfContained=$true;Commit=$commit;Sdk=(& $Dotnet --version)} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDir 'build-receipt.json') -Encoding utf8
    $outputRoot=[IO.Path]::GetFullPath($OutputDir)
    $files=Get-ChildItem -LiteralPath $outputRoot -File -Recurse | Where-Object {$_.FullName -ne (Join-Path $outputRoot 'files.sha256.json')} | ForEach-Object {[pscustomobject]@{File=[IO.Path]::GetRelativePath($outputRoot,$_.FullName);Bytes=$_.Length;SHA256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}}
    $files | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $OutputDir 'files.sha256.json') -Encoding utf8
}
