# Dumps the public members of the Sage.Peachtree.API types this project uses.
# Must run in 32-bit PowerShell (the assembly is x86-only):
#   C:\Windows\SysWOW64\WindowsPowerShell\v1.0\powershell.exe -NoProfile -File tools\dump-sage-api.ps1
param(
    [string]$DllPath = "$PSScriptRoot\..\lib\sage\Sage.Peachtree.API.dll",
    [string]$OutPath = "$PSScriptRoot\..\docs\sage-api\reflected-api.txt"
)

$asm = [System.Reflection.Assembly]::LoadFrom((Resolve-Path $DllPath))
try { $types = $asm.GetTypes() }
catch [System.Reflection.ReflectionTypeLoadException] { $types = $_.Exception.Types | Where-Object { $null -ne $_ } }

# The types the integration touches; everything else is noise.
$wanted = @(
    'PeachtreeSession', 'Company', 'CompanyFactoryGroup', 'CompanyIdentifier', 'CompanyIdentifierList',
    'AuthorizationResult',
    'CustomerFactory', 'Customer', 'CustomerList', 'Contact', 'Address', 'PhoneNumberCollection',
    'CustomFieldValue', 'CustomFieldValueCollection', 'CustomFieldDefinition',
    'SalesInvoiceFactory', 'SalesInvoice', 'SalesInvoiceList', 'SalesInvoiceSalesLine',
    'ReceiptFactory', 'Receipt', 'ReceiptInvoiceLine', 'ReceiptSalesLine',
    'AccountFactory', 'Account', 'AccountList', 'AccountClassification',
    'GeneralJournalEntryFactory', 'GeneralJournalEntry', 'GeneralJournalEntryLine',
    'EntityReference', 'EntityReference`1', 'LoadModifiers', 'FilterExpression',
    'Transaction', 'NameAndAddress', 'PaymentMethod'
)

$sb = New-Object System.Text.StringBuilder
foreach ($t in ($types | Where-Object { $_.IsPublic -and ($wanted -contains $_.Name) } | Sort-Object FullName)) {
    [void]$sb.AppendLine('=======================================================')
    $kind = if ($t.IsEnum) { 'enum' } elseif ($t.IsInterface) { 'interface' } elseif ($t.IsValueType) { 'struct' } else { 'class' }
    [void]$sb.AppendLine("$kind $($t.FullName)  (base: $($t.BaseType))")
    if ($t.IsEnum) {
        foreach ($n in [System.Enum]::GetNames($t)) {
            [void]$sb.AppendLine("  $n = $([int][System.Enum]::Parse($t, $n))")
        }
        continue
    }
    foreach ($p in ($t.GetProperties() | Sort-Object Name)) {
        $acc = @(); if ($p.CanRead) { $acc += 'get' }; if ($p.CanWrite) { $acc += 'set' }
        [void]$sb.AppendLine("  prop $($p.PropertyType.FullName) $($p.Name) { $($acc -join '; ') }")
    }
    foreach ($m in ($t.GetMethods() | Where-Object { -not $_.IsSpecialName -and $_.DeclaringType -ne [object] } | Sort-Object Name)) {
        $params = ($m.GetParameters() | ForEach-Object { "$($_.ParameterType.FullName) $($_.Name)" }) -join ', '
        [void]$sb.AppendLine("  method $($m.ReturnType.FullName) $($m.Name)($params)")
    }
}
$sb.ToString() | Out-File -FilePath $OutPath -Encoding utf8
Write-Output "wrote $OutPath ($((Get-Item $OutPath).Length) bytes)"
