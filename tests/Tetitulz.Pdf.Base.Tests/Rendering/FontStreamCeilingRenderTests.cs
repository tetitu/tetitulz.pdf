// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.IO.Filters;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// The renderers' font cache reads a font's <c>/ToUnicode</c> under
/// <see cref="PdfLimits.MaxCMapStreamBytes"/>: past it the font still loads, without the CMap, and
/// <see cref="RenderDiagnosticKind.FontToUnicodeUnread"/> names the font and the reason.
/// </summary>
[Collection(TestInfrastructure.PdfLimitsCollection.Name)]
public sealed class FontStreamCeilingRenderTests
{
    [Fact]
    public void AdversarialToUnicodeBomb_LoadsTheFont_AndReportsItsCMapUnread()
    {
        byte[] cmap = Encoding.ASCII.GetBytes("%" + new string(' ', 4000) + "\nbeginbfchar\n<41> <05D0>\nendbfchar\n");
        byte[] pdf = Document(FlateEncodeFilter.Encode(cmap));

        var reported = new List<string>();
        void OnEvent(RenderDiagnosticKind kind, string detail, Exception ex)
        {
            if (kind == RenderDiagnosticKind.FontToUnicodeUnread && detail.StartsWith("FCeiling:", StringComparison.Ordinal))
                lock (reported) reported.Add(detail);
        }

        RenderDiagnostics.OnEvent += OnEvent;
        try
        {
            // The control: under the ceiling the CMap is read, and nothing is said.
            Assert.Equal("א", TextOfCode41(pdf));
            Assert.Empty(reported);

            PdfLimits.MaxCMapStreamBytes = 1024;
            Assert.Equal("A", TextOfCode41(pdf));
            Assert.Contains("CMap stream was not decoded", Assert.Single(reported));
        }
        finally
        {
            RenderDiagnostics.OnEvent -= OnEvent;
            PdfLimits.ResetToDefaults();
        }
    }

    private static string TextOfCode41(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        using var cache = new FontCache();
        return cache.GetPdfFont("FCeiling", doc.Pages[0].Resources, doc).Decode(new byte[] { 0x41 });
    }

    private static byte[] Document(byte[] toUnicode)
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 612 792] >>",
            "<< /Type /Page /Parent 2 0 R /Resources << /Font << /FCeiling 4 0 R >> >> >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /ToUnicode 5 0 R >>",
        };
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        foreach (string body in bodies)
        {
            offsets.Add(sb.Length);
            sb.Append(offsets.Count).Append(" 0 obj\n").Append(body).Append("\nendobj\n");
        }
        offsets.Add(sb.Length);
        sb.Append(offsets.Count).Append(" 0 obj\n<< /Length ").Append(toUnicode.Length).Append(" /Filter /FlateDecode >>\nstream\n");
        var head = Encoding.ASCII.GetBytes(sb.ToString());
        var tail = new StringBuilder("\nendstream\nendobj\n");
        int xref = head.Length + toUnicode.Length + tail.Length;
        tail.Append("xref\n0 ").Append(offsets.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) tail.Append(o.ToString("D10")).Append(" 00000 n \n");
        tail.Append("trailer\n<< /Size ").Append(offsets.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        var ms = new MemoryStream();
        ms.Write(head, 0, head.Length);
        ms.Write(toUnicode, 0, toUnicode.Length);
        byte[] t = Encoding.ASCII.GetBytes(tail.ToString());
        ms.Write(t, 0, t.Length);
        return ms.ToArray();
    }
}
