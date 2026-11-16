// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Windows.Forms;

namespace Tetitulz.Pdf.Splitter
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            // Referencing Tetitulz.Pdf.Encryption is not enough: this call installs the decryptor.
            // Without it the renderer gets the still-encrypted bytes, the page count is 0, and the
            // password prompt refuses the correct password, since nothing consults it.
            Tetitulz.Pdf.Encryption.PdfEncryptionOperations.Register();

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm(args != null && args.Length > 0 ? args[0] : null));
        }
    }
}
