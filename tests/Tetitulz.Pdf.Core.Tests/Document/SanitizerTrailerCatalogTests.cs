// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Document;

/// <summary>
/// The sanitizer cleans the catalog a reader uses, however the trailer names it: an inline
/// <c>/Root</c> (which pdf.js and MuPDF take), a reference to a reference, or an ordinary reference.
/// Handed only the last, it skipped every catalog-level removal for the other two and copied an
/// inline catalog through whole: its open-action script, its document scripts and its attachment.
/// </summary>
public sealed class SanitizerTrailerCatalogTests
{
    private const string Script = "app.alert(1)";

    /// <summary>The script as the output is searched for: the writer escapes its parentheses, so a surviving script reads app.alert\(1\).</summary>
    private const string ScriptMarker = "app.alert";
    private const string Payload = "MZPAYLOAD";
    private const string XfaScript = "xfa.host.messageBox";

    private const string CatalogBody =
        "<< /Type /Catalog /Pages 1 0 R " +
        "/OpenAction << /S /JavaScript /JS (" + Script + ") >> " +
        "/Names << /JavaScript << /Names [(x) << /S /JavaScript /JS (" + Script + ") >>] >> " +
        "/EmbeddedFiles << /Names [(a.exe) << /Type /Filespec /F (a.exe) /EF << /F 3 0 R >> >>] >> >> " +
        "/AcroForm << /Fields [] /XFA 4 0 R >> /URI << /Base (file://server/share/) >> >>";

    [Fact]
    public void Sanitize_AnInlineTrailerRoot_RemovesTheScriptsTheAttachmentTheXfaAndTheFileBase()
    {
        byte[] output = Sanitize(Build(rootInTrailer: CatalogBody, extra: Array.Empty<(int, string)>()));

        string text = Latin1(output);
        Assert.DoesNotContain(ScriptMarker, text, StringComparison.Ordinal);
        Assert.DoesNotContain(Payload, text, StringComparison.Ordinal);
        Assert.DoesNotContain(XfaScript, text, StringComparison.Ordinal);
        Assert.DoesNotContain("file://server", text, StringComparison.Ordinal);
        // Potency: the output is a document whose catalog is an object of its own and still has its pages.
        using var doc = PdfDocument.Open(new MemoryStream(output));
        Assert.IsType<PdfReference>(doc.Trailer[PdfName.Root]);
        Assert.Single(doc.Pages);
        Assert.Null(doc.Catalog.Dictionary[PdfName.OpenAction]);
    }

    [Fact]
    public void Sanitize_ARootThatIsAReferenceToAReference_CleansTheCatalogItLeadsTo()
    {
        byte[] output = Sanitize(Build(rootInTrailer: "6 0 R", extra: new[] { (6, "7 0 R"), (7, CatalogBody) }));

        string text = Latin1(output);
        Assert.DoesNotContain(ScriptMarker, text, StringComparison.Ordinal);
        Assert.DoesNotContain(Payload, text, StringComparison.Ordinal);
        Assert.DoesNotContain(XfaScript, text, StringComparison.Ordinal);
        Assert.DoesNotContain("file://server", text, StringComparison.Ordinal);
        using var doc = PdfDocument.Open(new MemoryStream(output));
        Assert.Single(doc.Pages);
    }

    [Fact]
    public void Sanitize_AnOrdinaryRoot_CleansTheSameCatalog()
    {
        // The control: the shape the sanitizer always handled.
        byte[] input = Build(rootInTrailer: "7 0 R", extra: new[] { (7, CatalogBody) });
        Assert.Contains(ScriptMarker, Latin1(input), StringComparison.Ordinal);

        byte[] output = Sanitize(input);

        string text = Latin1(output);
        Assert.DoesNotContain(ScriptMarker, text, StringComparison.Ordinal);
        Assert.DoesNotContain(Payload, text, StringComparison.Ordinal);
        Assert.DoesNotContain(XfaScript, text, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ fixtures

    private static byte[] Sanitize(byte[] input)
    {
        using var output = new MemoryStream();
        PdfSanitizer.Sanitize(new MemoryStream(input), output);
        return output.ToArray();
    }

    /// <summary>
    /// Objects 1 (pages), 2 (a page), 3 (an embedded file with no /Type) and 4 (an XFA packet), plus
    /// <paramref name="extra"/>; the trailer's /Root is <paramref name="rootInTrailer"/> as written.
    /// </summary>
    private static byte[] Build(string rootInTrailer, (int Number, string Body)[] extra)
    {
        var objects = new List<(int Number, string Body)>
        {
            (1, "<< /Type /Pages /Kids [2 0 R] /Count 1 >>"),
            (2, "<< /Type /Page /Parent 1 0 R /MediaBox [0 0 100 100] >>"),
            (3, "<< /Length " + Payload.Length + " >>\nstream\n" + Payload + "\nendstream"),
            (4, Stream("<xdp:xdp xmlns:xdp=\"http://ns.adobe.com/xdp/\"><template><script>" + XfaScript + "('x')</script></template></xdp:xdp>")),
        };
        objects.AddRange(extra);

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new Dictionary<int, int>();
        foreach (var (number, body) in objects.OrderBy(o => o.Number))
        {
            offsets[number] = sb.Length;
            sb.Append(number).Append(" 0 obj\n").Append(body).Append("\nendobj\n");
        }
        int max = objects.Max(o => o.Number);
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(max + 1).Append('\n').Append("0000000000 65535 f \n");
        for (int n = 1; n <= max; n++)
            sb.Append(offsets.TryGetValue(n, out int at) ? at.ToString("D10") + " 00000 n \n" : "0000000000 00000 f \n");
        sb.Append("trailer\n<< /Size ").Append(max + 1).Append(" /Root ").Append(rootInTrailer)
          .Append(" >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static string Stream(string content) => "<< /Length " + content.Length + " >>\nstream\n" + content + "\nendstream";

    private static string Latin1(byte[] bytes) => Encoding.Latin1.GetString(bytes);
}
