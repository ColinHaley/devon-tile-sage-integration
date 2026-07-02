# devon-tile-sage-integration

Companion desktop app connecting **Sage 50 Quantum Accounting 2026 (US)** to
**Stripe**: push open Sage sales invoices to Stripe as finalized invoices, and
pull fully paid Stripe invoices back into Sage as receipts (gross applied to
the invoice, Stripe fee split to a fee account, net landing in a clearing
account). Full spec: `sage50-stripe-integration-trd.md`.

## Layout

- `src/SageStripeSync/` — C# WinForms app (classic csproj, .NET Framework 4.8, x86)
- `lib/sage/` — Sage 50 .NET SDK assemblies (extracted from install media; not
  committed — see `docs/sage-api/DISCOVERY.md`)
- `packages/` — NuGet payloads (Stripe.net 45.14.0 + deps; fetched, not committed)
- `docs/sage-api/` — verified Sage SDK surface (`reflected-api.txt` is dumped
  from the real DLL by `tools/dump-sage-api.ps1`)
- `tools/` — reflection dump + smoke-test scripts (32-bit PowerShell)

## Build

No Visual Studio or dotnet SDK required — uses the MSBuild that ships with the
.NET Framework runtime:

```
build.cmd
```

Output: `src\SageStripeSync\bin\Release\SageStripeSync.exe` (x86 — required,
the Sage SDK assemblies are 32-bit only).

If `lib\sage` or `packages` are missing (fresh clone), re-extract the Sage
DLLs from the install media cab and re-download the packages — the exact steps
are recorded in `docs/sage-api/DISCOVERY.md` (Sage) and the package
list/versions in `src/SageStripeSync/SageStripeSync.csproj` (NuGet flat
container URLs). The Sage reference path is overridable:
`build.cmd /p:SageSdkPath=C:\path\to\sage\dlls`.

## Runtime requirements (target machine)

- Sage 50 Quantum 2026 (US) installed, company file accessible locally.
- Sage user role with **SDK Data Access = Full** (Maintain > Users > Set Up
  Security > Roles > Company tab).
- A Sage **application id** (blank works against sample companies only).
- On first connect the app requests access; approve the prompt inside Sage.

## Configuration

Everything is edited in-app via **Settings**:

- non-secrets → `%LOCALAPPDATA%\SageStripeSync\config.json`
- Stripe keys + Sage application id → `%LOCALAPPDATA%\SageStripeSync\secrets.bin`
  (DPAPI-encrypted, current user)
- logs → `%LOCALAPPDATA%\SageStripeSync\logs\sync-YYYYMMDD.log`

Sage needs a **Stripe Clearing** (bank/GL) account and a **Stripe Fees**
(expense) account; the names/IDs are configurable.

## Verification status

- Builds clean; all types load in a 32-bit CLR (`tools/smoke-load.ps1`).
- Money rounding, config and DPAPI secret round-trips, and the missing-key
  guard pass `tools/smoke-functional.ps1`.
- End-to-end Sage/Stripe flows require a machine with Sage 50 installed and a
  Stripe test key — not yet exercised on this dev machine.
