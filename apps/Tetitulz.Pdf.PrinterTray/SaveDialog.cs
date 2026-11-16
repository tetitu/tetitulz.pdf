// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Printing;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using Tetitulz.Pdf.Plugins.CertStoreSign;

namespace Tetitulz.Pdf.PrinterTray;

/// <summary>
/// The window each print opens: the PDF's name and folder, filled in from the document and the last
/// save; open it after saving; and the three things the library can do to it — PDF/A-2b for archiving,
/// a password, a signature with a certificate from the user's own store. PDF/A and a password exclude
/// each other (ISO 19005 forbids encryption), so choosing one turns the other off. On Save it checks
/// what it was given and answers <see cref="Request"/>; on Cancel, nothing is saved.
/// </summary>
internal sealed class SaveDialog : Form
{
    private readonly TraySettings _settings;
    private readonly TextBox _name = new() { Dock = DockStyle.Fill, AccessibleName = "File name" };
    private readonly TextBox _folder = new() { Dock = DockStyle.Fill, AccessibleName = "Folder" };
    private readonly CheckBox _open = new() { Text = "Open the PDF after saving", AutoSize = true };
    private readonly CheckBox _pdfa = new() { Text = "PDF/A-2b — for long-term archiving", AutoSize = true };
    private readonly CheckBox _protect = new() { Text = "Protect with a password", AutoSize = true };
    private readonly TextBox _password = new() { UseSystemPasswordChar = true, Dock = DockStyle.Fill, PlaceholderText = "Password", AccessibleName = "Password" };
    private readonly TextBox _confirm = new() { UseSystemPasswordChar = true, Dock = DockStyle.Fill, PlaceholderText = "Type it again", AccessibleName = "Confirm password" };
    private readonly CheckBox _sign = new() { Text = "Sign with", AutoSize = true };
    private readonly ComboBox _certificates = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, AccessibleName = "Certificate" };
    private readonly TextBox _reason = new() { Dock = DockStyle.Fill, PlaceholderText = "Reason (optional)", AccessibleName = "Reason" };
    private readonly TextBox _timestamp = new() { Dock = DockStyle.Fill, PlaceholderText = "Timestamp authority URL (optional)", AccessibleName = "Timestamp authority" };
    private readonly Label _note = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    private readonly List<(string Thumbprint, string Label)> _certificateList = new();

    /// <summary>What to save, once the dialog closed with Save.</summary>
    public SaveRequest? Request { get; private set; }

    public SaveDialog(string documentName, int pages, TraySettings settings)
    {
        _settings = settings;
        Text = "Save the print as PDF — Tetitulz PDF";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        ShowInTaskbar = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);
        Font = SystemFonts.MessageBoxFont ?? Font;

        var layout = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, Dock = DockStyle.Fill };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 340));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var heading = new Label { Text = documentName, AutoSize = true, Font = new Font(Font, FontStyle.Bold), MaximumSize = new Size(480, 0) };
        layout.Controls.Add(heading, 0, 0);
        layout.SetColumnSpan(heading, 3);
        var pageCount = new Label { Text = pages == 1 ? "1 page" : $"{pages} pages", AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 0, 3, 10) };
        layout.Controls.Add(pageCount, 0, 1);
        layout.SetColumnSpan(pageCount, 3);

        layout.Controls.Add(Caption("File name"), 0, 2);
        layout.Controls.Add(_name, 1, 2);
        layout.Controls.Add(new Label { Text = ".pdf", AutoSize = true, Anchor = AnchorStyles.Left }, 2, 2);

        layout.Controls.Add(Caption("Folder"), 0, 3);
        layout.Controls.Add(_folder, 1, 3);
        var browse = new Button { Text = "Browse…", AutoSize = true };
        browse.Click += (_, _) => Browse();
        layout.Controls.Add(browse, 2, 3);

        layout.Controls.Add(_open, 1, 4);

        var separator = Separator();
        layout.Controls.Add(separator, 0, 5);
        layout.SetColumnSpan(separator, 3);

        layout.Controls.Add(_pdfa, 1, 6);
        layout.Controls.Add(_protect, 1, 7);
        layout.Controls.Add(_password, 1, 8);
        layout.Controls.Add(_confirm, 1, 9);
        layout.Controls.Add(_sign, 1, 10);
        layout.Controls.Add(_certificates, 1, 11);
        layout.Controls.Add(_reason, 1, 12);
        layout.Controls.Add(_timestamp, 1, 13);
        layout.Controls.Add(_note, 1, 14);

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 12, 0, 0) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        var save = new Button { Text = "Save", AutoSize = true };
        save.Click += (_, _) => TrySave();
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(save);
        layout.Controls.Add(buttons, 0, 15);
        layout.SetColumnSpan(buttons, 3);

        Controls.Add(layout);
        AcceptButton = save;
        CancelButton = cancel;

        _name.Text = DocumentNames.Suggest(documentName);
        _folder.Text = settings.Folder;
        _open.Checked = settings.OpenAfterSaving;
        _pdfa.Checked = settings.PdfA;
        _reason.Text = settings.Reason ?? "";
        _timestamp.Text = settings.TimestampUrl ?? "";
        LoadCertificates(settings);

        _pdfa.CheckedChanged += (_, _) => { if (_pdfa.Checked) _protect.Checked = false; UpdateOptions(); };
        _protect.CheckedChanged += (_, _) => { if (_protect.Checked) _pdfa.Checked = false; UpdateOptions(); };
        _sign.CheckedChanged += (_, _) => UpdateOptions();
        UpdateOptions();
        Shown += (_, _) => { Activate(); _name.Focus(); _name.SelectAll(); };
    }

    private static Label Caption(string text) => new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left };

    private static Label Separator() => new() { BorderStyle = BorderStyle.Fixed3D, Height = 2, Dock = DockStyle.Fill, Margin = new Padding(3, 8, 3, 8) };

    private void LoadCertificates(TraySettings settings)
    {
        X509Certificate2Collection found = CertStoreLookup.FindUsableSigningCertificates(StoreLocation.CurrentUser, DateTime.UtcNow);
        foreach (X509Certificate2 certificate in found)
        {
            using (certificate)
            {
                string subject = certificate.GetNameInfo(X509NameType.SimpleName, false);
                string issuer = certificate.GetNameInfo(X509NameType.SimpleName, true);
                _certificateList.Add((certificate.Thumbprint, $"{subject} — issued by {issuer}, until {certificate.NotAfter:d}"));
            }
        }
        foreach (var (_, label) in _certificateList) _certificates.Items.Add(label);
        if (_certificateList.Count == 0)
        {
            _sign.Text = "Sign (no certificate with a private key in your certificate store)";
            _sign.Enabled = false;
            return;
        }
        int remembered = _certificateList.FindIndex(c => string.Equals(c.Thumbprint, settings.CertificateThumbprint, StringComparison.OrdinalIgnoreCase));
        _certificates.SelectedIndex = Math.Max(0, remembered);
        _sign.Checked = settings.Sign && remembered >= 0;
    }

    private void UpdateOptions()
    {
        _password.Enabled = _confirm.Enabled = _protect.Checked;
        _certificates.Enabled = _reason.Enabled = _timestamp.Enabled = _sign.Checked;
        _note.Text = _pdfa.Checked || _protect.Checked ? "PDF/A does not allow a password (ISO 19005), so only one of the two." : "";
    }

    private void Browse()
    {
        using var dialog = new FolderBrowserDialog { SelectedPath = _folder.Text, UseDescriptionForTitle = true, Description = "Where to save the PDF" };
        if (dialog.ShowDialog(this) == DialogResult.OK) _folder.Text = dialog.SelectedPath;
    }

    private void TrySave()
    {
        string name = _name.Text.Trim();
        if (name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) name = name[..^4].TrimEnd();
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            Refuse(_name, "Give the PDF a file name without \\ / : * ? \" < > |.");
            return;
        }
        string folder = _folder.Text.Trim();
        if (folder.Length == 0 || !Path.IsPathFullyQualified(folder))
        {
            Refuse(_folder, "Choose a folder — a full path such as C:\\Users\\you\\Documents.");
            return;
        }
        if (_protect.Checked && _password.Text.Length == 0)
        {
            Refuse(_password, "Type the password, or clear 'Protect with a password'.");
            return;
        }
        if (_protect.Checked && _password.Text != _confirm.Text)
        {
            Refuse(_confirm, "The two passwords are not the same.");
            return;
        }
        if (_sign.Checked && _certificates.SelectedIndex < 0)
        {
            Refuse(_certificates, "Choose the certificate to sign with.");
            return;
        }

        string timestamp = _timestamp.Text.Trim();
        if (_sign.Checked && timestamp.Length > 0 &&
            !(Uri.TryCreate(timestamp, UriKind.Absolute, out Uri? tsa) && (tsa.Scheme == Uri.UriSchemeHttp || tsa.Scheme == Uri.UriSchemeHttps)))
        {
            Refuse(_timestamp, "The timestamp authority is a web address starting with http:// or https://, or empty.");
            return;
        }

        string destination = Path.Combine(folder, name + ".pdf");
        if (File.Exists(destination) &&
            MessageBox.Show(this, $"{name}.pdf is already in that folder. Replace it?", "Tetitulz PDF",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;

        string? thumbprint = _sign.Checked ? _certificateList[_certificates.SelectedIndex].Thumbprint : null;
        Request = new SaveRequest(destination, name, _open.Checked, _pdfa.Checked,
            _protect.Checked ? _password.Text : null, thumbprint, _sign.Checked ? _reason.Text.Trim() : null,
            _sign.Checked && timestamp.Length > 0 ? timestamp : null);

        _settings.Folder = folder;
        _settings.OpenAfterSaving = _open.Checked;
        _settings.PdfA = _pdfa.Checked;
        _settings.Sign = _sign.Checked;
        if (thumbprint != null) _settings.CertificateThumbprint = thumbprint;
        _settings.Reason = _reason.Text.Trim();
        _settings.TimestampUrl = timestamp;
        _settings.Save();

        DialogResult = DialogResult.OK;
        Close();
    }

    private void Refuse(Control control, string message)
    {
        MessageBox.Show(this, message, "Tetitulz PDF", MessageBoxButtons.OK, MessageBoxIcon.Information);
        control.Focus();
    }
}
