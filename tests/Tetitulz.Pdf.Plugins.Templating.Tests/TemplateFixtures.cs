// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Text;

namespace Tetitulz.Pdf.Plugins.Templating.Tests;

/// <summary>
/// Small in-memory PDF templates the tests run against.
///
/// <para>Self-contained by convention: this repo forbids cross-project test dependencies and
/// requires tests to build a byte array rather than read a file from disk, so these are deliberately
/// a local copy of the shape <c>Base.Tests</c>' <c>FormPdfFixtures</c> uses rather than a reference
/// to it.</para>
///
/// <para>Object numbering is positional — the first body is object 1 — so the cross-references in
/// the bodies below are stable and readable top-down.</para>
/// </summary>
internal static class TemplateFixtures
{
    /// <summary>1 = catalog, 2 = page tree, 3 = AcroForm, 4 = page, 5 = the /DR Helvetica.</summary>
    internal const int CatalogObj = 1;
    internal const int AcroFormObj = 3;
    internal const int PageObj = 4;

    /// <summary>The single form field / widget every fixture here defines.</summary>
    internal const int FieldObj = 6;

    /// <summary>An AcroForm template with one text field, named <c>name</c> by default.</summary>
    internal static byte[] TextField(
        string fieldName = "name",
        string rect = "[100 700 300 720]",
        string fieldEntries = "/DA (/Helv 12 Tf 0 g)") => Build(
        "<< /Type /Catalog /Pages 2 0 R /AcroForm 3 0 R >>",
        "<< /Type /Pages /Kids [4 0 R] /Count 1 /MediaBox [0 0 612 792] >>",
        "<< /Fields [6 0 R] /DR << /Font << /Helv 5 0 R >> >> >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [6 0 R] >>",
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        $"<< /Type /Annot /Subtype /Widget /FT /Tx /T ({fieldName}) /Rect {rect} {fieldEntries} >>");

    /// <summary>
    /// A page carrying literal placeholder text and no form at all — the other kind of template.
    /// </summary>
    internal static byte[] PageWithText(string text)
    {
        string content = $"BT /F1 12 Tf 72 700 Td ({Escape(text)}) Tj ET";
        return Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 612 792] >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            StreamBody(string.Empty, content),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
    }

    /// <summary>Assembles a PDF from raw object bodies; <paramref name="bodies"/>[0] is object 1.</summary>
    internal static byte[] Build(params string[] bodies)
    {
        var pdf = new StringBuilder();
        pdf.Append("%PDF-1.7\n");

        var offsets = new int[bodies.Length + 1];
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets[i + 1] = pdf.Length;
            pdf.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }

        int xref = pdf.Length;
        pdf.Append("xref\n0 ").Append(bodies.Length + 1).Append('\n');
        pdf.Append("0000000000 65535 f \n");
        for (int i = 1; i <= bodies.Length; i++)
            pdf.AppendFormat("{0:D10} 00000 n \n", offsets[i]);

        pdf.Append("trailer\n<< /Size ").Append(bodies.Length + 1).Append(" /Root 1 0 R >>\n");
        pdf.AppendFormat("startxref\n{0}\n%%EOF\n", xref);

        return Encoding.ASCII.GetBytes(pdf.ToString());
    }

    /// <summary>A stream object body whose <c>/Length</c> matches its content.</summary>
    internal static string StreamBody(string dictionaryEntries, string content) =>
        $"<< {dictionaryEntries} /Length {content.Length} >>\nstream\n{content}\nendstream";

    private static string Escape(string s) =>
        s.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
}
