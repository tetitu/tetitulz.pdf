// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Plugins.Templating;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Templating.Tests;

/// <summary>
/// An encrypted template must be reported as unreadable, not as a set of misspelled keys.
///
/// <para><b>The bug.</b> <c>HasAcroForm</c> answers false for an encrypted document because no
/// catalog is loaded, and the code justified swallowing that by saying the token pass reports its
/// own reason — which does not hold when the caller disabled the token pass. With
/// <c>Sources = TemplateSources.FormFields</c> alone, <c>wantFields</c> became false, nothing ran,
/// the input bytes were written back out unchanged, and <b>every</b> supplied key landed in
/// <c>UnmatchedKeys</c> — whose own documentation tells the caller to go and check the key for a
/// typo. That sends them to the wrong problem entirely: the document could not be opened.</para>
/// </summary>
public class EncryptedTemplateTests
{
    private static readonly Dictionary<string, string> Values =
        new() { ["Name"] = "Ada", ["Total"] = "42" };

    private static TemplateFillReport FillEncrypted(TemplateSources sources)
    {
        using var input = new MemoryStream(BuildPdf(encrypted: true));
        using var output = new MemoryStream();
        return PdfTemplateFiller.Fill(input, output, Values,
            new TemplateOptions { Sources = sources });
    }

    /// <summary>
    /// The exact scenario from the finding. The keys must NOT be reported as unmatched, and the
    /// report must say why nothing happened.
    /// </summary>
    [Fact]
    public void FormFieldsOnly_OnAnEncryptedTemplate_WarnsInsteadOfBlamingTheKeys()
    {
        var report = FillEncrypted(TemplateSources.FormFields);

        Assert.Empty(report.UnmatchedKeys);
        Assert.NotEmpty(report.Warnings);
        Assert.Contains(report.Warnings,
            w => w.IndexOf("encrypted", StringComparison.OrdinalIgnoreCase) >= 0);
        Assert.Contains(report.Warnings,
            w => w.IndexOf("Tetitulz.Pdf.Encryption", StringComparison.Ordinal) >= 0);
    }

    /// <summary>
    /// The token-only path reaches the same conclusion by the same route, so a caller does not get
    /// two different stories about one document.
    /// </summary>
    [Fact]
    public void PageTokensOnly_OnAnEncryptedTemplate_WarnsToo()
    {
        var report = FillEncrypted(TemplateSources.PageTokens);

        Assert.Empty(report.UnmatchedKeys);
        Assert.Contains(report.Warnings,
            w => w.IndexOf("encrypted", StringComparison.OrdinalIgnoreCase) >= 0);
    }

    /// <summary>Nothing was filled, and the report says so rather than claiming a partial run.</summary>
    [Fact]
    public void AnEncryptedTemplate_FillsNothing()
    {
        var report = FillEncrypted(TemplateSources.FormFields | TemplateSources.PageTokens);

        Assert.Empty(report.FilledFields);
        Assert.Empty(report.FilledTokens);
    }

    /// <summary>
    /// The output is still the template's own bytes — the operation fails closed rather than
    /// truncating the caller's stream.
    /// </summary>
    [Fact]
    public void AnEncryptedTemplate_WritesTheInputBackUnchanged()
    {
        byte[] template = BuildPdf(encrypted: true);
        using var input = new MemoryStream(template);
        using var output = new MemoryStream();

        PdfTemplateFiller.Fill(input, output, Values,
            new TemplateOptions { Sources = TemplateSources.FormFields });

        Assert.Equal(template, output.ToArray());
    }

    /// <summary>
    /// <b>The control.</b> The same fixture without <c>/Encrypt</c> and without an AcroForm is a
    /// genuine "your keys matched nothing" case, and must still report it that way — otherwise the
    /// fix would have replaced one wrong answer with another.
    /// </summary>
    [Fact]
    public void AnUnencryptedTemplateWithNoMatch_StillReportsUnmatchedKeys()
    {
        using var input = new MemoryStream(BuildPdf(encrypted: false));
        using var output = new MemoryStream();

        var report = PdfTemplateFiller.Fill(input, output, Values,
            new TemplateOptions { Sources = TemplateSources.FormFields });

        Assert.Equal(2, report.UnmatchedKeys.Count);
        Assert.DoesNotContain(report.Warnings,
            w => w.IndexOf("encrypted", StringComparison.OrdinalIgnoreCase) >= 0);
    }

    /// <summary>
    /// A minimal single-page PDF, optionally carrying a trailer <c>/Encrypt</c> reference.
    /// <c>PdfDocument.IsEncrypted</c> is driven purely by that trailer key, so no real crypto
    /// handler — and no Tetitulz.Pdf.Encryption reference — is needed to exercise the guard.
    /// </summary>
    private static byte[] BuildPdf(bool encrypted)
    {
        const string content = "BT /F1 12 Tf 72 700 Td (Hello) Tj ET";
        var pdf = new StringBuilder();
        pdf.Append("%PDF-1.7\n");

        int off1 = pdf.Length;
        pdf.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int off2 = pdf.Length;
        pdf.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int off3 = pdf.Length;
        pdf.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                   "/Contents 4 0 R /Resources << >> >>\nendobj\n");
        int off4 = pdf.Length;
        pdf.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            content.Length, content);
        int off5 = pdf.Length;
        pdf.Append("5 0 obj\n<< /Filter /Standard /V 2 /R 3 /Length 128 /P -1 " +
                   "/O (0123456789ABCDEF0123456789ABCDEF) " +
                   "/U (FEDCBA9876543210FEDCBA9876543210) >>\nendobj\n");

        int xref = pdf.Length;
        pdf.Append("xref\n0 6\n0000000000 65535 f \n");
        foreach (int o in new[] { off1, off2, off3, off4, off5 })
            pdf.AppendFormat("{0:D10} 00000 n \n", o);

        pdf.Append(encrypted
            ? "trailer\n<< /Size 6 /Root 1 0 R /Encrypt 5 0 R "
              + "/ID [<0123456789ABCDEF0123456789ABCDEF> <0123456789ABCDEF0123456789ABCDEF>] >>\n"
            : "trailer\n<< /Size 6 /Root 1 0 R >>\n");
        pdf.AppendFormat("startxref\n{0}\n%%EOF\n", xref);

        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
