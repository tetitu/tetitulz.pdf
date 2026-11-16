// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Windows.Forms;

namespace Tetitulz.Pdf.StampSign
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            // Without this the viewer cannot decrypt, and an encrypted document looks like a wrong password.
            Tetitulz.Pdf.Encryption.PdfEncryptionOperations.Register();

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => Ui.ShowError(null, "Something went wrong.", e.Exception);

            var form = new MainForm();
            if (args.Length > 0) form.OpenOnStart = args[0];   // "Open with…" or a file dropped on the exe
            Application.Run(form);
        }
    }
}
