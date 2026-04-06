function RunSign {
    param (
        [string]$p
    )
    Write-Host $p

    $args = 'sign /tr http://timestamp.sectigo.com?td=sha256 /td SHA256 /fd SHA256 /a "' + $p + '"'
    Start-Process "C:\Program Files (x86)\Windows Kits\10\bin\10.0.22621.0\x64\signtool.exe" -ArgumentList $args -Wait
    Write-Host $args
}

cd "C:\Users\polarwolves\Documents\GitHub\PolarWolves\PolarWolves-Client\bin\x64\Release\PolarWolves"

$objects = Get-ChildItem PolarWolves*.exe,PolarWolves*.dll,runas.exe,_First* -Recurse | ForEach-object {Get-AuthenticodeSignature $_} | Where-Object {$_.status -eq "NotSigned"}

DO {
    Write-Host "Signing: $($objects.Length) binaries"
    foreach ($o in $objects)
    {
        ForEach-Object { RunSign($o.path)}
    }
    $objects = Get-ChildItem PolarWolves*.exe,PolarWolves*.dll,runas.exe,_First* -Recurse | ForEach-object {Get-AuthenticodeSignature $_} | Where-Object {$_.status -eq "NotSigned"}
} While ($objects.Length â€“ge 1)
