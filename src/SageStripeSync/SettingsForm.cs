using System;
using System.Drawing;
using System.Windows.Forms;

namespace SageStripeSync
{
    /// <summary>
    /// Edits all configuration (TRD section 10). Non-secret values go to
    /// config.json; Stripe keys and the Sage application id go to the
    /// DPAPI-encrypted secret store.
    /// </summary>
    public class SettingsForm : Form
    {
        public AppConfig Config { get; private set; }
        public SecretStore Secrets { get; private set; }

        private ComboBox _mode;
        private TextBox _testKey;
        private TextBox _liveKey;
        private TextBox _sageAppId;
        private TextBox _companyName;
        private NumericUpDown _fieldIndex;
        private TextBox _clearingAccount;
        private TextBox _feesAccount;
        private TextBox _fallbackEmail;
        private TextBox _currency;
        private ComboBox _feeMode;
        private TextBox _paymentMethod;

        public SettingsForm(AppConfig config, SecretStore secrets)
        {
            Config = config;
            Secrets = secrets;
            BuildUi();
            LoadValues();
        }

        private void BuildUi()
        {
            Text = "Settings";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            Width = 560;

            TableLayoutPanel table = new TableLayoutPanel();
            table.Dock = DockStyle.Top;
            table.ColumnCount = 2;
            table.AutoSize = true;
            table.Padding = new Padding(12);
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            _mode = new ComboBox();
            _mode.DropDownStyle = ComboBoxStyle.DropDownList;
            _mode.Items.AddRange(new object[] { "test", "live" });
            AddRow(table, "Stripe mode", _mode);

            _testKey = new TextBox();
            _testKey.UseSystemPasswordChar = true;
            AddRow(table, "Stripe TEST secret key", _testKey);

            _liveKey = new TextBox();
            _liveKey.UseSystemPasswordChar = true;
            AddRow(table, "Stripe LIVE secret key", _liveKey);

            _sageAppId = new TextBox();
            AddRow(table, "Sage application id", _sageAppId);

            _companyName = new TextBox();
            AddRow(table, "Sage company name (blank = only company)", _companyName);

            _fieldIndex = new NumericUpDown();
            _fieldIndex.Minimum = 1;
            _fieldIndex.Maximum = 5;
            AddRow(table, "Customer custom field # for Stripe id (1-5)", _fieldIndex);

            _clearingAccount = new TextBox();
            AddRow(table, "Stripe Clearing account (ID or name)", _clearingAccount);

            _feesAccount = new TextBox();
            AddRow(table, "Stripe Fees account (ID or name)", _feesAccount);

            _fallbackEmail = new TextBox();
            AddRow(table, "Fallback customer email", _fallbackEmail);

            _currency = new TextBox();
            AddRow(table, "Currency (ISO code)", _currency);

            _feeMode = new ComboBox();
            _feeMode.DropDownStyle = ComboBoxStyle.DropDownList;
            _feeMode.Items.AddRange(new object[] { "receipt-line", "journal-entry" });
            AddRow(table, "Fee booking mode", _feeMode);

            _paymentMethod = new TextBox();
            AddRow(table, "Sage payment method (blank = default)", _paymentMethod);

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.FlowDirection = FlowDirection.RightToLeft;
            buttons.Dock = DockStyle.Bottom;
            buttons.Padding = new Padding(12);
            buttons.Height = 52;

            Button cancel = new Button();
            cancel.Text = "Cancel";
            cancel.DialogResult = DialogResult.Cancel;
            Button save = new Button();
            save.Text = "Save";
            save.Click += OnSaveClick;
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(save);

            Controls.Add(table);
            Controls.Add(buttons);
            AcceptButton = save;
            CancelButton = cancel;

            Load += delegate { Height = table.PreferredSize.Height + buttons.Height + 60; };
        }

        private static void AddRow(TableLayoutPanel table, string label, Control control)
        {
            Label l = new Label();
            l.Text = label;
            l.AutoSize = true;
            l.Anchor = AnchorStyles.Left;
            l.Margin = new Padding(3, 8, 3, 3);
            control.Dock = DockStyle.Fill;
            int row = table.RowCount;
            table.RowCount = row + 1;
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            table.Controls.Add(l, 0, row);
            table.Controls.Add(control, 1, row);
        }

        private void LoadValues()
        {
            _mode.SelectedItem = Config.IsLiveMode ? "live" : "test";
            _testKey.Text = Secrets.StripeTestKey;
            _liveKey.Text = Secrets.StripeLiveKey;
            _sageAppId.Text = Secrets.SageApplicationId;
            _companyName.Text = Config.SageCompanyName;
            _fieldIndex.Value = Math.Max(1, Math.Min(5, Config.CustomerStripeIdFieldIndex));
            _clearingAccount.Text = Config.StripeClearingAccount;
            _feesAccount.Text = Config.StripeFeesAccount;
            _fallbackEmail.Text = Config.FallbackCustomerEmail;
            _currency.Text = Config.Currency;
            _feeMode.SelectedItem = string.Equals(Config.FeeBookingMode, "journal-entry", StringComparison.OrdinalIgnoreCase)
                ? "journal-entry" : "receipt-line";
            _paymentMethod.Text = Config.SagePaymentMethod;
        }

        private void OnSaveClick(object sender, EventArgs e)
        {
            string mode = (string)_mode.SelectedItem;
            string key = mode == "live" ? _liveKey.Text.Trim() : _testKey.Text.Trim();
            if (key.Length == 0)
            {
                MessageBox.Show(this, "No Stripe secret key is set for " + mode + " mode.",
                    "Settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (mode == "live" && !key.StartsWith("sk_live_", StringComparison.Ordinal))
            {
                DialogResult keep = MessageBox.Show(this,
                    "The LIVE key does not start with sk_live_ — save anyway?",
                    "Settings", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (keep != DialogResult.Yes) { return; }
            }

            Config.StripeMode = mode;
            Config.SageCompanyName = _companyName.Text.Trim();
            Config.CustomerStripeIdFieldIndex = (int)_fieldIndex.Value;
            Config.StripeClearingAccount = _clearingAccount.Text.Trim();
            Config.StripeFeesAccount = _feesAccount.Text.Trim();
            Config.FallbackCustomerEmail = _fallbackEmail.Text.Trim();
            Config.Currency = _currency.Text.Trim().ToLowerInvariant();
            Config.FeeBookingMode = (string)_feeMode.SelectedItem;
            Config.SagePaymentMethod = _paymentMethod.Text.Trim();

            Secrets.StripeTestKey = _testKey.Text.Trim();
            Secrets.StripeLiveKey = _liveKey.Text.Trim();
            Secrets.SageApplicationId = _sageAppId.Text.Trim();

            try
            {
                Config.Save();
                Secrets.Save();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not save settings: " + ex.Message,
                    "Settings", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            Logger.Info("Config", "Settings saved (mode: " + mode + ").");
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
