// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Plugins;

namespace Tetitulz.Pdf.Plugins.Svg
{
    /// <summary>
    /// <see cref="IExportPlugin"/> that exports a PDF as <b>one</b> SVG document — a single root
    /// <c>&lt;svg&gt;</c> containing one nested <c>&lt;svg&gt;</c> per page, stacked top to bottom —
    /// using the built-in <see cref="PdfSvgRenderer"/>.
    ///
    /// <para><b>Why it is one document.</b> The output is declared <c>image/svg+xml</c>, and
    /// well-formed XML has one root and one <c>&lt;?xml …?&gt;</c> declaration, so pages cannot be
    /// concatenated as separate SVG files. Nesting an <c>&lt;svg&gt;</c> per page keeps each page's
    /// own <c>viewBox</c>, so page geometry is untouched.</para>
    ///
    /// <para><b>Element ids are namespaced per page</b> (<c>p0-</c>, <c>p1-</c>, …). Each page is
    /// rendered independently and mints its ids from a counter that starts at 0, so without the
    /// prefix page 2's <c>clip1</c> would collide with page 1's and every <c>url(#clip1)</c> would
    /// resolve to whichever definition came last — the collision the renderer prevents within a
    /// page (<c>SvgSharedState.IdCounter</c>), here prevented across pages.</para>
    ///
    /// Registration:
    /// <code>
    ///   PdfPluginHost.Instance.Register(new SvgExportPlugin());
    /// </code>
    /// </summary>
    public sealed class SvgExportPlugin : IExportPlugin
    {
        public string Name           => "Tetitulz.Plugins.SvgExport";
        public string Description    => "Exports a PDF to a single SVG document, one nested <svg> per page.";
        public Version Version       => new Version(1, 0, 0);
        public string FormatKey      => "svg";
        public string TargetMimeType => "image/svg+xml";

        public void Initialize() { }
        public void Shutdown()   { }

        private const string SvgNamespace   = "http://www.w3.org/2000/svg";
        private const string XlinkNamespace = "http://www.w3.org/1999/xlink";

        /// <summary>Vertical gap between stacked pages, in user units.</summary>
        private const double PageGap = 0d;

        public void Export(PdfDocument document, Stream output, ExportOptions? options)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            if (output == null)   throw new ArgumentNullException(nameof(output));
            // Fail closed, with the reason. PdfDocument.Load skips the catalog of an encrypted
            // file, so `document.Pages` is empty and an export would be a 0-byte .svg that a batch
            // job cannot tell from a genuinely empty PDF. This assembly cannot decrypt (a plugin may
            // not reference Tetitulz.Pdf.Encryption without bringing the crypto stack into this
            // package graph), so it refuses. PdfEncryptedDocumentException is the type every refusal
            // of encrypted input in the library throws, so a caller catches one type for it.
            Tetitulz.Pdf.Base.Operations.EncryptedRead.RefuseConversion(document, "SVG");

            var renderOptions = ReadRenderOptions(options);

            long savedPos = document.BaseStream.Position;
            try
            {
                document.BaseStream.Position = 0;
                // One call, one parse of the document: rendering page by page through
                // RenderPageToSvg(BaseStream, i) would re-open and re-parse the whole file per page.
                string[] pages = PdfSvgRenderer.RenderAllPagesToSvg(document.BaseStream, renderOptions);

                byte[] bytes = Encoding.UTF8.GetBytes(Combine(pages));
                output.Write(bytes, 0, bytes.Length);
            }
            finally
            {
                document.BaseStream.Position = savedPos;
            }
        }

        // ────────────────────────────────────────────────────────────────────
        //  Combining
        // ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Wraps <paramref name="pages"/> — each a complete SVG document — in one root
        /// <c>&lt;svg&gt;</c>, stacked vertically.
        /// </summary>
        internal static string Combine(IReadOnlyList<string> pages)
        {
            XNamespace svg   = SvgNamespace;
            XNamespace xlink = XlinkNamespace;

            var root = new XElement(svg + "svg",
                new XAttribute(XNamespace.Xmlns + "xlink", xlink.NamespaceName));

            double totalHeight = 0d;
            double maxWidth    = 0d;

            for (int i = 0; pages != null && i < pages.Count; i++)
            {
                XElement? page;
                try
                {
                    // Under the rule every XML reader here keeps (CLAUDE.md): no DTD, no resolver. The page
                    // is the renderer's own output, whose text is the document's — escaped, but a reader
                    // that would expand a DTD's entities is one escape away from it.
                    // IgnoreWhitespace, as XDocument.Parse(string) reads.
                    var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreWhitespace = true };
                    using (var reader = XmlReader.Create(new System.IO.StringReader(pages[i]), settings))
                        page = XDocument.Load(reader).Root;
                }
                catch (XmlException ex)
                {
                    // The renderer produced something this plugin cannot combine. Refusing beats
                    // emitting a document that claims to hold the page and does not.
                    throw new InvalidOperationException(
                        $"SvgExportPlugin: the SVG rendered for page {i + 1} is not well-formed XML " +
                        $"and cannot be combined: {ex.Message}", ex);
                }
                if (page == null) continue;

                NamespaceIds(page, "p" + i.ToString(CultureInfo.InvariantCulture) + "-");

                double w = Length(page.Attribute("width"));
                double h = Length(page.Attribute("height"));

                // Nested viewport: x/y place the page, width/height keep its extent, and its own
                // viewBox (untouched) maps its content into that box exactly as it did standalone.
                page.SetAttributeValue("x", Num(0d));
                page.SetAttributeValue("y", Num(totalHeight));

                root.Add(page);

                totalHeight += h + (i + 1 < pages.Count ? PageGap : 0d);
                if (w > maxWidth) maxWidth = w;
            }

            root.SetAttributeValue("width",   Num(maxWidth));
            root.SetAttributeValue("height",  Num(totalHeight));
            root.SetAttributeValue("viewBox", Num(0d) + " " + Num(0d) + " " + Num(maxWidth) + " " + Num(totalHeight));

            return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" + root.ToString(SaveOptions.None);
        }

        /// <summary>
        /// Rewrites every <c>id</c> in <paramref name="page"/> to <paramref name="prefix"/> + id,
        /// and every reference to one of those ids with it.
        /// </summary>
        /// <remarks>
        /// References reach an id through <c>url(#id)</c> (<c>clip-path</c>, <c>mask</c>,
        /// <c>fill</c>, <c>stroke</c>) and through <c>#id</c> (<c>xlink:href</c> on
        /// <c>&lt;use&gt;</c>). Rather than enumerate those attribute names — a list that goes
        /// stale the moment the renderer emits a new one — every attribute value is scanned for
        /// <c>#token</c> and rewritten only when <c>token</c> is an id this page actually defines.
        /// That set membership is what keeps a colour literal such as <c>#ff0000</c> untouched.
        /// </remarks>
        private static void NamespaceIds(XElement page, string prefix)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var e in page.DescendantsAndSelf())
                foreach (var a in e.Attributes())
                    if (IsIdAttribute(a) && !string.IsNullOrEmpty(a.Value))
                        ids.Add(a.Value);

            if (ids.Count == 0) return;

            foreach (var e in page.DescendantsAndSelf())
                foreach (var a in e.Attributes())
                {
                    if (IsIdAttribute(a)) { a.Value = prefix + a.Value; continue; }

                    string value = a.Value;
                    if (value.IndexOf('#') < 0) continue;
                    a.Value = PrefixIdReferences(value, ids, prefix);
                }
        }

        private static bool IsIdAttribute(XAttribute a) =>
            a.Name.Namespace == XNamespace.None &&
            string.Equals(a.Name.LocalName, "id", StringComparison.Ordinal);

        /// <summary>
        /// <paramref name="value"/> with <paramref name="prefix"/> put after every <c>#</c> whose
        /// name is one of <paramref name="ids"/>. A name is taken whole — a letter or underscore,
        /// then letters, digits, <c>_</c>, <c>.</c> and <c>-</c>, as far as they go — and looked up
        /// in the page's own id set, so a non-id <c>#…</c> (a colour) is left alone.
        /// </summary>
        /// <remarks>
        /// A scan, not the regular expression <c>#([A-Za-z_][A-Za-z0-9_.\-]*)</c>: attribute values
        /// carry the document's own text (a link's URI), and a scan needs no timeout. The test holds
        /// the scan to that pattern's answers.
        /// </remarks>
        internal static string PrefixIdReferences(string value, HashSet<string> ids, string prefix)
        {
            StringBuilder? rewritten = null;
            int copied = 0;
            for (int hash = value.IndexOf('#'); hash >= 0; hash = value.IndexOf('#', hash))
            {
                int start = hash + 1, end = start;
                if (end < value.Length && IsIdStart(value[end]))
                    do end++; while (end < value.Length && IsIdChar(value[end]));

                if (end > start && ids.Contains(value.Substring(start, end - start)))
                {
                    rewritten ??= new StringBuilder(value.Length + prefix.Length);
                    rewritten.Append(value, copied, start - copied).Append(prefix);
                    copied = start;
                }
                hash = end;
            }
            if (rewritten == null) return value;
            return rewritten.Append(value, copied, value.Length - copied).ToString();
        }

        private static bool IsIdStart(char c) => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || c == '_';

        private static bool IsIdChar(char c) => IsIdStart(c) || (c >= '0' && c <= '9') || c == '.' || c == '-';

        /// <summary>
        /// Reads a length attribute the renderer wrote. It emits bare numbers, but a unit suffix is
        /// tolerated so a future change to the renderer degrades to "0" rather than to a throw.
        /// </summary>
        private static double Length(XAttribute? a)
        {
            if (a == null) return 0d;
            string s = a.Value;
            if (string.IsNullOrEmpty(s)) return 0d;

            int end = 0;
            while (end < s.Length &&
                   (char.IsDigit(s[end]) || s[end] == '.' || s[end] == '-' || s[end] == '+' ||
                    s[end] == 'e' || s[end] == 'E'))
                end++;

            return double.TryParse(s.Substring(0, end), NumberStyles.Float,
                CultureInfo.InvariantCulture, out double v) && !double.IsNaN(v) && !double.IsInfinity(v)
                ? v : 0d;
        }

        /// <summary>
        /// Formats a coordinate for SVG. <see cref="CultureInfo.InvariantCulture"/> is not a
        /// nicety: a comma-decimal host would emit <c>height="1584,0"</c>, which an SVG viewer
        /// reads as an invalid length. Fixed-point, never six significant digits: past 999 999 units
        /// of stacked pages (some 1 263 Letter pages) <c>G6</c> would round each page's <c>y</c> to
        /// the nearest ten and the pages would overlap.
        /// </summary>
        private static string Num(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);

        // ────────────────────────────────────────────────────────────────────
        //  Options
        // ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Maps <see cref="ExportOptions.Extra"/> onto <see cref="PdfRenderOptions"/>, refusing any
        /// key it cannot honour. Returns <c>null</c> for an empty bag, which is what every renderer
        /// entry point treats as "defaults".
        /// </summary>
        /// <remarks>
        /// <see cref="PdfRenderOptions"/>' properties are <c>init</c>-only, so the values are read
        /// into locals first and the object is built once.
        ///
        /// <para>The reader is a private copy rather than a shared helper on purpose: a
        /// plugin → plugin reference is banned outright (CLAUDE.md) and promoting four boolean
        /// coercions into <c>Core</c> would put an adapter's convenience into the public surface of
        /// the assembly every consumer depends on.</para>
        /// </remarks>
        private static PdfRenderOptions? ReadRenderOptions(ExportOptions? options)
        {
            var extra = options?.Extra;
            if (extra == null || extra.Count == 0) return null;

            var defaults = PdfRenderOptions.Default;
            bool simulateOverprint = defaults.SimulateOverprint;
            bool useTrimBox  = defaults.UseTrimBox;
            bool useBleedBox = defaults.UseBleedBox;
            bool useArtBox   = defaults.UseArtBox;

            RejectUnknownKeys(extra,
                nameof(PdfRenderOptions.SimulateOverprint),
                nameof(PdfRenderOptions.UseTrimBox),
                nameof(PdfRenderOptions.UseBleedBox),
                nameof(PdfRenderOptions.UseArtBox));

            ReadBool(extra, nameof(PdfRenderOptions.SimulateOverprint), ref simulateOverprint);
            ReadBool(extra, nameof(PdfRenderOptions.UseTrimBox),  ref useTrimBox);
            ReadBool(extra, nameof(PdfRenderOptions.UseBleedBox), ref useBleedBox);
            ReadBool(extra, nameof(PdfRenderOptions.UseArtBox),   ref useArtBox);

            return new PdfRenderOptions
            {
                SimulateOverprint = simulateOverprint,
                UseTrimBox  = useTrimBox,
                UseBleedBox = useBleedBox,
                UseArtBox   = useArtBox,
            };
        }

        private static void RejectUnknownKeys(IReadOnlyDictionary<string, object> extra,
                                              params string[] recognised)
        {
            var known = new HashSet<string>(recognised, StringComparer.OrdinalIgnoreCase);
            var unknown = new List<string>();
            foreach (var key in extra.Keys)
                if (!known.Contains(key)) unknown.Add(key);
            if (unknown.Count == 0) return;

            unknown.Sort(StringComparer.Ordinal);
            Array.Sort(recognised, StringComparer.Ordinal);
            throw new ArgumentException(
                "SvgExportPlugin: unrecognised option key(s) " +
                string.Join(", ", unknown.ConvertAll(k => "'" + k + "'")) +
                ". Recognised keys are " +
                string.Join(", ", Array.ConvertAll(recognised, k => "'" + k + "'")) +
                ". A dropped setting is indistinguishable from an honoured one, so the request is " +
                "refused rather than ignored.",
                "options");
        }

        private static void ReadBool(IReadOnlyDictionary<string, object> extra, string key, ref bool value)
        {
            object? raw = null;
            bool found = false;
            foreach (var kvp in extra)
            {
                if (string.Equals(kvp.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    raw = kvp.Value;
                    found = true;
                    break;
                }
            }
            if (!found) return;

            switch (raw)
            {
                case bool b: value = b; return;
                case string s when bool.TryParse(s, out bool parsed): value = parsed; return;
                default:
                    throw new ArgumentException(
                        $"SvgExportPlugin: option '{key}' must be a boolean, but was " +
                        (raw == null ? "null" : "'" + Convert.ToString(raw, CultureInfo.InvariantCulture) +
                                                "' (" + raw.GetType().Name + ")") +
                        ". The request is refused rather than silently defaulted.",
                        "options");
            }
        }
    }
}
