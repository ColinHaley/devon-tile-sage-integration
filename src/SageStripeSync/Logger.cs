using System;
using System.IO;
using System.Text;

namespace SageStripeSync
{
    /// <summary>
    /// Structured per-operation file logger (TRD section 9).
    /// Writes one line per event to a daily log file under
    /// %LOCALAPPDATA%\SageStripeSync\logs and raises an event so the UI can
    /// mirror the log live. Never log secrets through this class.
    /// </summary>
    public static class Logger
    {
        private static readonly object WriteLock = new object();

        /// <summary>Raised for each formatted log line (UI subscribes to this).</summary>
        public static event Action<string> LineLogged;

        public static string LogDirectory
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SageStripeSync", "logs");
            }
        }

        public static void Info(string operation, string message) { Write("INFO", operation, message); }
        public static void Warn(string operation, string message) { Write("WARN", operation, message); }
        public static void Error(string operation, string message) { Write("ERROR", operation, message); }

        public static void Error(string operation, string message, Exception ex)
        {
            // Include exception type and full detail in the file; the first line
            // is what the UI shows.
            Write("ERROR", operation, message + " | " + ex.GetType().Name + ": " + ex.Message);
            try
            {
                lock (WriteLock)
                {
                    File.AppendAllText(CurrentLogFile(), ex.ToString() + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch (IOException) { /* logging must never take the app down */ }
        }

        private static void Write(string level, string operation, string message)
        {
            string line = string.Format("{0:yyyy-MM-dd HH:mm:ss} [{1}] [{2}] {3}",
                DateTime.Now, level, operation, message);
            try
            {
                lock (WriteLock)
                {
                    Directory.CreateDirectory(LogDirectory);
                    File.AppendAllText(CurrentLogFile(), line + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch (Exception) { /* swallow: a failed disk write must not crash a sync */ }

            Action<string> handler = LineLogged;
            if (handler != null)
            {
                try { handler(line); }
                catch (Exception) { }
            }
        }

        private static string CurrentLogFile()
        {
            return Path.Combine(LogDirectory, string.Format("sync-{0:yyyyMMdd}.log", DateTime.Now));
        }
    }
}
