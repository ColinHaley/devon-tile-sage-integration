using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace SageStripeSync
{
    /// <summary>
    /// Secrets (Stripe API keys and the Sage application id) encrypted at rest
    /// with Windows DPAPI, current-user scope (TRD section 9). Stored at
    /// %LOCALAPPDATA%\SageStripeSync\secrets.bin — never in source control and
    /// never in plain text.
    /// </summary>
    public class SecretStore
    {
        public string StripeTestKey { get; set; }
        public string StripeLiveKey { get; set; }
        public string SageApplicationId { get; set; }

        public SecretStore()
        {
            StripeTestKey = "";
            StripeLiveKey = "";
            SageApplicationId = "";
        }

        /// <summary>The Stripe secret key for the given mode, or empty string.</summary>
        public string StripeKeyForMode(bool live)
        {
            string key = live ? StripeLiveKey : StripeTestKey;
            return key == null ? "" : key.Trim();
        }

        private static string SecretsFile
        {
            get { return Path.Combine(AppConfig.ConfigDirectory, "secrets.bin"); }
        }

        public static SecretStore Load()
        {
            try
            {
                if (File.Exists(SecretsFile))
                {
                    byte[] encrypted = File.ReadAllBytes(SecretsFile);
                    byte[] plain = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
                    SecretStore store = JsonConvert.DeserializeObject<SecretStore>(Encoding.UTF8.GetString(plain));
                    if (store != null) { return store; }
                }
            }
            catch (Exception ex)
            {
                // A corrupt/foreign-user secrets file just means "not configured yet".
                Logger.Warn("Config", "Could not read secrets store (will need re-entry): " + ex.Message);
            }
            return new SecretStore();
        }

        public void Save()
        {
            Directory.CreateDirectory(AppConfig.ConfigDirectory);
            byte[] plain = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(this));
            byte[] encrypted = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(SecretsFile, encrypted);
        }
    }
}
