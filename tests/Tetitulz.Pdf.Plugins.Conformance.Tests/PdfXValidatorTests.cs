// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Encryption;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Encryption;
using Tetitulz.Pdf.Plugins.Conformance;

namespace Tetitulz.Pdf.Plugins.Conformance.Tests;

/// <summary>
/// Tests for <see cref="PdfXValidator"/> (ISO 15930 print exchange).
///
/// <para>
/// Every fixture is built in memory. The suite is written as matched pairs: a clean
/// PDF/X-1a document that must validate with <em>no</em> violations, and one mutation
/// per check that must produce exactly that check's code. Without the clean control a
/// "reports X0n" test proves nothing — a validator that reported every code on every
/// document would pass all of them.
/// </para>
/// </summary>
public class PdfXValidatorTests
{
    // ------------------------------------------------------------------
    //  Fixture construction
    // ------------------------------------------------------------------

    /// <summary>Accumulates numbered objects and serialises them with a valid xref table.</summary>
    private sealed class ObjectFile
    {
        private readonly List<string?> _bodies = new();

        /// <summary>Claims an object number whose body is supplied later (forward references).</summary>
        public int Reserve() { _bodies.Add(null); return _bodies.Count; }

        public int Add(string body) { _bodies.Add(body); return _bodies.Count; }

        public void Set(int number, string body) { _bodies[number - 1] = body; }

        /// <summary>Object number of the trailer's /Info dictionary, or 0 when absent.</summary>
        public int InfoNumber { get; set; }

        public static string StreamObject(string dictEntries, string data)
            => $"<< {dictEntries} /Length {data.Length} >>\nstream\n{data}\nendstream";

        /// <summary>Header version. 1.6 by default so the fixture satisfies the PDF/X-4 floor.</summary>
        public string HeaderVersion { get; set; } = "1.6";

        public byte[] Build(int rootNumber)
        {
            var sb = new StringBuilder($"%PDF-{HeaderVersion}\n");
            var offsets = new int[_bodies.Count + 1];

            for (int i = 1; i <= _bodies.Count; i++)
            {
                offsets[i] = sb.Length;
                sb.Append(i).Append(" 0 obj\n")
                  .Append(_bodies[i - 1] ?? "null")
                  .Append("\nendobj\n");
            }

            int xref = sb.Length;
            sb.Append("xref\n0 ").Append(_bodies.Count + 1).Append('\n');
            sb.Append("0000000000 65535 f \n");
            for (int i = 1; i <= _bodies.Count; i++)
                sb.AppendFormat("{0:D10} 00000 n \n", offsets[i]);

            sb.Append("trailer\n<< /Size ").Append(_bodies.Count + 1)
              .Append(" /Root ").Append(rootNumber).Append(" 0 R");
            if (InfoNumber > 0) sb.Append(" /Info ").Append(InfoNumber).Append(" 0 R");
            sb.Append(" >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");

            return Encoding.ASCII.GetBytes(sb.ToString());
        }
    }

    internal enum FontKind
    {
        /// <summary>No /Font resource at all.</summary>
        None,
        /// <summary>Subset Type1 with /FontDescriptor → /FontFile3. Conforming.</summary>
        EmbeddedType1,
        /// <summary>TrueType whose descriptor carries no /FontFile* entry.</summary>
        DescriptorWithoutFontFile,
        /// <summary>Standard-14 Type1 with no /FontDescriptor at all.</summary>
        Standard14NoDescriptor,
        /// <summary>Type0 whose CIDFont descendant carries no /FontFile* entry.</summary>
        Type0NotEmbedded,
        /// <summary>Type3 — glyphs are content streams, so nothing to embed.</summary>
        Type3,
    }

    internal enum BoxKind { Trim, Art, Both, None }

    /// <summary>Where a non-embedded font is hidden — every one of these is unreachable from the
    /// page's own <c>/Resources /Font</c>, so a page-dictionary-only X04 cannot see it.</summary>
    public enum NestedFontHost
    {
        FormXObject,
        TilingPattern,
        AnnotationAppearance,
        /// <summary><c>/AP /N</c> as a dictionary of appearance states rather than one stream.</summary>
        AnnotationAppearanceState,
        Type3CharProc,
    }

    /// <summary>Declarative description of the fixture; every field defaults to conforming.</summary>
    /// <remarks><c>internal</c> rather than private only so <c>PdfOperationsValidateTests</c> can
    /// build the same "conforming, but one check could not run" document through the facade —
    /// duplicating this builder there is how the two would drift.</remarks>
    internal sealed class XDoc
    {
        public string? XmpVersion = "PDF/X-1a:2001";
        public bool XmpAsAttribute;
        /// <summary>When set, replaces the generated packet wholesale.</summary>
        public string? RawXmp;
        /// <summary>Points /Metadata at a dictionary rather than a stream.</summary>
        public bool MetadataNotAStream;
        /// <summary>
        /// Info-dictionary <c>/Trapped</c> value, written as a name. <c>null</c> omits the Info
        /// dictionary altogether (the "absent" case). Defaults to conforming.
        /// </summary>
        public string? Trapped = "False";
        public string? OutputIntentSubtype = "GTS_PDFX";
        public bool OutputIntentOnPage;
        /// <summary>Text of /OutputConditionIdentifier; null omits the required entry.</summary>
        public string? OutputConditionIdentifier = "CGATS TR 001";
        /// <summary>Omits /DestOutputProfile — no embedded ICC profile.</summary>
        public bool OutputIntentWithoutProfile;
        /// <summary>
        /// <c>/N</c> of the output intent's ICC profile: 4 = CMYK (the default), 3 = an RGB
        /// printing condition, under which DeviceRGB is legal at X-3 / X-4.
        /// </summary>
        public int OutputIntentProfileComponents = 4;
        /// <summary>Adds a second, different GTS_PDFX intent, making the condition ambiguous.</summary>
        public bool DuplicateOutputIntent;
        public FontKind Font = FontKind.EmbeddedType1;
        public BoxKind Boxes = BoxKind.Trim;
        public bool CatalogAdditionalActions;
        public bool PageAdditionalActions;
        /// <summary>Adds an annotation carrying /AA.</summary>
        public bool AnnotationAdditionalActions;
        /// <summary>Adds an annotation whose /A action is of this type, e.g. "Launch".</summary>
        public string? AnnotationActionType;
        /// <summary>Adds a catalog /Names /JavaScript name tree.</summary>
        public bool DocumentJavaScript;
        /// <summary>Adds a catalog /OpenAction of this type, e.g. "JavaScript".</summary>
        public string? OpenActionType;
        /// <summary>PDF header version, e.g. "1.4". PDF/X-4 requires 1.6 or later.</summary>
        public string HeaderVersion = "1.6";
        public bool Opi;
        /// <summary>Puts the /OPI image inside a form XObject instead of on the page.</summary>
        public bool OpiInNestedForm;
        /// <summary>Literal PDF for a named /ColorSpace resource entry, e.g. "/DeviceRGB".</summary>
        public string? NamedColorSpace;
        /// <summary>
        /// Names the page's <c>/ColorSpace /CS0</c> as <c>[/ICCBased …]</c> over a profile
        /// declaring this many components — ordinary Illustrator / InDesign output when it is 3.
        /// Takes precedence over <see cref="NamedColorSpace"/>.
        /// </summary>
        public int? NamedColorSpaceIccComponents;
        /// <summary>Replaces the default page content stream.</summary>
        public string? PageContent;
        /// <summary>Extra entries for the page content stream's dictionary, e.g. a <c>/Filter</c>.</summary>
        public string PageContentDictEntries = "";
        /// <summary>A second, unfiltered <c>/Contents</c> part, making <c>/Contents</c> an array.</summary>
        public string? SecondContentPart;
        /// <summary>Literal PDF for an image XObject's /ColorSpace; adds the image to the page.</summary>
        public string? ImageColorSpace;
        /// <summary>Adds an image XObject in an [/ICCBased …] space with this many components.</summary>
        public int? ImageIccComponents;
        /// <summary>Adds a form XObject with this content stream, drawn by the page.</summary>
        public string? FormContent;
        /// <summary>Literal PDF for a /ColorSpace entry in the *form's own* resources.</summary>
        public string? FormColorSpace;
        /// <summary>Adds a form XObject that draws itself — a cycle the walk must survive.</summary>
        public bool SelfReferencingForm;
        /// <summary>Adds an XObject carrying <c>/OC</c> that names an optional-content group.</summary>
        public bool OptionalContentXObject;
        /// <summary>
        /// Wraps the page content stream in <c>/OC /MC0 BDC ... EMC</c>, naming a group through a
        /// <c>/Properties</c> resource entry rather than an XObject's <c>/OC</c> dictionary entry.
        /// Shares the same OCG object <see cref="OptionalContentXObject"/> creates, so a test can
        /// set both to check a page whose <c>/OC</c> reaches the group two different ways.
        /// </summary>
        public bool MarkedContentOc;
        /// <summary>
        /// Writes <c>/MC0</c>'s <c>/Properties</c> entry as a name that resolves to nothing — the
        /// resource is simply absent — rather than to a real OCG. Only meaningful with
        /// <see cref="MarkedContentOc"/>; used to prove an unresolvable property is skipped rather
        /// than reported (§12b's fail-safe direction for this specific check).
        /// </summary>
        public bool MarkedContentOcPropertyUnresolvable;
        /// <summary>Whether that group is listed in the catalog's <c>/OCProperties /OCGs</c>.</summary>
        public bool DeclareOcgInCatalog = true;
        /// <summary>
        /// Writes the name values a check dispatches on — <c>/Subtype</c>, an action's <c>/S</c>,
        /// the output intent's <c>/S</c>, <c>/Trapped</c> — as <b>indirect references</b> to name
        /// objects. Every one is legal PDF, and a reader that does not follow the reference sees
        /// them as absent.
        /// </summary>
        public bool IndirectNameValues;
        /// <summary>Hides a non-embedded font somewhere only the nested walk reaches.</summary>
        public NestedFontHost? NestedFontIn;
        public bool PageTransparencyGroup;
        public bool XObjectSoftMask;
        public bool ExtGStateSoftMask;
        /// <summary>Puts an /ExtGState soft mask inside a form XObject's own resources.</summary>
        public bool ExtGStateSoftMaskInNestedForm;
        /// <summary>
        /// The content of the soft mask's <c>/G</c> group, for both placements above. Defaults to
        /// nothing drawn; <c>"1 0 0 rg 0 0 5 5 re f"</c> paints RGB inside the mask, which only a
        /// scan that descends the group can see.
        /// </summary>
        public string SoftMaskGroupContent = "  ";
        /// <summary>
        /// Writes the <c>/SMask</c> dictionary as an indirect object rather than inline — legal PDF,
        /// and a reader that does not resolve the reference sees no mask and so no group to descend.
        /// </summary>
        public bool IndirectSoftMaskDictionary;
        /// <summary>Puts an /SMask-bearing image inside a tiling pattern's own resources.</summary>
        public bool SoftMaskInPattern;

        /// <summary>
        /// Literal PDF for an /ExtGState <c>/ca</c> value — "0.5", or "N 0 R" written by
        /// <see cref="IndirectExtGStateAlpha"/>. Null omits the entry.
        /// </summary>
        public string? ExtGStateFillAlpha;
        /// <summary>Literal PDF for an /ExtGState <c>/CA</c> value. Null omits the entry.</summary>
        public string? ExtGStateStrokeAlpha;
        /// <summary>
        /// Literal PDF for an /ExtGState <c>/BM</c> value — "/Multiply", or "[/Multiply]" for the
        /// array form §11.6.3 permits. Null omits the entry.
        /// </summary>
        public string? ExtGStateBlendMode;
        /// <summary>
        /// Writes the <c>/ca</c> and <c>/CA</c> values as indirect references to number objects.
        /// Legal PDF, and a reader that does not follow them sees no alpha at all — which fails
        /// toward a false <i>pass</i>, the harmful direction for a print-exchange validator.
        /// </summary>
        public bool IndirectExtGStateAlpha;

        /// <summary>Literal PDF for a /Shading resource's /ColorSpace, e.g. "/DeviceRGB".</summary>
        public string? ShadingColorSpace;
        /// <summary>
        /// Literal PDF for the /ColorSpace of a shading reached through a /PatternType 2 pattern —
        /// the form `scn` selects, and the one press artwork actually uses for a gradient.
        /// </summary>
        public string? ShadingPatternColorSpace;

        /// <summary>Writes the ICC profile stream's <c>/N</c> as an indirect reference.</summary>
        public bool IndirectIccComponentCount;

        /// <summary>Adds an AcroForm whose single field carries <c>/AA</c>.</summary>
        public bool FormFieldAdditionalActions;
        /// <summary>Adds an AcroForm field whose <c>/A</c> action is of this type, e.g. "Launch".</summary>
        public string? FormFieldActionType;
        /// <summary>
        /// Also lists that field dictionary in the page's <c>/Annots</c> — ISO 32000-1 §12.7.3.1's
        /// <i>merged</i> field+widget, the one shape both the annotation loop and the field walk
        /// reach. Left <c>false</c>, the field is on no page at all, which is the shape X07 could
        /// not see.
        /// </summary>
        public bool FormFieldMergedWithWidget;
        /// <summary>
        /// Builds an AcroForm of this many field nodes that all name <b>one shared</b>
        /// <c>/Kids</c> array of that same length. <c>AcroFormFieldWalk</c> spends budget per
        /// <i>iteration</i> rather than per distinct node, so the walk costs N² edges — 450
        /// exhausts the 200 000-edge ceiling in a file of ~450 objects instead of 200 000.
        /// </summary>
        public int SharedKidsFieldCount;

        /// <summary>
        /// Extra dictionary entries for an annotation's <c>/AP /N</c> appearance stream, e.g.
        /// <c>"/Group &lt;&lt; /S /Transparency &gt;&gt;"</c>. An <b>empty</b> string adds the
        /// annotation with a plain appearance, which is the control. Null adds neither.
        /// <para>The appearance is a form XObject that no <c>/XObject</c> resource dictionary
        /// names, so it is reachable only through <c>/AP</c>.</para>
        /// </summary>
        public string? AnnotationAppearanceEntries;

        /// <summary>
        /// The <c>/F</c> flag word on that annotation. Defaults to
        /// <see cref="PdfAnnotationFlags.Print"/>, because ISO 15930-9:2020, 6.2.2 makes an
        /// annotation's appearance a <b>print element</b> only when its printable flag is set and
        /// its invisible and hidden flags are clear — so a stamp that actually reaches the press is
        /// the right default for a fixture about what a validator may judge.
        /// <para>The fixture carried no <c>/F</c> at all before, which meant every annotation test
        /// in this file was asserting against a <i>non-printing</i> annotation.</para>
        /// </summary>
        /// <para><see langword="null"/> omits <c>/F</c> altogether, which is the commonest shape in
        /// the wild and the one the default-to-zero reading exists for.</para>
        public int? AnnotationFlags = PdfAnnotationFlags.Print;

        /// <summary>
        /// The appearance stream's own content. Defaults to a DeviceCMYK fill, which X08 permits;
        /// <c>"1 0 0 rg 0 0 5 5 re f"</c> makes the same appearance an X08 violation, so the two
        /// together separate "the check is scoped out" from "the fixture was never potent".
        /// </summary>
        public string AnnotationAppearanceContent = "0 0 0 1 k 0 0 5 5 re f";

        /// <summary>
        /// Gives that appearance stream a <c>/FlateDecode</c> filter over bytes that are not Flate,
        /// so <c>GetDecodedData</c> throws and the content-level scan is genuinely partial. It is
        /// the only way to make a check skip at the <b>default</b> ceilings, which is what the
        /// plugin surface uses — <c>PdfXValidationPlugin.Validate(Stream)</c> takes no ceilings.
        /// </summary>
        public bool UndecodableAnnotationAppearance;
    }

    private static string Xmp(string version, bool asAttribute)
    {
        string body = asAttribute
            ? $@"<rdf:Description xmlns:pdfxid=""http://www.npes.org/pdfx/ns/id/"" pdfxid:GTS_PDFXVersion=""{version}"" />"
            : $@"<rdf:Description xmlns:pdfxid=""http://www.npes.org/pdfx/ns/id/"">
      <pdfxid:GTS_PDFXVersion>{version}</pdfxid:GTS_PDFXVersion>
    </rdf:Description>";

        return $@"<?xpacket begin="""" id=""W5M0MpCehiHzreSzNTczkc9d""?>
<x:xmpmeta xmlns:x=""adobe:ns:meta/"">
  <rdf:RDF xmlns:rdf=""http://www.w3.org/1999/02/22-rdf-syntax-ns#"">
    {body}
  </rdf:RDF>
</x:xmpmeta>
<?xpacket end=""w""?>";
    }

    internal static byte[] Build(XDoc d)
    {
        var f = new ObjectFile { HeaderVersion = d.HeaderVersion };
        int catalogNum = f.Reserve();
        int pagesNum   = f.Reserve();
        int pageNum    = f.Reserve();

        int contentsNum = f.Add(ObjectFile.StreamObject(
            d.PageContentDictEntries, d.PageContent ?? (d.MarkedContentOc
                ? "/OC /MC0 BDC 0 0 0 1 k 0 0 5 5 re f EMC"
                : "BT /F1 12 Tf 72 700 Td (Hi) Tj ET")));

        // A name value, written literally or as a reference to a name object (§7.3.10 allows any
        // object type to be indirect). The reference form is what proves a check resolves rather
        // than reading the raw entry, where `as PdfName` yields null and looks like "key absent".
        string Nm(string name) => d.IndirectNameValues ? $"{f.Add("/" + name)} 0 R" : "/" + name;

        // ---- fonts ----------------------------------------------------
        string? fontResource = null;
        switch (d.Font)
        {
            case FontKind.None:
                break;

            case FontKind.EmbeddedType1:
            {
                int file = f.Add(ObjectFile.StreamObject("/Subtype /Type1C", "cff-program-bytes"));
                int fd   = f.Add("<< /Type /FontDescriptor /FontName /AAAAAA+Helvetica /Flags 32" +
                                 $" /FontFile3 {file} 0 R >>");
                int font = f.Add("<< /Type /Font /Subtype /Type1 /BaseFont /AAAAAA+Helvetica" +
                                 $" /FontDescriptor {fd} 0 R >>");
                fontResource = $"/F1 {font} 0 R";
                break;
            }

            case FontKind.DescriptorWithoutFontFile:
            {
                int fd   = f.Add("<< /Type /FontDescriptor /FontName /ArialMT /Flags 32 >>");
                int font = f.Add("<< /Type /Font /Subtype /TrueType /BaseFont /ArialMT" +
                                 $" /FontDescriptor {fd} 0 R >>");
                fontResource = $"/F1 {font} 0 R";
                break;
            }

            case FontKind.Standard14NoDescriptor:
            {
                int font = f.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
                fontResource = $"/F1 {font} 0 R";
                break;
            }

            case FontKind.Type0NotEmbedded:
            {
                int fd  = f.Add("<< /Type /FontDescriptor /FontName /MS-Mincho /Flags 4 >>");
                int cid = f.Add("<< /Type /Font /Subtype /CIDFontType0 /BaseFont /MS-Mincho" +
                                $" /FontDescriptor {fd} 0 R >>");
                int font = f.Add("<< /Type /Font /Subtype /Type0 /BaseFont /MS-Mincho" +
                                 $" /Encoding /Identity-H /DescendantFonts [{cid} 0 R] >>");
                fontResource = $"/F1 {font} 0 R";
                break;
            }

            case FontKind.Type3:
            {
                int proc = f.Add(ObjectFile.StreamObject("", "0 0 0 0 0 0 d0"));
                int font = f.Add("<< /Type /Font /Subtype /Type3 /FontBBox [0 0 1 1]" +
                                 " /FontMatrix [0.001 0 0 0.001 0 0]" +
                                 $" /CharProcs << /a {proc} 0 R >>" +
                                 " /Encoding << /Type /Encoding /Differences [97 /a] >>" +
                                 " /FirstChar 97 /LastChar 97 /Widths [1000] >>");
                fontResource = $"/F1 {font} 0 R";
                break;
            }
        }

        // ---- XObjects (OPI / soft mask) --------------------------------
        var xobjects = new List<string>();
        var patterns = new List<string>();
        var annots = new List<string>();
        var extraFonts = new List<string>();
        if (d.Opi)
        {
            int form = f.Add(ObjectFile.StreamObject(
                "/Type /XObject /Subtype /Form /BBox [0 0 10 10]" +
                " /OPI << /1.3 << /Version 1.3 /F (scan.tif) >> >>", "  "));
            xobjects.Add($"/Fm0 {form} 0 R");
        }
        if (d.OpiInNestedForm)
        {
            // The OPI-bearing image sits in a form's OWN resources, reachable from nowhere on the
            // page — which is where a placed image normally lives in real prepress output.
            int img = f.Add(ObjectFile.StreamObject(
                "/Type /XObject /Subtype /Image /Width 1 /Height 1" +
                " /ColorSpace /DeviceGray /BitsPerComponent 8" +
                " /OPI << /1.3 << /Version 1.3 /F (scan.tif) >> >>", " "));
            int wrapper = f.Add(ObjectFile.StreamObject(
                "/Type /XObject /Subtype /Form /BBox [0 0 10 10]" +
                $" /Resources << /XObject << /InnerIm {img} 0 R >> >>",
                "/InnerIm Do"));
            xobjects.Add($"/FmOpi {wrapper} 0 R");
        }
        if (d.XObjectSoftMask)
        {
            int smask = f.Add(ObjectFile.StreamObject(
                "/Type /XObject /Subtype /Image /Width 1 /Height 1" +
                " /ColorSpace /DeviceGray /BitsPerComponent 8", " "));
            int image = f.Add(ObjectFile.StreamObject(
                "/Type /XObject /Subtype /Image /Width 1 /Height 1" +
                " /ColorSpace /DeviceGray /BitsPerComponent 8" +
                $" /SMask {smask} 0 R", " "));
            xobjects.Add($"/Im0 {image} 0 R");
        }

        // ---- image XObject in a specific colour space --------------------
        if (d.ImageColorSpace != null || d.ImageIccComponents != null)
        {
            string cs = d.ImageColorSpace;
            if (cs == null)
            {
                string nValue = d.IndirectIccComponentCount
                    ? $"{f.Add(d.ImageIccComponents.ToString())} 0 R"
                    : d.ImageIccComponents.ToString();
                int profile = f.Add(ObjectFile.StreamObject(
                    $"/N {nValue}", "icc-profile-bytes"));
                cs = $"[/ICCBased {profile} 0 R]";
            }
            int img = f.Add(ObjectFile.StreamObject(
                $"/Type /XObject /Subtype {Nm("Image")} /Width 1 /Height 1" +
                $" /BitsPerComponent 8 /ColorSpace {cs}", "    "));
            xobjects.Add($"/ImCs {img} 0 R");
        }

        // ---- form XObject with its own content and resources -------------
        if (d.FormContent != null || d.FormColorSpace != null)
        {
            string formResources = d.FormColorSpace != null
                ? $" /Resources << /ColorSpace << /FCS {d.FormColorSpace} >> >>"
                : "";
            int form = f.Add(ObjectFile.StreamObject(
                "/Type /XObject /Subtype /Form /BBox [0 0 10 10]" + formResources,
                d.FormContent ?? "  "));
            xobjects.Add($"/FmCs {form} 0 R");
        }

        // ---- a non-embedded font reachable ONLY through a nested construct ----
        // Each of these is deliberately absent from the page's own /Resources /Font, so a check
        // that reads the page dictionary alone cannot see it.
        if (d.NestedFontIn != null)
        {
            int nested = f.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
            string res = $" /Resources << /Font << /NF {nested} 0 R >> >>";

            switch (d.NestedFontIn)
            {
                case NestedFontHost.FormXObject:
                {
                    int form = f.Add(ObjectFile.StreamObject(
                        $"/Type /XObject /Subtype {Nm("Form")} /BBox [0 0 10 10]" + res,
                        "BT /NF 12 Tf (x) Tj ET"));
                    xobjects.Add($"/FmFont {form} 0 R");
                    break;
                }
                case NestedFontHost.TilingPattern:
                {
                    int pat = f.Add(ObjectFile.StreamObject(
                        "/Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1" +
                        " /BBox [0 0 10 10] /XStep 10 /YStep 10" + res,
                        "BT /NF 12 Tf (x) Tj ET"));
                    patterns.Add($"/P1 {pat} 0 R");
                    break;
                }
                case NestedFontHost.AnnotationAppearance:
                {
                    int ap = f.Add(ObjectFile.StreamObject(
                        "/Type /XObject /Subtype /Form /BBox [0 0 10 10]" + res,
                        "BT /NF 12 Tf (x) Tj ET"));
                    // /F carries the printable flag: ISO 15930-9:2020, 6.2.2 makes an
                    // appearance a print element only then, and a widget a validator should
                    // judge is one that prints.
                    //
                    // Every fixture annotation in this file sits at [0 0 8 8]: inside the MediaBox
                    // and strictly outside the TrimBox [9 9 603 783], so X24 (no annotation in the
                    // print area) stays silent and each test judges the rule it is about. They were
                    // [0 0 10 10] until X24 existed, which overlaps the TrimBox by one point.
                    annots.Add($"<< /Type /Annot /Subtype /Widget /Rect [0 0 8 8]" +
                               $" /F {PdfAnnotationFlags.Print} /AP << /N {ap} 0 R >> >>");
                    break;
                }
                case NestedFontHost.AnnotationAppearanceState:
                {
                    int ap = f.Add(ObjectFile.StreamObject(
                        "/Type /XObject /Subtype /Form /BBox [0 0 10 10]" + res,
                        "BT /NF 12 Tf (x) Tj ET"));
                    annots.Add($"<< /Type /Annot /Subtype /Widget /Rect [0 0 8 8]" +
                               $" /F {PdfAnnotationFlags.Print}" +
                               $" /AP << /N << /Off {ap} 0 R >> >> >>");
                    break;
                }
                case NestedFontHost.Type3CharProc:
                {
                    int proc = f.Add(ObjectFile.StreamObject("", "BT /NF 12 Tf (x) Tj ET"));
                    int t3 = f.Add($"<< /Type /Font /Subtype {Nm("Type3")} /FontBBox [0 0 10 10]" +
                                   " /FontMatrix [0.001 0 0 0.001 0 0]" +
                                   $" /CharProcs << /a {proc} 0 R >>" +
                                   " /Encoding << /Type /Encoding >> /FirstChar 97 /LastChar 97" +
                                   $" /Widths [10]{res} >>");
                    extraFonts.Add($"/T3 {t3} 0 R");
                    break;
                }
            }
        }

        // ---- transparency reachable only through a nested construct -------
        if (d.ExtGStateSoftMaskInNestedForm)
        {
            int group = f.Add(ObjectFile.StreamObject(
                "/Type /XObject /Subtype /Form /BBox [0 0 10 10]" +
                " /Group << /S /Transparency /CS /DeviceGray >>", d.SoftMaskGroupContent));
            string smaskValue = d.IndirectSoftMaskDictionary
                ? $"{f.Add($"<< /S /Luminosity /G {group} 0 R >>")} 0 R"
                : $"<< /S /Luminosity /G {group} 0 R >>";
            int wrapper = f.Add(ObjectFile.StreamObject(
                "/Type /XObject /Subtype /Form /BBox [0 0 10 10]" +
                " /Resources << /ExtGState << /GSn << /Type /ExtGState" +
                $" /SMask {smaskValue} >> >> >>",
                "/GSn gs 0 0 5 5 re f"));
            xobjects.Add($"/FmSm {wrapper} 0 R");
        }
        if (d.SoftMaskInPattern)
        {
            int smask = f.Add(ObjectFile.StreamObject(
                "/Type /XObject /Subtype /Image /Width 1 /Height 1" +
                " /ColorSpace /DeviceGray /BitsPerComponent 8", " "));
            int img = f.Add(ObjectFile.StreamObject(
                "/Type /XObject /Subtype /Image /Width 1 /Height 1" +
                " /ColorSpace /DeviceGray /BitsPerComponent 8" +
                $" /SMask {smask} 0 R", " "));
            int pat = f.Add(ObjectFile.StreamObject(
                "/Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1" +
                " /BBox [0 0 10 10] /XStep 10 /YStep 10" +
                $" /Resources << /XObject << /PatIm {img} 0 R >> >>",
                "/PatIm Do"));
            patterns.Add($"/PSm {pat} 0 R");
        }

        // ---- annotation-level actions (X07) ------------------------------
        if (d.AnnotationAdditionalActions)
            annots.Add("<< /Type /Annot /Subtype /Widget /Rect [0 0 8 8]" +
                       " /AA << /E << /S /JavaScript /JS (enter()) >> >> >>");
        if (d.AnnotationActionType != null)
            annots.Add("<< /Type /Annot /Subtype /Link /Rect [0 0 8 8]" +
                       $" /A << /S /{d.AnnotationActionType} >> >>");

        // ---- an annotation whose /AP /N carries entries of its own (X09) ---
        // The appearance is reachable only through /AP: nothing in /Resources /XObject names it,
        // which is why the resource walk could not see its own /Group or /SMask.
        if (d.AnnotationAppearanceEntries != null)
        {
            string filter = d.UndecodableAnnotationAppearance ? " /Filter /FlateDecode" : "";
            int ap = f.Add(ObjectFile.StreamObject(
                "/Type /XObject /Subtype /Form /BBox [0 0 10 10]" + filter +
                (d.AnnotationAppearanceEntries.Length > 0 ? " " + d.AnnotationAppearanceEntries : ""),
                // Not a Flate stream. With the filter above, GetDecodedData throws and the scan
                // reports itself incomplete instead of quietly examining nothing.
                d.AnnotationAppearanceContent));
            string flags = d.AnnotationFlags.HasValue ? $" /F {d.AnnotationFlags.Value}" : "";
            annots.Add($"<< /Type /Annot /Subtype /Stamp /Rect [0 0 8 8]" +
                       $"{flags} /AP << /N {ap} 0 R >> >>");
        }

        // ---- AcroForm field actions (X07) --------------------------------
        // The field is deliberately NOT on the page unless FormFieldMergedWithWidget is set: a
        // field whose widgets are separate /Kids, or that has no widget at all, is reachable only
        // from /AcroForm /Fields — and that is where /AA keystroke and format scripts live.
        int acroFormNum = 0;
        if (d.FormFieldAdditionalActions || d.FormFieldActionType != null)
        {
            var field = new StringBuilder("<< /FT /Tx /T (Field1)");
            if (d.FormFieldMergedWithWidget)
                field.Append(" /Type /Annot /Subtype /Widget /Rect [0 0 8 8]");
            if (d.FormFieldAdditionalActions)
                field.Append(" /AA << /K << /S /JavaScript /JS (keystroke()) >> >>");
            if (d.FormFieldActionType != null)
                field.Append($" /A << /S /{d.FormFieldActionType} >>");
            field.Append(" >>");

            int fieldNum = f.Add(field.ToString());
            acroFormNum = f.Add($"<< /Fields [{fieldNum} 0 R] >>");
            if (d.FormFieldMergedWithWidget) annots.Add($"{fieldNum} 0 R");
        }
        else if (d.SharedKidsFieldCount > 0)
        {
            // Adversarial input for the field walk's node budget. The kids are bare widget
            // annotations — no /FT, /T or /Kids — so IsFieldNode says no and the inner loop never
            // breaks early, which is what makes the cost N × N rather than N.
            int widget = f.Add("<< /Type /Annot /Subtype /Widget /Rect [0 0 1 1] >>");

            var kids = new StringBuilder("[");
            for (int i = 0; i < d.SharedKidsFieldCount; i++) kids.Append(widget).Append(" 0 R ");
            int kidsArray = f.Add(kids.Append(']').ToString());

            var fields = new StringBuilder("[");
            for (int i = 0; i < d.SharedKidsFieldCount; i++)
                fields.Append(f.Add($"<< /T (F{i}) /Kids {kidsArray} 0 R >>")).Append(" 0 R ");
            acroFormNum = f.Add($"<< /Fields {fields.Append(']')} >>");
        }

        // ---- optional content (X13) --------------------------------------
        int ocgNumber = 0;
        if (d.OptionalContentXObject || d.MarkedContentOc)
        {
            ocgNumber = f.Add("<< /Type /OCG /Name (Layer 1) >>");
        }
        if (d.OptionalContentXObject)
        {
            int ocForm = f.Add(ObjectFile.StreamObject(
                "/Type /XObject /Subtype /Form /BBox [0 0 10 10]" + $" /OC {ocgNumber} 0 R", "  "));
            xobjects.Add($"/FmOc {ocForm} 0 R");
        }

        // ---- BDC /OC (marked-content optional content, X13) --------------
        string? propertiesResource = null;
        if (d.MarkedContentOc && !d.MarkedContentOcPropertyUnresolvable)
            propertiesResource = $"/MC0 {ocgNumber} 0 R";

        // ---- a form XObject that draws itself ----------------------------
        if (d.SelfReferencingForm)
        {
            int self = f.Reserve();
            f.Set(self, ObjectFile.StreamObject(
                "/Type /XObject /Subtype /Form /BBox [0 0 10 10]" +
                $" /Resources << /XObject << /Loop {self} 0 R >> >>",
                "/Loop Do 1 0 0 rg 0 0 5 5 re f"));
            xobjects.Add($"/Loop {self} 0 R");
        }

        // ---- ExtGState soft mask ---------------------------------------
        // The luminosity group is deliberately NOT reachable from the page's /XObject
        // resources, so only the /ExtGState path can observe this transparency.
        string? extGStateResource = null;
        if (d.ExtGStateSoftMask)
        {
            int group = f.Add(ObjectFile.StreamObject(
                "/Type /XObject /Subtype /Form /BBox [0 0 10 10]" +
                " /Group << /S /Transparency /CS /DeviceGray >>", d.SoftMaskGroupContent));
            string smaskValue = d.IndirectSoftMaskDictionary
                ? $"{f.Add($"<< /S /Luminosity /G {group} 0 R >>")} 0 R"
                : $"<< /S /Luminosity /G {group} 0 R >>";
            extGStateResource = $"/GS0 << /Type /ExtGState /SMask {smaskValue} >>";
        }

        // The other three arms of CheckTransparency — constant alpha and blend mode — which
        // shipped with no coverage at all. Each value can be written indirectly, which is what
        // exposes a read that does not resolve.
        if (d.ExtGStateFillAlpha != null || d.ExtGStateStrokeAlpha != null || d.ExtGStateBlendMode != null)
        {
            string Ref(string literal) =>
                d.IndirectExtGStateAlpha ? $"{f.Add(literal)} 0 R" : literal;

            var entries = new StringBuilder("/GS1 << /Type /ExtGState");
            if (d.ExtGStateFillAlpha != null)
                entries.Append(" /ca ").Append(Ref(d.ExtGStateFillAlpha));
            if (d.ExtGStateStrokeAlpha != null)
                entries.Append(" /CA ").Append(Ref(d.ExtGStateStrokeAlpha));
            if (d.ExtGStateBlendMode != null)
                entries.Append(" /BM ").Append(d.ExtGStateBlendMode);
            entries.Append(" >>");

            extGStateResource = extGStateResource == null
                ? entries.ToString()
                : extGStateResource + " " + entries;
        }

        // ---- shadings ---------------------------------------------------
        // A shading carries its own /ColorSpace and is reached either as a /Shading resource
        // (`sh`) or through a /PatternType 2 pattern (`scn`). X08 read neither.
        string? shadingResource = null;
        if (d.ShadingColorSpace != null)
            shadingResource =
                $"/Sh0 << /ShadingType 2 /ColorSpace {d.ShadingColorSpace} " +
                "/Coords [0 0 1 0] /Function << /FunctionType 2 /Domain [0 1] /N 1 >> >>";

        if (d.ShadingPatternColorSpace != null)
        {
            int sh = f.Add(
                $"<< /ShadingType 2 /ColorSpace {d.ShadingPatternColorSpace} " +
                "/Coords [0 0 1 0] /Function << /FunctionType 2 /Domain [0 1] /N 1 >> >>");
            patterns.Add($"/P0 {f.Add($"<< /PatternType 2 /Shading {sh} 0 R >>")} 0 R");
        }

        // ---- output intent ---------------------------------------------
        int outputIntentNum = 0;
        int secondOutputIntentNum = 0;
        if (d.OutputIntentSubtype != null)
        {
            string conditionId = d.OutputConditionIdentifier == null
                ? ""
                : $" /OutputConditionIdentifier ({d.OutputConditionIdentifier})";
            string destProfile = "";
            if (!d.OutputIntentWithoutProfile)
            {
                int profile = f.Add(ObjectFile.StreamObject(
                    $"/N {d.OutputIntentProfileComponents}", "icc-profile-bytes"));
                destProfile = $" /DestOutputProfile {profile} 0 R";
            }

            outputIntentNum = f.Add($"<< /Type /OutputIntent /S {Nm(d.OutputIntentSubtype)}" +
                                    conditionId +
                                    " /OutputCondition (none)" +
                                    " /RegistryName (http://www.color.org)" +
                                    destProfile + " >>");

            if (d.DuplicateOutputIntent)
            {
                int profile2 = f.Add(ObjectFile.StreamObject("/N 4", "other-profile-bytes"));
                secondOutputIntentNum = f.Add(
                    $"<< /Type /OutputIntent /S /{d.OutputIntentSubtype}" +
                    " /OutputConditionIdentifier (FOGRA39)" +
                    " /OutputCondition (none)" +
                    " /RegistryName (http://www.color.org)" +
                    $" /DestOutputProfile {profile2} 0 R >>");
            }
        }

        // ---- XMP --------------------------------------------------------
        int metadataNum = 0;
        if (d.MetadataNotAStream)
        {
            metadataNum = f.Add("<< /Type /Metadata /Subtype /XML >>");
        }
        else
        {
            string? xmp = d.RawXmp ?? (d.XmpVersion == null ? null : Xmp(d.XmpVersion, d.XmpAsAttribute));
            if (xmp != null)
                metadataNum = f.Add(ObjectFile.StreamObject("/Type /Metadata /Subtype /XML", xmp));
        }

        // ---- catalog -----------------------------------------------------
        var catalog = new StringBuilder("<< /Type /Catalog /Pages ").Append(pagesNum).Append(" 0 R");
        if (metadataNum > 0) catalog.Append(" /Metadata ").Append(metadataNum).Append(" 0 R");
        if (outputIntentNum > 0 && !d.OutputIntentOnPage)
        {
            catalog.Append(" /OutputIntents [").Append(outputIntentNum).Append(" 0 R");
            if (secondOutputIntentNum > 0) catalog.Append(' ').Append(secondOutputIntentNum).Append(" 0 R");
            catalog.Append(']');
        }
        if (ocgNumber > 0 && d.DeclareOcgInCatalog)
            catalog.Append($" /OCProperties << /OCGs [{ocgNumber} 0 R] /D << /Order [] >> >>");
        if (d.CatalogAdditionalActions)
            catalog.Append(" /AA << /WC << /S /JavaScript /JS (close()) >> >>");
        if (d.DocumentJavaScript)
        {
            int js = f.Add("<< /S /JavaScript /JS (app.alert\\(1\\)) >>");
            int tree = f.Add($"<< /Names [(Script1) {js} 0 R] >>");
            catalog.Append(" /Names << /JavaScript ").Append(tree).Append(" 0 R >>");
        }
        if (d.OpenActionType != null)
            catalog.Append($" /OpenAction << /S {Nm(d.OpenActionType)} >>");
        if (acroFormNum > 0)
            catalog.Append(" /AcroForm ").Append(acroFormNum).Append(" 0 R");
        catalog.Append(" >>");
        f.Set(catalogNum, catalog.ToString());

        f.Set(pagesNum, $"<< /Type /Pages /Kids [{pageNum} 0 R] /Count 1 >>");

        // ---- page ---------------------------------------------------------
        var resources = new StringBuilder("<<");
        var fontEntries = new List<string>();
        if (fontResource != null) fontEntries.Add(fontResource);
        fontEntries.AddRange(extraFonts);
        if (fontEntries.Count > 0)
            resources.Append(" /Font << ").Append(string.Join(" ", fontEntries)).Append(" >>");
        if (xobjects.Count > 0)
            resources.Append(" /XObject << ").Append(string.Join(" ", xobjects)).Append(" >>");
        if (patterns.Count > 0)
            resources.Append(" /Pattern << ").Append(string.Join(" ", patterns)).Append(" >>");
        if (propertiesResource != null)
            resources.Append(" /Properties << ").Append(propertiesResource).Append(" >>");
        if (d.NamedColorSpaceIccComponents != null)
        {
            int profile = f.Add(ObjectFile.StreamObject(
                $"/N {d.NamedColorSpaceIccComponents}", "icc-profile-bytes"));
            resources.Append(" /ColorSpace << /CS0 [/ICCBased ").Append(profile).Append(" 0 R] >>");
        }
        else if (d.NamedColorSpace != null)
            resources.Append(" /ColorSpace << /CS0 ").Append(d.NamedColorSpace).Append(" >>");
        if (shadingResource != null)
            resources.Append(" /Shading << ").Append(shadingResource).Append(" >>");
        if (extGStateResource != null)
            resources.Append(" /ExtGState << ").Append(extGStateResource).Append(" >>");
        resources.Append(" >>");

        var page = new StringBuilder("<< /Type /Page /Parent ").Append(pagesNum).Append(" 0 R")
            .Append(" /MediaBox [0 0 612 792]")
            .Append(" /Contents ").Append(d.SecondContentPart == null
                ? $"{contentsNum} 0 R"
                : $"[{contentsNum} 0 R {f.Add(ObjectFile.StreamObject("", d.SecondContentPart))} 0 R]")
            .Append(" /Resources ").Append(resources);

        switch (d.Boxes)
        {
            case BoxKind.Trim: page.Append(" /TrimBox [9 9 603 783]"); break;
            case BoxKind.Art:  page.Append(" /ArtBox [9 9 603 783]");  break;
            case BoxKind.Both: page.Append(" /TrimBox [9 9 603 783] /ArtBox [18 18 594 774]"); break;
            case BoxKind.None: break;
        }
        if (annots.Count > 0)
            page.Append(" /Annots [").Append(string.Join(" ", annots)).Append("]");
        if (d.PageAdditionalActions)
            page.Append(" /AA << /O << /S /JavaScript /JS (open()) >> >>");
        if (d.PageTransparencyGroup)
            page.Append(" /Group << /S /Transparency /CS /DeviceCMYK /I true >>");
        if (outputIntentNum > 0 && d.OutputIntentOnPage)
            page.Append(" /OutputIntents [").Append(outputIntentNum).Append(" 0 R]");
        page.Append(" >>");
        f.Set(pageNum, page.ToString());

        // ---- Info /Trapped (X10) --------------------------------------
        // ISO 15930-1 §6.2.2 requires an explicit /True or /False, so the conforming default
        // supplies one. `Trapped = null` omits the Info dictionary entirely, which is the
        // "absent" case; any other string is written verbatim so /Unknown can be exercised.
        if (d.Trapped != null)
            f.InfoNumber = f.Add($"<< /Trapped {Nm(d.Trapped)} >>");

        return f.Build(catalogNum);
    }

    private static PdfXValidationResult Validate(
        XDoc doc, PdfXConformanceLevel level = PdfXConformanceLevel.X1a)
    {
        using var ms = new MemoryStream(Build(doc));
        return PdfXValidator.Validate(ms, level);
    }

    private static string Describe(PdfXValidationResult r)
        => r.Violations.Count == 0 ? "(none)" : string.Join("; ", r.Violations);

    // ------------------------------------------------------------------
    //  Control — the clean document must pass, or every mutation below is vacuous
    // ------------------------------------------------------------------

    [Fact]
    public void CleanX1aDocument_HasNoViolations()
    {
        var result = Validate(new XDoc());
        Assert.True(result.IsConforming, Describe(result));
        Assert.Empty(result.Violations);
    }

    [Fact]
    public void CleanDocument_ValidatesAtEveryLevel()
    {
        foreach (var level in new[]
                 {
                     PdfXConformanceLevel.X1a, PdfXConformanceLevel.X3, PdfXConformanceLevel.X4
                 })
        {
            string version = level == PdfXConformanceLevel.X1a ? "PDF/X-1a:2001"
                           : level == PdfXConformanceLevel.X3  ? "PDF/X-3:2003"
                           : "PDF/X-4";
            var result = Validate(new XDoc { XmpVersion = version }, level);
            Assert.True(result.IsConforming, $"{level}: {Describe(result)}");
            Assert.Equal(level, result.Level);
        }
    }

    // ------------------------------------------------------------------
    //  X02 — /OutputIntents with /S /GTS_PDFX
    // ------------------------------------------------------------------

    [Fact]
    public void MissingOutputIntent_ReportsX02()
    {
        var result = Validate(new XDoc { OutputIntentSubtype = null });
        Assert.Contains(result.Violations, v => v.Code == "X02");
    }

    [Fact]
    public void OutputIntentWithNonPdfxSubtype_ReportsX02()
    {
        // A PDF/A output intent is not a PDF/X one.
        var result = Validate(new XDoc { OutputIntentSubtype = "GTS_PDFA1" });
        Assert.Contains(result.Violations, v => v.Code == "X02");
    }

    [Fact]
    public void PageLevelOutputIntent_DoesNotSatisfyX02()
    {
        // A page's /OutputIntents is PDF 2.0's (ISO 32000-2 Table 31); PDF/X-1a, -3 and -4 predate it,
        // and X-4 forbids it (X25) — the printing condition is the document's.
        var result = Validate(new XDoc { OutputIntentOnPage = true });
        Assert.Contains(result.Violations, v => v.Code == "X02");
    }

    // ------------------------------------------------------------------
    //  X11 — the output intent's own contents (ISO 15930-1 §6.2.3)
    // ------------------------------------------------------------------
    //
    //  X02 only ever asked whether an intent with /S /GTS_PDFX existed. An intent naming no
    //  printing condition, embedding no profile, or contradicted by a second intent satisfied it.

    [Fact]
    public void OutputIntentWithoutDestOutputProfile_ReportsX11OnX1a()
    {
        var result = Validate(new XDoc { OutputIntentWithoutProfile = true });
        Assert.Contains(result.Violations, v => v.Code == "X11");
    }

    [Fact]
    public void OutputIntentWithoutConditionIdentifier_ReportsX11()
    {
        var result = Validate(new XDoc { OutputConditionIdentifier = null });
        Assert.Contains(result.Violations, v => v.Code == "X11");
    }

    [Fact]
    public void TwoConflictingPdfxOutputIntents_ReportX11()
    {
        var result = Validate(new XDoc { DuplicateOutputIntent = true });
        Assert.Contains(result.Violations,
            v => v.Code == "X11" && v.Message.Contains("exactly one"));
    }

    [Fact]
    public void ConformingOutputIntent_DoesNotReportX11()
    {
        // The control — without it every assertion above would also pass if X11 simply always fired.
        var result = Validate(new XDoc());
        Assert.DoesNotContain(result.Violations, v => v.Code == "X11");
    }

    [Fact]
    public void MissingProfileButRegisteredCondition_IsAcceptedOutsideX1a()
    {
        // X-1a admits no substitute for the embedded profile; the later parts accept a registered
        // characterisation name. Asserted as the DIFFERENCE between levels on one fixture, so it
        // pins the level-dependence rather than just "some level passes".
        var doc = new XDoc { OutputIntentWithoutProfile = true, XmpVersion = "PDF/X-3:2002" };

        var x1a = Validate(doc, PdfXConformanceLevel.X1a);
        var x3  = Validate(doc, PdfXConformanceLevel.X3);

        Assert.Contains(x1a.Violations, v => v.Code == "X11");
        Assert.DoesNotContain(x3.Violations, v => v.Code == "X11");
    }

    // ------------------------------------------------------------------
    //  X04 — fonts reachable only through a nested construct
    // ------------------------------------------------------------------
    //
    //  Until 2026-08-08 X04 read `page.Resources` alone, so a non-embedded font used only inside
    //  a form XObject, a tiling pattern, a Type 3 glyph procedure or an annotation appearance
    //  stream was never examined and the document validated clean. Each fixture below puts the
    //  font ONLY in that place — the page's own /Font resource holds the conforming embedded one.

    [Theory]
    [InlineData(NestedFontHost.FormXObject)]
    [InlineData(NestedFontHost.TilingPattern)]
    [InlineData(NestedFontHost.AnnotationAppearance)]
    [InlineData(NestedFontHost.AnnotationAppearanceState)]
    [InlineData(NestedFontHost.Type3CharProc)]
    public void NonEmbeddedFontInANestedConstruct_ReportsX04(NestedFontHost host)
    {
        var result = Validate(new XDoc { NestedFontIn = host });

        Assert.Contains(result.Violations, v => v.Code == "X04");
        Assert.Contains(result.Violations,
            v => v.Code == "X04" && v.Message.Contains("Helvetica"));
    }

    [Fact]
    public void EmbeddedFontEverywhere_DoesNotReportX04()
    {
        // The control. Without it the theory above would also pass if X04 had simply become
        // "always report a violation" — the page's own font is embedded and must stay clean.
        var result = Validate(new XDoc());
        Assert.DoesNotContain(result.Violations, v => v.Code == "X04");
    }

    [Fact]
    public void FontReferencedFromTwoPlaces_IsReportedOnce()
    {
        // One font object reachable through both the page and a form XObject. Walking every
        // reachable resource dictionary means visiting it twice; the report must not say so.
        var result = Validate(new XDoc
        {
            Font = FontKind.Standard14NoDescriptor,
            FormContent = "BT /F1 12 Tf (x) Tj ET",
        });

        var x04 = Assert.Single(result.Violations, v => v.Code == "X04");
        int occurrences = x04.Message.Split("Helvetica").Length - 1;
        Assert.Equal(1, occurrences);
    }

    // ------------------------------------------------------------------
    //  X10 — /Trapped (ISO 15930-1 §6.2.2)
    // ------------------------------------------------------------------

    [Fact]
    public void MissingTrapped_ReportsX10()
    {
        var result = Validate(new XDoc { Trapped = null });
        Assert.Contains(result.Violations, v => v.Code == "X10");
    }

    [Fact]
    public void TrappedUnknown_ReportsX10()
    {
        // /Unknown is the PDF default and is exactly what PDF/X refuses: "nobody recorded
        // whether this file is trapped" is not an answer a print exchange can accept.
        var result = Validate(new XDoc { Trapped = "Unknown" });
        Assert.Contains(result.Violations, v => v.Code == "X10");
    }

    [Theory]
    [InlineData("True")]
    [InlineData("False")]
    public void TrappedTrueOrFalse_SatisfiesX10(string value)
    {
        var result = Validate(new XDoc { Trapped = value });
        Assert.DoesNotContain(result.Violations, v => v.Code == "X10");
    }

    // ------------------------------------------------------------------
    //  X03 — XMP GTS_PDFXVersion
    // ------------------------------------------------------------------

    [Fact]
    public void MissingXmpMetadata_ReportsX03()
    {
        var result = Validate(new XDoc { XmpVersion = null });
        Assert.Contains(result.Violations, v => v.Code == "X03");
    }

    [Fact]
    public void EmptyXmpPacket_ReportsX03()
    {
        var result = Validate(new XDoc { RawXmp = "" });
        Assert.Contains(result.Violations, v => v.Code == "X03");
    }

    [Fact]
    public void XmpWithoutGtsPdfxVersion_ReportsX03()
    {
        string xmp = @"<?xpacket begin="""" id=""W5M0MpCehiHzreSzNTczkc9d""?>
<x:xmpmeta xmlns:x=""adobe:ns:meta/"">
  <rdf:RDF xmlns:rdf=""http://www.w3.org/1999/02/22-rdf-syntax-ns#"">
    <rdf:Description xmlns:dc=""http://purl.org/dc/elements/1.1/"">
      <dc:title>Untitled</dc:title>
    </rdf:Description>
  </rdf:RDF>
</x:xmpmeta>
<?xpacket end=""w""?>";
        var result = Validate(new XDoc { RawXmp = xmp });
        Assert.Contains(result.Violations, v => v.Code == "X03");
    }

    [Fact]
    public void VersionStringPresentOnlyInTitle_ReportsX03()
    {
        // Regression: substring-matching the whole packet would accept this document
        // even though GTS_PDFXVersion is never declared.
        string xmp = @"<?xpacket begin="""" id=""W5M0MpCehiHzreSzNTczkc9d""?>
<x:xmpmeta xmlns:x=""adobe:ns:meta/"">
  <rdf:RDF xmlns:rdf=""http://www.w3.org/1999/02/22-rdf-syntax-ns#"">
    <rdf:Description xmlns:dc=""http://purl.org/dc/elements/1.1/"">
      <dc:title>How to make a PDF/X-1a:2001 file</dc:title>
    </rdf:Description>
  </rdf:RDF>
</x:xmpmeta>
<?xpacket end=""w""?>";
        var result = Validate(new XDoc { RawXmp = xmp });
        Assert.Contains(result.Violations, v => v.Code == "X03");
    }

    [Fact]
    public void XmpDeclaringDifferentLevel_ReportsX03()
    {
        var result = Validate(new XDoc { XmpVersion = "PDF/X-3:2003" }, PdfXConformanceLevel.X1a);
        Assert.Contains(result.Violations, v => v.Code == "X03");
    }

    [Fact]
    public void GtsPdfxVersionAsAttribute_IsAccepted()
    {
        var result = Validate(new XDoc { XmpAsAttribute = true });
        Assert.DoesNotContain(result.Violations, v => v.Code == "X03");
    }

    [Fact]
    public void X4_AcceptsProfileSuffixedVersion()
    {
        // "PDF/X-4p" — externally-referenced ICC profile variant of PDF/X-4.
        var result = Validate(new XDoc { XmpVersion = "PDF/X-4p" }, PdfXConformanceLevel.X4);
        Assert.DoesNotContain(result.Violations, v => v.Code == "X03");
    }

    // ------------------------------------------------------------------
    //  X04 — font embedding
    // ------------------------------------------------------------------

    [Fact]
    public void FontDescriptorWithoutFontFile_ReportsX04()
    {
        var result = Validate(new XDoc { Font = FontKind.DescriptorWithoutFontFile });
        var x04 = Assert.Single(result.Violations, v => v.Code == "X04");
        Assert.Contains("ArialMT", x04.Message);
    }

    [Fact]
    public void Type0DescendantWithoutFontFile_ReportsX04()
    {
        var result = Validate(new XDoc { Font = FontKind.Type0NotEmbedded });
        Assert.Contains(result.Violations, v => v.Code == "X04");
    }

    [Fact]
    public void Standard14FontWithoutDescriptor_ReportsX04()
    {
        // ISO 15930 requires *all* fonts to be embedded — there is no standard-14
        // exemption as there is in plain ISO 32000-1.
        var result = Validate(new XDoc { Font = FontKind.Standard14NoDescriptor });
        var x04 = Assert.Single(result.Violations, v => v.Code == "X04");
        Assert.Contains("Helvetica", x04.Message);
    }

    [Fact]
    public void Type3Font_DoesNotReportX04()
    {
        // Type3 glyphs are content streams; there is no font program to embed.
        var result = Validate(new XDoc { Font = FontKind.Type3 });
        Assert.DoesNotContain(result.Violations, v => v.Code == "X04");
    }

    [Fact]
    public void PageWithoutFontResources_DoesNotReportX04()
    {
        var result = Validate(new XDoc { Font = FontKind.None });
        Assert.DoesNotContain(result.Violations, v => v.Code == "X04");
    }

    // ------------------------------------------------------------------
    //  X05 — /TrimBox or /ArtBox
    // ------------------------------------------------------------------

    [Fact]
    public void PageWithoutTrimOrArtBox_ReportsX05()
    {
        var result = Validate(new XDoc { Boxes = BoxKind.None });
        var x05 = Assert.Single(result.Violations, v => v.Code == "X05");
        Assert.Contains("1", x05.Message);   // names the offending page
    }

    [Fact]
    public void PageWithArtBoxOnly_DoesNotReportX05()
    {
        var result = Validate(new XDoc { Boxes = BoxKind.Art });
        Assert.DoesNotContain(result.Violations, v => v.Code == "X05");
    }

    [Fact]
    public void PageWithBothTrimAndArtBox_ReportsX05()
    {
        // ISO 15930-1 §6.2.4: a page shall contain a TrimBox or an ArtBox, but not both.
        var result = Validate(new XDoc { Boxes = BoxKind.Both });
        Assert.Contains(result.Violations, v => v.Code == "X05");
    }

    // ------------------------------------------------------------------
    //  X06 — /OPI
    // ------------------------------------------------------------------

    [Fact]
    public void XObjectWithOpiDictionary_ReportsX06()
    {
        var result = Validate(new XDoc { Opi = true });
        Assert.Contains(result.Violations, v => v.Code == "X06");
    }

    [Fact]
    public void OpiInsideANestedForm_ReportsX06()
    {
        // Until 2026-08-08 X06 read the page's own /XObject dictionary, so an /OPI on an image
        // inside a form XObject — where a placed image normally lives in prepress output — was
        // never seen and the file validated clean.
        var result = Validate(new XDoc { OpiInNestedForm = true });
        Assert.Contains(result.Violations, v => v.Code == "X06");
    }

    [Fact]
    public void NoOpiAnywhere_DoesNotReportX06()
    {
        // The control: the nested walk must not start reporting X06 for ordinary form XObjects.
        var result = Validate(new XDoc { FormContent = "0 0 0 1 k 0 0 5 5 re f" });
        Assert.DoesNotContain(result.Violations, v => v.Code == "X06");
    }

    // ------------------------------------------------------------------
    //  X07 — /AA additional actions
    // ------------------------------------------------------------------

    [Fact]
    public void CatalogAdditionalActions_ReportsX07()
    {
        var result = Validate(new XDoc { CatalogAdditionalActions = true });
        // The fixture's /AA holds a script, which is a second X07 for its type (X07 reads every
        // action since 2026-09-24): the /AA entry is still exactly one.
        var x07 = Assert.Single(result.Violations, v => v.Code == "X07" && v.Message.Contains("additional-actions"));
        Assert.Contains("catalog", x07.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PageAdditionalActions_ReportsX07()
    {
        var result = Validate(new XDoc { PageAdditionalActions = true });
        var x07 = Assert.Single(result.Violations, v => v.Code == "X07" && v.Message.Contains("additional-actions"));
        Assert.Contains("page", x07.Message, StringComparison.OrdinalIgnoreCase);
    }

    // X07 reached only the catalog and page dictionaries until 2026-08-08. The four cases below
    // are where actions actually live in a real file.

    [Fact]
    public void AnnotationAdditionalActions_ReportsX07()
    {
        var result = Validate(new XDoc { AnnotationAdditionalActions = true });
        var x07 = Assert.Single(result.Violations, v => v.Code == "X07" && v.Message.Contains("additional-actions"));
        Assert.Contains("annotation", x07.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("JavaScript")]
    [InlineData("Launch")]
    [InlineData("Sound")]
    [InlineData("Movie")]
    [InlineData("ResetForm")]
    [InlineData("ImportData")]
    public void ForbiddenAnnotationActionType_ReportsX07(string type)
    {
        var result = Validate(new XDoc { AnnotationActionType = type });
        Assert.Contains(result.Violations,
            v => v.Code == "X07" && v.Message.Contains(type));
    }

    [Theory]
    [InlineData("GoTo")]
    [InlineData("URI")]
    public void PermittedAnnotationActionType_DoesNotReportX07(string type)
    {
        // The control, and the one that keeps the theory above from degenerating into
        // "any /A action is a violation". A link that navigates is not a scripting hazard.
        var result = Validate(new XDoc { AnnotationActionType = type });
        Assert.DoesNotContain(result.Violations, v => v.Code == "X07");
    }

    [Fact]
    public void DocumentLevelJavaScriptNameTree_ReportsX07()
    {
        // /Names /JavaScript is where document-level script actually lives — never in /AA,
        // which is the only place the check used to look.
        var result = Validate(new XDoc { DocumentJavaScript = true });
        Assert.Contains(result.Violations,
            v => v.Code == "X07" && v.Message.Contains("JavaScript"));
    }

    [Fact]
    public void OpenActionRunningJavaScript_ReportsX07()
    {
        var result = Validate(new XDoc { OpenActionType = "JavaScript" });
        Assert.Contains(result.Violations, v => v.Code == "X07");
    }

    [Fact]
    public void NoActionsAnywhere_DoesNotReportX07()
    {
        var result = Validate(new XDoc());
        Assert.DoesNotContain(result.Violations, v => v.Code == "X07");
    }

    // ------------------------------------------------------------------
    //  X07 across the AcroForm field tree
    // ------------------------------------------------------------------
    //
    //  The /Annots loop above reaches a field only when the field dictionary IS the widget
    //  annotation. ISO 32000-1 §12.7.3.1 permits the other shape too — widgets as separate /Kids —
    //  and a field with no widget at all (a hidden calculation field) appears on no page in either.
    //  /AA /K keystroke and /F format scripts live on the field, so all of those were invisible
    //  while GAPS.md §12a claimed form-field coverage.

    [Fact]
    public void FormFieldAdditionalActions_OnAFieldNoPageLists_ReportsX07()
    {
        // The field is in /AcroForm /Fields and in no /Annots array anywhere, so nothing but the
        // field walk can reach it. Before that walk existed this document validated clean.
        var result = Validate(new XDoc { FormFieldAdditionalActions = true });

        var x07 = Assert.Single(result.Violations, v => v.Code == "X07" && v.Message.Contains("additional-actions"));
        Assert.Contains("form field", x07.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("JavaScript")]
    [InlineData("Launch")]
    [InlineData("ImportData")]
    public void ForbiddenFormFieldActionType_ReportsX07(string type)
    {
        var result = Validate(new XDoc { FormFieldActionType = type });

        Assert.Contains(result.Violations,
            v => v.Code == "X07" && v.Message.Contains(type) && v.Message.Contains("form-field"));
    }

    [Fact]
    public void PermittedFormFieldActionType_DoesNotReportX07()
    {
        // The control that stops the theory above from degenerating into "any field /A is a
        // violation" — a field that navigates on click is not a scripting hazard.
        var result = Validate(new XDoc { FormFieldActionType = "GoTo" });

        Assert.DoesNotContain(result.Violations, v => v.Code == "X07");
    }

    [Fact]
    public void MergedFieldAndWidget_ReportsX07Once()
    {
        // The merged shape — one dictionary that is both the field and its widget annotation — is
        // reached by BOTH the /Annots loop and the field walk. Without the shared reported-set the
        // two produce two identical entries for one defect, which is also what would silently
        // break every `Assert.Single(..., v => v.Code == "X07")` in this file.
        var result = Validate(new XDoc
        {
            FormFieldAdditionalActions = true,
            FormFieldActionType        = "JavaScript",
            FormFieldMergedWithWidget  = true,
        });

        Assert.Equal(2, result.Violations.Count);   // one for /AA, one for the /JavaScript /A
        var aa = Assert.Single(result.Violations,
            v => v.Code == "X07" && v.Message.Contains("additional-actions"));
        // Entries with one message are counted in one (since 2026-09-24), so without the shared
        // reported-set the /AA would still be a single entry, and say "2 found".
        Assert.Contains("; found on", aa.Message, StringComparison.Ordinal);
        Assert.Single(result.Violations,
            v => v.Code == "X07" && v.Message.Contains("JavaScript"));
    }

    [Fact]
    public void AcroFormWithNoActions_DoesNotReportX07()
    {
        // The control for the whole walk: an ordinary form must not become non-conforming just
        // because it has fields. FormFieldActionType = "GoTo" is the only reason a field exists
        // here at all — PermittedFormFieldActionType_DoesNotReportX07 asserts the same on the
        // action; this one asserts nothing else about the document changed either.
        var result = Validate(new XDoc { FormFieldActionType = "GoTo" });

        Assert.True(result.IsConforming, Describe(result));
        Assert.Empty(result.Violations);
    }

    /// <summary>
    /// A field walk that ran out of budget examined some fields and not others, which is the
    /// "check did not run in full" state X99 exists for. Reporting nothing would be
    /// indistinguishable from a form that carries no actions — the false-pass direction, and for
    /// a print-exchange standard the one that reaches a RIP.
    /// </summary>
    /// <remarks>
    /// The fixture is adversarial rather than large: 450 field nodes sharing one 450-entry
    /// <c>/Kids</c> array cost 202 950 edges against a 200 000 ceiling, because the walk spends
    /// budget per iteration and a visited set does nothing about the cost of one expansion.
    /// Asserting the guard's own evidence — the X99 entry — never a wall clock.
    /// </remarks>
    [Fact]
    public void FormFieldWalk_BudgetExhausted_ReportsX99RatherThanPassingSilently()
    {
        var result = Validate(new XDoc { SharedKidsFieldCount = 450 });

        Assert.True(result.IsPartial);
        var x99 = Assert.Single(result.Informational, v => v.Code == "X99");
        Assert.Contains("X07", x99.Message);
        Assert.Contains("AcroForm field walk", x99.Message);
    }

    [Fact]
    public void FormFieldWalk_WithinBudget_ReportsNoX99()
    {
        // The control: an ordinary form must not be reported as partially checked. 100 fields
        // sharing a 100-entry /Kids array costs ~10 100 edges, comfortably inside the ceiling.
        var result = Validate(new XDoc { SharedKidsFieldCount = 100 });

        Assert.False(result.IsPartial);
        Assert.Empty(result.Informational);
    }

    // ------------------------------------------------------------------
    //  X08 — colour spaces (PDF/X-1a only)
    // ------------------------------------------------------------------

    [Fact]
    public void X1a_DeviceRgbColorSpaceResource_ReportsX08()
    {
        var result = Validate(new XDoc { NamedColorSpace = "/DeviceRGB" });
        var x08 = Assert.Single(result.Violations, v => v.Code == "X08");
        Assert.Contains("DeviceRGB", x08.Message);
    }

    [Fact]
    public void X1a_CalRgbArrayForm_ReportsX08()
    {
        var result = Validate(new XDoc
        {
            NamedColorSpace = "[/CalRGB << /WhitePoint [0.9505 1 1.089] >>]"
        });
        Assert.Contains(result.Violations, v => v.Code == "X08");
    }

    /// <summary>
    /// The colour operators <b>inside</b> an /ExtGState soft mask's group are the page's colour
    /// too (GAPS §12b, 2026-10-14): an RGB fill drawn only to make the mask is X08, where the scan
    /// read the group's /CS and nothing it painted. The same mask drawing nothing is the control —
    /// X09 reports the mask itself at X-1a either way, which is not what these assert.
    /// </summary>
    [Theory]
    [InlineData(false)]   // the mask in the page's own /ExtGState
    [InlineData(true)]    // the mask in a form XObject's resources, reached by the nested walk
    public void X1a_RgbPaintedInsideASoftMasksGroup_ReportsX08(bool inNestedForm)
    {
        var painted = Validate(new XDoc
        {
            ExtGStateSoftMask = !inNestedForm,
            ExtGStateSoftMaskInNestedForm = inNestedForm,
            SoftMaskGroupContent = "1 0 0 rg 0 0 5 5 re f",
        });
        var blank = Validate(new XDoc
        {
            ExtGStateSoftMask = !inNestedForm,
            ExtGStateSoftMaskInNestedForm = inNestedForm,
        });

        var x08 = Assert.Single(painted.Violations, v => v.Code == "X08");
        Assert.Contains("rg", x08.Message);
        Assert.DoesNotContain(blank.Violations, v => v.Code == "X08");
        Assert.Contains(blank.Violations, v => v.Code == "X09");
    }

    /// <summary>
    /// The <c>/SMask</c> dictionary written as an indirect object is still followed to its group:
    /// a reader that took the entry as it stands would see no dictionary, no group, and clean colour.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void X1a_RgbInsideASoftMaskNamedIndirectly_ReportsX08(bool inNestedForm)
    {
        var result = Validate(new XDoc
        {
            ExtGStateSoftMask = !inNestedForm,
            ExtGStateSoftMaskInNestedForm = inNestedForm,
            IndirectSoftMaskDictionary = true,
            SoftMaskGroupContent = "1 0 0 rg 0 0 5 5 re f",
        });

        Assert.Contains(result.Violations, v => v.Code == "X08");
    }

    [Fact]
    public void X1a_DeviceCmyk_DoesNotReportX08()
    {
        var result = Validate(new XDoc { NamedColorSpace = "/DeviceCMYK" });
        Assert.DoesNotContain(result.Violations, v => v.Code == "X08");
    }

    // ------------------------------------------------------------------
    //  X08 at X-3 / X-4 — UNVERIFIED reading of ISO 15930-3 / -7
    // ------------------------------------------------------------------
    //
    //  These two cases asserted the opposite until 2026-08-09, on the rationale "PDF/X-3 permits
    //  device-independent colour; the colour restriction is X-1a only." The premise is right and
    //  does not reach the conclusion: DeviceRGB is device-DEPENDENT, so "device-independent colour
    //  is permitted" says nothing about it. The reading now implemented is that the later parts
    //  admit ICCBased / CalRGB / CalGray / Lab, and admit DeviceRGB only when the output intent is
    //  itself an RGB condition.
    //
    //  Implemented on an explicit accept-the-risk decision without the standard text, and written
    //  to fail safe: an absent or unreadable destination profile counts as not-RGB, so DeviceRGB
    //  is reported. See ColourPolicy for what to check against ISO 15930-3 §6.2.

    [Fact]
    public void X3_DeviceRgb_AgainstACmykOutputIntent_ReportsX08()
    {
        var result = Validate(
            new XDoc { XmpVersion = "PDF/X-3:2003", NamedColorSpace = "/DeviceRGB" },
            PdfXConformanceLevel.X3);
        Assert.Contains(result.Violations, v => v.Code == "X08");
    }

    [Fact]
    public void X3_DeviceRgb_AgainstAnRgbOutputIntent_DoesNotReportX08()
    {
        var result = Validate(
            new XDoc
            {
                XmpVersion = "PDF/X-3:2003",
                NamedColorSpace = "/DeviceRGB",
                OutputIntentProfileComponents = 3,
            },
            PdfXConformanceLevel.X3);
        Assert.DoesNotContain(result.Violations, v => v.Code == "X08");
    }

    /// <summary>
    /// The control that keeps X-3 from silently becoming X-1a: device-independent spaces stay
    /// legal. Without it, forbidding everything would satisfy the two cases above.
    /// </summary>
    [Theory]
    [InlineData("[/CalRGB << /WhitePoint [0.9505 1 1.089] >>]")]
    [InlineData("[/Lab << /WhitePoint [0.9505 1 1.089] >>]")]
    public void X3_DeviceIndependentColour_DoesNotReportX08(string space)
    {
        var result = Validate(
            new XDoc { XmpVersion = "PDF/X-3:2003", NamedColorSpace = space },
            PdfXConformanceLevel.X3);
        Assert.DoesNotContain(result.Violations, v => v.Code == "X08");
    }

    /// <summary>
    /// ICCBased RGB is the canonical device-independent space the later parts exist to admit — an
    /// sRGB or AdobeRGB working space is ordinary Illustrator / InDesign output — so it must not be
    /// X08 at X-3 or X-4.
    /// </summary>
    /// <remarks>
    /// The classification used to answer on <c>/N 3</c> alone, without consulting the policy it was
    /// handed, so the violation message read "PDF/X-4 allows device-independent colour (ICCBased,
    /// …) … found an ICCBased 3-component (RGB)" — naming the space as allowed and condemning it in
    /// one sentence. <c>PdfXConverter</c> then folded that blocker in and withheld the
    /// <c>GTS_PDFXVersion</c> identifier from a file it should have stamped.
    /// </remarks>
    [Theory]
    [InlineData(PdfXConformanceLevel.X3, "PDF/X-3:2003")]
    [InlineData(PdfXConformanceLevel.X4, "PDF/X-4")]
    public void X3AndX4_IccBasedRgbColourSpace_DoesNotReportX08(
        PdfXConformanceLevel level, string version)
    {
        var result = Validate(
            new XDoc { XmpVersion = version, NamedColorSpaceIccComponents = 3 }, level);
        Assert.DoesNotContain(result.Violations, v => v.Code == "X08");
        Assert.True(result.IsConforming, Describe(result));
    }

    [Theory]
    [InlineData(PdfXConformanceLevel.X3, "PDF/X-3:2003")]
    [InlineData(PdfXConformanceLevel.X4, "PDF/X-4")]
    public void X3AndX4_IccBasedRgbImage_DoesNotReportX08(
        PdfXConformanceLevel level, string version)
    {
        var result = Validate(new XDoc { XmpVersion = version, ImageIccComponents = 3 }, level);
        Assert.DoesNotContain(result.Violations, v => v.Code == "X08");
    }

    /// <summary>
    /// The counterpart that stops the fix above collapsing X-1a's rule too: X-1a admits 1- and
    /// 4-component output only, so the same file is a violation there.
    /// </summary>
    [Fact]
    public void X1a_IccBasedRgbColourSpace_StillReportsX08()
    {
        var result = Validate(new XDoc { NamedColorSpaceIccComponents = 3 });
        var x08 = Assert.Single(result.Violations, v => v.Code == "X08");
        Assert.Contains("ICCBased", x08.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(PdfXConformanceLevel.X3, "PDF/X-3:2003")]
    [InlineData(PdfXConformanceLevel.X4, "PDF/X-4")]
    public void X3AndX4_DeviceRgb_IsStillReported(PdfXConformanceLevel level, string version)
    {
        // DeviceRGB is device-DEPENDENT, so admitting ICCBased says nothing about it: the fix must
        // not widen into "the later parts have no colour rule".
        var result = Validate(
            new XDoc { XmpVersion = version, NamedColorSpace = "/DeviceRGB" }, level);
        Assert.Contains(result.Violations, v => v.Code == "X08");
    }

    // ------------------------------------------------------------------
    //  X08 — colour the dictionary walk cannot see (content streams, images)
    // ------------------------------------------------------------------

    [Fact]
    public void X1a_RgbFillOperatorInContentStream_ReportsX08()
    {
        // The page names no colour space at all — `rg` needs none. This is the case a
        // resource-dictionary-only check cannot see.
        var result = Validate(new XDoc { PageContent = "1 0 0 rg 10 10 100 100 re f" });
        var x08 = Assert.Single(result.Violations, v => v.Code == "X08");
        Assert.Contains("rg", x08.Message);
    }

    [Fact]
    public void X1a_RgbStrokeOperatorInContentStream_ReportsX08()
    {
        var result = Validate(new XDoc { PageContent = "0 0 1 RG 10 10 m 100 100 l S" });
        Assert.Contains(result.Violations, v => v.Code == "X08");
    }

    [Fact]
    public void X1a_CmykAndGrayOperators_DoNotReportX08()
    {
        // DeviceCMYK and DeviceGray are both legal PDF/X-1a colour.
        var result = Validate(new XDoc
        {
            PageContent = "0 0 0 1 k 0.5 g 10 10 100 100 re f"
        });
        Assert.DoesNotContain(result.Violations, v => v.Code == "X08");
    }

    [Fact]
    public void X1a_CsOperatorNamingDeviceRgbDirectly_ReportsX08()
    {
        var result = Validate(new XDoc { PageContent = "/DeviceRGB cs 1 0 0 sc 0 0 5 5 re f" });
        Assert.Contains(result.Violations, v => v.Code == "X08");
    }

    [Fact]
    public void X1a_DeviceRgbImage_ReportsX08()
    {
        var result = Validate(new XDoc { ImageColorSpace = "/DeviceRGB" });
        var x08 = Assert.Single(result.Violations, v => v.Code == "X08");
        Assert.Contains("image", x08.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void X1a_IccBasedThreeComponentImage_ReportsX08()
    {
        // /N decides, whatever the profile bytes claim: 3 components is RGB.
        var result = Validate(new XDoc { ImageIccComponents = 3 });
        Assert.Contains(result.Violations, v => v.Code == "X08");
    }

    [Fact]
    public void X1a_IccBasedFourComponentImage_DoesNotReportX08()
    {
        var result = Validate(new XDoc { ImageIccComponents = 4 });
        Assert.DoesNotContain(result.Violations, v => v.Code == "X08");
    }

    [Fact]
    public void X1a_IndexedImageWithRgbBase_ReportsX08()
    {
        // The palette entries live in the base space, so an /Indexed RGB image is RGB.
        var result = Validate(new XDoc
        {
            ImageColorSpace = "[/Indexed /DeviceRGB 1 <FF0000FFFFFF>]"
        });
        Assert.Contains(result.Violations, v => v.Code == "X08");
    }

    [Fact]
    public void X1a_IndexedImageWithCmykBase_DoesNotReportX08()
    {
        var result = Validate(new XDoc
        {
            ImageColorSpace = "[/Indexed /DeviceCMYK 1 <00000000FFFFFFFF>]"
        });
        Assert.DoesNotContain(result.Violations, v => v.Code == "X08");
    }

    [Fact]
    public void X1a_SeparationWithRgbAlternate_ReportsX08()
    {
        // A spot colour is legal, but its tint transform must not land in RGB.
        var result = Validate(new XDoc
        {
            ImageColorSpace = "[/Separation /Spot /DeviceRGB " +
                              "<< /FunctionType 2 /Domain [0 1] /C0 [0 0 0] /C1 [1 0 0] /N 1 >>]"
        });
        Assert.Contains(result.Violations, v => v.Code == "X08");
    }

    [Fact]
    public void X1a_SeparationWithCmykAlternate_DoesNotReportX08()
    {
        var result = Validate(new XDoc
        {
            ImageColorSpace = "[/Separation /Spot /DeviceCMYK " +
                              "<< /FunctionType 2 /Domain [0 1] /C0 [0 0 0 0] /C1 [0 0 0 1] /N 1 >>]"
        });
        Assert.DoesNotContain(result.Violations, v => v.Code == "X08");
    }

    [Fact]
    public void X1a_RgbInlineImage_ReportsX08()
    {
        var result = Validate(new XDoc
        {
            PageContent = "q 5 0 0 5 0 0 cm BI /W 1 /H 1 /CS /RGB /BPC 8 /L 3 ID abc EI Q"
        });
        Assert.Contains(result.Violations, v => v.Code == "X08");
    }

    [Fact]
    public void X1a_GrayInlineImage_DoesNotReportX08()
    {
        var result = Validate(new XDoc
        {
            PageContent = "q 5 0 0 5 0 0 cm BI /W 1 /H 1 /CS /G /BPC 8 /L 1 ID a EI Q"
        });
        Assert.DoesNotContain(result.Violations, v => v.Code == "X08");
    }

    [Fact]
    public void X1a_RgbInsideFormXObject_ReportsX08()
    {
        // The page's own content is clean; the RGB is one level down.
        var result = Validate(new XDoc { FormContent = "1 0 0 rg 0 0 5 5 re f" });
        Assert.Contains(result.Violations, v => v.Code == "X08");
    }

    [Fact]
    public void X1a_FormXObjectWithItsOwnRgbColorSpaceResource_ReportsX08()
    {
        // The RGB space sits in the *form's own* /Resources, which the page's resource
        // dictionary does not contain. Isolated deliberately: the form's content stream is
        // empty, so only the descent into its resources can find this.
        var result = Validate(new XDoc { FormColorSpace = "/DeviceRGB" });
        var x08 = Assert.Single(result.Violations, v => v.Code == "X08");
        Assert.Contains("FCS", x08.Message);
    }

    [Fact]
    public void X1a_SelfReferencingFormXObject_TerminatesAndStillReportsX08()
    {
        // Cycle guard: the form draws itself. The walk must terminate and still see the `rg`.
        var result = Validate(new XDoc { SelfReferencingForm = true });
        Assert.Contains(result.Violations, v => v.Code == "X08");
    }

    [Fact]
    public void X3_RgbInContentStream_ReportsX08()
    {
        var result = Validate(
            new XDoc { XmpVersion = "PDF/X-3:2003", PageContent = "1 0 0 rg 0 0 5 5 re f" },
            PdfXConformanceLevel.X3);
        Assert.Contains(result.Violations, v => v.Code == "X08");
    }

    // ------------------------------------------------------------------
    //  X99 — a check that was skipped must say so
    // ------------------------------------------------------------------

    [Fact]
    public void OversizedPageContent_SkipsX08AndReportsX99()
    {
        // A one-byte ceiling: the colour check cannot run, so the RGB fill goes unseen.
        // The point is that the result says so instead of reading as conforming.
        byte[] pdf = Build(new XDoc { PageContent = "1 0 0 rg 10 10 100 100 re f" });
        using var ms = new MemoryStream(pdf);
        var result = PdfXValidator.Validate(ms, PdfXConformanceLevel.X1a,
            maxPageContentBytes: 1, maxDocumentContentBytes: PdfXValidator.DefaultMaxDocumentContentBytes);

        Assert.DoesNotContain(result.Violations, v => v.Code == "X08");
        Assert.True(result.IsConforming, Describe(result));
        Assert.True(result.IsPartial);
        var x99 = Assert.Single(result.Informational, v => v.Code == "X99" && v.Message.Contains("X08"));
        Assert.Contains("X08", x99.Message);
        Assert.Contains("ceiling", x99.Message);
    }

    /// <summary>An unfiltered stream over the ceiling is refused without being decoded.</summary>
    /// <remarks>
    /// <b>The reason text is the evidence of <i>where</i> the ceiling was applied.</b> Decoding first
    /// and measuring after produced "the per-page content ceiling … would be exceeded"; handing the
    /// ceiling to <c>PdfStream.TryGetDecodedData</c> refuses an unfiltered stream on its raw length,
    /// and says so. A test asserting only "ceiling" passes either way.
    /// </remarks>
    [Fact]
    public void AnUnfilteredStreamOverTheCeilingIsRefusedWithoutBeingDecoded()
    {
        byte[] pdf = Build(new XDoc { PageContent = "1 0 0 rg 10 10 100 100 re f" });
        using var ms = new MemoryStream(pdf);
        var result = PdfXValidator.Validate(ms, PdfXConformanceLevel.X1a,
            maxPageContentBytes: 10, maxDocumentContentBytes: PdfXValidator.DefaultMaxDocumentContentBytes);

        var x99 = Assert.Single(result.Informational, v => v.Code == "X99" && v.Message.Contains("X08"));
        Assert.Contains("known without decoding", x99.Message);
        Assert.DoesNotContain(result.Violations, v => v.Code == "X08");
    }

    /// <summary>A filtered stream is decoded under the ceiling rather than measured after it.</summary>
    [Fact]
    public void AFilteredStreamIsDecodedUnderTheCeiling()
    {
        const string Content = "1 0 0 rg 10 10 100 100 re f";
        string hex = string.Concat(Encoding.ASCII.GetBytes(Content).Select(b => b.ToString("X2"))) + ">";

        byte[] pdf = Build(new XDoc { PageContent = hex, PageContentDictEntries = "/Filter /ASCIIHexDecode" });
        using var ms = new MemoryStream(pdf);
        var result = PdfXValidator.Validate(ms, PdfXConformanceLevel.X1a,
            maxPageContentBytes: 10, maxDocumentContentBytes: PdfXValidator.DefaultMaxDocumentContentBytes);

        var x99 = Assert.Single(result.Informational, v => v.Code == "X99" && v.Message.Contains("X08"));
        Assert.Contains("the caller's limit is 10", x99.Message);

        // The control: the same stream at the default ceiling decodes and is checked.
        using var ms2 = new MemoryStream(pdf);
        Assert.Contains(PdfXValidator.Validate(ms2, PdfXConformanceLevel.X1a).Violations, v => v.Code == "X08");
    }

    /// <summary>The document ceiling bounds the decode too, not only the page's.</summary>
    [Fact]
    public void TheDocumentCeilingIsWhatIsLeftForTheDecoder()
    {
        byte[] pdf = Build(new XDoc { PageContent = "1 0 0 rg 10 10 100 100 re f" });
        using var ms = new MemoryStream(pdf);
        var result = PdfXValidator.Validate(ms, PdfXConformanceLevel.X1a,
            maxPageContentBytes: PdfXValidator.DefaultMaxPageContentBytes, maxDocumentContentBytes: 12);

        Assert.Contains(result.Informational, v => v.Code == "X99" && v.Message.Contains("the limit is 12"));
    }

    /// <summary>A refused part of an array-valued <c>/Contents</c> drops the whole page, not just the part.</summary>
    /// <remarks>
    /// The first part draws in DeviceRGB, which X-1a forbids; the second fits the ceiling on its own
    /// but not after the first. Scanning the first alone would report X08 from a page read with a
    /// part missing — and a part missing can be the one that set the colour space the next part
    /// relies on, so the verdict would be about content that is not the page. <b>The budget is
    /// shared across the parts</b>: 27 bytes and a separator leave 72 of 100, which the 78-byte second
    /// part exceeds — and the reason names the 72, which a per-part budget of 100 would not. The
    /// control shows the violation is there to be found.
    /// </remarks>
    [Fact]
    public void ARefusedContentPartDropsTheWholePage()
    {
        var doc = new XDoc
        {
            PageContent = "1 0 0 rg 10 10 100 100 re f",
            SecondContentPart = string.Concat(Enumerable.Repeat("0 0 5 5 re f\n", 6)),
        };

        using (var ms = new MemoryStream(Build(doc)))
        {
            var result = PdfXValidator.Validate(ms, PdfXConformanceLevel.X1a,
                maxPageContentBytes: 100, maxDocumentContentBytes: PdfXValidator.DefaultMaxDocumentContentBytes);
            Assert.DoesNotContain(result.Violations, v => v.Code == "X08");
            Assert.Contains(result.Informational, v => v.Code == "X99" && v.Message.Contains("the limit is 72"));
        }

        using (var ms = new MemoryStream(Build(doc)))
            Assert.Contains(PdfXValidator.Validate(ms, PdfXConformanceLevel.X1a).Violations, v => v.Code == "X08");
    }

    /// <summary>
    /// A form XObject's and an annotation appearance's content decode within what the page's own
    /// content left, not within the whole ceiling.
    /// </summary>
    /// <remarks>
    /// The page's default content is 33 bytes, so a 40-byte ceiling leaves 7 for everything the
    /// page reaches. "the limit is" is the bounded decoder's refusal; decoding first and measuring
    /// after says "would be exceeded" instead.
    /// </remarks>
    [Theory]
    [InlineData("form")]
    [InlineData("appearance")]
    public void NestedContentDecodesWithinWhatThePageLeft(string host)
    {
        var doc = host == "form"
            ? new XDoc { FormContent = "1 0 0 rg 10 10 100 100 re f" }
            : new XDoc { AnnotationAppearanceEntries = "" };

        using var ms = new MemoryStream(Build(doc));
        var result = PdfXValidator.Validate(ms, PdfXConformanceLevel.X1a,
            maxPageContentBytes: 40, maxDocumentContentBytes: PdfXValidator.DefaultMaxDocumentContentBytes);

        var x99 = Assert.Single(result.Informational, v => v.Code == "X99" && v.Message.Contains("X08"));
        Assert.Contains(host == "form" ? "form XObject" : "annotation appearance", x99.Message);
        Assert.Contains("and the limit is 7", x99.Message);
    }

    [Fact]
    public void OversizedDocumentContent_ReportsX99()
    {
        byte[] pdf = Build(new XDoc { PageContent = "1 0 0 rg 10 10 100 100 re f" });
        using var ms = new MemoryStream(pdf);
        var result = PdfXValidator.Validate(ms, PdfXConformanceLevel.X1a,
            maxPageContentBytes: PdfXValidator.DefaultMaxPageContentBytes,
            maxDocumentContentBytes: 1);

        Assert.DoesNotContain(result.Violations, v => v.Code == "X08");
        Assert.Contains(result.Informational, v => v.Code == "X99");
    }

    [Fact]
    public void WithinCeilings_NoX99IsReported()
    {
        // The control for the two above: at the default ceilings the check runs, so the
        // violation is found and nothing is reported as skipped.
        var result = Validate(new XDoc { PageContent = "1 0 0 rg 10 10 100 100 re f" });
        Assert.Contains(result.Violations, v => v.Code == "X08");
        Assert.Empty(result.Informational);
        Assert.False(result.IsPartial);
    }

    [Fact]
    public void CleanDocument_ReportsNothingAsSkipped()
    {
        var result = Validate(new XDoc());
        Assert.Empty(result.Informational);
        Assert.False(result.IsPartial);
    }

    // ------------------------------------------------------------------
    //  X09 — the constant-alpha and blend-mode arms
    // ------------------------------------------------------------------
    //
    //  CheckTransparency has four arms and only /SMask had a test. The other three shipped with
    //  zero coverage, which is how the /ca defect below survived: the read never resolved an
    //  indirect value, so `null < 1.0` was false and a page of 50%-alpha artwork validated clean.

    [Theory]
    [InlineData("0.5", null, null)]        // /ca alone
    [InlineData(null, "0.5", null)]        // /CA alone
    [InlineData(null, null, "/Multiply")]  // /BM as a name
    [InlineData(null, null, "[/Multiply]")] // /BM as an array (§11.6.3 permits it)
    public void X1a_ExtGStateTransparency_ReportsX09(string? ca, string? CA, string? bm)
    {
        var result = Validate(new XDoc
        {
            ExtGStateFillAlpha = ca, ExtGStateStrokeAlpha = CA, ExtGStateBlendMode = bm,
        });

        Assert.Contains(result.Violations, v => v.Code == "X09");
    }

    /// <summary>
    /// The control: fully opaque and /Normal is what a conforming file writes, and it must not
    /// report X09. Without this the theory above would be satisfied by "any /ExtGState is a
    /// violation".
    /// </summary>
    [Fact]
    public void X1a_OpaqueExtGState_DoesNotReportX09()
    {
        var result = Validate(new XDoc
        {
            ExtGStateFillAlpha = "1.0", ExtGStateStrokeAlpha = "1.0", ExtGStateBlendMode = "/Normal",
        });

        Assert.DoesNotContain(result.Violations, v => v.Code == "X09");
    }

    /// <summary>
    /// §7.3.10 lets any object be indirect, and <c>PdfDictionary.GetDouble</c> reads the raw entry.
    /// So <c>/ca 9 0 R</c> answered null, `null &lt; 1.0` is false, and the transparency went
    /// unreported — a **false pass**, which for a print-exchange standard is the direction that
    /// reaches a RIP.
    /// </summary>
    [Fact]
    public void X1a_ExtGStateAlphaWrittenIndirectly_StillReportsX09()
    {
        var result = Validate(new XDoc
        {
            ExtGStateFillAlpha = "0.5", IndirectExtGStateAlpha = true,
        });

        Assert.Contains(result.Violations, v => v.Code == "X09");
    }

    // ------------------------------------------------------------------
    //  X08 — shadings, which the colour check could not see at all
    // ------------------------------------------------------------------
    //
    //  FindForbiddenColour read named /ColorSpace resources and image XObjects. A shading carries
    //  its own /ColorSpace and appears in neither, so **a DeviceRGB gradient validated PDF/X-1a
    //  clean** — and a gradient is ordinary press artwork, not an exotic construct.

    [Fact]
    public void X1a_DeviceRgbShadingResource_ReportsX08()
    {
        var result = Validate(new XDoc { ShadingColorSpace = "/DeviceRGB" });
        Assert.Contains(result.Violations, v => v.Code == "X08");
    }

    /// <summary>
    /// The form a gradient actually takes in artwork: a <c>/PatternType 2</c> pattern selected
    /// with <c>scn</c>, whose shading is one indirection further away.
    /// </summary>
    [Fact]
    public void X1a_DeviceRgbShadingPattern_ReportsX08()
    {
        var result = Validate(new XDoc { ShadingPatternColorSpace = "/DeviceRGB" });
        Assert.Contains(result.Violations, v => v.Code == "X08");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void X1a_DeviceCmykShading_DoesNotReportX08(bool asPattern)
    {
        // The control. A CMYK gradient is exactly what a conforming press file contains, so a
        // check that reported it would be worse than the blind spot it replaced.
        var result = Validate(new XDoc
        {
            ShadingColorSpace        = asPattern ? null : "/DeviceCMYK",
            ShadingPatternColorSpace = asPattern ? "/DeviceCMYK" : null,
        });

        Assert.DoesNotContain(result.Violations, v => v.Code == "X08");
    }

    /// <summary>
    /// The ICCBased arm of the same defect class: <c>/N</c> decides whether the profile is RGB,
    /// and it was read without resolving — so an RGB profile declaring <c>/N 9 0 R</c> passed.
    /// </summary>
    [Fact]
    public void X1a_IccProfileComponentCountWrittenIndirectly_StillReportsX08()
    {
        var result = Validate(new XDoc
        {
            ImageIccComponents = 3, IndirectIccComponentCount = true,
        });

        Assert.Contains(result.Violations, v => v.Code == "X08");
    }

    [Fact]
    public void X1a_IccProfileWithFourComponentsWrittenIndirectly_DoesNotReportX08()
    {
        // The control that stops the fix above from meaning "an indirect /N is always RGB".
        var result = Validate(new XDoc
        {
            ImageIccComponents = 4, IndirectIccComponentCount = true,
        });

        Assert.DoesNotContain(result.Violations, v => v.Code == "X08");
    }

    // ------------------------------------------------------------------
    //  X09 — transparency (PDF/X-1a and PDF/X-3)
    // ------------------------------------------------------------------

    [Fact]
    public void X1a_PageTransparencyGroup_ReportsX09()
    {
        var result = Validate(new XDoc { PageTransparencyGroup = true });
        Assert.Contains(result.Violations, v => v.Code == "X09");
    }

    [Fact]
    public void X1a_XObjectSoftMask_ReportsX09()
    {
        var result = Validate(new XDoc { XObjectSoftMask = true });
        Assert.Contains(result.Violations, v => v.Code == "X09");
    }

    [Fact]
    public void X1a_ExtGStateSoftMask_ReportsX09()
    {
        // /ExtGState /SMask is the ordinary way a producer introduces transparency;
        // it never touches the /XObject resource dictionary.
        var result = Validate(new XDoc { ExtGStateSoftMask = true });
        Assert.Contains(result.Violations, v => v.Code == "X09");
    }

    [Theory]
    [InlineData(true, false)]   // /ExtGState /SMask inside a form XObject
    [InlineData(false, true)]   // /SMask-bearing image inside a tiling pattern
    public void X1a_TransparencyInsideANestedConstruct_ReportsX09(bool inForm, bool inPattern)
    {
        // Until 2026-08-08 X09 read page.Resources alone, so transparency inside a form XObject
        // or a pattern validated clean — and a form is the ordinary home for placed artwork,
        // which is exactly the artwork most likely to carry a soft mask.
        var result = Validate(new XDoc
        {
            ExtGStateSoftMaskInNestedForm = inForm,
            SoftMaskInPattern = inPattern,
        });

        Assert.Contains(result.Violations, v => v.Code == "X09");
    }

    [Fact]
    public void X1a_OpaqueNestedConstructs_DoNotReportX09()
    {
        // The control: the widened walk must not start reporting X09 for ordinary nested content.
        var result = Validate(new XDoc { FormContent = "0 0 0 1 k 0 0 5 5 re f" });
        Assert.DoesNotContain(result.Violations, v => v.Code == "X09");
    }

    // ------------------------------------------------------------------
    //  X09 — an annotation appearance stream's OWN /Group and /SMask
    // ------------------------------------------------------------------
    //
    //  The page scan seeds every /AP /N stream so its /Resources are walked, and then never
    //  examined the stream's own dictionary — the one place a stamp or a filled form field
    //  actually writes /Group << /S /Transparency >>. Adding it to seenForms made it *look*
    //  covered: nothing else reaches an appearance stream, so no other loop could catch it.

    [Theory]
    [InlineData("/Group << /S /Transparency /CS /DeviceCMYK >>")]
    [InlineData("/SMask << /S /Luminosity >>")]
    public void X1a_AnnotationAppearanceTransparency_ReportsX09(string entries)
    {
        var result = Validate(new XDoc { AnnotationAppearanceEntries = entries });

        // Naming /AP is load-bearing: the XObject loop's message would be the wrong answer here,
        // because no /XObject resource dictionary names this stream at all.
        var x09 = Assert.Single(result.Violations, v => v.Code == "X09");
        Assert.Contains("/AP", x09.Message);
    }

    [Fact]
    public void X1a_PlainAnnotationAppearance_DoesNotReportX09()
    {
        // The control. Without it, reporting every annotation that has an appearance would
        // satisfy the theory above — and a stamp with an appearance is what a conforming file has.
        var result = Validate(new XDoc { AnnotationAppearanceEntries = "" });

        Assert.True(result.IsConforming, Describe(result));
        Assert.DoesNotContain(result.Violations, v => v.Code == "X09");
    }

    // ------------------------------------------------------------------
    //  ISO 15930-9:2020, 6.2.2 — a non-printing annotation's appearance is a NON-PRINT element
    // ------------------------------------------------------------------
    //
    //  "print elements are those drawn or referenced … from: the Contents stream(s) of Page
    //  objects; or the appearance stream of any annotation, including those with a Subtype value
    //  of Widget (form fields), WHICH HAS ITS PRINTABLE FLAG SET AND ITS INVISIBLE AND HIDDEN
    //  FLAGS CLEAR (see ISO 32000-2:—, 12.5.3)."
    //  — docs/iso/ISO-15930-9-2020-PDFX6-preview.pdf, clause 6.2.2.
    //
    //  The page walk descended into every /AP /N with no flag test at all, so a stamp that never
    //  reaches the press had its colour, its fonts and its transparency judged anyway. That is a
    //  FALSE FAIL, which §12b calls the direction that trains users to ignore the validator.
    //
    //  X09 is the instrument because it is the check that fires on the appearance stream's own
    //  dictionary, so it needs nothing else on the page to be wrong.

    [Theory]
    [InlineData(0)]                                                     // /F present, no Print bit
    [InlineData(PdfAnnotationFlags.Print | PdfAnnotationFlags.Hidden)]  // printable, but hidden
    [InlineData(PdfAnnotationFlags.Print | PdfAnnotationFlags.Invisible)]
    [InlineData(null)]                                                  // no /F at all — defaults to 0
    public void X1a_AnnotationThatIsNotAPrintElement_HasItsAppearanceLeftUnjudged(int? flags)
    {
        // The null row is the one that pins the DEFAULT rather than the predicate: /F is optional,
        // and ISO 32000-2 Table 166 gives it a default of 0, which leaves the printable flag clear.
        // Every other row writes an explicit /F, so without this one a reading that defaulted a
        // missing /F to Print would restore the false fail and nothing here would notice.
        var result = Validate(new XDoc
        {
            AnnotationFlags = flags,
            AnnotationAppearanceEntries = "/Group << /S /Transparency /CS /DeviceCMYK >>",
        });

        Assert.DoesNotContain(result.Violations, v => v.Code == "X09");
        Assert.True(result.IsConforming, Describe(result));
    }

    /// <summary>
    /// X08 is scoped out too, and its acceptance control proves the fixture is potent.
    /// </summary>
    /// <remarks>
    /// X09 alone could not pin this: the four checks take <i>separate</i> scans, so each one opts
    /// into the print-element scope on its own line and a mutation that removes X08's opt-in is
    /// invisible to a transparency test. ISO 15930-9:2020 titles the clause 6.6.3 <i>Print element
    /// colour spaces</i>, which is the most explicit statement in the standard that colour is
    /// judged on print elements and nothing else.
    /// </remarks>
    [Theory]
    [InlineData(PdfAnnotationFlags.Print, true)]   // a stamp that prints — RGB really is a violation
    [InlineData(0, false)]                          // the same stamp, not printing — out of scope
    public void X1a_RgbInAnAnnotationAppearance_IsJudgedOnlyWhenItPrints(int flags, bool expectX08)
    {
        var result = Validate(new XDoc
        {
            AnnotationFlags = flags,
            AnnotationAppearanceEntries = "",
            AnnotationAppearanceContent = "1 0 0 rg 0 0 5 5 re f",
        });

        Assert.Equal(expectX08, result.Violations.Any(v => v.Code == "X08"));
    }

    [Fact]
    public void X4_AnnotationAppearanceTransparency_IsAllowed()
    {
        // X09 does not run at X-4, the part that exists to permit transparency. Without this the
        // new loop could be firing at every level and the theory above would still pass.
        var result = Validate(
            new XDoc
            {
                XmpVersion = "PDF/X-4",
                AnnotationAppearanceEntries = "/Group << /S /Transparency /CS /DeviceCMYK >>",
            },
            PdfXConformanceLevel.X4);

        Assert.DoesNotContain(result.Violations, v => v.Code == "X09");
    }

    // ------------------------------------------------------------------
    //  X12 — PDF/X-4 version floor (ISO 15930-7)
    // ------------------------------------------------------------------

    [Fact]
    public void X4_WithPdf14Header_ReportsX12()
    {
        var result = Validate(
            new XDoc { XmpVersion = "PDF/X-4", HeaderVersion = "1.4" },
            PdfXConformanceLevel.X4);
        Assert.Contains(result.Violations, v => v.Code == "X12");
    }

    [Fact]
    public void X4_WithPdf16Header_DoesNotReportX12()
    {
        var result = Validate(
            new XDoc { XmpVersion = "PDF/X-4", HeaderVersion = "1.6" },
            PdfXConformanceLevel.X4);
        Assert.DoesNotContain(result.Violations, v => v.Code == "X12");
    }

    [Fact]
    public void X1a_WithPdf14Header_DoesNotReportX12()
    {
        // The floor is an X-4 rule only. Without this, X12 could be firing for every level and
        // the two assertions above would still pass.
        var result = Validate(new XDoc { HeaderVersion = "1.4" });
        Assert.DoesNotContain(result.Violations, v => v.Code == "X12");
    }

    // ------------------------------------------------------------------
    //  Indirect name values — a check must resolve, not read the raw entry
    // ------------------------------------------------------------------
    //
    //  §7.3.10 lets any object be indirect, including the name a check dispatches on.
    //  PdfDictionary.GetName is `as PdfName` over the raw entry, so `/Subtype 6 0 R` answered null
    //  and was indistinguishable from the key being absent. Four of the eight sites failed in the
    //  FALSE-PASS direction, which for a print-exchange standard is the harmful one — a form
    //  XObject whose /Subtype was indirect was never descended into, so X04, X06, X08, X09 and X13
    //  were all blind to everything inside it. The same defect class CLAUDE.md's PdfResolve rule
    //  exists for in the renderers.

    [Theory]
    [InlineData(NestedFontHost.FormXObject)]
    [InlineData(NestedFontHost.Type3CharProc)]
    public void IndirectSubtype_StillWalksIntoTheHost_AndReportsX04(NestedFontHost host)
    {
        // Both hosts are entered only if their /Subtype matches, so an unresolved one drops the
        // whole subtree — and with it the non-embedded Helvetica hidden there.
        var result = Validate(new XDoc { NestedFontIn = host, IndirectNameValues = true });

        // Naming the font is load-bearing, not decoration. A bare `v.Code == "X04"` passed the
        // Type3 case against the unfixed code: with /Subtype unresolved the font no longer matched
        // the `continue` that exempts Type 3 from embedding checks, so X04 fired on the Type 3 font
        // *itself* — the right code for the wrong reason, while the nested Helvetica stayed
        // invisible. Only the font name distinguishes the two.
        Assert.Contains(result.Violations,
            v => v.Code == "X04" && v.Message.Contains("Helvetica"));
    }

    [Fact]
    public void IndirectSubtype_StillClassifiesAnImageColourSpace_AndReportsX08()
    {
        var result = Validate(new XDoc { ImageColorSpace = "/DeviceRGB", IndirectNameValues = true });

        Assert.Contains(result.Violations, v => v.Code == "X08");
    }

    [Fact]
    public void IndirectActionType_IsStillReportedAsX07()
    {
        var result = Validate(new XDoc { OpenActionType = "JavaScript", IndirectNameValues = true });

        Assert.Contains(result.Violations, v => v.Code == "X07");
    }

    [Fact]
    public void IndirectNameValues_OnAnOtherwiseConformingFile_ReportNothing()
    {
        // The other direction, and the control for all four cases above: /Trapped and the output
        // intent's /S written indirectly must not be read as absent, which would invent X10 and X02
        // on a conforming file. Asserting the whole violation list empty also catches a check that
        // starts reporting something new when a value moves behind a reference.
        var result = Validate(new XDoc { IndirectNameValues = true });

        Assert.Empty(result.Violations);
    }

    // ------------------------------------------------------------------
    //  X13 (X-4) — optional content must be declared. UNVERIFIED reading.
    // ------------------------------------------------------------------
    //
    //  Implemented on the accept-the-risk decision, without ISO 15930-7. The reading: X-4 is the
    //  part that permits optional content, and a print exchange needs every group's visibility
    //  determined by the file rather than by the consumer's defaults.

    [Fact]
    public void X4_UndeclaredOptionalContentGroup_ReportsX13()
    {
        var result = Validate(
            new XDoc
            {
                XmpVersion = "PDF/X-4",
                OptionalContentXObject = true,
                DeclareOcgInCatalog = false,
            },
            PdfXConformanceLevel.X4);

        Assert.Contains(result.Violations, v => v.Code == "X13");
    }

    [Fact]
    public void X4_DeclaredOptionalContentGroup_DoesNotReportX13()
    {
        // The control. Without it, reporting every /OC would satisfy the case above.
        var result = Validate(
            new XDoc { XmpVersion = "PDF/X-4", OptionalContentXObject = true },
            PdfXConformanceLevel.X4);

        Assert.DoesNotContain(result.Violations, v => v.Code == "X13");
    }

    [Fact]
    public void X1a_UndeclaredOptionalContentGroup_DoesNotReportX13()
    {
        // X13 is an X-4 rule only — X-1a and X-3 have no optional content to declare. Without
        // this, X13 could be firing at every level and both cases above would still pass.
        var result = Validate(
            new XDoc { OptionalContentXObject = true, DeclareOcgInCatalog = false });

        Assert.DoesNotContain(result.Violations, v => v.Code == "X13");
    }

    // ------------------------------------------------------------------
    //  X13 — the BDC /OC marked-content form (§8.11.4.2, §14.6.2)
    // ------------------------------------------------------------------
    //
    //  Extends the XObject/annotation check above to the third place §8.11.4.2 lets a file name
    //  optional content: a BDC operator tagged /OC, whose property operand resolves through the
    //  page's /Properties resource to the OCG/OCMD dictionary itself.

    [Fact]
    public void X4_UndeclaredMarkedContentOc_ReportsX13()
    {
        var result = Validate(
            new XDoc
            {
                XmpVersion = "PDF/X-4",
                MarkedContentOc = true,
                DeclareOcgInCatalog = false,
            },
            PdfXConformanceLevel.X4);

        Assert.Contains(result.Violations, v => v.Code == "X13");
    }

    [Fact]
    public void X4_DeclaredMarkedContentOc_DoesNotReportX13()
    {
        // The control. Without it, reporting every BDC /OC would satisfy the case above.
        var result = Validate(
            new XDoc { XmpVersion = "PDF/X-4", MarkedContentOc = true },
            PdfXConformanceLevel.X4);

        Assert.DoesNotContain(result.Violations, v => v.Code == "X13");
    }

    [Fact]
    public void X4_MarkedContentOcPropertyUnresolvable_DoesNotReportX13()
    {
        // The fail-safe direction named in CheckOptionalContentDeclared's remarks: unlike an
        // XObject/annotation /OC entry, a BDC property name that fails to resolve (no matching
        // /Properties entry here) is imperfect-parse uncertainty, not a definite finding — so it
        // must NOT be reported, even though DeclareOcgInCatalog defaults to true and the group
        // that exists is perfectly declared.
        var result = Validate(
            new XDoc { XmpVersion = "PDF/X-4", MarkedContentOc = true,
                       MarkedContentOcPropertyUnresolvable = true },
            PdfXConformanceLevel.X4);

        Assert.DoesNotContain(result.Violations, v => v.Code == "X13");
    }

    [Fact]
    public void X1a_UndeclaredMarkedContentOc_DoesNotReportX13()
    {
        // X13 is an X-4 rule only, same as the XObject form.
        var result = Validate(
            new XDoc { MarkedContentOc = true, DeclareOcgInCatalog = false });

        Assert.DoesNotContain(result.Violations, v => v.Code == "X13");
    }

    [Fact]
    public void X4_OutputIntentWithoutProfile_ReportsX11()
    {
        // The X-4 half of X11's profile rule, also an unverified reading: X-1a and X-4 require the
        // embedded profile, X-3 accepts a registered characterisation name. Requiring it is the
        // stricter of the two plausible readings, and a file that embeds one conforms either way.
        // MissingProfileButRegisteredCondition_IsAcceptedOutsideX1a pins the X-3 side of the same
        // rule, so between them the level-dependence is asserted in both directions.
        var doc = new XDoc { XmpVersion = "PDF/X-4", OutputIntentWithoutProfile = true };

        var x4 = Validate(doc, PdfXConformanceLevel.X4);
        var x3 = Validate(doc, PdfXConformanceLevel.X3);

        Assert.Contains(x4.Violations, v => v.Code == "X11");
        Assert.DoesNotContain(x3.Violations, v => v.Code == "X11");
    }

    [Fact]
    public void X4_TransparencyIsAllowed()
    {
        var result = Validate(
            new XDoc { XmpVersion = "PDF/X-4", PageTransparencyGroup = true, XObjectSoftMask = true },
            PdfXConformanceLevel.X4);
        Assert.DoesNotContain(result.Violations, v => v.Code == "X09");
    }

    [Fact]
    public void X3_TransparencyIsForbidden()
    {
        var result = Validate(
            new XDoc { XmpVersion = "PDF/X-3:2003", PageTransparencyGroup = true },
            PdfXConformanceLevel.X3);
        Assert.Contains(result.Violations, v => v.Code == "X09");
    }

    // ------------------------------------------------------------------
    //  X01 — encryption, and the early return it triggers
    // ------------------------------------------------------------------

    [Fact]
    public void EncryptedDocument_ReportsOnlyX01()
    {
        // The document is otherwise non-conforming (no boxes, no output intent, no XMP),
        // so a validator that kept going would report several codes. It must stop at X01
        // rather than report checks it cannot actually perform on ciphertext.
        byte[] clear = Build(new XDoc
        {
            Boxes = BoxKind.None, OutputIntentSubtype = null, XmpVersion = null
        });

        using var input = new MemoryStream(clear);
        using var encrypted = new MemoryStream();
        PdfEncryptor.Encrypt(input, encrypted, new EncryptionParameters
        {
            UserPassword  = "u",
            OwnerPassword = "o",
            Algorithm     = EncryptionAlgorithm.AES128,
        });
        encrypted.Position = 0;

        var result = PdfXValidator.Validate(encrypted, PdfXConformanceLevel.X1a);
        var only = Assert.Single(result.Violations);
        Assert.Equal("X01", only.Code);
    }

    // ------------------------------------------------------------------
    //  Diagnostics — a check that could not run must say so
    // ------------------------------------------------------------------

    [Fact]
    public void MetadataNotAStream_SetsLastValidationError()
    {
        var result = Validate(new XDoc { MetadataNotAStream = true });

        // The GTS_PDFXVersion rule was skipped, not passed — the caller can only tell
        // via the diagnostic, since a skipped check reports no violation.
        Assert.DoesNotContain(result.Violations, v => v.Code == "X03");
        Assert.NotNull(PdfXValidator.LastValidationError);
        Assert.Contains("Metadata", PdfXValidator.LastValidationError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MalformedXmp_ReportsX03AndSetsLastValidationError()
    {
        var result = Validate(new XDoc { RawXmp = "<x:xmpmeta><unclosed>" });
        Assert.Contains(result.Violations, v => v.Code == "X03");
        Assert.NotNull(PdfXValidator.LastValidationError);
    }

    [Fact]
    public void LastValidationError_IsClearedByASubsequentCleanValidation()
    {
        Validate(new XDoc { MetadataNotAStream = true });
        Assert.NotNull(PdfXValidator.LastValidationError);

        Validate(new XDoc());
        Assert.Null(PdfXValidator.LastValidationError);
    }

    // ------------------------------------------------------------------
    //  Public surface
    // ------------------------------------------------------------------

    [Fact]
    public void PathOverload_AgreesWithStreamOverload()
    {
        byte[] pdf = Build(new XDoc { Boxes = BoxKind.None });
        string path = Path.Combine(Path.GetTempPath(),
            "tetitulz-pdfx-" + Guid.NewGuid().ToString("N") + ".pdf");
        try
        {
            File.WriteAllBytes(path, pdf);
            var fromPath = PdfXValidator.Validate(path, PdfXConformanceLevel.X1a);
            using var ms = new MemoryStream(pdf);
            var fromStream = PdfXValidator.Validate(ms, PdfXConformanceLevel.X1a);

            Assert.Equal(
                fromStream.Violations.Select(v => v.Code),
                fromPath.Violations.Select(v => v.Code));
            Assert.Contains(fromPath.Violations, v => v.Code == "X05");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => PdfXValidator.Validate((string)null));
        Assert.Throws<ArgumentNullException>(() => PdfXValidator.Validate((Stream)null));
    }

    [Theory]
    [InlineData(PdfXConformanceLevel.X1a, "PDF/X-1a")]
    [InlineData(PdfXConformanceLevel.X3,  "PDF/X-3")]
    [InlineData(PdfXConformanceLevel.X4,  "PDF/X-4")]
    public void Plugin_ReportsTheConformanceLevelItValidates(
        PdfXConformanceLevel level, string expected)
    {
        var plugin = new PdfXValidationPlugin(level);
        Assert.Equal(expected, plugin.ConformanceLevel);
    }

    [Fact]
    public void Plugin_SurfacesViolationsAndRestoresStreamPosition()
    {
        byte[] pdf = Build(new XDoc { Boxes = BoxKind.None });
        using var ms = new MemoryStream(pdf);
        ms.Position = 0;

        var plugin = new PdfXValidationPlugin(PdfXConformanceLevel.X1a);
        var result = plugin.Validate(ms);

        Assert.False(result.IsConformant);
        Assert.Contains(result.Violations, v => v.Code == "X05");
        Assert.Equal("PDF/X-1a", result.ConformanceLevel);
        Assert.Equal(0, ms.Position);
    }

    [Fact]
    public void Plugin_ConformantDocument_IsReportedConformant()
    {
        using var ms = new MemoryStream(Build(new XDoc()));
        var result = new PdfXValidationPlugin(PdfXConformanceLevel.X1a).Validate(ms);
        Assert.True(result.IsConformant, string.Join("; ", result.Violations.Select(v => v.Code)));
        Assert.Empty(result.Violations);
    }

    [Fact]
    public void Plugin_NullStream_Throws()
    {
        var plugin = new PdfXValidationPlugin();
        Assert.Throws<ArgumentNullException>(() => plugin.Validate(null));
    }

    /// <summary>
    /// A check the plugin could not run must reach the caller as <b>partial</b>, never as a clean
    /// pass.
    /// </summary>
    /// <remarks>
    /// <para><c>PdfXValidator</c> has reported skipped checks as X99 since its ceilings were
    /// added, and <c>PdfXValidationPlugin.Validate</c> copied only <c>Violations</c> — so an
    /// otherwise-conforming document whose content scan could not run came back as
    /// <c>IsConformant == true</c> with an empty violation list and nothing at all to say that
    /// nothing had been examined. GAPS.md §12c recorded that state as impossible; it was reachable
    /// through this method the whole time.</para>
    /// <para>The fixture is <b>otherwise conforming</b> on purpose. Asserting only
    /// <c>NotEmpty(Informational)</c> on a document that already fails would prove nothing about
    /// the claim, which is specifically <c>IsConformant &amp;&amp; Violations.Count == 0 &amp;&amp;
    /// IsPartial</c>.</para>
    /// </remarks>
    [Fact]
    public void Plugin_ASkippedCheck_IsSurfacedAsPartialNotAsAFullPass()
    {
        using var ms = new MemoryStream(Build(new XDoc
        {
            AnnotationAppearanceEntries     = "",
            UndecodableAnnotationAppearance = true,
        }));

        var result = new PdfXValidationPlugin(PdfXConformanceLevel.X1a).Validate(ms);

        Assert.True(result.IsConformant, string.Join("; ", result.Violations.Select(v => v.Code)));
        Assert.Empty(result.Violations);
        Assert.True(result.IsPartial);
        // One X99 per check that skipped the stream: X08's colour scan and X15's inline-image
        // scan both read content.
        Assert.All(result.Informational, i => Assert.Equal("X99", i.Code));
        Assert.Single(result.Informational, i => i.Message.Contains("X08"));
        Assert.Single(result.Informational, i => i.Message.Contains("X15"));
    }

    [Fact]
    public void Plugin_ConformantDocument_IsNotReportedAsPartial()
    {
        // The control for the case above: IsPartial must not be true for every document, and an
        // informational entry must not be manufactured where the validator reported none.
        using var ms = new MemoryStream(Build(new XDoc()));

        var result = new PdfXValidationPlugin(PdfXConformanceLevel.X1a).Validate(ms);

        Assert.False(result.IsPartial);
        Assert.Empty(result.Informational);
    }
}
