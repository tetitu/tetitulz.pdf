// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Document;

/// <summary>
/// The cleaning walk enters an array written as its own object. A page's <c>/Annots</c> and the
/// form's <c>/Fields</c> are commonly indirect (<c>/Annots 13 0 R</c>), and every widget and every
/// field script lies beyond them; a walk that follows a reference only to a dictionary leaves each
/// field's <c>/AA</c> scripts in the sanitized file.
/// </summary>
public class SanitizerIndirectArrayTests
{
    /// <summary>
    /// A widget listed only in an indirect <c>/Annots</c> array, its keystroke script an indirect
    /// action. The script is gone; the widget, and the field name it carries, are kept.
    /// </summary>
    [Fact]
    public void AWidgetScriptBehindAnIndirectAnnotsArrayIsRemoved()
    {
        const string marker = "PWNED_INDIRECT_ANNOTS";
        byte[] pdf = Assemble(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots 4 0 R >>",
            "[5 0 R]",
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (KeptWidget) /Rect [10 10 90 30] /P 3 0 R /AA << /K 6 0 R >> >>",
            "<< /S /JavaScript /JS (app.alert\\('" + marker + "'\\)) >>",
        });
        Assert.Contains(marker, Latin1(pdf), StringComparison.Ordinal);

        string cleaned = Latin1(Sanitize(pdf));

        Assert.DoesNotContain(marker, cleaned, StringComparison.Ordinal);
        Assert.Contains("(KeptWidget)", cleaned, StringComparison.Ordinal);
    }

    /// <summary>
    /// A field reached only through an indirect <c>/AcroForm /Fields</c> array, its calculate script
    /// inline in its <c>/AA</c>. The script is gone; the field is kept.
    /// </summary>
    [Fact]
    public void AFieldScriptBehindAnIndirectFieldsArrayIsRemoved()
    {
        const string marker = "PWNED_INDIRECT_FIELDS";
        byte[] pdf = Assemble(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields 4 0 R >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
            "[5 0 R]",
            "<< /FT /Tx /T (KeptField) /AA << /C << /S /JavaScript /JS (event.value = '" + marker + "') >> >> >>",
        });
        Assert.Contains(marker, Latin1(pdf), StringComparison.Ordinal);

        string cleaned = Latin1(Sanitize(pdf));

        Assert.DoesNotContain(marker, cleaned, StringComparison.Ordinal);
        Assert.Contains("(KeptField)", cleaned, StringComparison.Ordinal);
    }

    /// <summary>
    /// An indirect array that lists itself: the walk visits it once and finishes.
    /// </summary>
    [Fact]
    public void AnIndirectArrayHoldingItself_IsWalkedOnce_HostileCycle()
    {
        const string marker = "PWNED_SELF_ARRAY";
        byte[] pdf = Assemble(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots 4 0 R >>",
            "[4 0 R 5 0 R]",
            "<< /Type /Annot /Subtype /Link /Rect [0 0 1 1] /A << /S /JavaScript /JS (" + marker + ") >> >>",
        });

        string cleaned = Latin1(Sanitize(pdf));

        Assert.DoesNotContain(marker, cleaned, StringComparison.Ordinal);
        // catalog, pages, page, the link annotation: each once.
        Assert.Equal(4, PdfSanitizer.LastDictionariesCleaned);
    }

    private static byte[] Sanitize(byte[] pdf)
    {
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfSanitizer.Sanitize(input, output, new SanitizeOptions());
        return output.ToArray();
    }

    private static string Latin1(byte[] b) => Encoding.Latin1.GetString(b);

    private static byte[] Assemble(string[] objs)
    {
        var sb = new StringBuilder("%PDF-1.7\n");
        var offs = new List<int>();
        for (int i = 0; i < objs.Length; i++)
        {
            offs.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(objs[i]).Append("\nendobj\n");
        }
        int x = sb.Length;
        sb.Append("xref\n0 ").Append(objs.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (var o in offs) sb.AppendFormat("{0:D10} 00000 n \n", o);
        sb.Append("trailer\n<< /Size ").Append(objs.Length + 1).Append(" /Root 1 0 R >>\nstartxref\n")
          .Append(x).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
