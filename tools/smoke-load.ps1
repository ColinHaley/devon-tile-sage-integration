# Smoke test: load the built exe in a 32-bit CLR and force-resolve every type,
# which verifies the Sage/Stripe/Newtonsoft references bind correctly.
$exe = "$PSScriptRoot\..\src\SageStripeSync\bin\Release\SageStripeSync.exe"
$asm = [System.Reflection.Assembly]::LoadFrom((Resolve-Path $exe))
Write-Output ("loaded: " + $asm.FullName)
try {
    $types = $asm.GetTypes()
    Write-Output ("types resolved: " + $types.Count)
    $types | ForEach-Object { $_.FullName } | Sort-Object
}
catch [System.Reflection.ReflectionTypeLoadException] {
    Write-Output "TYPE LOAD FAILURES:"
    $_.Exception.LoaderExceptions | Select-Object -First 10 | ForEach-Object { Write-Output ("  " + $_.Message) }
    exit 1
}
