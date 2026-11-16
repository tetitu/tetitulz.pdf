// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using System.Xml;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Plugins.Conformance
{
    public static partial class PdfAValidator
    {
        /// <summary>
        /// The document-level rules. A60 — no <c>/NeedAppearances true</c> (ISO 19005-1 6.9, 19005-2/3/4
        /// 6.4.1). A61 — PDF/A-2 onwards: a permissions dictionary holds only <c>/UR3</c> and
        /// <c>/DocMDP</c> (only <c>/DocMDP</c> in PDF/A-4), and in PDF/A-2 and -3 the DocMDP signature's
        /// references carry no <c>/DigestLocation</c>, <c>/DigestMethod</c> or <c>/DigestValue</c>
        /// (19005-2/3 6.1.12, 19005-4 6.1.11). A62 — PDF/A-4: a catalog <c>/Version</c> reads 2.d
        /// (6.1.12). A63 — PDF/A-2 onwards: no <c>/AlternatePresentations</c> in the name dictionary
        /// and no <c>/PresSteps</c> on a page (19005-2/3 6.10, 19005-4 6.11). A64 — the XMP packet's
        /// header carries no <c>bytes</c> or <c>encoding</c> attribute (19005-1 6.7.5, 19005-2/3
        /// 6.6.2.1, 19005-4 6.7.2.1). A69 — PDF/A-1: no <c>/OCProperties</c> in the catalog (19005-1
        /// 6.1.13). A73 — level A of PDF/A-1 to -3: a <c>/MarkInfo</c> whose <c>/Marked</c> is true
        /// (19005-1 6.8.2.2, 19005-2/3 6.7.2.2). A79 — PDF/A-2 onwards: the catalog's metadata stream is
        /// encoded as UTF-8 (19005-2/3 6.6.2.1, 19005-4 6.7.2.1). the validation rules of those numbers.
        /// </summary>
        private static void CheckDocumentLevel(PdfDocument doc, PdfAConformancePart part, PdfAIdentification identification,
            string conformance, List<PdfAViolation> v)
        {
            var catalog = doc?.Catalog?.Dictionary;
            if (catalog == null) return;
            bool part1 = part == PdfAConformancePart.Part1;
            bool part4 = part == PdfAConformancePart.Part4;
            string iso = $"ISO 19005-{(int)part}";

            if (doc.ResolveObject(catalog[PdfName.AcroForm]) is PdfDictionary acroForm
                && (doc.ResolveObject(acroForm[PdfName.NeedAppearances]) as PdfBoolean)?.Value == true)
                v.Add(new PdfAViolation("A60",
                    $"The interactive form dictionary sets /NeedAppearances true ({iso} " +
                    (part1 ? "6.9; rule 6.9-1" : part4 ? "6.4.1; rule 6.4.1-2" : "6.4.1; rule 6.4.1-3") + ")."));

            if (!part1 && doc.ResolveObject(catalog[PdfName.Perms]) is PdfDictionary perms)
            {
                var problems = new Occurrences();
                foreach (var key in perms.Keys)
                    if (key.Value != PdfName.DocMDP.Value && (part4 || key.Value != PdfName.UR3.Value))
                        problems.Add("/Perms /" + key.Value);
                if (!part4 && doc.ResolveObject(perms[PdfName.DocMDP]) is PdfDictionary signature
                    && doc.ResolveObject(signature[PdfName.Reference]) is PdfArray references)
                    for (int i = 0; i < references.Count; i++)
                        if (doc.ResolveObject(references[i]) is PdfDictionary reference)
                            foreach (var key in reference.Keys)
                                if (key.Value == "DigestLocation" || key.Value == "DigestMethod" || key.Value == "DigestValue")
                                    problems.Add($"the DocMDP signature's /Reference {i.ToString(CultureInfo.InvariantCulture)} /{key.Value}");
                if (problems.Count > 0)
                    v.Add(new PdfAViolation("A61",
                        $"The permissions hold what the part forbids: {problems} ({iso} " +
                        (part4 ? "6.1.11; rule 6.1.11-1" : "6.1.12; rules 6.1.12-1/-2") + ")."));
            }

            if (part4 && catalog[PdfName.Version] != null)
            {
                string version = doc.ResolveObject(catalog[PdfName.Version]) is PdfName name ? name.Value : null;
                if (version == null || version.Length != 3 || version[0] != '2' || version[1] != '.' || version[2] < '0' || version[2] > '9')
                    v.Add(new PdfAViolation("A62",
                        $"The catalog's /Version is not 2.d: {(version == null ? "not a name" : "/" + version)} " +
                        "(ISO 19005-4 6.1.12; rule 6.1.12-1)."));
            }

            if (!part1)
            {
                var presentations = new Occurrences();
                if (doc.ResolveObject(catalog[PdfName.Names]) is PdfDictionary names && names[PdfName.AlternatePresentations] != null)
                    presentations.Add("/Names /AlternatePresentations");
                if (doc.Pages != null)
                    for (int p = 0; p < doc.Pages.Count; p++)
                        if (doc.Pages[p].Dictionary?[PdfName.PresSteps] != null)
                            presentations.Add("page " + (p + 1).ToString(CultureInfo.InvariantCulture) + " /PresSteps");
                if (presentations.Count > 0)
                    v.Add(new PdfAViolation("A63",
                        $"An alternate presentation or a presentation step: {presentations} ({iso} " +
                        (part4 ? "6.11; rules 6.11-1/-2" : "6.10; rules 6.10-1/-2") + ")."));
            }

            if (part1 && catalog[PdfName.OCProperties] != null)
                v.Add(new PdfAViolation("A69",
                    "The catalog has /OCProperties: PDF/A-1 has no optional content (ISO 19005-1 6.1.13; rule 6.1.13-1)."));

            if (!part4 && conformance == "A")
            {
                var markInfo = doc.ResolveObject(catalog[PdfName.MarkInfo]) as PdfDictionary;
                var marked = doc.ResolveObject(markInfo?[PdfName.Marked]);
                string problem = markInfo == null ? "the catalog has no /MarkInfo"
                    : marked == null ? "/MarkInfo has no /Marked"
                    : (marked as PdfBoolean)?.Value == true ? null
                    : "/MarkInfo /Marked is not true";
                if (problem != null)
                    v.Add(new PdfAViolation("A73",
                        $"A level A file is not marked: {problem} ({iso} " +
                        (part1 ? "6.8.2.2; rule 6.8.2.2-1" : "6.7.2.2; rule 6.7.2.2-1") + ")."));
            }

            if (!part1 && doc.ResolveObject(catalog[PdfName.Metadata]) is PdfStream metadata)
            {
                string notUtf8 = WhyNotUtf8(metadata);
                if (notUtf8 != null)
                    v.Add(new PdfAViolation("A79",
                        $"The XMP metadata stream is not encoded as UTF-8: {notUtf8} ({iso} " +
                        (part4 ? "6.7.2.1; rule 6.7.2.1-5" : "6.6.2.1; rule 6.6.2.1-5") + ")."));
            }

            var header = PacketHeader(identification?.Packet);
            if (header != null)
            {
                var attributes = new List<string>();
                foreach (string attribute in new[] { "bytes", "encoding" })
                    if (HasPseudoAttribute(header.Data, attribute)) attributes.Add(attribute);
                if (attributes.Count > 0)
                    v.Add(new PdfAViolation("A64",
                        $"The XMP packet's header carries the {string.Join(" and ", attributes)} attribute ({iso} " +
                        (part1 ? "6.7.5; rules 6.7.5-1/-2" : part4 ? "6.7.2.1; rules 6.7.2.1-2/-3"
                            : "6.6.2.1; rules 6.6.2.1-2/-3") + ")."));
            }
        }

        /// <summary>
        /// Why a metadata stream's bytes are not UTF-8 — a UTF-16 or UTF-32 byte-order mark, or bytes a
        /// strict UTF-8 decoder refuses — or <see langword="null"/>; a stream that will not decode is
        /// A07's.
        /// </summary>
        private static string WhyNotUtf8(PdfStream metadata)
        {
            byte[] bytes;
            try
            {
                bytes = XmpStreamBytes.Read(metadata);
            }
            catch (System.Exception)
            {
                return null;
            }
            if (bytes == null || bytes.Length == 0) return null;
            if (bytes.Length >= 4 && bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0xFE && bytes[3] == 0xFF) return "a UTF-32 byte-order mark";
            if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0 && bytes[3] == 0) return "a UTF-32 byte-order mark";
            if (bytes.Length >= 2 && ((bytes[0] == 0xFE && bytes[1] == 0xFF) || (bytes[0] == 0xFF && bytes[1] == 0xFE))) return "a UTF-16 byte-order mark";
            try
            {
                new System.Text.UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
                return null;
            }
            catch (System.Text.DecoderFallbackException)
            {
                return "bytes that are not UTF-8";
            }
        }

        /// <summary>The packet's header, <c>&lt;?xpacket begin=…?&gt;</c>: the first <c>xpacket</c> processing instruction.</summary>
        private static XmlProcessingInstruction PacketHeader(XmlDocument packet)
        {
            if (packet == null) return null;
            foreach (XmlNode node in packet.ChildNodes)
                if (node is XmlProcessingInstruction pi && pi.Name == "xpacket")
                    return pi.Data.Contains("begin") ? pi : null;
            return null;
        }

        /// <summary>Whether a processing instruction's data names <paramref name="name"/> as a pseudo-attribute (<c>name=</c>).</summary>
        private static bool HasPseudoAttribute(string data, string name)
        {
            for (int at = data.IndexOf(name, System.StringComparison.Ordinal); at >= 0;
                 at = data.IndexOf(name, at + 1, System.StringComparison.Ordinal))
            {
                bool startsWord = at == 0 || char.IsWhiteSpace(data[at - 1]);
                int after = at + name.Length;
                while (after < data.Length && char.IsWhiteSpace(data[after])) after++;
                if (startsWord && after < data.Length && data[after] == '=') return true;
            }
            return false;
        }
    }
}
