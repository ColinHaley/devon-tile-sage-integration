# Lists assemblies referenced by Sage.Peachtree.API.dll (run in 32-bit PowerShell).
$asm = [System.Reflection.Assembly]::LoadFrom("$PSScriptRoot\..\lib\sage\Sage.Peachtree.API.dll")
$asm.GetReferencedAssemblies() | ForEach-Object { $_.FullName }
