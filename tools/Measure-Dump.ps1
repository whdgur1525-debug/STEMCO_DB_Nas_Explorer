param(
    [Parameter(Mandatory=$true)][string]$DumpFile,
    [Parameter(Mandatory=$true)][string]$OutputFile,
    [string]$PgRestore='C:\Program Files\PostgreSQL\18\bin\pg_restore.exe'
)
$ErrorActionPreference='Stop'
$start=[Diagnostics.ProcessStartInfo]::new()
$start.FileName=$PgRestore
$start.UseShellExecute=$false
$start.CreateNoWindow=$true
$start.RedirectStandardOutput=$true
$start.RedirectStandardError=$true
foreach($argument in @('--data-only','--table=defects','--file=-','--no-owner','--no-privileges','--no-subscriptions','--no-publications',$DumpFile)){$start.ArgumentList.Add($argument)}
$process=[Diagnostics.Process]::Start($start)
$errors=$process.StandardError.ReadToEndAsync()
$reading=$false
$imageIndex=-1
[long]$rows=0
[long]$imageBytes=0
[long]$largestImage=0
$clock=[Diagnostics.Stopwatch]::StartNew()
try{
    while($null -ne ($line=$process.StandardOutput.ReadLine())){
        if($line.StartsWith('COPY public.defects ')){
            $columns=([regex]::Match($line,'\((.*)\) FROM').Groups[1].Value -replace '"','').Split(',') | ForEach-Object {$_.Trim()}
            $imageIndex=[array]::IndexOf($columns,'image')
            if($imageIndex -lt 0){throw 'The defects image column is missing.'}
            $reading=$true
            continue
        }
        if($line -eq '\.'){$reading=$false;continue}
        if(!$reading){continue}
        $fields=$line.Split("`t")
        $image=$fields[$imageIndex]
        [long]$size=0
        if($image -ne '\N'){
            if(!$image.StartsWith('\\x') -or ($image.Length-3)%2 -ne 0){throw 'Unexpected COPY bytea representation.'}
            $size=($image.Length-3)/2
        }
        $imageBytes+=$size
        $largestImage=[math]::Max($largestImage,$size)
        $rows++
    }
    $process.WaitForExit()
    $errorText=$errors.GetAwaiter().GetResult()
    if($process.ExitCode -ne 0){throw ('pg_restore stream failed: '+$errorText)}
    if($imageIndex -lt 0){throw 'No defects COPY section found.'}
    $result=[pscustomobject]@{
        Dump=[IO.Path]::GetFullPath($DumpFile)
        DumpBytes=(Get-Item -LiteralPath $DumpFile).Length
        DumpSHA256=(Get-FileHash -LiteralPath $DumpFile -Algorithm SHA256).Hash
        Rows=$rows
        ImageBytes=$imageBytes
        LargestImageBytes=$largestImage
        ConservativeRestoreBudgetBytes=[long]($imageBytes*1.5+$rows*4096+2GB)
        Seconds=$clock.Elapsed.TotalSeconds
        Method='Read-only pg_restore COPY stream; no decompressed dump file or database created.'
    }
    New-Item -ItemType Directory -Path (Split-Path ([IO.Path]::GetFullPath($OutputFile)) -Parent) -Force | Out-Null
    $result|ConvertTo-Json|Set-Content -LiteralPath $OutputFile -Encoding utf8
    $result|ConvertTo-Json
}finally{if(!$process.HasExited){$process.Kill();$process.WaitForExit()};$process.Dispose()}
