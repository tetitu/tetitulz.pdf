// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Plugins.Conformance
{
    public static partial class PdfAValidator
    {
        /// <summary>
        /// A86 — PDF/A-2 to -4: "a … compliant document shall not contain a reference to the .notdef glyph
        /// from any of the text showing operators, regardless of text rendering mode, in any content
        /// stream" (ISO 19005-2/3 6.2.11.8, 19005-4 6.2.10.9; the validation rules of those numbers, whose test is
        /// the shown glyph's name). PDF/A-1 has no such clause. Which codes reference <c>.notdef</c> is
        /// <see cref="NotdefReferences"/>'s answer, the one PDF/UA-2's §8.4.5.9 check asks; every string
        /// shown in any mode is read (<see cref="PdfUaFontUse.ShownStrings"/>).
        /// </summary>
        /// <remarks>
        /// A font this library cannot read is not judged and is said in <see cref="LastValidationError"/>,
        /// as A84's are — never an A99, which would refuse <see cref="PdfAConverter"/> a file the profiles
        /// passes. Content the walk could not finish is an A99: a real check cut short.
        /// </remarks>
        private static void CheckNotdefReferences(PdfDocument doc, PdfAConformancePart part, List<PdfAViolation> v)
        {
            if (part == PdfAConformancePart.Part1 || doc?.Pages == null) return;
            var readers = new Dictionary<PdfDictionary, NotdefReferences>(PdfReferenceEquality.Instance);
            var listed = new HashSet<string>(System.StringComparer.Ordinal);
            var found = new Occurrences();
            bool complete = PdfUaFontUse.ShownStrings(doc,
                PdfXValidator.DefaultMaxPageContentBytes, PdfXValidator.DefaultMaxDocumentContentBytes, (font, shown) =>
                {
                    if (!readers.TryGetValue(font, out NotdefReferences reader))
                    {
                        reader = NotdefReferences.For(font, doc);
                        readers[font] = reader;
                    }
                    foreach (NotdefReference reference in reader.In(shown))
                    {
                        string entry = FontLabel(doc, font) + ": " + Describe(reference);
                        if (listed.Add(entry)) found.Add(entry);
                    }
                });
            if (!complete)
                ReportPartial("The A86 .notdef check did not read every content stream" +
                    (PdfUaFontUse.LastError != null ? " — " + PdfUaFontUse.LastError : "") + ".", "A86");
            foreach (var reader in readers)
                if (reader.Value.Unreadable != null)
                    Report("A86", FontLabel(doc, reader.Key) + ": " + reader.Value.Unreadable);

            if (found.Count > 0)
                v.Add(new PdfAViolation("A86",
                    $"A text-showing operator references the .notdef glyph: {found} (" +
                    (part == PdfAConformancePart.Part4 ? "ISO 19005-4 6.2.10.9; rule 6.2.10.9-1"
                        : $"ISO 19005-{((int)part).ToString(CultureInfo.InvariantCulture)} 6.2.11.8; rule 6.2.11.8-1") + ")."));
        }

        private static string FontLabel(PdfDocument doc, PdfDictionary font)
            => (doc.ResolveObject(font[PdfName.BaseFont]) as PdfName)?.Value ?? "a font with no /BaseFont";

        private static string Describe(NotdefReference reference)
        {
            string code = "code " + reference.Code.ToString(CultureInfo.InvariantCulture);
            switch (reference.Cause)
            {
                case NotdefCause.Cid0: return code + " selects CID 0";
                case NotdefCause.NoCodespace: return code + " matches no codespace range of the CMap";
                case NotdefCause.NoCidMapping: return code + " is mapped by no cidchar or cidrange of the CMap";
                case NotdefCause.GlyphZero: return code + "'s /Differences name /" + reference.GlyphName + " resolves to glyph 0";
                case NotdefCause.NameNotInProgram: return code + "'s /Differences name /" + reference.GlyphName + " is not in the embedded program";
                case NotdefCause.UnnamedCode: return code + " is named by no entry of the font's encoding";
                default: return code + "'s /Differences name is .notdef";
            }
        }
    }
}
