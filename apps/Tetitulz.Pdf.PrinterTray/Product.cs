// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Printing;

namespace Tetitulz.Pdf.PrinterTray;

/// <summary>Tetitulz PDF's printer, which this tray app serves: its names, folders, install and logon start.</summary>
internal static class Product
{
    internal static readonly PrinterSetup Printer = new(PrinterIdentity.TetitulzPdf);
}
