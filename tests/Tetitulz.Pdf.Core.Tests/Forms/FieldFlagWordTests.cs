// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Forms;

/// <summary>
/// A field's <c>/Ff</c> is a flag word: its low 32 bits (<c>PdfNumber.FlagWord</c>). Read through
/// <c>IntValue</c>, an unsigned value with bit 32 set saturated to <c>int.MaxValue</c>, and every flag —
/// read-only, password, comb — read as set.
/// </summary>
public sealed class FieldFlagWordTests
{
    private static int FlagsOf(string ff)
    {
        string pdf = "%PDF-1.7\n";
        int o1 = pdf.Length; pdf += "1 0 obj\n<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [3 0 R] >> >>\nendobj\n";
        int o2 = pdf.Length; pdf += "2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n";
        int o3 = pdf.Length; pdf += $"3 0 obj\n<< /FT /Tx /T (a) /Ff {ff} >>\nendobj\n";
        int x = pdf.Length;
        pdf += "xref\n0 4\n0000000000 65535 f \n" + o1.ToString("D10") + " 00000 n \n" + o2.ToString("D10") + " 00000 n \n" +
               o3.ToString("D10") + " 00000 n \ntrailer\n<< /Size 4 /Root 1 0 R >>\nstartxref\n" + x + "\n%%EOF\n";
        using var doc = PdfDocument.Open(new MemoryStream(Encoding.ASCII.GetBytes(pdf)));
        return doc.AcroForm.Fields.Single().Flags;
    }

    [Fact]
    public void AnUnsignedFlagWordWithBit32Set_KeepsItsLow32Bits()
    {
        int flags = FlagsOf("2147483650");   // 0x80000002: Required, and reserved bit 32
        Assert.Equal(unchecked((int)0x80000002), flags);
        Assert.Equal(0, flags & 1);           // not read-only

        Assert.Equal(2, FlagsOf("2"));        // the control
    }
}
