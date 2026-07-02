using System;

namespace SageStripeSync
{
    /// <summary>Money helpers: all Stripe amounts are integer cents (TRD 7.2).</summary>
    public static class Money
    {
        /// <summary>
        /// Converts a Sage decimal dollar amount to integer cents with explicit,
        /// controlled rounding (away from zero, i.e. normal commercial rounding).
        /// </summary>
        public static long ToCents(decimal amount)
        {
            return (long)decimal.Round(amount * 100m, 0, MidpointRounding.AwayFromZero);
        }

        public static string FormatCents(long cents)
        {
            return (cents / 100m).ToString("C2");
        }
    }

    /// <summary>A row in the main invoice grid (read from Sage).</summary>
    public class SageInvoiceSummary
    {
        public string ReferenceNumber { get; set; }
        public DateTime Date { get; set; }
        public string CustomerId { get; set; }
        public string CustomerName { get; set; }
        public decimal AmountDue { get; set; }
    }

    /// <summary>Result of the per-invoice "Send to Stripe" workflow (TRD 6.1).</summary>
    public class SendResult
    {
        public bool Success { get; set; }
        /// <summary>True for the duplicate-guard case: not an error, but nothing was created.</summary>
        public bool DuplicateWarning { get; set; }
        public string Message { get; set; }
        public string StripeInvoiceId { get; set; }
        public string StripeInvoiceUrl { get; set; }

        public static SendResult Ok(string invoiceId, string url, string message)
        {
            SendResult r = new SendResult();
            r.Success = true;
            r.StripeInvoiceId = invoiceId;
            r.StripeInvoiceUrl = url;
            r.Message = message;
            return r;
        }

        public static SendResult Duplicate(string invoiceId, string message)
        {
            SendResult r = new SendResult();
            r.DuplicateWarning = true;
            r.StripeInvoiceId = invoiceId;
            r.Message = message;
            return r;
        }

        public static SendResult Fail(string message)
        {
            SendResult r = new SendResult();
            r.Message = message;
            return r;
        }
    }

    public enum ReconcileOutcome
    {
        Recorded,          // payment recorded in Sage, Stripe flagged reconciled
        AlreadyClosed,     // Sage invoice already fully paid; flagged reconciled, nothing recorded
        SkippedNotSettled, // Stripe balance transaction not available yet; retry next run
        SkippedNoMatch,    // missing/unknown sage_invoice_id metadata
        RecordedFeeManual, // receipt saved but fee could not be booked; needs manual entry
        Failed
    }

    /// <summary>Per-invoice result line for "Check for Paid Invoices" (TRD 6.2).</summary>
    public class ReconcileResult
    {
        public string StripeInvoiceId { get; set; }
        public string SageInvoiceRef { get; set; }
        public ReconcileOutcome Outcome { get; set; }
        public string Message { get; set; }
    }

    /// <summary>Amounts pulled from a paid Stripe invoice's balance transaction.</summary>
    public class StripeFeeInfo
    {
        /// <summary>False while the charge's balance transaction isn't available yet.</summary>
        public bool Settled { get; set; }
        public long GrossCents { get; set; }
        public long FeeCents { get; set; }
        public long NetCents { get; set; }
        public DateTime? PaidAt { get; set; }
        public string Note { get; set; }
    }

    /// <summary>Outcome of writing one payment (receipt + fee) into Sage.</summary>
    public enum PaymentRecordOutcome
    {
        Recorded,
        AlreadyClosedInSage,
        ReceiptSavedFeeUnbooked
    }

    public class PaymentRecordResult
    {
        public PaymentRecordOutcome Outcome { get; set; }
        public string Message { get; set; }
    }
}
