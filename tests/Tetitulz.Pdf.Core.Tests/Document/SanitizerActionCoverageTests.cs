// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Document;

/// <summary>
/// The sanitizer reaches dangerous content in the places the old key-by-key walk did not — a
/// <c>/Next</c> written as an array, an inline <c>/OpenAction</c> the caller kept, a link
/// annotation's <c>/PA</c> — and removes the action kinds no option used to cover: remote
/// <c>/GoToR</c>/<c>/GoToE</c>/<c>/ImportData</c>, a non-web <c>/URI</c>, and <c>/3D</c> annotations.
/// </summary>
public class SanitizerActionCoverageTests
{
    [Fact]
    public void AJavaScriptActionInsideANextArrayIsNeutralized()
    {
        const string marker = "PWNED_NEXT_ARRAY";
        byte[] pdf = Page("/AA << /O << /S /GoTo /D [3 0 R /Fit] /Next [ << /S /JavaScript /JS (" +
                          marker + ") >> ] >> >>");
        Assert.True(Contains(pdf, marker));

        byte[] cleaned = Sanitize(pdf, new SanitizeOptions());

        Assert.False(Contains(cleaned, marker), "a JavaScript action inside a /Next array survived");
    }

    [Fact]
    public void AnInlineOpenActionIsCleanedEvenWhenTheOpenActionKeyIsKept()
    {
        const string marker = "PWNED_INLINE_OPENACTION";
        byte[] pdf = Catalog("/OpenAction << /S /JavaScript /JS (" + marker + ") >>");
        Assert.True(Contains(pdf, marker));

        // RemoveOpenAction off: the key stays, but its JavaScript must not.
        byte[] cleaned = Sanitize(pdf, new SanitizeOptions { RemoveOpenAction = false });

        Assert.False(Contains(cleaned, marker),
            "an inline JavaScript /OpenAction survived when RemoveOpenAction was off");
    }

    [Fact]
    public void ALinkAnnotationsPrimaryActionIsCleaned()
    {
        const string marker = "PWNED_LINK_PA";
        byte[] pdf = Page("/Annots [ << /Type /Annot /Subtype /Link /Rect [0 0 50 50] " +
                          "/PA << /S /JavaScript /JS (" + marker + ") >> >> ]");
        Assert.True(Contains(pdf, marker));

        byte[] cleaned = Sanitize(pdf, new SanitizeOptions());

        Assert.False(Contains(cleaned, marker), "a link annotation's /PA JavaScript survived");
    }

    [Theory]
    [InlineData("/S /GoToR /F (\\\\\\\\attacker.example\\\\share\\\\x.pdf) /D [0 /Fit]", "GoToR")]
    [InlineData("/S /GoToE /T << /R /C /N (x) >> /F (evil.pdf)", "GoToE")]
    [InlineData("/S /ImportData /F (\\\\\\\\attacker.example\\\\share\\\\x.fdf)", "ImportData")]
    public void RemoteActionsAreRemovedByDefault(string action, string label)
    {
        byte[] pdf = Page("/AA << /O << " + action + " >> >>");

        byte[] cleaned = Sanitize(pdf, new SanitizeOptions());
        Assert.False(ContainsName(cleaned, label), label + " survived the default sanitize");

        // Acceptance control: with the option off, it is kept.
        byte[] kept = Sanitize(pdf, new SanitizeOptions { RemoveRemoteGoToActions = false });
        Assert.True(ContainsName(kept, label), label + " was removed even with RemoveRemoteGoToActions off");
    }

    [Fact]
    public void AnInDocumentGoToIsNeverRemoved()
    {
        byte[] pdf = Page("/AA << /O << /S /GoTo /D [3 0 R /Fit] >> >>");
        byte[] cleaned = Sanitize(pdf, new SanitizeOptions());
        Assert.True(ContainsName(cleaned, "GoTo"), "an in-document /GoTo was removed, but it is navigation");
    }

    [Fact]
    public void ANonWebUriIsDroppedWhileAWebLinkSurvives()
    {
        byte[] fileUri = Page("/Annots [ << /Subtype /Link /Rect [0 0 1 1] " +
                              "/A << /S /URI /URI (file://attacker.example/share/x) >> >> ]");
        byte[] httpUri = Page("/Annots [ << /Subtype /Link /Rect [0 0 1 1] " +
                              "/A << /S /URI /URI (https://example.com/ok) >> >> ]");

        // RemoveURIActions is off by default; RemoveNonWebUriActions is on.
        byte[] cleanedFile = Sanitize(fileUri, new SanitizeOptions());
        byte[] cleanedHttp = Sanitize(httpUri, new SanitizeOptions());

        Assert.False(Contains(cleanedFile, "attacker.example"), "a file:// URI action survived");
        Assert.True(Contains(cleanedHttp, "example.com"), "an ordinary https link was removed");
    }

    [Fact]
    public void A3DAnnotationIsRemovedByDefault()
    {
        const string marker = "PWNED_3D_ONINSTANTIATE";
        byte[] pdf = ThreeDPdf(marker);
        Assert.True(Contains(pdf, marker));

        byte[] cleaned = Sanitize(pdf, new SanitizeOptions());
        Assert.False(Contains(cleaned, marker), "a /3D annotation's OnInstantiate script survived");

        // Acceptance control.
        byte[] kept = Sanitize(pdf, new SanitizeOptions { Remove3DAnnotations = false });
        Assert.True(Contains(kept, marker), "the /3D annotation was removed even with Remove3DAnnotations off");
    }

    [Fact]
    public void RemoveAllAdditionalActionsDropsABenignTrigger()
    {
        // An /AA holding only a benign in-document /GoTo.
        byte[] pdf = Page("/AA << /O << /S /GoTo /D [3 0 R /Fit] >> >>");

        byte[] kept = Sanitize(pdf, new SanitizeOptions());
        Assert.True(ContainsName(kept, "GoTo"), "the default sanitize should keep a benign /AA /GoTo");

        byte[] stripped = Sanitize(pdf, new SanitizeOptions { RemoveAllAdditionalActions = true });
        Assert.False(ContainsName(stripped, "GoTo"),
            "RemoveAllAdditionalActions did not drop a benign /AA entry");
    }

    // ================================================================= helpers

    private static byte[] Sanitize(byte[] pdf, SanitizeOptions options)
    {
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfSanitizer.Sanitize(input, output, options);
        return output.ToArray();
    }

    private static string Latin1(byte[] b) => Encoding.GetEncoding("iso-8859-1").GetString(b);
    private static bool Contains(byte[] pdf, string s) => Latin1(pdf).Contains(s, StringComparison.Ordinal);
    private static bool ContainsName(byte[] pdf, string name) => Latin1(pdf).Contains("/" + name, StringComparison.Ordinal);

    private static byte[] Page(string pageExtra) => Assemble(new[]
    {
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] " + pageExtra + " >>",
    });

    private static byte[] Catalog(string catalogExtra) => Assemble(new[]
    {
        "<< /Type /Catalog /Pages 2 0 R " + catalogExtra + " >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
    });

    private static byte[] ThreeDPdf(string marker)
    {
        string script = "host.app.alert('" + marker + "')";
        return Assemble(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [4 0 R] >>",
            "<< /Type /Annot /Subtype /3D /Rect [0 0 50 50] /3DD 5 0 R >>",
            "<< /Type /3D /Subtype /U3D /OnInstantiate 6 0 R /Length 3 >>\nstream\nU3D\nendstream",
            "<< /Length " + script.Length + " >>\nstream\n" + script + "\nendstream",
        });
    }

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
