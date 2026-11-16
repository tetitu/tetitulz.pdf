// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Drawing;
using System.Windows.Forms;

namespace Tetitulz.Pdf.Splitter
{
    /// <summary>Asks for a user password. Returns <c>null</c> when the user cancels.</summary>
    internal sealed class PasswordForm : Form
    {
        private readonly TextBox _box;

        private PasswordForm(string fileName, string retryReason)
        {
            Text = "Password required";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(380, 130);

            var prompt = new Label
            {
                AutoSize = false,
                Location = new Point(12, 12),
                Size = new Size(356, 34),
                Text = string.IsNullOrEmpty(retryReason)
                    ? fileName + " is encrypted. Enter the password to open it."
                    : retryReason,
            };

            _box = new TextBox
            {
                Location = new Point(12, 52),
                Size = new Size(356, 23),
                UseSystemPasswordChar = true,
            };

            var ok = new Button
            {
                Text = "OK",
                DialogResult = DialogResult.OK,
                Location = new Point(212, 88),
                Size = new Size(75, 26),
            };

            var cancel = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Location = new Point(293, 88),
                Size = new Size(75, 26),
            };

            Controls.Add(prompt);
            Controls.Add(_box);
            Controls.Add(ok);
            Controls.Add(cancel);

            AcceptButton = ok;
            CancelButton = cancel;
        }

        /// <summary>
        /// The empty password is a real answer, so <c>null</c> — not <c>""</c> — means "cancelled".
        /// </summary>
        public static string Ask(IWin32Window owner, string fileName, string retryReason = null)
        {
            using (var dialog = new PasswordForm(fileName, retryReason))
                return dialog.ShowDialog(owner) == DialogResult.OK ? dialog._box.Text : null;
        }
    }
}
