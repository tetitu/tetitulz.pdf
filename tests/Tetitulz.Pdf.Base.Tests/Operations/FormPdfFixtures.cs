// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Text;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// Builds the small in-memory AcroForm PDFs the form-filling and form-reading tests run against.
///
/// <para>Object numbering is positional: the first body is object 1 (the catalog), so every
/// fixture below reads top-down and the cross-references in the bodies are stable.</para>
/// </summary>
internal static class FormPdfFixtures
{
    /// <summary>Object 1 = catalog, 2 = page tree, 3 = AcroForm, 4 = page, 5 = the /DR Helvetica.</summary>
    internal const int CatalogObj = 1;
    internal const int AcroFormObj = 3;
    internal const int PageObj = 4;
    internal const int HelveticaObj = 5;
    /// <summary>First object number a fixture is free to use for its own fields.</summary>
    internal const int FirstFieldObj = 6;

    /// <summary>
    /// Assembles a PDF from raw object bodies. <paramref name="bodies"/>[0] becomes
    /// <c>1 0 obj</c>, and the trailer's <c>/Root</c> points at it.
    /// </summary>
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

    /// <summary>A stream object body with a <c>/Length</c> that matches its content.</summary>
    internal static string StreamBody(string dictionaryEntries, string content) =>
        $"<< {dictionaryEntries} /Length {content.Length} >>\nstream\n{content}\nendstream";

    /// <summary>A minimal form XObject, enough to be a real appearance state.</summary>
    internal static string AppearanceStream(double width = 20, double height = 20) =>
        StreamBody($"/Type /XObject /Subtype /Form /BBox [0 0 {width} {height}]", "q Q");

    // ----------------------------------------------------------------
    //  Shared prologue
    // ----------------------------------------------------------------

    private static string Catalog() => "<< /Type /Catalog /Pages 2 0 R /AcroForm 3 0 R >>";

    private static string PageTree() =>
        "<< /Type /Pages /Kids [4 0 R] /Count 1 /MediaBox [0 0 612 792] >>";

    private static string AcroForm(string fields, string extras = "") =>
        $"<< /Fields [{fields}] /DR << /Font << /Helv 5 0 R >> >> {extras} >>";

    private static string Page(string annots) =>
        $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [{annots}] >>";

    private static string Helvetica() =>
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>";

    // ----------------------------------------------------------------
    //  Fixtures
    // ----------------------------------------------------------------

    /// <summary>
    /// A merged check-box field/widget (object 6) whose <c>/AP /N</c> offers
    /// <paramref name="onState"/> and <c>/Off</c>.
    /// </summary>
    /// <param name="startsChecked">
    /// Builds the box already selected. A test that clears a box has to start from one that is
    /// set, or "it ended up /Off" is true of a filler that writes nothing at all.
    /// </param>
    internal static byte[] Checkbox(string fieldName = "agree", string onState = "Yes",
                                    bool startsChecked = false)
    {
        string initial = startsChecked ? onState : "Off";
        return Build(
            Catalog(),
            PageTree(),
            AcroForm("6 0 R"),
            Page("6 0 R"),
            Helvetica(),
            $"<< /Type /Annot /Subtype /Widget /FT /Btn /T ({fieldName}) /Rect [100 700 120 720] " +
            $"/AP << /N << /{onState} 7 0 R /Off 8 0 R >> >> /AS /{initial} /V /{initial} >>",
            AppearanceStream(),
            AppearanceStream());
    }

    /// <summary>
    /// A radio group (object 6) with two widget kids (7, 8) offering <c>/Red</c> and
    /// <c>/Green</c>. The group carries the Radio flag; the widgets carry no <c>/T</c>, which is
    /// what makes the parent the terminal field.
    /// </summary>
    internal static byte[] RadioGroup(string fieldName = "colour") => Build(
        Catalog(),
        PageTree(),
        AcroForm("6 0 R"),
        Page("7 0 R 8 0 R"),
        Helvetica(),
        $"<< /FT /Btn /Ff 32768 /T ({fieldName}) /Kids [7 0 R 8 0 R] /V /Off >>",
        "<< /Type /Annot /Subtype /Widget /Parent 6 0 R /Rect [100 700 120 720] " +
        "/AP << /N << /Red 9 0 R /Off 10 0 R >> >> /AS /Off >>",
        "<< /Type /Annot /Subtype /Widget /Parent 6 0 R /Rect [140 700 160 720] " +
        "/AP << /N << /Green 9 0 R /Off 10 0 R >> >> /AS /Off >>",
        AppearanceStream(),
        AppearanceStream());

    /// <summary>
    /// The same radio group, with <c>/Kids</c> written as an <b>indirect reference</b> (object 11)
    /// rather than a direct array. Legal, and routinely emitted.
    /// </summary>
    /// <remarks>
    /// The separating input for a whole cluster of non-resolving reads in <c>Core.Forms</c>:
    /// <c>PdfDictionary</c>'s typed getters do not follow a reference, so an indirect
    /// <c>/Kids</c> read as absent — which made the group look <b>terminal</b>, and
    /// <c>WidgetDictionaries</c> hand back the group's own dictionary instead of the two widgets.
    /// </remarks>
    internal static byte[] RadioGroupWithIndirectKids(string fieldName = "colour") => Build(
        Catalog(),
        PageTree(),
        AcroForm("6 0 R"),
        Page("7 0 R 8 0 R"),
        Helvetica(),
        $"<< /FT /Btn /Ff 32768 /T ({fieldName}) /Kids 11 0 R /V /Off >>",
        "<< /Type /Annot /Subtype /Widget /Parent 6 0 R /Rect [100 700 120 720] " +
        "/AP << /N << /Red 9 0 R /Off 10 0 R >> >> /AS /Off >>",
        "<< /Type /Annot /Subtype /Widget /Parent 6 0 R /Rect [140 700 160 720] " +
        "/AP << /N << /Green 9 0 R /Off 10 0 R >> >> /AS /Off >>",
        AppearanceStream(),
        AppearanceStream(),
        "[7 0 R 8 0 R]");

    /// <summary>
    /// A signature field written as a group (object 6) whose only widget kid (object 7) carries the
    /// <c>/FT /Sig</c> and no <c>/T</c>.
    /// </summary>
    /// <remarks>
    /// §12.7.3.1 distinguishes a widget kid by its lack of <c>/T</c>, so object 6 is the terminal
    /// field — and it declares no <c>/FT</c> anywhere in its own chain. The type therefore resolved
    /// to <c>Unknown</c>, and <c>PdfFormFiller.ApplyValue</c> fell past its signature guard into
    /// <c>ApplyVariableTextValue</c> and wrote a text value onto a signature field.
    /// </remarks>
    internal static byte[] SignatureGroupWithTypeOnTheWidget(string fieldName = "sig") => Build(
        Catalog(),
        PageTree(),
        AcroForm("6 0 R"),
        Page("7 0 R"),
        Helvetica(),
        $"<< /T ({fieldName}) /Kids [7 0 R] >>",
        "<< /Type /Annot /Subtype /Widget /Parent 6 0 R /FT /Sig /Rect [100 700 300 720] >>");

    /// <summary>
    /// A merged text field/widget (object 6). <paramref name="fieldEntries"/> adds the entries a
    /// given test cares about — <c>/DA</c>, <c>/Q</c>, <c>/MaxLen</c>, <c>/Ff</c>.
    /// </summary>
    internal static byte[] TextField(
        string fieldName = "name",
        string rect = "[100 700 300 720]",
        string fieldEntries = "/DA (/Helv 12 Tf 0 g)",
        string acroFormExtras = "") => Build(
        Catalog(),
        PageTree(),
        AcroForm("6 0 R", acroFormExtras),
        Page("6 0 R"),
        Helvetica(),
        $"<< /Type /Annot /Subtype /Widget /FT /Tx /T ({fieldName}) /Rect {rect} {fieldEntries} >>");

    /// <summary>
    /// A text field (object 6) that carries a <b>single appearance stream</b> at
    /// <c>/AP /N</c> (object 7) — §12.5.5's other shape.
    ///
    /// <para><b>Why it matters.</b> <see cref="TextField"/> writes no <c>/AP</c> at all, so
    /// assertions about appearance states made against it are made against a widget that has none.
    /// A reader that mistook a single stream for a state sub-dictionary — which is easy, because
    /// <c>PdfStream</c> derives from <c>PdfDictionary</c> — would be invisible to them. This fixture
    /// is what makes that distinguishable: the stream's own keys
    /// (<c>/Type</c>, <c>/Subtype</c>, <c>/BBox</c>, <c>/Length</c>) are exactly what a
    /// mis-reading reports as "on states".</para>
    /// </summary>
    internal static byte[] TextFieldWithSingleAppearanceStream(string fieldName = "name") => Build(
        Catalog(),
        PageTree(),
        AcroForm("6 0 R"),
        Page("6 0 R"),
        Helvetica(),
        $"<< /Type /Annot /Subtype /Widget /FT /Tx /T ({fieldName}) /Rect [100 700 300 720] " +
        "/DA (/Helv 12 Tf 0 g) /AP << /N 7 0 R >> >>",
        AppearanceStream());

    /// <summary>
    /// A form carrying one of each thing FDF / XFDF interchange has to decide about: a filled text
    /// field (6), a <b>nested</b> one whose fully-qualified name is <c>address.city</c> (7 → 8), a
    /// multi-select list box holding two selections (9), a push button (10), a signature field
    /// (11), and a text field with no value (12).
    ///
    /// <para>The last three are the interesting ones: a push button has no value, a signature
    /// field's value is a dictionary rather than text, and an empty field is only exported on
    /// request — so a fixture without them cannot tell a correct export from one that exports
    /// everything it finds.</para>
    /// </summary>
    internal static byte[] ExchangeForm(string cityValue = "London") => Build(
        Catalog(),
        PageTree(),
        AcroForm("6 0 R 7 0 R 9 0 R 10 0 R 11 0 R 12 0 R"),
        Page("6 0 R 8 0 R 9 0 R 10 0 R 11 0 R 12 0 R"),
        Helvetica(),
        "<< /Type /Annot /Subtype /Widget /FT /Tx /T (name) /Rect [100 700 300 720] " +
        "/DA (/Helv 12 Tf 0 g) /V (Ada) >>",
        "<< /T (address) /Kids [8 0 R] >>",
        "<< /Type /Annot /Subtype /Widget /Parent 7 0 R /FT /Tx /T (city) " +
        $"/Rect [100 660 300 680] /DA (/Helv 12 Tf 0 g) /V ({cityValue}) >>",
        "<< /Type /Annot /Subtype /Widget /FT /Ch /Ff 2097152 /T (langs) " +
        "/Rect [100 600 300 640] /DA (/Helv 10 Tf 0 g) " +
        "/Opt [(en) (he) (fr)] /V [(en) (he)] >>",
        "<< /Type /Annot /Subtype /Widget /FT /Btn /Ff 65536 /T (print) /Rect [100 560 200 580] >>",
        "<< /Type /Annot /Subtype /Widget /FT /Sig /T (sig) /Rect [100 500 300 540] >>",
        "<< /Type /Annot /Subtype /Widget /FT /Tx /T (empty) /Rect [100 460 300 480] " +
        "/DA (/Helv 12 Tf 0 g) >>");

    /// <summary>A combo box (object 6) whose <c>/Opt</c> entries are export/display pairs.</summary>
    internal static byte[] ChoiceField(string fieldName = "country") => Build(
        Catalog(),
        PageTree(),
        AcroForm("6 0 R"),
        Page("6 0 R"),
        Helvetica(),
        $"<< /Type /Annot /Subtype /Widget /FT /Ch /Ff 131072 /T ({fieldName}) " +
        "/Rect [100 600 300 620] /DA (/Helv 10 Tf 0 g) " +
        "/Opt [[(IL) (Israel)] [(GB) (United Kingdom)]] /V (IL) >>");

    /// <summary>
    /// Two fields whose <c>/Kids</c> point at each other, so a naive walk of the field tree never
    /// terminates. Neither is a legal form; both are what a hostile file looks like.
    /// </summary>
    internal static byte[] CyclicFieldTree() => Build(
        Catalog(),
        PageTree(),
        AcroForm("6 0 R"),
        Page(""),
        Helvetica(),
        "<< /FT /Tx /T (outer) /Kids [7 0 R] >>",
        "<< /T (inner) /Kids [6 0 R] >>");
}
