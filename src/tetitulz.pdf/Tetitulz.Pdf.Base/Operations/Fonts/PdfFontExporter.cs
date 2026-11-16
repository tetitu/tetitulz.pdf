// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Base.Operations.Fonts
{
    /// <summary>One embedded font program, as a file a font tool can open.</summary>
    public sealed class PdfExtractedFont
    {
        internal PdfExtractedFont(string baseFont, string subtype, string programKey,
                                  string fileExtension, byte[] data, bool isSubset,
                                  FontEmbeddingLicense license)
        {
            BaseFont = baseFont;
            Subtype = subtype;
            ProgramKey = programKey;
            FileExtension = fileExtension;
            Data = data;
            IsSubset = isSubset;
            License = license;
        }

        /// <summary>The font's <c>/BaseFont</c>, subset tag retained.</summary>
        public string BaseFont { get; }

        /// <summary>The font dictionary's <c>/Subtype</c> — <c>Type1</c>, <c>TrueType</c>, <c>Type0</c>…</summary>
        public string Subtype { get; }

        /// <summary>Which descriptor key held the program: <c>FontFile</c>, <c>FontFile2</c> or <c>FontFile3</c>.</summary>
        public string ProgramKey { get; }

        /// <summary>The extension the bytes should be saved under — <c>.pfb</c>, <c>.ttf</c> or <c>.otf</c>.</summary>
        public string FileExtension { get; }

        /// <summary>
        /// The program, <b>as a file</b>: PFB sectors rebuilt for Type 1, an OTF envelope added
        /// around a bare CFF. Never the raw stream bytes for those two — see
        /// <see cref="PdfFontExporter"/>.
        /// </summary>
        public byte[] Data { get; }

        /// <summary>
        /// The <c>/BaseFont</c> carries a <c>ABCDEF+</c> subset tag (§9.6.4), so the program holds
        /// only the glyphs the document used.
        /// </summary>
        /// <remarks>
        /// <b>Worth checking before doing anything with the file.</b> A subset is not the foundry's
        /// font: most of its glyphs are absent, and installing or redistributing it is neither
        /// useful nor, generally, permitted.
        /// </remarks>
        public bool IsSubset { get; }

        /// <summary>
        /// What the program's own <c>OS/2</c> <c>fsType</c> permits, when it has one.
        /// </summary>
        /// <remarks>
        /// Surfaced because extracting a font is the operation most likely to redistribute one, and
        /// the field is the only statement the file makes about that. It is reported, never
        /// enforced: the caller holds whatever licence applies, and this library cannot see it.
        /// </remarks>
        public FontEmbeddingLicense License { get; }
    }

    /// <summary>
    /// Extracts the font programs a document embeds, as files rather than as raw stream bytes.
    /// </summary>
    /// <remarks>
    /// <para><b>Two of the four flavours are not files, and that is what makes this more than
    /// <c>GetDecodedData</c>.</b> <c>/FontFile2</c> is a bare sfnt (a <c>.ttf</c> as it stands) and
    /// <c>/FontFile3</c> <c>/OpenType</c> is a <c>.otf</c> as it stands — but <c>/FontFile</c> is a
    /// Type 1 blob whose PFB sector headers have to be rebuilt from <c>/Length1..3</c>, and
    /// <c>/FontFile3</c> <c>/Type1C</c> or <c>/CIDFontType0C</c> is a <b>bare CFF</b> with no sfnt
    /// envelope. Handing a caller those bytes yields a file no font tool opens. Both repairs already
    /// existed inside the renderer and neither was reachable.</para>
    ///
    /// <para><b><c>/FontFile3</c>'s own <c>/Subtype</c> has to be read, and nothing in the tree read
    /// it.</b> <c>FontCache</c> hard-codes "FontFile3 means CFF", which is harmless there because it
    /// tries <c>SKTypeface.FromData</c> first — and fatal here, because an <c>/OpenType</c> program
    /// is already an sfnt and wrapping it again produces a corrupt double envelope.</para>
    ///
    /// <para><b>Licensing is reported, not enforced.</b> Extraction is the operation most likely to
    /// redistribute a font, so every result carries its <see cref="FontEmbeddingLicense"/> and its
    /// <see cref="PdfExtractedFont.IsSubset"/> flag. The caller holds whatever licence applies and
    /// this library cannot see it — the same rule <c>SystemFontResolver</c> applies to a
    /// caller-supplied program.</para>
    /// </remarks>
    public static class PdfFontExporter
    {
        [ThreadStatic] private static string _lastError;

        /// <summary>
        /// Why a font this document embeds is missing from the result — or null when none is.
        /// Cleared at the start of every call, on this thread.
        /// </summary>
        /// <remarks>
        /// An empty list is the correct answer for a document that embeds nothing, so without this a
        /// caller cannot tell "no embedded fonts" from "three of them would not decode".
        /// </remarks>
        public static string LastError => _lastError;

        /// <summary>Extracts every embedded font program in the document.</summary>
        /// <param name="input">The PDF to read. Not disposed.</param>
        /// <remarks>
        /// A font embedded once and referenced from several pages is returned once: results are
        /// de-duplicated by the descriptor — its object number, or the dictionary itself when it is
        /// written directly inside its font, which has none — so a 40-page document with one font
        /// does not yield forty copies of it.
        /// </remarks>
        public static IReadOnlyList<PdfExtractedFont> ExtractFonts(Stream input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            _lastError = null;

            using var doc = PdfDocument.Open(input);
            // Opened without its password, an encrypted document has no pages, so this returned an
            // empty list with no error: "embeds no fonts", for a document whose fonts it never saw.
            // Said through LastError rather than thrown, the way the facade's reads say it
            // (PdfOperations.LastExtractionError): an empty answer, and the reason beside it.
            if (doc.IsEncrypted)
            {
                _lastError = "the document is encrypted and was not decrypted, so no font was read " +
                             "(pass the password to PdfOperations.ExtractFonts)";
                return new List<PdfExtractedFont>();
            }

            var results = new List<PdfExtractedFont>();
            var seenDescriptors = new HashSet<int>();
            var seenDirect = new HashSet<PdfDictionary>(PdfReferenceEquality.Instance);
            var problems = new List<string>();

            foreach (var fontDict in EnumerateFontDictionaries(doc))
            {
                PdfDictionary descriptor = ResolveDescriptor(fontDict, doc, out int descriptorObjNum);
                if (descriptor == null) continue;
                // A direct descriptor has no object number, so it is de-duplicated by identity;
                // otherwise a font dictionary several pages reach would come back once per page.
                if (descriptorObjNum > 0 ? !seenDescriptors.Add(descriptorObjNum) : !seenDirect.Add(descriptor)) continue;

                string baseFont = (PdfResolve.Name(doc, fontDict, PdfName.BaseFont))?.Value;
                string subtype = (PdfResolve.Name(doc, fontDict, PdfName.Subtype))?.Value;

                var extracted = TryExtract(doc, descriptor, baseFont, subtype, problems);
                if (extracted != null) results.Add(extracted);
            }

            if (problems.Count > 0) _lastError = string.Join("; ", problems);
            return results;
        }

        // ----------------------------------------------------------------

        private static PdfExtractedFont TryExtract(
            PdfDocument doc, PdfDictionary descriptor, string baseFont, string subtype,
            List<string> problems)
        {
            foreach (PdfName key in new[] { PdfName.FontFile2, PdfName.FontFile3, PdfName.FontFile })
            {
                if (!(PdfResolve.Value(doc, descriptor[key]) is PdfStream program)) continue;

                byte[] raw;
                try { raw = Core.Fonts.FontStreamBytes.Program(program); }
                catch (Exception ex)
                {
                    problems.Add((baseFont ?? "an unnamed font") + "'s /" + key.Value +
                                 " could not be decoded (" + ex.Message + ")");
                    return null;
                }

                if (raw == null || raw.Length < 4)
                {
                    problems.Add((baseFont ?? "an unnamed font") + "'s /" + key.Value +
                                 " decoded to nothing usable");
                    return null;
                }

                byte[] file;
                string extension;

                if (key == PdfName.FontFile2)
                {
                    file = raw;            // a bare sfnt already is a .ttf
                    extension = ".ttf";
                }
                else if (key == PdfName.FontFile3)
                {
                    // The /Subtype decides, and nothing else in the tree reads it. /OpenType is
                    // already an sfnt; /Type1C and /CIDFontType0C are bare CFF and need an envelope.
                    string programSubtype = (PdfResolve.Name(doc, program, PdfName.Subtype))?.Value;
                    extension = ".otf";

                    if (string.Equals(programSubtype, "OpenType", StringComparison.Ordinal))
                    {
                        file = raw;
                    }
                    else
                    {
                        file = WrapBareCff(raw);
                        if (file == null)
                        {
                            problems.Add((baseFont ?? "an unnamed font") +
                                         "'s CFF program could not be wrapped as OpenType, so it was " +
                                         "skipped rather than returned as bytes no font tool opens");
                            return null;
                        }
                    }
                }
                else
                {
                    // §9.9 Type 1: the three segments, whose PFB sector headers a file needs.
                    // Resolved, because /Length1..3 are routinely written as indirect references —
                    // reading them raw yields 0 and the wrapping is silently skipped.
                    int l1 = PdfResolve.Int(doc, program, PdfName.Length1, 0);
                    int l2 = PdfResolve.Int(doc, program, PdfName.Length2, 0);
                    int l3 = PdfResolve.Int(doc, program, PdfName.Length3, 0);

                    file = PfbAssembler.Build(raw, l1, l2, l3);
                    extension = ".pfb";
                    if (file == null)
                    {
                        problems.Add((baseFont ?? "an unnamed font") +
                                     "'s /FontFile does not carry usable /Length1 and /Length2, so " +
                                     "its PFB segments could not be rebuilt");
                        return null;
                    }
                }

                string name = baseFont ?? string.Empty;
                return new PdfExtractedFont(
                    baseFont, subtype, key.Value, extension, file,
                    isSubset: name.Length > 0 && SubsetTag.Strip(name) != name,
                    license: FontEmbeddingLicense.Read(file));
            }

            return null;   // not embedded — a standard-14 or an externally-referenced font
        }

        /// <summary>Puts an sfnt envelope round a bare CFF, reusing the renderer's wrapper.</summary>
        private static byte[] WrapBareCff(byte[] rawCff)
        {
            try
            {
                var parsed = Core.Fonts.Cff.CffParser.Parse(rawCff);
                return parsed == null ? null : CffOtfWrapper.TryBuild(rawCff, parsed);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// The descriptor a font dictionary's program lives on — for a Type0, that is the
        /// <b>descendant</b> CIDFont's, not the Type0's own.
        /// </summary>
        private static PdfDictionary ResolveDescriptor(
            PdfDictionary fontDict, PdfDocument doc, out int objectNumber)
        {
            objectNumber = 0;
            PdfObject fdObj = fontDict[PdfName.FontDescriptor];

            if (fdObj == null)
            {
                var descendants = PdfResolve.Array(doc, fontDict, PdfName.DescendantFonts);
                if (descendants != null && descendants.Count > 0 &&
                    PdfResolve.Value(doc, descendants[0]) is PdfDictionary descendant)
                    fdObj = descendant[PdfName.FontDescriptor];
            }

            if (fdObj is PdfReference reference) objectNumber = reference.ReferenceObjectNumber;
            return PdfResolve.Value(doc, fdObj) as PdfDictionary;
        }

        /// <summary>
        /// Every font dictionary the pages reach — through their own resources and every form
        /// XObject, tiling pattern, Type 3 font and annotation appearance under them.
        /// </summary>
        /// <remarks>
        /// <b>Not only each page's own <c>/Font</c></b>: a font used only inside a form XObject — an
        /// imported page, a stamp, a letterhead placed by a layout tool, a filled field's appearance
        /// — is extracted too. The walk is
        /// <see cref="Images.DocumentImageIndex"/>'s, with its cycle guard and depth cap. The same
        /// font dictionary is reached once per page that uses it, so it can arrive here many times;
        /// the descriptor check in <see cref="ExtractFonts"/> returns it once.
        /// </remarks>
        private static IEnumerable<PdfDictionary> EnumerateFontDictionaries(PdfDocument doc)
        {
            var found = new List<PdfDictionary>();
            Images.DocumentImageIndex.Build(doc, onResources: (_, resources) =>
            {
                var fonts = PdfResolve.Dict(doc, resources, PdfName.Font);
                if (fonts == null) return;
                foreach (var entry in fonts)
                    if (PdfResolve.Value(doc, entry.Value) is PdfDictionary fontDict)
                        found.Add(fontDict);
            });
            return found;
        }
    }
}
