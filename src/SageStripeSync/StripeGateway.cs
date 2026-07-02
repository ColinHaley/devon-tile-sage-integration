using System;
using System.Collections.Generic;
using System.Net;
using Stripe;

namespace SageStripeSync
{
    /// <summary>
    /// Wraps Stripe.net (v45, pinned to Stripe API 2024-06-20): customer upsert
    /// lookups, invoice creation/finalization, paid-invoice listing, and fee
    /// extraction from balance transactions. All calls are synchronous; callers
    /// run them off the UI thread.
    /// </summary>
    public class StripeGateway
    {
        private const string Op = "Stripe";

        private readonly StripeClient _client;
        private readonly string _currency;

        public StripeGateway(string apiKey, string currency)
        {
            if (string.IsNullOrEmpty(apiKey))
            {
                throw new InvalidOperationException("No Stripe API key configured for the current mode — open Settings.");
            }
            _client = new StripeClient(apiKey);
            _currency = string.IsNullOrEmpty(currency) ? "usd" : currency.ToLowerInvariant();
        }

        // ------------------------------------------------------------------
        // Customers
        // ------------------------------------------------------------------

        /// <summary>Fetches a customer by id; null if it no longer exists (or was deleted).</summary>
        public Customer TryGetCustomer(string customerId)
        {
            try
            {
                Customer c = new CustomerService(_client).Get(customerId);
                if (c != null && c.Deleted == true) { return null; }
                return c;
            }
            catch (StripeException ex)
            {
                if (IsMissing(ex)) { return null; }
                throw Friendly(ex);
            }
        }

        /// <summary>Searches customers by metadata.sage_customer_id.</summary>
        public Customer FindCustomerBySageId(string sageCustomerId)
        {
            CustomerSearchOptions options = new CustomerSearchOptions();
            options.Query = "metadata['sage_customer_id']:'" + EscapeQueryValue(sageCustomerId) + "'";
            try
            {
                StripeSearchResult<Customer> result = new CustomerService(_client).Search(options);
                return (result != null && result.Data != null && result.Data.Count > 0) ? result.Data[0] : null;
            }
            catch (StripeException ex)
            {
                throw Friendly(ex);
            }
        }

        public Customer CreateCustomer(CustomerCreateOptions options)
        {
            try { return new CustomerService(_client).Create(options); }
            catch (StripeException ex) { throw Friendly(ex); }
        }

        public Customer UpdateCustomer(string customerId, CustomerUpdateOptions options)
        {
            try { return new CustomerService(_client).Update(customerId, options); }
            catch (StripeException ex) { throw Friendly(ex); }
        }

        // ------------------------------------------------------------------
        // Invoices (send side)
        // ------------------------------------------------------------------

        /// <summary>Duplicate guard: finds an existing Stripe invoice carrying this sage_invoice_id.</summary>
        public Invoice FindInvoiceBySageId(string sageInvoiceId)
        {
            InvoiceSearchOptions options = new InvoiceSearchOptions();
            options.Query = "metadata['sage_invoice_id']:'" + EscapeQueryValue(sageInvoiceId) + "'";
            try
            {
                StripeSearchResult<Invoice> result = new InvoiceService(_client).Search(options);
                return (result != null && result.Data != null && result.Data.Count > 0) ? result.Data[0] : null;
            }
            catch (StripeException ex)
            {
                throw Friendly(ex);
            }
        }

        /// <summary>
        /// Creates the draft invoice shell (no lines yet): auto_advance off,
        /// collection_method send_invoice, sage_invoice_id metadata, and an
        /// idempotency key derived from the Sage invoice (TRD 6.1 step 6).
        /// </summary>
        public Invoice CreateDraftInvoice(string customerId, string sageInvoiceId, DateTime? sageDueDate, string idempotencyKey)
        {
            InvoiceCreateOptions options = new InvoiceCreateOptions();
            options.Customer = customerId;
            options.AutoAdvance = false;
            options.CollectionMethod = "send_invoice";
            options.Currency = _currency;
            options.PendingInvoiceItemsBehavior = "exclude"; // never vacuum up unrelated pending items
            options.Description = "Sage 50 invoice " + sageInvoiceId;
            options.Metadata = new Dictionary<string, string>();
            options.Metadata["sage_invoice_id"] = sageInvoiceId;

            // send_invoice requires a due date; Stripe rejects dates in the past,
            // so an overdue Sage invoice gets "due tomorrow".
            DateTime minimumDue = DateTime.UtcNow.AddDays(1);
            if (sageDueDate.HasValue && sageDueDate.Value.ToUniversalTime() > minimumDue)
            {
                options.DueDate = sageDueDate.Value;
            }
            else
            {
                options.DueDate = minimumDue;
                if (sageDueDate.HasValue)
                {
                    Logger.Warn(Op, "Sage due date " + sageDueDate.Value.ToShortDateString()
                        + " for invoice " + sageInvoiceId + " is in the past; Stripe due date set to tomorrow.");
                }
            }

            RequestOptions req = new RequestOptions();
            req.IdempotencyKey = idempotencyKey;
            try { return new InvoiceService(_client).Create(options, req); }
            catch (StripeException ex) { throw Friendly(ex); }
        }

        /// <summary>
        /// Adds one line item directly to the draft invoice. Uses quantity ×
        /// unit_amount when exact, otherwise a single amount (both integer cents).
        /// </summary>
        public void AddInvoiceLine(string customerId, string invoiceId, string description,
                                   long? quantity, long? unitAmountCents, long? amountCents, string idempotencyKey)
        {
            InvoiceItemCreateOptions options = new InvoiceItemCreateOptions();
            options.Customer = customerId;
            options.Invoice = invoiceId;
            options.Currency = _currency;
            options.Description = string.IsNullOrEmpty(description) ? "(no description)" : description;
            if (quantity.HasValue && unitAmountCents.HasValue)
            {
                options.Quantity = quantity;
                options.UnitAmount = unitAmountCents;
            }
            else
            {
                options.Amount = amountCents;
            }

            RequestOptions req = new RequestOptions();
            req.IdempotencyKey = idempotencyKey;
            try { new InvoiceItemService(_client).Create(options, req); }
            catch (StripeException ex) { throw Friendly(ex); }
        }

        public Invoice GetInvoice(string invoiceId)
        {
            try { return new InvoiceService(_client).Get(invoiceId); }
            catch (StripeException ex) { throw Friendly(ex); }
        }

        /// <summary>Finalizes the draft to status "open" without auto-advancing (no emails sent).</summary>
        public Invoice FinalizeInvoice(string invoiceId)
        {
            InvoiceFinalizeOptions options = new InvoiceFinalizeOptions();
            options.AutoAdvance = false;
            try { return new InvoiceService(_client).FinalizeInvoice(invoiceId, options); }
            catch (StripeException ex) { throw Friendly(ex); }
        }

        /// <summary>Deletes an unfinalized draft (mismatch cleanup, TRD 6.1 step 4). Best-effort.</summary>
        public void TryDeleteDraft(string invoiceId)
        {
            try
            {
                new InvoiceService(_client).Delete(invoiceId);
                Logger.Info(Op, "Deleted draft invoice " + invoiceId + ".");
            }
            catch (Exception ex)
            {
                Logger.Error(Op, "Could not delete draft invoice " + invoiceId
                    + " — delete it manually in the Stripe dashboard.", ex);
            }
        }

        // ------------------------------------------------------------------
        // Invoices (reconcile side)
        // ------------------------------------------------------------------

        /// <summary>All paid invoices (listing API — no search-index lag), auto-paginated.</summary>
        public List<Invoice> ListPaidInvoices()
        {
            InvoiceListOptions options = new InvoiceListOptions();
            options.Status = "paid";
            options.Limit = 100;
            List<Invoice> all = new List<Invoice>();
            try
            {
                foreach (Invoice inv in new InvoiceService(_client).ListAutoPaging(options))
                {
                    all.Add(inv);
                }
            }
            catch (StripeException ex)
            {
                throw Friendly(ex);
            }
            return all;
        }

        /// <summary>
        /// Reads gross/fee/net from the invoice's payment intent's latest charge
        /// balance transaction (TRD 6.2 step 4). Settled=false when the balance
        /// transaction isn't available yet — caller skips and retries next run.
        /// </summary>
        public StripeFeeInfo GetFeeInfo(Invoice invoice)
        {
            StripeFeeInfo info = new StripeFeeInfo();
            info.GrossCents = invoice.AmountPaid;
            info.PaidAt = invoice.StatusTransitions != null ? invoice.StatusTransitions.PaidAt : null;

            if (string.IsNullOrEmpty(invoice.PaymentIntentId))
            {
                // Marked paid outside Stripe payments (e.g. paid_out_of_band):
                // there is no processing fee to book.
                info.Settled = true;
                info.FeeCents = 0;
                info.NetCents = info.GrossCents;
                info.Note = "no payment intent on invoice (paid out of band?) — fee assumed 0";
                return info;
            }

            PaymentIntent intent;
            try
            {
                PaymentIntentGetOptions options = new PaymentIntentGetOptions();
                options.AddExpand("latest_charge.balance_transaction");
                intent = new PaymentIntentService(_client).Get(invoice.PaymentIntentId, options);
            }
            catch (StripeException ex)
            {
                throw Friendly(ex);
            }

            Charge charge = intent != null ? intent.LatestCharge : null;
            BalanceTransaction bt = charge != null ? charge.BalanceTransaction : null;
            if (bt == null)
            {
                info.Settled = false;
                info.Note = "balance transaction not available yet (charge still settling)";
                return info;
            }

            info.Settled = true;
            info.FeeCents = bt.Fee;
            info.NetCents = bt.Net;
            return info;
        }

        /// <summary>Sets the reconciled_in_sage flag so the invoice is skipped on future runs (TRD 6.2 step 6).</summary>
        public void MarkReconciled(string invoiceId)
        {
            InvoiceUpdateOptions options = new InvoiceUpdateOptions();
            options.Metadata = new Dictionary<string, string>();
            options.Metadata["reconciled_in_sage"] = "true";
            options.Metadata["reconciled_at"] = DateTime.UtcNow.ToString("o");
            try { new InvoiceService(_client).Update(invoiceId, options); }
            catch (StripeException ex) { throw Friendly(ex); }
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private static bool IsMissing(StripeException ex)
        {
            if (ex.HttpStatusCode == HttpStatusCode.NotFound) { return true; }
            return ex.StripeError != null && ex.StripeError.Code == "resource_missing";
        }

        /// <summary>Wraps StripeExceptions with plain, actionable messages (TRD section 9).</summary>
        private static Exception Friendly(StripeException ex)
        {
            string message;
            if (ex.HttpStatusCode == HttpStatusCode.Unauthorized)
            {
                message = "Stripe rejected the API key (unauthorized). Check the key for the current mode in Settings.";
            }
            else if (ex.HttpStatusCode == (HttpStatusCode)429)
            {
                message = "Stripe rate limit hit — wait a moment and try again.";
            }
            else if (ex.StripeError != null && !string.IsNullOrEmpty(ex.StripeError.Message))
            {
                message = "Stripe error: " + ex.StripeError.Message;
            }
            else
            {
                message = "Stripe error: " + ex.Message;
            }
            return new InvalidOperationException(message, ex);
        }

        /// <summary>Escapes a value for use inside a single-quoted Stripe search query string.</summary>
        private static string EscapeQueryValue(string value)
        {
            return value.Replace("\\", "\\\\").Replace("'", "\\'");
        }
    }
}
