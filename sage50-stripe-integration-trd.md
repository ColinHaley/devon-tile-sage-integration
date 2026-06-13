# Technical Requirements Document: Sage 50 Quantum 2026 to Stripe Integration

**Version:** 1.1 (gross-plus-fee, finalized-only)
**Target build agent:** Claude Code
**Deliverable:** A Windows desktop companion application written in C# that connects Sage 50 Quantum Accounting 2026 (US Edition) to Stripe.

---

## 1. Purpose

Build a companion desktop application that lets a user push Sage 50 sales invoices to Stripe as invoices, and pull fully paid invoices back from Stripe to record payments against the matching Sage invoices. The two primary actions are:

1. **Send to Stripe** (per invoice): upsert the customer into Stripe and create a mirrored Stripe invoice.
2. **Check for Paid Invoices** (global): find fully paid Stripe invoices and record the corresponding receipt against each Sage invoice.

---

## 2. Background and key technical constraints

These constraints are non-negotiable and shape the entire architecture. The build must respect them.

1. **Sage 50 US has no REST API and no extensible native UI.** You cannot add a "Send to Stripe" button onto Sage's own invoice window, nor a button onto Sage's main screen. All custom UI lives in a separate companion application that runs alongside Sage.

2. **Integration uses the Sage 50 US COM SDK.** The relevant component is `Interop.PeachwServer.dll` (the successor to the older `Interop.PeachtreeAccounting.dll`), a COM based interop that supports both reads and writes (customers, invoices, receipts/payments). The legacy ODBC driver is read-only and is **not** used in this project.
   - **Confirmed SDK install path on the target machine:** `C:\Program Files (x86)\Sage\Sage 50 2026.0 SDK`. Reference the interop assembly from here, with **Embed Interop Types set to False** on the project reference.
   - If `Interop.PeachwServer.dll` is not in the root of that folder, it will be in a subfolder (for example a `bin`, `Redist`, or sample-app folder) or in the Sage 50 program directory. The build agent must locate the exact file before adding the reference (see R4).

3. **SDK access prerequisites inside Sage:**
   - The Sage user role must have **SDK Data Access set to Full** (Maintain > Users > Set Up Security > Roles > Company tab).
   - On first connection the app authenticates with an `applicationId` and Sage prompts the user to authorize the external application. This authorization must be granted once and persists thereafter.
   - Sage must be installed and the target company file accessible on the same machine the companion app runs on.

4. **Version-specific DLL.** Each Sage 50 version ships its own interop DLL with identical class names but no shared base. This build targets the 2026 US Edition DLL only. Supporting other versions is out of scope.

5. **Exact SDK class and member names must be verified against the installed type library.** This document specifies the SDK at a functional level (which objects to read and write). The build agent must confirm precise class names, method signatures, and property names against the installed `Interop.PeachwServer.dll` type library and the SDK help file before relying on them, rather than assuming names.

---

## 3. Environment

| Item | Value |
|---|---|
| Sage product | Sage 50 Quantum Accounting 2026, US Edition (formerly Peachtree) |
| Company files | Single company file |
| Concurrent users | 1 |
| Data location | Local machine |
| Companion app platform | Windows desktop |
| Language | C# |
| Recommended runtime | .NET Framework 4.8 with WinForms (most reliable COM interop path). .NET 8 on Windows with COM interop is acceptable if the agent prefers, but interop stability with the Peachtree COM component must be validated first. |
| Stripe client | Stripe.net (official NuGet package) |

---

## 4. Architecture

A single Windows desktop application with these internal components:

1. **Sage gateway**: wraps the COM SDK. Responsible for the session lifecycle (begin/end), reading customers and invoices, writing customer custom fields, and writing receipts.
2. **Stripe gateway**: wraps Stripe.net. Responsible for customer upsert, invoice creation, invoice listing/search, and metadata writes.
3. **Sync engine**: orchestrates the two workflows (Send to Stripe, Check for Paid Invoices), including matching and idempotency logic.
4. **UI**: a list of Sage invoices with a per-row **Send to Stripe** action, and a global **Check for Paid Invoices** button. Displays operation results and errors.
5. **Config**: loads connection settings and operational parameters (Section 10).
6. **Logging**: structured, per-operation logs (Section 9).

There is no locally maintained mapping database. All linkage is stored either in Sage (via the SDK) or in Stripe (via metadata), per Section 5.

---

## 5. Identity and ID mapping strategy

IDs are cross-stored bidirectionally so either system can find its counterpart.

**Customer linkage:**
- Store the **Stripe customer ID** in a Sage customer custom field (one of the five user-defined customer custom fields, written via the SDK). The specific field index is a config parameter.
- Store the **Sage customer ID** in the Stripe customer's `metadata` under key `sage_customer_id`.

**Invoice linkage:**
- Store the **Sage invoice number** in the Stripe invoice's `metadata` under key `sage_invoice_id`. This is the authoritative key used by Check for Paid Invoices.
- Optionally store the **Stripe invoice ID** back onto the Sage invoice if a writable field is available on the sales invoice record (see Risk R3). If no writable invoice field exists, reconciliation still works using `sage_invoice_id` on the Stripe side, so this back-reference is a nice-to-have, not a requirement.

**Reconciliation flag:**
- When a paid Stripe invoice has been recorded in Sage, set Stripe invoice `metadata.reconciled_in_sage = "true"` and `metadata.reconciled_at = <ISO 8601 timestamp>`. This prevents reprocessing.

---

## 6. Functional requirements

### 6.1 Send to Stripe (per invoice)

Triggered by the per-row button in the companion app's invoice list.

**Step 1: Upsert customer**
1. Read the full customer record from Sage for the invoice's customer.
2. Determine if a Stripe customer already exists, in this order:
   a. If the Sage customer custom field holds a Stripe customer ID, fetch that customer to confirm it still exists.
   b. Otherwise search Stripe customers by `metadata['sage_customer_id']` equal to the Sage customer ID.
3. If found, **update** the Stripe customer with current Sage data. If not found, **create** a new Stripe customer.
4. Map customer fields per Section 7.1. If the Sage customer has no email, use the configured fallback email (`contact@devontile.com`).
5. Write the resulting Stripe customer ID back to the Sage customer custom field via the SDK.
6. Ensure `metadata.sage_customer_id` is set on the Stripe customer.

**Step 2: Create the Stripe invoice**
1. **Duplicate guard:** before creating, check whether a Stripe invoice already exists for this Sage invoice (via the stored Stripe invoice ID if present, or by searching Stripe invoices for `metadata['sage_invoice_id']`). If one exists, **warn the user and do not create a second invoice.** Abort the operation cleanly.
2. Create a Stripe invoice for the customer with `auto_advance = false`, `collection_method = "send_invoice"`, and `metadata.sage_invoice_id = <Sage invoice number>`. Email sending is disabled (do not call the send-invoice endpoint).
3. Add one Stripe line item per Sage invoice line, mirroring description, quantity, and unit amount (Section 7.2). Tax, shipping, and freight are each added as their own ordinary line items (not Stripe tax rates).
4. **Total reconciliation check:** after building the invoice, compare the Stripe invoice total against the Sage invoice total. If they do not match exactly (to the cent), **fail loudly**: do not finalize, surface a clear error, and leave the operation in a safe state (delete or void the partial draft so no orphaned invoice remains).
5. **Invoice status:** always finalize the invoice so it becomes status `open`. There is no draft mode. Finalized/open is required for the Check for Paid Invoices workflow to function.
6. Use a Stripe **idempotency key** derived from the Sage invoice number on the create call so accidental retries do not duplicate.

**Result handling:** report success with the Stripe invoice ID and URL, or a clear, actionable error. All steps are logged.

### 6.2 Check for Paid Invoices (global)

Triggered by the global button on the companion app's main screen.

1. List Stripe invoices with `status = "paid"`. Expected volume is under 10 per run, so simple pagination is sufficient.
2. For each paid invoice, skip it if `metadata.reconciled_in_sage == "true"`.
3. Read `metadata.sage_invoice_id` to identify the target Sage invoice. If missing or no matching Sage invoice is found, log a warning and skip (do not fail the whole run).
4. Read the amounts needed to record the payment:
   - **Gross**: the invoice's paid total (`amount_paid`), which equals the Sage invoice total.
   - **Stripe fee**: read from the balance transaction of the invoice's payment (expand the invoice's payment intent / latest charge to its `balance_transaction.fee`). Net (`balance_transaction.net`) equals gross minus fee and should reconcile as a sanity check.
5. Record the payment in Sage via the SDK so the invoice closes fully and the cash reconciles:
   - Apply the **gross** amount against the identified Sage invoice, so the invoice is marked fully paid with no residual balance.
   - Book the **Stripe fee** to the configured **Stripe Fees** expense account.
   - The **net** (gross minus fee) lands in the configured **Stripe Clearing** account, matching the cash Stripe actually deposits.
   - Payment date: the Stripe invoice paid-at date.
   - The exact SDK mechanism for splitting the fee must be confirmed against the SDK (see R4). If the receipt/Receive Money object cannot record the fee deduction directly, record the fee as a separate journal entry or bank charge within the same operation so the net to clearing is correct.
6. On success, set Stripe `metadata.reconciled_in_sage = "true"` and `metadata.reconciled_at` so the invoice is skipped on future runs.
7. Continue to the next invoice. One failure must not abort the remaining invoices. Produce a per-invoice summary (succeeded, skipped, failed with reason).

---

## 7. Field mappings

### 7.1 Customer (Sage to Stripe)

| Stripe field | Source (Sage) | Notes |
|---|---|---|
| `name` | Customer name / bill-to name | |
| `email` | Customer email | Fallback to `contact@devontile.com` if empty |
| `phone` | Customer phone | |
| `address.line1` | Bill-to address line 1 | |
| `address.line2` | Bill-to address line 2 | |
| `address.city` | Bill-to city | |
| `address.state` | Bill-to state | |
| `address.postal_code` | Bill-to ZIP | |
| `address.country` | Bill-to country | Default to US if Sage value is blank |
| `metadata.sage_customer_id` | Sage customer ID | Required for reverse lookup |

Push as many of these as Sage exposes for the customer. The build agent should confirm exact Sage customer property names against the SDK.

### 7.2 Invoice line items (Sage to Stripe)

For each Sage invoice line, create a Stripe invoice line item with:

| Stripe line field | Source (Sage) |
|---|---|
| `description` | Line description / item description |
| `quantity` | Line quantity |
| `unit_amount` (or `unit_amount_decimal`) | Line unit price, in the smallest currency unit (cents) |

Additional Sage amounts are added as their own dedicated line items, each with a clear description:
- **Sales tax**: one line item for the Sage tax amount.
- **Shipping**: one line item.
- **Freight**: one line item.

Currency: USD unless configured otherwise. All monetary values must be converted to integer cents with explicit, controlled rounding, and the sum must equal the Sage invoice total exactly (Section 6.1 step 4).

---

## 8. Idempotency and safety rules

1. Sending the same Sage invoice twice must warn and not create a duplicate (duplicate guard, Section 6.1).
2. Stripe create calls use idempotency keys derived from the Sage invoice number.
3. Reconciliation uses the Stripe `reconciled_in_sage` metadata flag to guarantee each paid invoice is recorded in Sage at most once.
4. Any total mismatch on send fails loudly and leaves no orphaned Stripe invoice.
5. Writes to Sage (customer custom field, receipt) should be verified after write where the SDK allows read-back.

---

## 9. Non-functional requirements

- **Logging:** structured per-operation logs (timestamp, operation, Sage invoice number, Stripe IDs, outcome, error detail). Persist to a local log file. No secrets in logs.
- **Error handling:** every Sage SDK call and every Stripe call is wrapped with explicit error handling. User-facing messages are plain and actionable. Reconciliation continues past individual failures.
- **Secrets:** Stripe API keys (test and live) and the Sage `applicationId` are stored outside source control, ideally in an encrypted local store (for example Windows DPAPI) rather than plain text. Support a test/live mode switch.
- **Performance:** trivial scale (single user, under 10 paid invoices per run). No special optimization required, but Stripe calls should be sequential and rate-limit aware.
- **Resilience:** the app must handle Sage not running, the company file being locked, SDK authorization not yet granted, and Stripe network/auth errors, each with a clear message.

---

## 10. Configuration parameters

| Key | Description | Example |
|---|---|---|
| `STRIPE_API_KEY_TEST` | Stripe test secret key | `sk_test_...` |
| `STRIPE_API_KEY_LIVE` | Stripe live secret key | `sk_live_...` |
| `STRIPE_MODE` | `test` or `live` | `test` |
| `SAGE_APPLICATION_ID` | Application ID for the SDK authorization handshake | |
| `SAGE_SDK_PATH` | Folder containing the Sage 50 SDK / `Interop.PeachwServer.dll` | `C:\Program Files (x86)\Sage\Sage 50 2026.0 SDK` |
| `SAGE_COMPANY_PATH` | Path to the Sage company data, if required by the SDK | |
| `SAGE_CUSTOMER_STRIPE_ID_FIELD` | Which customer custom field index holds the Stripe customer ID | `1` |
| `STRIPE_CLEARING_ACCOUNT` | Sage GL/bank account where net Stripe cash is deposited | `Stripe Clearing` |
| `STRIPE_FEES_ACCOUNT` | Sage expense account where Stripe fees are booked | `Stripe Fees` |
| `FALLBACK_CUSTOMER_EMAIL` | Email used when a Sage customer has none | `contact@devontile.com` |
| `CURRENCY` | ISO currency code | `usd` |

---

## 11. Open decisions and risks

These should be reviewed before or during build. R1 in particular affects whether the stated goal ("mark the Sage invoices as paid") is actually achieved.

- **R1 (resolved): Payments are recorded as gross plus a separate fee.** Each payment applies the gross invoice total so the Sage invoice closes fully, the Stripe fee is booked to the Stripe Fees account, and the net lands in Stripe Clearing to match the cash deposited. This depends on reliably reading `balance_transaction.fee` from each paid invoice's payment, which is available once the charge has settled. Confirm the SDK can split the fee in the receipt; if not, use a paired journal entry (see Section 6.2 step 5 and R4).

- **R2 (resolved): Invoices are always finalized.** Invoices are created and finalized to status `open`; there is no draft mode. This matches the original "open invoice" requirement and is required for reconciliation.

- **R3: Writable field on the Sage sales invoice.** Sage 50 US customers have five custom fields suitable for storing the Stripe customer ID. The sales invoice record may not expose an equally clean writable custom field for the Stripe invoice ID. If none is available via the SDK, the back-reference on the Sage side is dropped; reconciliation still works via `sage_invoice_id` stored in Stripe metadata. Confirm field availability against the SDK early.

- **R4: SDK location and class/member verification.** The SDK is installed at `C:\Program Files (x86)\Sage\Sage 50 2026.0 SDK` on the target machine. The build agent's first task should be a small discovery spike that (a) locates the exact `Interop.PeachwServer.dll` under that folder or the Sage program directory, (b) opens a session and authorizes the app, (c) reads one customer and one invoice, and (d) confirms the precise COM class names, method signatures, and the receipt-creation object (the "Receive Money" / Receipts journal equivalent) against the installed type library and SDK help file. Do not hardcode unverified names or paths.

- **R5: Tax handling.** Tax is mirrored as a flat line item from the Sage tax amount. This does not reproduce Stripe's tax engine or per-jurisdiction breakdowns, which is acceptable per requirements but means Stripe will not compute or report tax independently.

- **R6: Multi-currency, refunds, voids, partial payments, and re-syncing edited invoices are out of scope** (Section 12). If a Stripe invoice is refunded or voided after reconciliation, nothing flows back to Sage in v1.

---

## 12. Out of scope (v1)

- Editing an already-sent invoice and re-syncing changes to Stripe.
- Refunds or voids in Stripe flowing back to Sage.
- Partial payments (only fully paid Stripe invoices are reconciled).
- Multi-currency.
- Supporting Sage versions other than 2026 US Edition.
- Multiple company files or multi-user concurrency.
- Native injection of buttons into Sage's own UI (not technically possible).

---

## 13. Acceptance criteria

1. Pressing **Send to Stripe** on a Sage invoice for a new customer creates a Stripe customer with mapped fields and metadata, stores the Stripe customer ID back on the Sage customer, creates a Stripe invoice whose total matches the Sage invoice exactly, finalizes it to `open`, and stores `sage_invoice_id` in the invoice metadata.
2. Pressing **Send to Stripe** on an invoice already sent warns the user and does not create a duplicate.
3. A customer with no email produces a Stripe customer using the fallback email.
4. A total mismatch causes a loud failure with no orphaned Stripe invoice.
5. Pressing **Check for Paid Invoices** records the payment in Sage against each fully paid, not-yet-reconciled Stripe invoice so the Sage invoice closes fully at the gross amount with no residual balance, books the Stripe fee to the Stripe Fees account with the net landing in Stripe Clearing, dates it the Stripe paid-at date, and marks the Stripe invoice `reconciled_in_sage = true`.
6. Re-running **Check for Paid Invoices** does not double-record any already-reconciled invoice.
7. One failing invoice during reconciliation does not stop the others, and the run reports a clear per-invoice summary.
8. The app handles Sage not running, SDK authorization missing, and Stripe auth/network errors with clear messages.
