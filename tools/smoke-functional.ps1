# Functional smoke test (32-bit PowerShell): exercises Money rounding,
# AppConfig JSON round-trip, and the DPAPI SecretStore without touching
# Sage or Stripe.
$exe = "$PSScriptRoot\..\src\SageStripeSync\bin\Release\SageStripeSync.exe"
$asm = [System.Reflection.Assembly]::LoadFrom((Resolve-Path $exe))
$fail = 0

function Check($name, $actual, $expected) {
    if ("$actual" -eq "$expected") { Write-Output "PASS $name" }
    else { Write-Output "FAIL $name : got '$actual', expected '$expected'"; $script:fail = 1 }
}

# Money.ToCents: controlled away-from-zero rounding
$money = $asm.GetType('SageStripeSync.Money')
$toCents = $money.GetMethod('ToCents')
Check 'ToCents(10.00)'  $toCents.Invoke($null, @([decimal]10.00))   1000
Check 'ToCents(0.005)'  $toCents.Invoke($null, @([decimal]0.005))   1
Check 'ToCents(-0.005)' $toCents.Invoke($null, @([decimal]-0.005))  -1
Check 'ToCents(123.456)' $toCents.Invoke($null, @([decimal]123.456)) 12346
Check 'ToCents(0.1+0.2 style)' $toCents.Invoke($null, @([decimal]0.30000000001)) 30

# AppConfig defaults + JSON round-trip
$cfgType = $asm.GetType('SageStripeSync.AppConfig')
$cfg = [Activator]::CreateInstance($cfgType)
Check 'default fallback email' $cfg.FallbackCustomerEmail 'contact@devontile.com'
Check 'default clearing acct'  $cfg.StripeClearingAccount 'Stripe Clearing'
Check 'default mode'           $cfg.StripeMode 'test'
Check 'default field index'    $cfg.CustomerStripeIdFieldIndex 1
$cfg.Save()
$loaded = $cfgType.GetMethod('Load').Invoke($null, @())
Check 'config round-trip' $loaded.FallbackCustomerEmail 'contact@devontile.com'

# SecretStore DPAPI round-trip
$ssType = $asm.GetType('SageStripeSync.SecretStore')
$ss = [Activator]::CreateInstance($ssType)
$ss.StripeTestKey = 'sk_test_smoketest123'
$ss.Save()
$ss2 = $ssType.GetMethod('Load').Invoke($null, @())
Check 'secret DPAPI round-trip' $ss2.StripeTestKey 'sk_test_smoketest123'
$secretsFile = Join-Path $cfgType.GetProperty('ConfigDirectory').GetValue($null, $null) 'secrets.bin'
$raw = [System.IO.File]::ReadAllBytes($secretsFile)
$plainVisible = [System.Text.Encoding]::UTF8.GetString($raw) -match 'sk_test_smoketest123'
Check 'secret encrypted at rest' $plainVisible 'False'
# clean up the test secret so it doesn't linger
$ss3 = [Activator]::CreateInstance($ssType); $ss3.Save()

# StripeGateway rejects a missing key with a clear message
$sgType = $asm.GetType('SageStripeSync.StripeGateway')
try {
    [Activator]::CreateInstance($sgType, @('', 'usd')) | Out-Null
    Write-Output 'FAIL empty-key guard: no exception'; $fail = 1
} catch {
    $inner = $_.Exception.InnerException
    if ($inner -and $inner.Message -match 'No Stripe API key') { Write-Output 'PASS empty-key guard' }
    else { Write-Output "FAIL empty-key guard: $($inner.Message)"; $fail = 1 }
}

exit $fail
