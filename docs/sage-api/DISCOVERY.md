# R4 Discovery Spike — Findings

Date: 2026-06-11. Source: SDK docs installed at
`C:\Program Files (x86)\Sage\Sage 50 2026.0 SDK` (CHM reference decompiled; raw
extracted pages live alongside this file as `*.txt`).

## Headline finding: the SDK is the .NET API, not the COM interop

The TRD (Section 2.2) assumed `Interop.PeachwServer.dll` (COM). **That DLL does
not exist in the installed SDK.** What Sage ships for 2026 is the **Sage 50
.NET SDK**: a managed assembly `Sage.Peachtree.API.dll`, version
**2026.0.0.82**, documented by `Sage50DotNETSDK.chm` and
`SagePeachtreeAPIDocumentation.chm`.

Consequences (all favorable):

- No COM interop at all — plain managed references, no "Embed Interop Types"
  concern.
- Per the Getting Started doc: the DLL to reference is **in the Sage 50
  program install folder** (e.g. `C:\Program Files (x86)\Sage\Peachtree\`),
  *not* in the SDK docs folder. The build resolves it via the configurable
  `SageSdkPath` HintPath in the csproj.
- The dev machine does not have Sage 50 installed (only the SDK docs), so the
  project compiles against a generated **stub assembly** locally; on the
  target machine the reference resolves to the real DLL. The stub contains
  only the verified signatures below.

## Verified API surface (from the 2026.0 reference CHM)

### Session / authorization

- `PeachtreeSession` (sealed, IDisposable): `Begin(string applicationIdentifier)`
  (blank ID = sample companies only; real ID required otherwise; throws
  `SessionAlreadyOpenedException`, `ApplicationIdentifierExpiredException`),
  `CompanyList()` / `CompanyList(string server)` → `CompanyIdentifierList`,
  `LookupCompanyIdentifier(...)`, `VerifyAccess(CompanyIdentifier)`,
  `RequestAccess(CompanyIdentifier)`, `Open(CompanyIdentifier)` → `Company`,
  `Close(...)`, `End()`, `Dispose()`, property `SessionActive`.
- `AuthorizationResult` enum: `None=0, Pending=1, Denied=2, Granted=3,
  CorruptedOrTampered=4, LoginRestricted=5, CompanyLocked=6, NoCredentials=7`.
  Flow: `VerifyAccess` → if `NoCredentials` call `RequestAccess` (user approves
  inside Sage) → poll until `Granted`.

### Company and factories

- `Company` (sealed, IDisposable): `Factories` (→ `CompanyFactoryGroup`),
  `CompanyIdentifier`, `IsClosed`, `Close()`.
- `Company.Factories.CustomerFactory / SalesInvoiceFactory / ReceiptFactory /
  AccountFactory / GeneralJournalEntryFactory ...` — each exposes
  `Create()`, `List()`, `Load(EntityReference)`.

### Lists and filtering

- `CustomerList` / `SalesInvoiceList` / `AccountList` etc.:
  `Load()` or `Load(LoadModifiers)`, enumerable.
- `FilterExpression`: static `Equal(...)`, `Property(Type, string path)`,
  `Constant(object)`, `StringConstant(...)`, `AndAlso`, `OrElse`, `Contains`...
  Used with `LoadModifiers` (`.Create()`, set `.Filters`).
  Note: some properties are documented "cannot be used for filtering or
  sorting" (e.g. `SalesInvoice.SalesTaxAmount`, `FreightAmount`).

### Customer

Properties: `ID`, `Name`, `Email`, `BillToContact` / `ShipToContact`
(→ `Contact`), `PhoneNumbers`, `CustomFieldValues`
(→ `CustomFieldValueCollection`), `Key`, `Balance`, `IsInactive`, ...
Methods: `Save()`, `Delete()`, `Validate()`.

- `Contact`: `Address` (→ `Address`), `Email`, `CompanyName`, `FirstName`,
  `LastName`, `Name`, `PhoneNumbers`, `NameToUseOnForms`.
- `Address`: `Address1`, `Address2`, `City`, `State`, `Zip`, `Country`,
  `SalesTaxCode`.
- `CustomFieldValueCollection`: read-only collection, indexer **by int and by
  string label**; `CustomFieldValue` has `Key` (get) and `Value` (**get/set**).
  → Stripe customer ID is written to `customer.CustomFieldValues[i].Value`,
  then `customer.Save()`. Config holds the field index (1–5, mapped to 0-based).

### SalesInvoice

Properties: `ReferenceNumber` (invoice number), `CustomerReference`
(→ `EntityReference<Customer>`), `Date`, `DateDue`, `Amount` (sum of lines),
`AmountDue`, `ApplyToSalesLines` (collection of `SalesInvoiceSalesLine`),
`FreightAmount`, `SalesTaxAmount`, `DiscountAmount`, `CustomerPurchaseOrderNumber`,
`Key`. Methods: `Save()`, `AddSalesLine()`, ...

- `SalesInvoiceSalesLine`: `Description`, `Quantity`, `UnitPrice`, `Amount`
  (= qty × unit price), `AccountReference`, `InventoryItemReference`.
- **No custom-field / user-writable note field on SalesInvoice → R3 resolved:
  no Sage-side back-reference of the Stripe invoice ID.** Reconciliation uses
  `metadata.sage_invoice_id` on the Stripe side only, as the TRD allows.

### Receipt (Receive Money) — payment recording

- Created via `Company.Factories.ReceiptFactory.Create()`.
- Properties: `CustomerReference`, `AccountReference` (**the GL/cash account
  the money lands in — set to the Stripe Clearing account**), `Date`,
  `ReferenceNumber`, `ReceiptNumber`, `PaymentMethod`, `Amount` (sum of lines).
- `AddInvoiceLine(Transaction referencedInvoice)` → `ReceiptInvoiceLine` with
  settable `AmountPaid` → **apply the gross amount to the Sage invoice**.
- `AddSalesLine()` → `ReceiptSalesLine` with `Amount`/`Quantity`/`UnitPrice`,
  `AccountReference`, `Description` → **negative line for the Stripe fee,
  booked to the Stripe Fees account**, so the receipt total equals the net
  that lands in Stripe Clearing.
- `Save()`, `Delete()`, `Validate()`.
- Fallback (TRD 6.2 step 5) if the negative sales line is rejected at runtime:
  record receipt at gross to Stripe Clearing + a `GeneralJournalEntry`
  (factory verified present) moving the fee from Clearing to Stripe Fees.
  Both paths are implemented; config flag `FeeBookingMode` selects
  (`receipt-line` default, `journal-entry` fallback).

### Account

- `Account` properties include `ID`, `Description`, `Classification` (see
  `Account-Properties.txt`); resolved via `AccountFactory.List()` + filter,
  matching the configured Stripe Clearing / Stripe Fees account names/IDs.

## Build environment on this dev machine

- No Visual Studio, no .NET SDK (`dotnet`), no NuGet CLI.
- .NET Framework 4.x **runtime + legacy MSBuild/csc** present at
  `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\` (MSBuild.exe, csc.exe).
- Therefore: classic (non-SDK-style) csproj, packages fetched directly from
  nuget.org as .nupkg and extracted, compile with legacy MSBuild against the
  stub Sage assembly.
