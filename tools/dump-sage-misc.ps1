# One-off: dump PhoneNumber members and public exception type names (32-bit PowerShell).
$asm = [System.Reflection.Assembly]::LoadFrom("$PSScriptRoot\..\lib\sage\Sage.Peachtree.API.dll")
try { $types = $asm.GetTypes() }
catch [System.Reflection.ReflectionTypeLoadException] { $types = $_.Exception.Types | Where-Object { $null -ne $_ } }

$pn = $types | Where-Object { $_.FullName -eq 'Sage.Peachtree.API.PhoneNumber' }
Write-Output "--- $($pn.FullName) ---"
$pn.GetProperties() | ForEach-Object { Write-Output "  prop $($_.PropertyType.Name) $($_.Name) canwrite=$($_.CanWrite)" }

Write-Output '--- exception types ---'
$types | Where-Object { $_.IsPublic -and [System.Exception].IsAssignableFrom($_) } | ForEach-Object { Write-Output "  $($_.FullName)" }
