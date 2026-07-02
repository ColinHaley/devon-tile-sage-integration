using System;
using System.Collections.Generic;
using System.Threading;
using Sage.Peachtree.API;
using Sage.Peachtree.API.Collections.Generic;

namespace SageStripeSync
{
    /// <summary>
    /// Wraps the Sage 50 .NET SDK (Sage.Peachtree.API 2026.0): session lifecycle,
    /// authorization handshake, invoice/customer reads, customer custom-field
    /// writes, and receipt (+ optional journal entry) writes.
    /// All member signatures verified against docs/sage-api/reflected-api.txt.
    /// </summary>
    public class SageGateway : IDisposable
    {
        private const string Op = "Sage";
        private const int AuthorizationTimeoutMs = 180000; // 3 minutes to click "allow" inside Sage
        private const int SageReferenceMaxLength = 20;     // Sage reference-number field limit

        private PeachtreeSession _session;
        private Company _company;
        private Dictionary<string, Account> _accountCache; // config value (ID or description) -> Account

        public string CompanyName { get; private set; }

        public bool IsConnected
        {
            get { return _company != null && !_company.IsClosed; }
        }

        /// <summary>
        /// Opens the SDK session and the configured company, walking the
        /// authorization flow (VerifyAccess -> RequestAccess -> poll) if the
        /// user hasn't approved this application inside Sage yet.
        /// </summary>
        public void Connect(string applicationId, string companyName, Action<string> status)
        {
            if (status == null) { status = delegate(string s) { }; }

            try
            {
                _session = new PeachtreeSession();
                // Blank application id restricts the SDK to sample companies —
                // useful for testing, logged so it isn't a silent surprise.
                if (string.IsNullOrEmpty(applicationId))
                {
                    Logger.Warn(Op, "No Sage application id configured; SDK allows sample companies only.");
                }
                _session.Begin(applicationId ?? "");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(FriendlySageMessage(ex), ex);
            }

            CompanyIdentifier target = PickCompany(companyName);

            status("Checking authorization for \"" + target.CompanyName + "\"...");
            AuthorizationResult auth = _session.VerifyAccess(target);
            if (auth == AuthorizationResult.NoCredentials)
            {
                status("Requesting access — switch to Sage 50 and approve the authorization prompt...");
                auth = _session.RequestAccess(target);
            }

            int waitedMs = 0;
            while ((auth == AuthorizationResult.Pending || auth == AuthorizationResult.NoCredentials)
                   && waitedMs < AuthorizationTimeoutMs)
            {
                status("Waiting for authorization inside Sage 50 (" + ((AuthorizationTimeoutMs - waitedMs) / 1000) + "s left)...");
                Thread.Sleep(3000);
                waitedMs += 3000;
                auth = _session.VerifyAccess(target);
            }

            if (auth != AuthorizationResult.Granted)
            {
                throw new InvalidOperationException(DescribeAuthFailure(auth, target.CompanyName));
            }

            status("Opening company \"" + target.CompanyName + "\"...");
            try
            {
                _company = _session.Open(target);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(FriendlySageMessage(ex), ex);
            }
            CompanyName = target.CompanyName;
            _accountCache = null;
            Logger.Info(Op, "Connected to Sage company \"" + CompanyName + "\".");
        }

        private CompanyIdentifier PickCompany(string companyName)
        {
            CompanyIdentifierList companies;
            try
            {
                companies = _session.CompanyList();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(FriendlySageMessage(ex), ex);
            }

            if (companies == null || companies.Count == 0)
            {
                throw new InvalidOperationException(
                    "No Sage companies were found on this machine. Is Sage 50 installed and the company file accessible?");
            }

            if (string.IsNullOrEmpty(companyName))
            {
                if (companies.Count == 1) { return companies[0]; }
                throw new InvalidOperationException(
                    "Multiple Sage companies found — set the company name in Settings. Available: "
                    + JoinCompanyNames(companies));
            }

            foreach (CompanyIdentifier c in companies)
            {
                if (string.Equals(c.CompanyName, companyName, StringComparison.OrdinalIgnoreCase))
                {
                    return c;
                }
            }
            throw new InvalidOperationException(
                "Sage company \"" + companyName + "\" was not found. Available: " + JoinCompanyNames(companies));
        }

        private static string JoinCompanyNames(CompanyIdentifierList companies)
        {
            List<string> names = new List<string>();
            foreach (CompanyIdentifier c in companies) { names.Add(c.CompanyName); }
            return string.Join(", ", names);
        }

        private static string DescribeAuthFailure(AuthorizationResult auth, string companyName)
        {
            switch (auth)
            {
                case AuthorizationResult.Denied:
                    return "Sage denied this application access to \"" + companyName + "\". Re-grant access inside "
                         + "Sage under Maintain > Users > Set Up Security (SDK Data Access must be Full).";
                case AuthorizationResult.Pending:
                case AuthorizationResult.None:
                case AuthorizationResult.NoCredentials:
                    return "Authorization was not granted in time. Open Sage 50, approve the access prompt for this "
                         + "application, then try again.";
                case AuthorizationResult.LoginRestricted:
                    return "Sage reports the login is restricted for \"" + companyName + "\" (another user/session may "
                         + "hold it). Close other Sage sessions and retry.";
                case AuthorizationResult.CompanyLocked:
                    return "The Sage company \"" + companyName + "\" is locked (single-user operation in progress?). "
                         + "Finish it in Sage and retry.";
                case AuthorizationResult.CorruptedOrTampered:
                    return "Sage reports the authorization record is corrupted. Revoke and re-grant this application's "
                         + "access inside Sage.";
                default:
                    return "Sage authorization failed: " + auth;
            }
        }

        /// <summary>
        /// Maps SDK exceptions (Sage.Peachtree.API.Exceptions.*) to actionable
        /// messages by type name, so we degrade gracefully on anything unexpected.
        /// </summary>
        public static string FriendlySageMessage(Exception ex)
        {
            switch (ex.GetType().Name)
            {
                case "SessionAlreadyOpenedException":
                    return "A Sage SDK session is already open in this process. Restart the app and try again.";
                case "ApplicationIdentifierExpiredException":
                    return "The Sage application id has expired. Request a new application id from Sage and update Settings.";
                case "InvalidApplicationIdentifierException":
                case "ApplicationIdentifierRejectedException":
                    return "Sage rejected the configured application id. Check the value in Settings (blank works for "
                         + "sample companies only).";
                case "CompanyClosedException":
                    return "The Sage company has been closed. Reconnect and try again.";
                case "CompanyExclusiveAccessException":
                case "CompanySharedAccessException":
                    return "The Sage company file is locked by another session. Close other Sage activity and retry.";
                case "InadequatePermissionException":
                    return "The Sage user does not have permission for this operation. Set SDK Data Access to Full in "
                         + "Maintain > Users > Set Up Security.";
                case "LicenseNotAvailableException":
                    return "No Sage license is available for an SDK session right now. Close another Sage session and retry.";
                case "ProductNotRegisteredException":
                    return "Sage 50 is not registered on this machine; the SDK cannot open a session.";
                case "RecordInUseException":
                    return "The Sage record is currently being edited inside Sage. Close the record's window and retry.";
                case "ValidationException":
                    return "Sage rejected the data: " + ex.Message;
                default:
                    return "Sage error (" + ex.GetType().Name + "): " + ex.Message;
            }
        }

        // ------------------------------------------------------------------
        // Reads
        // ------------------------------------------------------------------

        /// <summary>Lists open (unpaid) sales invoices for the main grid.</summary>
        public List<SageInvoiceSummary> ListOpenInvoices()
        {
            RequireCompany();

            // Load customers once so we can resolve names without per-invoice loads.
            CustomerList customers = _company.Factories.CustomerFactory.List();
            customers.Load();
            Dictionary<Guid, Customer> customersByKey = new Dictionary<Guid, Customer>();
            foreach (Customer c in customers)
            {
                customersByKey[c.Key.Guid] = c;
            }

            SalesInvoiceList invoices = _company.Factories.SalesInvoiceFactory.List();
            invoices.Load();

            List<SageInvoiceSummary> rows = new List<SageInvoiceSummary>();
            foreach (SalesInvoice inv in invoices)
            {
                if (inv.AmountDue <= 0m) { continue; }

                SageInvoiceSummary row = new SageInvoiceSummary();
                row.ReferenceNumber = inv.ReferenceNumber;
                row.Date = inv.Date;
                row.AmountDue = inv.AmountDue;

                Customer cust;
                if (inv.CustomerReference != null && customersByKey.TryGetValue(inv.CustomerReference.Guid, out cust))
                {
                    row.CustomerId = cust.ID;
                    row.CustomerName = cust.Name;
                }
                rows.Add(row);
            }
            rows.Sort(delegate(SageInvoiceSummary a, SageInvoiceSummary b) { return b.Date.CompareTo(a.Date); });
            Logger.Info(Op, "Loaded " + rows.Count + " open invoice(s) from Sage.");
            return rows;
        }

        /// <summary>
        /// Finds a sales invoice by its reference number (= our sage_invoice_id).
        /// Tries a server-side filter first; falls back to a full scan if the
        /// filter expression is rejected at runtime.
        /// </summary>
        public SalesInvoice FindInvoiceByReference(string referenceNumber)
        {
            RequireCompany();
            SalesInvoiceList list = _company.Factories.SalesInvoiceFactory.List();
            try
            {
                LoadModifiers mods = LoadModifiers.Create();
                mods.Filters = FilterExpression.Equal(
                    FilterExpression.Property("SalesInvoice.ReferenceNumber"),
                    FilterExpression.StringConstant(referenceNumber));
                list.Load(mods);
            }
            catch (Exception ex)
            {
                Logger.Warn(Op, "Filtered invoice load failed (" + ex.Message + "); falling back to full scan.");
                list = _company.Factories.SalesInvoiceFactory.List();
                list.Load();
            }

            foreach (SalesInvoice inv in list)
            {
                if (string.Equals(inv.ReferenceNumber, referenceNumber, StringComparison.OrdinalIgnoreCase))
                {
                    return inv;
                }
            }
            return null;
        }

        public Customer LoadCustomerForInvoice(SalesInvoice invoice)
        {
            RequireCompany();
            if (invoice.CustomerReference == null || invoice.CustomerReference.IsEmpty)
            {
                throw new InvalidOperationException(
                    "Sage invoice " + invoice.ReferenceNumber + " has no customer attached.");
            }
            return _company.Factories.CustomerFactory.Load(invoice.CustomerReference);
        }

        // ------------------------------------------------------------------
        // Customer custom field (Stripe customer id linkage, TRD section 5)
        // ------------------------------------------------------------------

        /// <summary>Reads the Stripe customer id custom field; null if the field isn't usable.</summary>
        public string ReadStripeCustomerIdField(Customer customer, int oneBasedIndex)
        {
            CustomFieldValue field = GetCustomField(customer, oneBasedIndex);
            if (field == null) { return null; }
            object value = field.Value;
            return value == null ? null : value.ToString().Trim();
        }

        /// <summary>
        /// Writes the Stripe customer id into the configured custom field, saves,
        /// and verifies by re-loading the customer (TRD section 8 rule 5).
        /// </summary>
        public void WriteStripeCustomerIdField(Customer customer, int oneBasedIndex, string stripeCustomerId)
        {
            CustomFieldValue field = GetCustomField(customer, oneBasedIndex);
            if (field == null)
            {
                throw new InvalidOperationException(
                    "Customer custom field #" + oneBasedIndex + " is not available in this company "
                    + "(enable it under Maintain > Default Information > Customers, or change the index in Settings).");
            }

            field.Value = stripeCustomerId;
            try
            {
                customer.Save();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(FriendlySageMessage(ex), ex);
            }

            // Read-back verification.
            Customer reloaded = _company.Factories.CustomerFactory.Load(customer.Key);
            string readBack = ReadStripeCustomerIdField(reloaded, oneBasedIndex);
            if (!string.Equals(readBack, stripeCustomerId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Wrote Stripe customer id to Sage custom field #" + oneBasedIndex
                    + " but read back \"" + readBack + "\" — check the field configuration.");
            }
            Logger.Info(Op, "Stored Stripe customer id " + stripeCustomerId + " on Sage customer "
                + customer.ID + " (custom field #" + oneBasedIndex + ", verified).");
        }

        private static CustomFieldValue GetCustomField(Customer customer, int oneBasedIndex)
        {
            CustomFieldValueCollection fields = customer.CustomFieldValues;
            if (fields == null) { return null; }
            int index = oneBasedIndex - 1;
            if (index < 0 || index >= fields.Count) { return null; }
            return fields[index];
        }

        // ------------------------------------------------------------------
        // Accounts
        // ------------------------------------------------------------------

        /// <summary>Resolves a GL account by ID or description (config values).</summary>
        public Account ResolveAccount(string idOrDescription)
        {
            RequireCompany();
            if (string.IsNullOrEmpty(idOrDescription))
            {
                throw new InvalidOperationException("A Sage account name is not configured — check Settings.");
            }

            if (_accountCache == null)
            {
                _accountCache = new Dictionary<string, Account>(StringComparer.OrdinalIgnoreCase);
                AccountList accounts = _company.Factories.AccountFactory.List();
                accounts.Load();
                foreach (Account a in accounts)
                {
                    if (!string.IsNullOrEmpty(a.ID) && !_accountCache.ContainsKey(a.ID)) { _accountCache[a.ID] = a; }
                    if (!string.IsNullOrEmpty(a.Description) && !_accountCache.ContainsKey(a.Description)) { _accountCache[a.Description] = a; }
                }
            }

            Account found;
            if (_accountCache.TryGetValue(idOrDescription.Trim(), out found)) { return found; }
            throw new InvalidOperationException(
                "Sage account \"" + idOrDescription + "\" was not found (searched account IDs and descriptions). "
                + "Create it in Sage or fix the name in Settings.");
        }

        // ------------------------------------------------------------------
        // Payment recording (TRD 6.2 step 5: gross to invoice, fee split out)
        // ------------------------------------------------------------------

        /// <summary>
        /// Records a Stripe payment in Sage: a Receive Money receipt applying the
        /// gross amount to the invoice, with the Stripe fee either as a negative
        /// sales line on the same receipt (net lands in clearing) or as a paired
        /// general journal entry, per feeBookingMode ("receipt-line" tries the
        /// line first and falls back to the journal entry automatically).
        /// </summary>
        public PaymentRecordResult RecordStripePayment(
            string sageInvoiceRef, long grossCents, long feeCents, DateTime paidAt,
            string stripeInvoiceLabel, string clearingAccountName, string feesAccountName,
            string feeBookingMode, string paymentMethod)
        {
            RequireCompany();

            SalesInvoice invoice = FindInvoiceByReference(sageInvoiceRef);
            if (invoice == null)
            {
                throw new InvalidOperationException("No Sage invoice with reference number \"" + sageInvoiceRef + "\" was found.");
            }

            PaymentRecordResult result = new PaymentRecordResult();

            if (invoice.AmountDue <= 0m)
            {
                result.Outcome = PaymentRecordOutcome.AlreadyClosedInSage;
                result.Message = "Sage invoice " + sageInvoiceRef + " is already fully paid (amount due is zero); nothing recorded.";
                return result;
            }

            long dueCents = Money.ToCents(invoice.AmountDue);
            if (dueCents != grossCents)
            {
                throw new InvalidOperationException(
                    "Amount mismatch for Sage invoice " + sageInvoiceRef + ": Stripe paid " + Money.FormatCents(grossCents)
                    + " but Sage shows " + Money.FormatCents(dueCents) + " due. Not recording — resolve manually.");
            }

            Account clearing = ResolveAccount(clearingAccountName);
            Account fees = feeCents > 0 ? ResolveAccount(feesAccountName) : null;

            decimal grossAmount = grossCents / 100m;
            decimal feeAmount = feeCents / 100m;
            bool preferReceiptLine = !string.Equals(feeBookingMode, "journal-entry", StringComparison.OrdinalIgnoreCase);

            Receipt receipt = _company.Factories.ReceiptFactory.Create();
            receipt.CustomerReference = invoice.CustomerReference;
            receipt.AccountReference = clearing.Key;       // the cash/GL account the money lands in
            receipt.Date = paidAt.Date;
            receipt.ReferenceNumber = Truncate("STR-" + sageInvoiceRef, SageReferenceMaxLength);
            if (!string.IsNullOrEmpty(paymentMethod)) { receipt.PaymentMethod = paymentMethod; }

            ReceiptInvoiceLine invoiceLine = receipt.AddInvoiceLine(invoice);
            invoiceLine.AmountPaid = grossAmount;          // apply GROSS so the invoice closes fully

            bool feeBookedOnReceipt = false;
            bool receiptSaved = false;

            if (feeCents > 0 && preferReceiptLine)
            {
                // Negative sales line books the fee to the fee account, so the
                // receipt total equals the net that Stripe deposits.
                ReceiptSalesLine feeLine = receipt.AddSalesLine();
                feeLine.AccountReference = fees.Key;
                feeLine.Amount = -feeAmount;
                feeLine.Description = Truncate("Stripe fee - " + stripeInvoiceLabel, 160);
                try
                {
                    receipt.Save();
                    receiptSaved = true;
                    feeBookedOnReceipt = true;
                }
                catch (Exception ex)
                {
                    Logger.Warn(Op, "Receipt with negative fee line was rejected (" + ex.Message
                        + "); falling back to gross receipt + journal entry for " + sageInvoiceRef + ".");
                    receipt.RemoveSalesLine(feeLine);
                }
            }

            if (!receiptSaved)
            {
                try
                {
                    receipt.Save();
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(FriendlySageMessage(ex), ex);
                }
            }
            Logger.Info(Op, "Receipt " + receipt.ReferenceNumber + " saved: " + Money.FormatCents(grossCents)
                + " applied to invoice " + sageInvoiceRef
                + (feeBookedOnReceipt ? " with fee line " + Money.FormatCents(-feeCents) : ""));

            // Fee not on the receipt: move it with a paired journal entry
            // (debit fee expense, credit clearing) in the same operation.
            if (feeCents > 0 && !feeBookedOnReceipt)
            {
                try
                {
                    GeneralJournalEntry gje = _company.Factories.GeneralJournalEntryFactory.Create();
                    gje.Date = paidAt.Date;
                    gje.ReferenceNumber = Truncate("STRFEE-" + sageInvoiceRef, SageReferenceMaxLength);

                    GeneralJournalEntryLine debit = gje.AddLine();
                    debit.AccountReference = fees.Key;
                    debit.Amount = feeAmount; // positive = debit
                    debit.Description = Truncate("Stripe fee - " + stripeInvoiceLabel, 160);

                    GeneralJournalEntryLine credit = gje.AddLine();
                    credit.AccountReference = clearing.Key;
                    credit.Amount = -feeAmount; // negative = credit
                    credit.Description = Truncate("Stripe fee out of clearing - " + stripeInvoiceLabel, 160);

                    gje.Save();
                    Logger.Info(Op, "Journal entry " + gje.ReferenceNumber + " saved: fee " + Money.FormatCents(feeCents)
                        + " moved from clearing to fee account.");
                }
                catch (Exception ex)
                {
                    // The receipt IS saved at this point; surface a precise
                    // manual-action message rather than un-doing the payment.
                    Logger.Error(Op, "Receipt saved but the fee journal entry failed for " + sageInvoiceRef, ex);
                    result.Outcome = PaymentRecordOutcome.ReceiptSavedFeeUnbooked;
                    result.Message = "Payment recorded (receipt " + receipt.ReferenceNumber + "), but the Stripe fee of "
                        + Money.FormatCents(feeCents) + " could NOT be booked automatically: " + FriendlySageMessage(ex)
                        + " — book it manually (debit \"" + feesAccountName + "\", credit \"" + clearingAccountName + "\").";
                    return result;
                }
            }

            // Read-back verification: the invoice should now be fully paid.
            string verifyNote = "";
            try
            {
                SalesInvoice reloaded = FindInvoiceByReference(sageInvoiceRef);
                if (reloaded != null && reloaded.AmountDue != 0m)
                {
                    verifyNote = " (warning: Sage still shows " + reloaded.AmountDue.ToString("C2") + " due after posting)";
                    Logger.Warn(Op, "Post-payment verification: invoice " + sageInvoiceRef + " still shows amount due "
                        + reloaded.AmountDue.ToString("C2"));
                }
            }
            catch (Exception ex)
            {
                verifyNote = " (could not verify amount due after posting: " + ex.Message + ")";
            }

            result.Outcome = PaymentRecordOutcome.Recorded;
            result.Message = "Recorded " + Money.FormatCents(grossCents) + " against Sage invoice " + sageInvoiceRef
                + (feeCents > 0 ? ", fee " + Money.FormatCents(feeCents)
                    + (feeBookedOnReceipt ? " on receipt" : " via journal entry") : "")
                + verifyNote;
            return result;
        }

        private static string Truncate(string value, int maxLength)
        {
            if (value == null) { return null; }
            return value.Length <= maxLength ? value : value.Substring(0, maxLength);
        }

        private void RequireCompany()
        {
            if (!IsConnected)
            {
                throw new InvalidOperationException("Not connected to a Sage company. Connect first.");
            }
        }

        public void Dispose()
        {
            try
            {
                if (_company != null && !_company.IsClosed) { _session.Close(_company); }
            }
            catch (Exception) { }
            try
            {
                if (_session != null) { _session.End(); _session.Dispose(); }
            }
            catch (Exception) { }
            _company = null;
            _session = null;
            _accountCache = null;
        }
    }
}
