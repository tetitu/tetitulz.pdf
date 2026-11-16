// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Documents;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <see cref="PdfIncrementalSaveValidator"/> reports a signature whose byte range ends at <c>%%EOF</c>,
/// with one end-of-line marker after it, as covering the whole file.
/// </summary>
/// <remarks>
/// ISO 32000-2 §12.8.1 has the range end at "the end of the "%%EOF" comment, possibly followed by an
/// optional EOL marker". The report compared the range's end with the file's length and so said
/// <c>CoversWholeFile = false</c> for a signature that leaves the marker out.
/// </remarks>
public sealed class IncrementalSaveEndOfFileMarkerTests
{
    private static byte[] SignedEndingAtEof(string afterEof)
    {
        const string rangePlaceholder = "[0 0000000000 0000000000 0000000000]";
        string contents = "<" + new string('0', 64) + ">";

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        void Obj(string body) { offsets.Add(sb.Length); sb.Append(offsets.Count).Append(" 0 obj\n").Append(body).Append("\nendobj\n"); }

        Obj("<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] /SigFlags 3 >> >>");
        Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [4 0 R] >>");
        Obj("<< /FT /Sig /T (Signature1) /Type /Annot /Subtype /Widget /Rect [0 0 0 0] /P 3 0 R /V 5 0 R >>");
        Obj($"<< /Type /Sig /Filter /Adobe.PPKLite /SubFilter /adbe.pkcs7.detached /ByteRange {rangePlaceholder} /Contents {contents} >>");

        int xref = sb.Length;
        sb.AppendFormat("xref\n0 {0}\n0000000000 65535 f \n", offsets.Count + 1);
        foreach (int offset in offsets) sb.AppendFormat("{0:D10} 00000 n \n", offset);
        sb.AppendFormat("trailer\n<< /Size {0} /Root 1 0 R >>\nstartxref\n{1}\n%%EOF", offsets.Count + 1, xref);

        string text = sb.ToString();
        int contentsStart = text.IndexOf("/Contents <", StringComparison.Ordinal) + "/Contents ".Length;
        int contentsEnd = contentsStart + contents.Length;
        string range = string.Format("[0 {0:D10} {1:D10} {2:D10}]", contentsStart, contentsEnd, text.Length - contentsEnd);
        return Encoding.ASCII.GetBytes(text.Replace(rangePlaceholder, range) + afterEof);
    }

    [Theory]
    [InlineData("\n", true)]
    [InlineData("\r\n", true)]
    [InlineData("\n\n", false)]
    public void ASignatureEndingAtEof_CoversTheWholeFileOnlyBeforeOneEndOfLine(string afterEof, bool covers)
    {
        var info = PdfIncrementalSaveValidator.Inspect(new MemoryStream(SignedEndingAtEof(afterEof)));

        Assert.Equal(covers, info.SignatureReports.Single().CoversWholeFile);
    }
}
