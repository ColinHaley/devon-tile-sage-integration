using System;
using System.Windows.Forms;

namespace SageStripeSync
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // Last-resort handlers so nothing dies silently (TRD section 9).
            Application.ThreadException += delegate(object sender, System.Threading.ThreadExceptionEventArgs e)
            {
                Logger.Error("App", "Unhandled UI exception", e.Exception);
                MessageBox.Show(e.Exception.Message, "Unexpected error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
            {
                Exception ex = e.ExceptionObject as Exception;
                if (ex != null) { Logger.Error("App", "Unhandled exception", ex); }
            };

            Application.Run(new MainForm());
        }
    }
}
