// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Parsing;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Parsing;

/// <summary>
/// <c>PdfParser.GetObject</c> reads an object from its own header when its cross-reference entry
/// misplaces it (GAPS §19h item 5b) — but only from a header inside the revision whose section
/// supplied that entry. Taking the file's last header let bytes appended after a signature, listed
/// by no section, stand in for a signed object, while the change analysis, which reads the entries'
/// offsets, saw nothing after the signature (the review of batch 131, 2026-10-15).
/// </summary>
public class HeaderRecoveryRevisionBoundTests
{
    private const string Benign = "benign-content-as-signed";
    private const string Injected = "content-appended-after-signing";

    /// <summary>
    /// Revision 1 misaims object 4's entry at object 3 and holds object 4's own header before its
    /// section; revision 2 appends a second <c>4 0 obj</c>, listed by no section, before its own
    /// section. Object 4 is read from revision 1's header.
    /// </summary>
    [Fact]
    public void AMisplacedEntry_IsReadFromAHeaderInItsOwnRevision_NotOneAppendedAfterIt()
    {
        PdfParser parser = ParserOver(Build(appendShadow: true));

        var content = Assert.IsType<PdfStream>(parser.GetObject(4));
        string text = Encoding.ASCII.GetString(content.GetDecodedData());
        Assert.Contains(Benign, text, StringComparison.Ordinal);
        Assert.DoesNotContain(Injected, text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The control, and the recovery still working: the same file without the appended object reads
    /// object 4 from its header — the entry still misplaced, so the fallback is what answers.
    /// </summary>
    [Fact]
    public void AMisplacedEntry_WithNothingAppended_IsStillReadFromItsHeader()
    {
        PdfParser parser = ParserOver(Build(appendShadow: false));

        var content = Assert.IsType<PdfStream>(parser.GetObject(4));
        Assert.Contains(Benign, Encoding.ASCII.GetString(content.GetDecodedData()), StringComparison.Ordinal);
    }

    private static PdfParser ParserOver(byte[] pdf)
    {
        var parser = new PdfParser(new MemoryStream(pdf));
        parser.ParseHeader();
        parser.ParseCrossReferences();
        return parser;
    }

    private static byte[] Build(bool appendShadow)
    {
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new Dictionary<int, int>();
        void Obj(int n, string body) { offsets[n] = sb.Length; sb.Append(n).Append(" 0 obj\n").Append(body).Append("\nendobj\n"); }
        string Stream(string text) => $"<< /Length {text.Length} >>\nstream\n{text}\nendstream";

        Obj(1, "<< /Type /Catalog /Pages 2 0 R >>");
        Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R >>");
        Obj(4, Stream(Benign));

        int xref1 = sb.Length;
        sb.Append("xref\n0 5\n0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", offsets[1]);
        sb.AppendFormat("{0:D10} 00000 n \n", offsets[2]);
        sb.AppendFormat("{0:D10} 00000 n \n", offsets[3]);
        sb.AppendFormat("{0:D10} 00000 n \n", offsets[3]);   // object 4's entry misaimed at object 3
        sb.Append("trailer\n<< /Size 5 /Root 1 0 R >>\nstartxref\n").Append(xref1).Append("\n%%EOF\n");

        // Revision 2: an object 4 no section lists, then an ordinary update naming object 5 only.
        if (appendShadow)
            sb.Append("4 0 obj\n").Append(Stream(Injected)).Append("\nendobj\n");
        Obj(5, "<< /Producer (revision two) >>");
        int xref2 = sb.Length;
        sb.Append("xref\n0 1\n0000000000 65535 f \n5 1\n");
        sb.AppendFormat("{0:D10} 00000 n \n", offsets[5]);
        sb.Append("trailer\n<< /Size 6 /Root 1 0 R /Info 5 0 R /Prev ").Append(xref1)
          .Append(" >>\nstartxref\n").Append(xref2).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
