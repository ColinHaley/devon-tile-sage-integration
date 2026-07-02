using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SageStripeSync
{
    /// <summary>
    /// Main window (TRD section 4 item 4): a grid of open Sage invoices with a
    /// per-row "Send to Stripe" button, a global "Check for Paid Invoices"
    /// button, a live log pane, and Settings. All Sage/Stripe work runs on
    /// worker threads via Task.Run so the UI stays responsive.
    /// </summary>
    public class MainForm : Form
    {
        private AppConfig _config;
        private SecretStore _secrets;
        private SageGateway _sage;

        private ToolStrip _toolbar;
        private ToolStripButton _refreshButton;
        private ToolStripButton _checkPaidButton;
        private ToolStripButton _settingsButton;
        private ToolStripLabel _modeLabel;
        private DataGridView _grid;
        private TextBox _logBox;
        private StatusStrip _statusStrip;
        private ToolStripStatusLabel _statusLabel;
        private bool _busy;

        public MainForm()
        {
            _config = AppConfig.Load();
            _secrets = SecretStore.Load();
            _sage = new SageGateway();
            BuildUi();

            // Mirror every log line into the UI pane (marshalled to the UI thread).
            Logger.LineLogged += OnLogLine;
            FormClosed += delegate { Logger.LineLogged -= OnLogLine; _sage.Dispose(); };
        }

        private void BuildUi()
        {
            Text = "Sage 50 ↔ Stripe — Devon Tile";
            Width = 1000;
            Height = 700;
            StartPosition = FormStartPosition.CenterScreen;

            _toolbar = new ToolStrip();
            _refreshButton = new ToolStripButton("Connect && Refresh Invoices");
            _refreshButton.Click += OnRefreshClick;
            _checkPaidButton = new ToolStripButton("Check for Paid Invoices");
            _checkPaidButton.Click += OnCheckPaidClick;
            _settingsButton = new ToolStripButton("Settings");
            _settingsButton.Click += OnSettingsClick;
            _modeLabel = new ToolStripLabel();
            _modeLabel.Alignment = ToolStripItemAlignment.Right;
            _toolbar.Items.Add(_refreshButton);
            _toolbar.Items.Add(new ToolStripSeparator());
            _toolbar.Items.Add(_checkPaidButton);
            _toolbar.Items.Add(new ToolStripSeparator());
            _toolbar.Items.Add(_settingsButton);
            _toolbar.Items.Add(_modeLabel);

            _grid = new DataGridView();
            _grid.Dock = DockStyle.Fill;
            _grid.AllowUserToAddRows = false;
            _grid.AllowUserToDeleteRows = false;
            _grid.ReadOnly = true;
            _grid.RowHeadersVisible = false;
            _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            _grid.CellContentClick += OnGridCellClick;

            DataGridViewTextBoxColumn colRef = new DataGridViewTextBoxColumn();
            colRef.HeaderText = "Invoice #";
            colRef.Name = "RefNumber";
            colRef.FillWeight = 60;
            DataGridViewTextBoxColumn colDate = new DataGridViewTextBoxColumn();
            colDate.HeaderText = "Date";
            colDate.Name = "Date";
            colDate.FillWeight = 50;
            DataGridViewTextBoxColumn colCust = new DataGridViewTextBoxColumn();
            colCust.HeaderText = "Customer";
            colCust.Name = "Customer";
            colCust.FillWeight = 110;
            DataGridViewTextBoxColumn colDue = new DataGridViewTextBoxColumn();
            colDue.HeaderText = "Amount Due";
            colDue.Name = "AmountDue";
            colDue.FillWeight = 50;
            colDue.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            DataGridViewButtonColumn colSend = new DataGridViewButtonColumn();
            colSend.HeaderText = "";
            colSend.Name = "Send";
            colSend.Text = "Send to Stripe";
            colSend.UseColumnTextForButtonValue = true;
            colSend.FillWeight = 55;
            DataGridViewTextBoxColumn colStatus = new DataGridViewTextBoxColumn();
            colStatus.HeaderText = "Result";
            colStatus.Name = "Status";
            colStatus.FillWeight = 120;
            _grid.Columns.AddRange(new DataGridViewColumn[] { colRef, colDate, colCust, colDue, colSend, colStatus });

            _logBox = new TextBox();
            _logBox.Multiline = true;
            _logBox.ReadOnly = true;
            _logBox.ScrollBars = ScrollBars.Vertical;
            _logBox.Dock = DockStyle.Fill;
            _logBox.Font = new Font(FontFamily.GenericMonospace, 8.5f);
            _logBox.BackColor = Color.White;

            SplitContainer split = new SplitContainer();
            split.Dock = DockStyle.Fill;
            split.Orientation = Orientation.Horizontal;
            split.Panel1.Controls.Add(_grid);
            split.Panel2.Controls.Add(_logBox);

            _statusStrip = new StatusStrip();
            _statusLabel = new ToolStripStatusLabel("Not connected to Sage.");
            _statusStrip.Items.Add(_statusLabel);

            Controls.Add(split);
            Controls.Add(_toolbar);
            Controls.Add(_statusStrip);

            Load += delegate
            {
                split.SplitterDistance = (int)(Height * 0.62);
                UpdateModeLabel();
                Logger.Info("App", "SageStripeSync started. Logs: " + Logger.LogDirectory);
            };
        }

        private void UpdateModeLabel()
        {
            if (_config.IsLiveMode)
            {
                _modeLabel.Text = "LIVE MODE";
                _modeLabel.ForeColor = Color.Firebrick;
            }
            else
            {
                _modeLabel.Text = "TEST MODE";
                _modeLabel.ForeColor = Color.SeaGreen;
            }
            _modeLabel.Font = new Font(_toolbar.Font, FontStyle.Bold);
        }

        private void OnLogLine(string line)
        {
            if (IsDisposed) { return; }
            try
            {
                BeginInvoke((Action)delegate
                {
                    _logBox.AppendText(line + Environment.NewLine);
                });
            }
            catch (InvalidOperationException) { /* form closing */ }
        }

        private void SetBusy(bool busy, string status)
        {
            _busy = busy;
            _refreshButton.Enabled = !busy;
            _checkPaidButton.Enabled = !busy;
            _settingsButton.Enabled = !busy;
            _grid.Enabled = !busy;
            UseWaitCursor = busy;
            if (status != null) { _statusLabel.Text = status; }
        }

        private void ReportStatus(string status)
        {
            if (IsDisposed) { return; }
            try
            {
                BeginInvoke((Action)delegate { _statusLabel.Text = status; });
            }
            catch (InvalidOperationException) { }
        }

        // ------------------------------------------------------------------
        // Connect & refresh
        // ------------------------------------------------------------------

        private async void OnRefreshClick(object sender, EventArgs e)
        {
            if (_busy) { return; }
            SetBusy(true, "Connecting to Sage...");
            try
            {
                List<SageInvoiceSummary> rows = await Task.Run(delegate
                {
                    EnsureConnected();
                    return _sage.ListOpenInvoices();
                });

                _grid.Rows.Clear();
                foreach (SageInvoiceSummary row in rows)
                {
                    int i = _grid.Rows.Add(
                        row.ReferenceNumber,
                        row.Date.ToShortDateString(),
                        (row.CustomerId ?? "") + (string.IsNullOrEmpty(row.CustomerName) ? "" : " — " + row.CustomerName),
                        row.AmountDue.ToString("C2"),
                        null,
                        "");
                    _grid.Rows[i].Tag = row;
                }
                _statusLabel.Text = "Connected to \"" + _sage.CompanyName + "\" — " + rows.Count + " open invoice(s).";
            }
            catch (Exception ex)
            {
                Logger.Error("App", "Connect/refresh failed", ex);
                _statusLabel.Text = "Connect failed.";
                MessageBox.Show(this, ex.Message, "Sage connection", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                SetBusy(false, null);
            }
        }

        /// <summary>Runs on a worker thread; connects the Sage gateway if needed.</summary>
        private void EnsureConnected()
        {
            if (_sage.IsConnected) { return; }
            _sage.Connect(_secrets.SageApplicationId, _config.SageCompanyName, ReportStatus);
        }

        // ------------------------------------------------------------------
        // Send to Stripe (per row)
        // ------------------------------------------------------------------

        private async void OnGridCellClick(object sender, DataGridViewCellEventArgs e)
        {
            if (_busy || e.RowIndex < 0) { return; }
            if (_grid.Columns[e.ColumnIndex].Name != "Send") { return; }

            SageInvoiceSummary row = _grid.Rows[e.RowIndex].Tag as SageInvoiceSummary;
            if (row == null) { return; }

            string modeWarning = _config.IsLiveMode
                ? "\n\nThis is LIVE mode — a real Stripe invoice will be created."
                : "";
            DialogResult confirm = MessageBox.Show(this,
                "Send Sage invoice " + row.ReferenceNumber + " (" + row.AmountDue.ToString("C2")
                + ", " + row.CustomerName + ") to Stripe?" + modeWarning,
                "Send to Stripe", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (confirm != DialogResult.Yes) { return; }

            DataGridViewRow gridRow = _grid.Rows[e.RowIndex];
            SetBusy(true, "Sending invoice " + row.ReferenceNumber + " to Stripe...");
            try
            {
                SyncEngine engine = new SyncEngine(_config, _secrets, _sage);
                SendResult result = await Task.Run(delegate
                {
                    EnsureConnected();
                    return engine.SendInvoiceToStripe(row.ReferenceNumber);
                });

                if (result.Success)
                {
                    gridRow.Cells["Status"].Value = "Sent: " + result.StripeInvoiceId;
                    _statusLabel.Text = "Invoice " + row.ReferenceNumber + " sent to Stripe.";
                }
                else if (result.DuplicateWarning)
                {
                    gridRow.Cells["Status"].Value = "Already in Stripe: " + result.StripeInvoiceId;
                    MessageBox.Show(this, result.Message, "Already sent", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                else
                {
                    gridRow.Cells["Status"].Value = "FAILED — see log";
                    MessageBox.Show(this, result.Message, "Send failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("App", "Send failed for invoice " + row.ReferenceNumber, ex);
                gridRow.Cells["Status"].Value = "FAILED — see log";
                MessageBox.Show(this, ex.Message, "Send failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                SetBusy(false, null);
            }
        }

        // ------------------------------------------------------------------
        // Check for paid invoices (global)
        // ------------------------------------------------------------------

        private async void OnCheckPaidClick(object sender, EventArgs e)
        {
            if (_busy) { return; }
            SetBusy(true, "Checking Stripe for paid invoices...");
            try
            {
                SyncEngine engine = new SyncEngine(_config, _secrets, _sage);
                List<ReconcileResult> results = await Task.Run(delegate
                {
                    EnsureConnected();
                    return engine.CheckForPaidInvoices();
                });

                // Per-invoice summary (TRD 6.2 step 7).
                if (results.Count == 0)
                {
                    _statusLabel.Text = "No new paid Stripe invoices to record.";
                    MessageBox.Show(this, "No new paid Stripe invoices were found.", "Check for Paid Invoices",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else
                {
                    List<string> linesOut = new List<string>();
                    bool anyProblem = false;
                    foreach (ReconcileResult r in results)
                    {
                        string prefix;
                        switch (r.Outcome)
                        {
                            case ReconcileOutcome.Recorded: prefix = "RECORDED"; break;
                            case ReconcileOutcome.AlreadyClosed: prefix = "ALREADY CLOSED"; break;
                            case ReconcileOutcome.RecordedFeeManual: prefix = "RECORDED (FEE NEEDS MANUAL ENTRY)"; anyProblem = true; break;
                            case ReconcileOutcome.SkippedNotSettled: prefix = "SKIPPED (settling)"; break;
                            case ReconcileOutcome.SkippedNoMatch: prefix = "SKIPPED (no match)"; anyProblem = true; break;
                            default: prefix = "FAILED"; anyProblem = true; break;
                        }
                        linesOut.Add(prefix + " — " + (r.SageInvoiceRef ?? r.StripeInvoiceId) + ": " + r.Message);
                    }
                    _statusLabel.Text = "Paid-invoice check finished: " + results.Count + " invoice(s) processed.";
                    MessageBox.Show(this, string.Join(Environment.NewLine + Environment.NewLine, linesOut),
                        "Check for Paid Invoices — summary", MessageBoxButtons.OK,
                        anyProblem ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("App", "Check for paid invoices failed", ex);
                _statusLabel.Text = "Paid-invoice check failed.";
                MessageBox.Show(this, ex.Message, "Check for Paid Invoices", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                SetBusy(false, null);
            }
        }

        // ------------------------------------------------------------------
        // Settings
        // ------------------------------------------------------------------

        private void OnSettingsClick(object sender, EventArgs e)
        {
            using (SettingsForm form = new SettingsForm(_config, _secrets))
            {
                if (form.ShowDialog(this) == DialogResult.OK)
                {
                    _config = form.Config;
                    _secrets = form.Secrets;
                    UpdateModeLabel();
                    // Company or app id may have changed; force a clean reconnect.
                    _sage.Dispose();
                    _sage = new SageGateway();
                    _statusLabel.Text = "Settings saved. Not connected to Sage.";
                }
            }
        }
    }
}
