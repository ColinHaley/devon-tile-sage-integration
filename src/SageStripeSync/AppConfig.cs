using System;
using System.IO;
using Newtonsoft.Json;

namespace SageStripeSync
{
    /// <summary>
    /// Non-secret configuration (TRD section 10), persisted as JSON at
    /// %LOCALAPPDATA%\SageStripeSync\config.json. Secrets (Stripe keys, Sage
    /// application id) live in <see cref="SecretStore"/> instead.
    /// </summary>
    public class AppConfig
    {
        /// <summary>"test" or "live" (selects which Stripe key is used).</summary>
        public string StripeMode { get; set; }

        /// <summary>Sage company to open; blank = the only company available.</summary>
        public string SageCompanyName { get; set; }

        /// <summary>Which of the 5 customer custom fields stores the Stripe customer id (1-based).</summary>
        public int CustomerStripeIdFieldIndex { get; set; }

        /// <summary>Sage GL/bank account (ID or description) where net Stripe cash lands.</summary>
        public string StripeClearingAccount { get; set; }

        /// <summary>Sage expense account (ID or description) for Stripe processing fees.</summary>
        public string StripeFeesAccount { get; set; }

        /// <summary>Email used when a Sage customer has none.</summary>
        public string FallbackCustomerEmail { get; set; }

        /// <summary>ISO currency code for Stripe invoices (lowercase, e.g. "usd").</summary>
        public string Currency { get; set; }

        /// <summary>
        /// How the Stripe fee is booked in Sage: "receipt-line" (negative sales
        /// line on the receipt; falls back to a journal entry if Sage rejects
        /// it) or "journal-entry" (always use a paired journal entry).
        /// </summary>
        public string FeeBookingMode { get; set; }

        /// <summary>Optional Sage payment method label for receipts (blank = Sage default).</summary>
        public string SagePaymentMethod { get; set; }

        public AppConfig()
        {
            // Defaults per TRD section 10.
            StripeMode = "test";
            SageCompanyName = "";
            CustomerStripeIdFieldIndex = 1;
            StripeClearingAccount = "Stripe Clearing";
            StripeFeesAccount = "Stripe Fees";
            FallbackCustomerEmail = "contact@devontile.com";
            Currency = "usd";
            FeeBookingMode = "receipt-line";
            SagePaymentMethod = "";
        }

        public bool IsLiveMode
        {
            get { return string.Equals(StripeMode, "live", StringComparison.OrdinalIgnoreCase); }
        }

        public static string ConfigDirectory
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SageStripeSync");
            }
        }

        private static string ConfigFile
        {
            get { return Path.Combine(ConfigDirectory, "config.json"); }
        }

        /// <summary>Loads the saved config, or returns defaults if none exists yet.</summary>
        public static AppConfig Load()
        {
            try
            {
                if (File.Exists(ConfigFile))
                {
                    AppConfig cfg = JsonConvert.DeserializeObject<AppConfig>(File.ReadAllText(ConfigFile));
                    if (cfg != null) { return cfg; }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("Config", "Could not read config.json, using defaults: " + ex.Message);
            }
            return new AppConfig();
        }

        public void Save()
        {
            Directory.CreateDirectory(ConfigDirectory);
            File.WriteAllText(ConfigFile, JsonConvert.SerializeObject(this, Formatting.Indented));
        }
    }
}
