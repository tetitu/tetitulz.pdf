// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Scripting;
using Tetitulz.Pdf.Core.Plugins;

namespace Tetitulz.Pdf.Plugins.JavaScript.Tests;

/// <summary>
/// What a document hands its scripts beyond its fields (GAPS §25b): its authors from the XMP
/// packet, its annotations, its optional content groups and its page words — each through the
/// library's own reader, so a script sees what the document holds and nothing it does not.
/// </summary>
[Collection(PluginHostCollection.Name)]
public sealed class DocumentFactsTests : IDisposable
{
    private readonly JintJavaScriptPlugin _plugin = new();

    public DocumentFactsTests() => PdfPluginHost.Instance.Register(_plugin);

    public void Dispose() => PdfPluginHost.Instance.Unregister(_plugin.Name);

    private const string Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";

    /// <summary>
    /// <c>info.Authors</c> is the packet's <c>dc:creator</c> list in order, <c>ContactEmail</c> is
    /// empty, and the keys are the ones Acrobat answers; with no packet the <c>/Author</c> is the one
    /// author, and with neither the key is absent (the control).
    /// </summary>
    [Fact]
    public void InfoAuthors_AreThePacketsCreators_AndTheKeysAreAcrobats()
    {
        const string script = "var k = []; for (var p in this.info) k.push(p); " +
                              "event.value = (this.info.Authors ? this.info.Authors.join('|') : typeof this.info.Authors) + '#' + " +
                              "this.info.ContactEmail + '#' + k.sort().join(',') + '#' + (this.info.authors ? this.info.authors.length : '-');";
        string xmp = "<x:xmpmeta xmlns:x='adobe:ns:meta/'><rdf:RDF xmlns:rdf='" + Rdf + "'>" +
                     "<rdf:Description rdf:about='' xmlns:dc='http://purl.org/dc/elements/1.1/'><dc:creator><rdf:Seq>" +
                     "<rdf:li>First Author</rdf:li><rdf:li>Second Author</rdf:li></rdf:Seq></dc:creator></rdf:Description></rdf:RDF></x:xmpmeta>";

        var withPacket = Run(Document(script, info: "<< /Author (First Author) /Title (Probe) >>", xmp: xmp));
        Assert.Equal("First Author|Second Author##Author,Authors,ContactEmail,Title#2", withPacket.EventValue);

        var infoOnly = Run(Document(script, info: "<< /Author (Only Author) >>"));
        Assert.Equal("Only Author##Author,Authors,ContactEmail#1", infoOnly.EventValue);

        var neither = Run(Document(script, info: "<< /Title (Untitled) >>"));
        Assert.Equal("undefined##ContactEmail,Title#-", neither.EventValue);
    }

    /// <summary>
    /// A document's dates reach a script through Core's one parser: a date that states its zone is
    /// that instant, one that states none the local time it names, as a viewer shows it; a zone
    /// written after the minutes, short of the seconds, is still the zone; a year below 100 is
    /// that year; and a date that does not read answers its text.
    /// </summary>
    [Fact]
    public void InfoDates_AreReadByTheOneParser_LocalWithoutAZone_TheInstantWithOne()
    {
        const string script =
            "var c = this.creationDate, m = this.modDate;" +
            "event.value = [c instanceof Date, c.getFullYear(), c.getMonth(), c.getDate(), c.getHours(), c.getMinutes(), c.getSeconds()," +
            " m.getTime() === Date.UTC(2024, 0, 1, 9, 0, 0)].join('|');";
        var ctx = Run(Document(script, info: "<< /CreationDate (D:20240101123005) /ModDate (D:202401011200+03'00') >>"));
        Assert.Equal("true|2024|0|1|12|30|5|true", ctx.EventValue);

        const string odd = "var c = this.creationDate, m = this.modDate; event.value = [typeof c, c, m.getFullYear(), m.getMonth()].join('|');";
        var oddCtx = Run(Document(odd, info: "<< /CreationDate (D:2024-01-01) /ModDate (D:00500615) >>"));
        Assert.Equal("string|D:2024-01-01|50|5", oddCtx.EventValue);
    }

    /// <summary>
    /// A text the file names from every <c>/Info</c> entry is handed to a script under one listing's
    /// budget: each text cut, the entries past the budget not listed and the report saying so — where
    /// written whole, a thousand names of one 64 KB string made a listing of 64 million characters
    /// the script could not parse under its memory limit. The entries listed hold the text whole.
    /// </summary>
    [Fact]
    public void AdversarialSharedInfoText_IsListedUnderTheBudget_AndTheRestReported()
    {
        var info = new StringBuilder("<<");
        for (int i = 0; i < 1000; i++) info.Append(" /K").Append(i).Append(" 9 0 R");
        info.Append(" >>");
        // The document's own entries: info adds ContactEmail beside them.
        const string script = "var n = 0, whole = 0; for (var p in this.info) { if (p.charAt(0) !== 'K') continue; n++; if (this.info[p].length === 65536) whole++; } event.value = n + '|' + whole;";

        var ctx = Run(Document(script, info: info.ToString(), extraObjects: new[] { "(" + new string('x', 65536) + ")" }));

        string[] counted = ctx.EventValue.Split('|');
        int listed = int.Parse(counted[0], System.Globalization.CultureInfo.InvariantCulture);
        int whole = int.Parse(counted[1], System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(listed, 32, 100);
        Assert.InRange(whole, listed - 1, listed);       // the last one listed may be the one cut
        Assert.Contains("info: " + (1000 - listed) + " entr(ies) past", PdfJsEngine.LastUnsupportedRequest, StringComparison.Ordinal);
    }

    /// <summary>
    /// A field's widgets are listed to a script at most ten thousand, the rest reported; a field of
    /// twenty thousand widgets sharing one caption runs its script (the potency half: the listed
    /// widgets answer the caption).
    /// </summary>
    [Fact]
    public void AdversarialWidgetFlood_IsListedToTheCap_AndTheRestReported()
    {
        var kids = new StringBuilder("[");
        for (int i = 0; i < 20_000; i++) kids.Append("<< /Type /Annot /Subtype /Widget /Rect [0 0 10 10] /MK 10 0 R >> ");
        kids.Append(']');
        const string script = "var f = this.getField('pb.0'); event.value = f.buttonGetCaption();";

        var ctx = Run(Document(script,
            catalogExtra: "/AcroForm << /Fields [9 0 R] >>",
            extraObjects: new[] { "<< /FT /Btn /Ff 65536 /T (pb) /Kids " + kids + " >>", "<< /CA (Go) >>" }));

        Assert.Equal("Go", ctx.EventValue);
        Assert.Contains("field pb: 10000 widget(s)", PdfJsEngine.LastUnsupportedRequest, StringComparison.Ordinal);
    }

    /// <summary>
    /// The packet's <c>pdfx:</c> custom properties fill the <c>info</c> keys the <c>/Info</c> lacks —
    /// <c>ContactEmail</c> among them — and the packet itself is <c>metadata</c>; an <c>/Info</c> key
    /// keeps its own value (the control).
    /// </summary>
    [Fact]
    public void InfoCustomKeys_ComeFromThePacketsPdfxProperties_AndMetadataIsThePacket()
    {
        string xmp = "<x:xmpmeta xmlns:x='adobe:ns:meta/'><rdf:RDF xmlns:rdf='" + Rdf + "'>" +
                     "<rdf:Description rdf:about='' xmlns:pdfx='http://ns.adobe.com/pdfx/1.3/' pdfx:Department='Sales'>" +
                     "<pdfx:ContactEmail>probe@example.com</pdfx:ContactEmail><pdfx:Custom>from the packet</pdfx:Custom>" +
                     "</rdf:Description></rdf:RDF></x:xmpmeta>";
        const string script = "event.value = [this.info.ContactEmail, this.info.Department, this.info.Custom, this.metadata.indexOf('<x:xmpmeta') >= 0, this.metadata.length > 100].join('|');";

        var ctx = Run(Document(script, info: "<< /Custom (from the info) >>", xmp: xmp));
        Assert.Equal("probe@example.com|Sales|from the info|true|true", ctx.EventValue);

        var none = Run(Document("event.value = [this.info.ContactEmail, this.metadata].join('|');"));
        Assert.Equal("|", none.EventValue);
    }

    /// <summary>
    /// Each page's rotation and boxes: <c>getPageRotation</c> as written, <c>getPageBox</c> in
    /// Acrobat's [left, top, right, bottom] order in rotated user space — the crop box's displayed
    /// lower-left corner the origin, so a cropped page turned a quarter answers its crop box as
    /// [0, width, height, 0] (<see cref="RotatedUserSpaceTests"/> holds the rule) — defaulting to
    /// the crop box and to the first page, and an empty box for a page or a name the document lacks.
    /// </summary>
    [Fact]
    public void PageRotationAndBoxes_AreThePagesOwn()
    {
        const string script = "event.value = [this.getPageRotation(0), this.getPageRotation(1), this.getPageRotation(), this.getPageRotation(9)," +
                              " this.getPageBox('Media', 0).join(','), this.getPageBox().join(','), this.getPageBox({cBox: 'Crop', nPage: 1}).join(',')," +
                              " this.getPageBox('Art', 1).join(','), this.getPageBox('Crop', 9).join(','), this.getPageBox('Nope', 0).join(',')].join('|');";
        var ctx = Run(Document(script, page2Extra: "/Rotate 90 /CropBox [10 20 110 220]"));
        Assert.Equal("0|90|0|0|0,200,200,0|0,200,200,0|0,100,200,0|0,100,200,0|0,0,0,0|0,0,0,0", ctx.EventValue);
    }

    /// <summary>
    /// A multi-select list box's value is the array of its selections, its indices theirs, and an
    /// array a script assigns reaches the host joined as the filler takes it.
    /// </summary>
    [Fact]
    public void AMultiSelectListBox_AnswersItsSelectionsAsAnArray_AndTakesOne()
    {
        const string script = "var f = this.getField('langs');" +
                              "event.value = [f.value.join('|'), f.multipleSelection, f.currentValueIndices.join('|'), f.numItems, f.type].join('#');" +
                              "f.value = ['he', 'fr'];";
        var ctx = Run(Document(script,
            catalogExtra: "/AcroForm << /Fields [9 0 R] >>",
            page1Annots: "/Annots [9 0 R]",
            extraObjects: new[] { "<< /Type /Annot /Subtype /Widget /FT /Ch /Ff 2097152 /T (langs) /Rect [0 0 50 50] /Opt [(en) (he) (fr)] /V [(en) (he)] >>" }));

        Assert.Equal("en|he#true#0|1#3#listbox", ctx.EventValue);
        Assert.Equal("he\nfr", ctx.ModifiedFieldValues["langs"]);
    }

    /// <summary>A hostile packet — one deeper than the reader's cap — names no author; the script still runs.</summary>
    [Fact]
    public void AdversarialDeepXmpPacket_NamesNoAuthor()
    {
        var deep = new StringBuilder("<x:xmpmeta xmlns:x='adobe:ns:meta/'><rdf:RDF xmlns:rdf='" + Rdf + "'><rdf:Description rdf:about='' xmlns:dc='http://purl.org/dc/elements/1.1/'>");
        for (int i = 0; i < 200; i++) deep.Append("<a>");
        deep.Append("<dc:creator>Buried</dc:creator>");
        for (int i = 0; i < 200; i++) deep.Append("</a>");
        deep.Append("</rdf:Description></rdf:RDF></x:xmpmeta>");

        var ctx = Run(Document("event.value = typeof this.info.Authors + '|' + this.info.Title;", info: "<< /Title (T) >>", xmp: deep.ToString()));
        Assert.Equal("undefined|T", ctx.EventValue);
    }

    /// <summary>
    /// <c>getAnnots</c> lists a page's markup annotations with Acrobat's properties, never its widgets,
    /// links or popups; a page with none answers null; <c>getAnnot</c> finds one by name; a write to
    /// one is kept for the script and recorded, never applied.
    /// </summary>
    [Fact]
    public void GetAnnots_ListsThePagesMarkup_NotItsWidgetsLinksOrPopups()
    {
        const string annots = "/Annots [ << /Type /Annot /Subtype /Text /NM (note-1) /T (Ann) /Contents (Hello) /M (D:20260102030405Z) /F 4 /Rect [10 20 30 40] >> " +
                              "<< /Type /Annot /Subtype /Square /NM (sq-1) /F 2 /Rect [50 60 70 80] >> " +
                              "<< /Type /Annot /Subtype /Link /Rect [0 0 1 1] >> << /Type /Annot /Subtype /Popup /Rect [0 0 1 1] >> " +
                              "<< /Type /Annot /Subtype /Widget /FT /Tx /T (f) /Rect [0 0 1 1] >> ]";
        const string script = "var a = this.getAnnots(0), b = this.getAnnots(1), n = this.getAnnot(0, 'note-1'), all = this.getAnnots({nPage: 0, nSortBy: 4, bReverse: true});" +
                              "n.hidden = true;" +
                              "event.value = [a.length, a[0].type, a[0].author, a[0].contents, a[0].rect.join(','), a[0].modDate.getUTCFullYear(), a[0].print, a[1].hidden," +
                              " String(b), n.name, n.page, n.hidden, this.getAnnots().length, all[0].type, String(this.getAnnot(0, 'none')), typeof n.getProps().rect].join('|');";

        var ctx = Run(Document(script, page1Annots: annots));

        Assert.Equal("2|Text|Ann|Hello|10,20,30,40|2026|true|true|null|note-1|0|true|2|Text|null|object", ctx.EventValue);
        Assert.Contains("annotation note-1.hidden = true not applied to the document", PdfJsEngine.LastUnsupportedRequest!, StringComparison.Ordinal);
    }

    /// <summary>The most annotations a script is handed — <c>JsHost.MaxListedObjects</c>, which the plugin does not expose.</summary>
    private const int MaxListedAnnotations = 10_000;

    /// <summary>
    /// A document listing more annotations than a script is handed hands the first that many and
    /// counts the rest, so the listing stays inside the engine's memory limit instead of failing
    /// every script that asks; the control: one under the cap lists them all and counts nothing.
    /// </summary>
    [Fact]
    public void AdversarialAnnotationFlood_IsListedToTheCap_AndTheRestCounted()
    {
        const string script = "var a = this.getAnnots(); event.value = a.length + '|' + a[a.length - 1].name;";
        var flood = Run(Document(script, page1Annots: Annots(MaxListedAnnotations + 7)));
        Assert.Equal(MaxListedAnnotations + "|n" + (MaxListedAnnotations - 1), flood.EventValue);
        Assert.Contains("getAnnots: 7 annotation(s) past the first " + MaxListedAnnotations + " not listed",
            PdfJsEngine.LastUnsupportedRequest!, StringComparison.Ordinal);

        var under = Run(Document(script, page1Annots: Annots(MaxListedAnnotations - 1)));
        Assert.Equal((MaxListedAnnotations - 1) + "|n" + (MaxListedAnnotations - 2), under.EventValue);
        Assert.Null(PdfJsEngine.LastUnsupportedRequest);

        static string Annots(int count)
        {
            var sb = new StringBuilder("/Annots [");
            for (int i = 0; i < count; i++) sb.Append("<< /Type /Annot /Subtype /Text /NM (n").Append(i).Append(") /Rect [0 0 1 1] >> ");
            return sb.Append(']').ToString();
        }
    }

    /// <summary>
    /// <c>getOCGs</c> answers the document's groups with their default states, for any page; a state
    /// a script sets is read back and recorded; a document without groups answers null (the control).
    /// </summary>
    [Fact]
    public void GetOCGs_ListsTheDocumentsGroups_WithTheirStates()
    {
        const string script = "var g = this.getOCGs(0); g[1].state = true;" +
                              "event.value = [g.length, g[0].name, g[0].state, g[1].name, g[1].state, g[1].initState, String(g[0]), this.getOCGs(1).length].join('|');";
        var ctx = Run(Document(script,
            catalogExtra: "/OCProperties << /OCGs [9 0 R 10 0 R] /D << /OFF [10 0 R] >> >>",
            extraObjects: new[] { "<< /Type /OCG /Name (Layer A) >>", "<< /Type /OCG /Name (Layer B) >>" }));

        Assert.Equal("2|Layer A|true|Layer B|true|false|Layer A|2", ctx.EventValue);
        Assert.Contains("OCG Layer B.state = true not applied to the document", PdfJsEngine.LastUnsupportedRequest!, StringComparison.Ordinal);

        var none = Run(Document("event.value = String(this.getOCGs(0));"));
        Assert.Equal("null", none.EventValue);
    }

    /// <summary>
    /// The page's words in reading order: counted, read by index — stripped of punctuation unless
    /// asked otherwise — and refused past the page's end or for a page the document lacks.
    /// </summary>
    [Fact]
    public void PageWords_AreThePagesWordsInOrder_StrippedUnlessAsked()
    {
        const string script = "event.value = [this.getPageNumWords(0), this.getPageNthWord(0, 1), this.getPageNthWord({nPage: 0, nWord: 1, bStrip: false})," +
                              " this.getPageNumWords(7), this.getPageNumWords(1)].join('|');" +
                              "try { this.getPageNthWord(0, 99); event.value += '|no'; } catch (e) { event.value += '|' + e.name; }";
        var ctx = Run(Document(script, content: "BT /F1 12 Tf 10 50 Td (Hello world, again) Tj ET"));
        Assert.Equal("3|world|world,|0|0|RangeError", ctx.EventValue);
    }

    private static PdfJsExecutionContext Run(byte[] pdf)
    {
        var ctx = new PdfJsExecutionContext();
        PdfOperations.ExecuteOpenScripts(new MemoryStream(pdf), ctx);
        Assert.True(ctx.Error == null, ctx.Error?.ToString());
        return ctx;
    }

    /// <summary>
    /// Two pages, the first with <paramref name="content"/> and <paramref name="page1Annots"/>, an
    /// open action running <paramref name="script"/>, an <c>/Info</c>, an XMP packet and extra
    /// objects (numbered from 9) when given.
    /// </summary>
    private static byte[] Document(string script, string? info = null, string? xmp = null, string page1Annots = "",
        string catalogExtra = "", string? content = null, string[]? extraObjects = null, string page2Extra = "")
    {
        string js = script.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
        content ??= "";
        string packet = xmp == null ? "" : "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>" + xmp + "<?xpacket end=\"w\"?>";
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /OpenAction 4 0 R " + (xmp == null ? "" : "/Metadata 7 0 R ") + catalogExtra + " >>",
            "<< /Type /Pages /Kids [3 0 R 5 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >> /Contents 6 0 R " + page1Annots + " >>",
            "<< /S /JavaScript /JS (" + js + ") >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] " + page2Extra + " >>",
            "<< /Length " + content.Length + " >>\nstream\n" + content + "\nendstream",
            xmp == null ? "null" : "<< /Type /Metadata /Subtype /XML /Length " + packet.Length + " >>\nstream\n" + packet + "\nendstream",
            info ?? "null",
        };
        if (extraObjects != null) objects.AddRange(extraObjects);

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objects.Count + 1).Append(" /Root 1 0 R")
          .Append(info == null ? "" : " /Info 8 0 R").Append(" >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
