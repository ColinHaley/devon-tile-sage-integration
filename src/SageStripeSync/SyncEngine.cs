using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Stripe;
using SageCustomer = Sage.Peachtree.API.Customer;
using SageInvoice = Sage.Peachtree.API.SalesInvoice;
using SageSalesLine = Sage.Peachtree.API.SalesInvoiceSalesLine;
using SagePhoneNumber = Sage.Peachtree.API.PhoneNumber;

namespace SageStripeSync
{
    /// <summary>
    /// Orchestrates the two workflows (TRD section 6): Send to Stripe and
    /// Check for Paid Invoices. Owns matching, idempotency, and the loud-failure
    /// total reconciliation rule. Runs on a worker thread; talks to the UI only
    /// through return values and the Logger.
    /// </summary>
    public class SyncEngine
    {
        private readonly AppConfig _config;
        private readonly SecretStore _secrets;
        private readonly SageGateway _sage;

        public SyncEngine(AppConfig config, SecretStore secrets, SageGateway sage)
        {
            _config = config;
            _secrets = secrets;
            _sage = sage;
        }

        private StripeGateway NewStripeGateway()
        {
            return new StripeGateway(_secrets.StripeKeyForMode(_config.IsLiveMode), _config.Currency);
        }

        // ==================================================================
        // 6.1 Send to Stripe (per invoice)
        // ==================================================================

        public SendResult SendInvoiceToStripe(string sageInvoiceRef)
        {
            const string Op = "SendToStripe";
            Logger.Info(Op, "--- Sending Sage invoice " + sageInvoiceRef + " to Stripe (" + _config.StripeMode + " mode) ---");
            StripeGateway stripe = NewStripeGateway();

            // Load and sanity-check the Sage invoice.
            SageInvoice invoice = _sage.FindInvoiceByReference(sageInvoiceRef);
            if (invoice == null)
            {
                return SendResult.Fail("Sage invoice \"" + sageInvoiceRef + "\" was not found.");
            }
            if (invoice.AmountDue <= 0m)
            {
                return SendResult.Fail("Sage invoice " + sageInvoiceRef + " has no open balance; nothing to send.");
            }

            // Duplicate guard (TRD 6.1 step 2.1): never create a second Stripe
            // invoice for the same Sage invoice.
            Invoice existing = stripe.FindInvoiceBySageId(sageInvoiceRef);
            if (existing != null)
            {
                string msg = "A Stripe invoice already exists for Sage invoice " + sageInvoiceRef + ": "
                    + existing.Id + " (status " + existing.Status + "). No new invoice was created.";
                Logger.Warn(Op, msg);
                return SendResult.Duplicate(existing.Id, msg);
            }

            // Build line items and verify they sum to the amount due BEFORE
            // touching Stripe — a mismatch here means a partial payment or a
            // Sage total we don't understand, and we must fail loudly.
            long targetCents = Money.ToCents(invoice.AmountDue);
            List<LineSpec> lines = BuildLineSpecs(invoice);
            long builtCents = 0;
            foreach (LineSpec l in lines) { builtCents += l.TotalCents; }
            if (builtCents != targetCents)
            {
                string msg = "Total mismatch for Sage invoice " + sageInvoiceRef + ": lines + tax + freight = "
                    + Money.FormatCents(builtCents) + " but amount due is " + Money.FormatCents(targetCents)
                    + ". This usually means a partial payment or discount was applied in Sage. Nothing was sent to Stripe.";
                Logger.Error(Op, msg);
                return SendResult.Fail(msg);
            }

            // Step 1: upsert the customer.
            SageCustomer sageCustomer = _sage.LoadCustomerForInvoice(invoice);
            Customer stripeCustomer = UpsertCustomer(stripe, sageCustomer, Op);

            // Step 2: create + fill + reconcile + finalize the invoice.
            string idempotencyBase = "sage-" + sageInvoiceRef + "-" + targetCents + "-" + HashLines(lines);
            Invoice draft = null;
            try
            {
                draft = stripe.CreateDraftInvoice(stripeCustomer.Id, sageInvoiceRef, invoice.DateDue, idempotencyBase + "-create");
                Logger.Info(Op, "Created draft Stripe invoice " + draft.Id + " for customer " + stripeCustomer.Id + ".");

                for (int i = 0; i < lines.Count; i++)
                {
                    LineSpec l = lines[i];
                    stripe.AddInvoiceLine(stripeCustomer.Id, draft.Id, l.Description,
                        l.Quantity, l.UnitAmountCents, l.AmountCents, idempotencyBase + "-line" + i);
                }

                // Total reconciliation check (TRD 6.1 step 4): exact to the cent
                // or the draft is deleted and the operation fails loudly.
                Invoice refreshed = stripe.GetInvoice(draft.Id);
                if (refreshed.Total != targetCents)
                {
                    string msg = "RECONCILIATION FAILURE for Sage invoice " + sageInvoiceRef + ": Stripe draft total "
                        + Money.FormatCents(refreshed.Total) + " != Sage amount due " + Money.FormatCents(targetCents)
                        + ". The draft was deleted; no Stripe invoice exists for this Sage invoice.";
                    Logger.Error(Op, msg);
                    stripe.TryDeleteDraft(draft.Id);
                    return SendResult.Fail(msg);
                }

                Invoice finalized = stripe.FinalizeInvoice(draft.Id);
                string ok = "Sage invoice " + sageInvoiceRef + " sent to Stripe: " + finalized.Id
                    + " (number " + finalized.Number + ", total " + Money.FormatCents(finalized.Total)
                    + ", status " + finalized.Status + ").";
                Logger.Info(Op, ok);
                return SendResult.Ok(finalized.Id, finalized.HostedInvoiceUrl, ok);
            }
            catch (Exception ex)
            {
                Logger.Error(Op, "Send failed for Sage invoice " + sageInvoiceRef, ex);
                if (draft != null) { stripe.TryDeleteDraft(draft.Id); }
                return SendResult.Fail(ex.Message);
            }
        }

        /// <summary>
        /// Upserts the Stripe customer per TRD 6.1 step 1: stored-id lookup,
        /// then metadata search, then create; always refreshes mapped fields and
        /// writes the Stripe id back to the Sage custom field.
        /// </summary>
        private Customer UpsertCustomer(StripeGateway stripe, SageCustomer sageCustomer, string op)
        {
            // (a) stored Stripe id from the Sage custom field, if still valid.
            string storedId = null;
            try
            {
                storedId = _sage.ReadStripeCustomerIdField(sageCustomer, _config.CustomerStripeIdFieldIndex);
            }
            catch (Exception ex)
            {
                Logger.Warn(op, "Could not read the Stripe-id custom field: " + ex.Message);
            }

            Customer existing = null;
            if (!string.IsNullOrEmpty(storedId))
            {
                existing = stripe.TryGetCustomer(storedId);
                if (existing == null)
                {
                    Logger.Warn(op, "Stored Stripe customer id " + storedId + " no longer exists; searching by metadata.");
                }
            }
            // (b) fall back to metadata search by Sage customer id.
            if (existing == null)
            {
                existing = stripe.FindCustomerBySageId(sageCustomer.ID);
            }

            // Map Sage fields (TRD 7.1).
            string name = sageCustomer.Name;
            string email = FirstNonEmpty(
                sageCustomer.Email,
                sageCustomer.BillToContact != null ? sageCustomer.BillToContact.Email : null,
                _config.FallbackCustomerEmail);
            string phone = FirstPhoneNumber(sageCustomer);
            AddressOptions address = MapBillToAddress(sageCustomer);
            Dictionary<string, string> metadata = new Dictionary<string, string>();
            metadata["sage_customer_id"] = sageCustomer.ID;

            Customer result;
            if (existing != null)
            {
                CustomerUpdateOptions upd = new CustomerUpdateOptions();
                upd.Name = name;
                upd.Email = email;
                upd.Phone = phone;
                upd.Address = address;
                upd.Metadata = metadata;
                result = stripe.UpdateCustomer(existing.Id, upd);
                Logger.Info(op, "Updated Stripe customer " + result.Id + " from Sage customer " + sageCustomer.ID + ".");
            }
            else
            {
                CustomerCreateOptions crt = new CustomerCreateOptions();
                crt.Name = name;
                crt.Email = email;
                crt.Phone = phone;
                crt.Address = address;
                crt.Metadata = metadata;
                result = stripe.CreateCustomer(crt);
                Logger.Info(op, "Created Stripe customer " + result.Id + " for Sage customer " + sageCustomer.ID + ".");
            }

            // Write the Stripe id back to Sage (TRD 6.1 step 1.5). A failure here
            // is logged loudly but doesn't abort: the metadata linkage above
            // still makes future lookups work.
            if (!string.Equals(storedId, result.Id, StringComparison.Ordinal))
            {
                try
                {
                    _sage.WriteStripeCustomerIdField(sageCustomer, _config.CustomerStripeIdFieldIndex, result.Id);
                }
                catch (Exception ex)
                {
                    Logger.Warn(op, "Could not store the Stripe customer id on the Sage customer: " + ex.Message
                        + " (continuing — the Stripe-side metadata link is in place).");
                }
            }
            return result;
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (string v in values)
            {
                if (!string.IsNullOrEmpty(v) && v.Trim().Length > 0) { return v.Trim(); }
            }
            return null;
        }

        private static string FirstPhoneNumber(SageCustomer customer)
        {
            if (customer.PhoneNumbers == null) { return null; }
            foreach (SagePhoneNumber p in customer.PhoneNumbers)
            {
                if (p != null && !string.IsNullOrEmpty(p.Number)) { return p.Number; }
            }
            return null;
        }

        private static AddressOptions MapBillToAddress(SageCustomer customer)
        {
            if (customer.BillToContact == null || customer.BillToContact.Address == null) { return null; }
            Sage.Peachtree.API.Address a = customer.BillToContact.Address;
            AddressOptions options = new AddressOptions();
            options.Line1 = a.Address1;
            options.Line2 = a.Address2;
            options.City = a.City;
            options.State = a.State;
            options.PostalCode = a.Zip;
            // TRD 7.1: default country to US when Sage leaves it blank.
            options.Country = string.IsNullOrEmpty(a.Country) ? "US" : a.Country;
            return options;
        }

        // ------------------------------------------------------------------
        // Line construction (TRD 7.2)
        // ------------------------------------------------------------------

        /// <summary>One Stripe line item to create: either qty × unit or a flat amount.</summary>
        private class LineSpec
        {
            public string Description;
            public long? Quantity;
            public long? UnitAmountCents;
            public long? AmountCents;

            public long TotalCents
            {
                get { return AmountCents.HasValue ? AmountCents.Value : Quantity.Value * UnitAmountCents.Value; }
            }
        }

        private static List<LineSpec> BuildLineSpecs(SageInvoice invoice)
        {
            List<LineSpec> lines = new List<LineSpec>();

            foreach (SageSalesLine sageLine in invoice.ApplyToSalesLines)
            {
                long lineCents = Money.ToCents(sageLine.Amount);
                string description = sageLine.Description;
                if (lineCents == 0 && string.IsNullOrEmpty(description))
                {
                    continue; // blank spacer line — nothing to mirror
                }

                LineSpec spec = new LineSpec();
                spec.Description = description;

                // Mirror quantity × unit price when it reproduces the Sage line
                // amount exactly in cents; otherwise use the amount as a single
                // line so the invoice total always matches (rounding rule, 7.2).
                long unitCents = Money.ToCents(sageLine.UnitPrice);
                decimal quantity = sageLine.Quantity;
                long wholeQuantity = (long)quantity;
                if (wholeQuantity > 0 && wholeQuantity == quantity && wholeQuantity * unitCents == lineCents)
                {
                    spec.Quantity = wholeQuantity;
                    spec.UnitAmountCents = unitCents;
                }
                else
                {
                    spec.AmountCents = lineCents;
                    if (quantity != 0m && quantity != 1m)
                    {
                        // Preserve the original qty/price detail for the customer.
                        spec.Description = (description ?? "") + " (" + quantity.ToString("0.####")
                            + " @ " + sageLine.UnitPrice.ToString("C2") + ")";
                    }
                }
                lines.Add(spec);
            }

            // Tax, shipping and freight become their own ordinary line items.
            long taxCents = Money.ToCents(invoice.SalesTaxAmount);
            if (taxCents != 0)
            {
                LineSpec tax = new LineSpec();
                tax.Description = "Sales tax";
                tax.AmountCents = taxCents;
                lines.Add(tax);
            }
            long freightCents = Money.ToCents(invoice.FreightAmount);
            if (freightCents != 0)
            {
                LineSpec freight = new LineSpec();
                freight.Description = "Shipping & freight";
                freight.AmountCents = freightCents;
                lines.Add(freight);
            }
            return lines;
        }

        /// <summary>
        /// Content hash folded into idempotency keys: retries of identical sends
        /// dedupe, while a corrected invoice (different lines/total) gets fresh keys.
        /// </summary>
        private static string HashLines(List<LineSpec> lines)
        {
            StringBuilder sb = new StringBuilder();
            foreach (LineSpec l in lines)
            {
                sb.Append(l.Description).Append('|').Append(l.TotalCents).Append(';');
            }
            using (SHA1 sha = SHA1.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
                return BitConverter.ToString(hash, 0, 6).Replace("-", "").ToLowerInvariant();
            }
        }

        // ==================================================================
        // 6.2 Check for Paid Invoices (global)
        // ==================================================================

        public List<ReconcileResult> CheckForPaidInvoices()
        {
            const string Op = "CheckPaid";
            Logger.Info(Op, "--- Checking for paid Stripe invoices (" + _config.StripeMode + " mode) ---");
            StripeGateway stripe = NewStripeGateway();

            List<Invoice> paid = stripe.ListPaidInvoices();
            Logger.Info(Op, paid.Count + " paid Stripe invoice(s) found.");

            List<ReconcileResult> results = new List<ReconcileResult>();
            int alreadyReconciled = 0;
            foreach (Invoice invoice in paid)
            {
                // Skip anything already recorded (TRD 6.2 step 2) — quietly;
                // these accumulate forever and aren't news.
                string flag;
                if (invoice.Metadata != null && invoice.Metadata.TryGetValue("reconciled_in_sage", out flag)
                    && string.Equals(flag, "true", StringComparison.OrdinalIgnoreCase))
                {
                    alreadyReconciled++;
                    continue;
                }

                // One failure must not abort the rest (TRD 6.2 step 7).
                ReconcileResult r = new ReconcileResult();
                r.StripeInvoiceId = invoice.Id;
                try
                {
                    ReconcileOne(stripe, invoice, r, Op);
                }
                catch (Exception ex)
                {
                    r.Outcome = ReconcileOutcome.Failed;
                    r.Message = ex.Message;
                    Logger.Error(Op, "Failed to reconcile Stripe invoice " + invoice.Id, ex);
                }
                results.Add(r);
            }

            int recorded = 0, skipped = 0, failed = 0;
            foreach (ReconcileResult r in results)
            {
                switch (r.Outcome)
                {
                    case ReconcileOutcome.Recorded:
                    case ReconcileOutcome.RecordedFeeManual:
                    case ReconcileOutcome.AlreadyClosed:
                        recorded++; break;
                    case ReconcileOutcome.Failed:
                        failed++; break;
                    default:
                        skipped++; break;
                }
            }
            Logger.Info(Op, "Run complete: " + recorded + " recorded, " + skipped + " skipped, " + failed + " failed, "
                + alreadyReconciled + " previously reconciled.");
            return results;
        }

        private void ReconcileOne(StripeGateway stripe, Invoice invoice, ReconcileResult r, string op)
        {
            // Identify the Sage invoice (TRD 6.2 step 3).
            string sageRef = null;
            if (invoice.Metadata != null) { invoice.Metadata.TryGetValue("sage_invoice_id", out sageRef); }
            if (string.IsNullOrEmpty(sageRef))
            {
                r.Outcome = ReconcileOutcome.SkippedNoMatch;
                r.Message = "Stripe invoice " + invoice.Id + " has no sage_invoice_id metadata — skipped.";
                Logger.Warn(op, r.Message);
                return;
            }
            r.SageInvoiceRef = sageRef;

            // Amounts (TRD 6.2 step 4).
            StripeFeeInfo fee = stripe.GetFeeInfo(invoice);
            if (!fee.Settled)
            {
                r.Outcome = ReconcileOutcome.SkippedNotSettled;
                r.Message = "Stripe invoice " + invoice.Id + " (Sage " + sageRef + "): " + fee.Note + " — will retry next run.";
                Logger.Warn(op, r.Message);
                return;
            }
            if (fee.Note != null) { Logger.Warn(op, "Stripe invoice " + invoice.Id + ": " + fee.Note); }
            if (fee.NetCents != fee.GrossCents - fee.FeeCents)
            {
                // Sanity cross-check per TRD; fee wins, but this deserves a loud note.
                Logger.Warn(op, "Stripe invoice " + invoice.Id + ": net " + Money.FormatCents(fee.NetCents)
                    + " != gross " + Money.FormatCents(fee.GrossCents) + " - fee " + Money.FormatCents(fee.FeeCents)
                    + " (multiple charges or currency conversion?). Using gross and fee as reported.");
            }

            DateTime paidAt = fee.PaidAt.HasValue ? fee.PaidAt.Value.ToLocalTime() : DateTime.Now;

            // Record in Sage (TRD 6.2 step 5).
            string label = "Stripe " + (string.IsNullOrEmpty(invoice.Number) ? invoice.Id : invoice.Number);
            PaymentRecordResult recorded = _sage.RecordStripePayment(
                sageRef, fee.GrossCents, fee.FeeCents, paidAt, label,
                _config.StripeClearingAccount, _config.StripeFeesAccount,
                _config.FeeBookingMode, _config.SagePaymentMethod);

            // Flag as reconciled for every outcome that put (or found) the money
            // in Sage — including the fee-needs-manual-entry case, where a re-run
            // must NOT record the receipt a second time.
            stripe.MarkReconciled(invoice.Id);

            switch (recorded.Outcome)
            {
                case PaymentRecordOutcome.Recorded:
                    r.Outcome = ReconcileOutcome.Recorded;
                    break;
                case PaymentRecordOutcome.AlreadyClosedInSage:
                    r.Outcome = ReconcileOutcome.AlreadyClosed;
                    break;
                case PaymentRecordOutcome.ReceiptSavedFeeUnbooked:
                    r.Outcome = ReconcileOutcome.RecordedFeeManual;
                    break;
            }
            r.Message = recorded.Message;
            Logger.Info(op, "Stripe invoice " + invoice.Id + " (Sage " + sageRef + "): " + recorded.Message);
        }
    }
}
