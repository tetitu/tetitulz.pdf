// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Accessibility.Editing;
using Tetitulz.Pdf.Accessibility.Internal;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Fonts;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Accessibility.Fonts
{
    public static partial class PdfFontEmbeddingRemediator
    {
        // ================================================================
        //  Embedding one font
        // ================================================================

        private readonly struct EmbedOutcome
        {
            internal EmbedOutcome(bool embedded, bool subsetted, string skipReason)
            {
                Embedded   = embedded;
                Subsetted  = subsetted;
                SkipReason = skipReason;
            }

            internal bool   Embedded   { get; }
            internal bool   Subsetted  { get; }
            internal string SkipReason { get; }

            internal static EmbedOutcome Ok(bool subsetted) => new EmbedOutcome(true, subsetted, null);
            internal static EmbedOutcome Skip(string reason) => new EmbedOutcome(false, false, reason);
        }

        private static EmbedOutcome EmbedOne(
            PdfDocument doc,
            PdfObjectIndex index,
            FontSite site,
            UsageScan usage,
            FontEmbeddingOptions options,
            Dictionary<int, PdfObject> modified,
            Dictionary<int, PdfObject> newObjects,
            List<string> warnings)
        {
            if (site.DescriptorOwner == null)
                return EmbedOutcome.Skip(
                    "the font is a Type0 composite with no readable /DescendantFonts entry, so there " +
                    "is no CIDFont to hang a /FontDescriptor on; repair the font dictionary first");

            // ---- 1. what does the caller want embedded? ----------------------------
            FontEmbeddingRequest request = BuildRequest(doc, site, usage);

            byte[] program = null;
            bool substituted = false;

            if (options.FontProvider != null)
            {
                try
                {
                    program = options.FontProvider(request);
                }
                catch (Exception ex)
                {
                    AccessibilityDiagnostics.Report(Op,
                        "the FontProvider threw for " + request + "; the font was left non-embedded", ex);
                    return EmbedOutcome.Skip(
                        "FontEmbeddingOptions.FontProvider threw for this font (" + ex.Message + ")");
                }
            }

            if (program == null || program.Length == 0)
            {
                if (!options.AllowSystemFontSubstitution)
                {
                    return EmbedOutcome.Skip(options.FontProvider == null
                        ? "no font source was available: FontEmbeddingOptions.FontProvider is null and " +
                          "AllowSystemFontSubstitution is false"
                        : "FontEmbeddingOptions.FontProvider returned no program for this font and " +
                          "AllowSystemFontSubstitution is false");
                }

                // Symbol and ZapfDingbats address a built-in symbolic encoding no installed text
                // font reproduces. Substituting one would silently replace every glyph on the page
                // with an unrelated Latin character, which is worse than the violation it closes.
                if (site.Standard14Name == StandardFonts.Symbol || site.Standard14Name == StandardFonts.ZapfDingbats)
                    return EmbedOutcome.Skip(
                        "the base font is " + site.Standard14Name + ", a symbolic font whose character " +
                        "codes address its own built-in encoding; no installed text font reproduces it, " +
                        "so substituting one would change every glyph it draws. Supply the real program " +
                        "through FontEmbeddingOptions.FontProvider instead");

                if (site.IsSymbolic)
                    return EmbedOutcome.Skip(
                        "the font descriptor declares the Symbolic flag, so its character codes address " +
                        "a built-in encoding that a substitute would not reproduce; supply the real " +
                        "program through FontEmbeddingOptions.FontProvider");

                // Refused here rather than after resolution so that no installed font is even
                // consulted: for a composite font the answer cannot depend on what this machine has.
                if (site.IsType0) return EmbedOutcome.Skip(CompositeSubstitutionRefusal);

                program = ResolveSystemFont(request, out string substitutionDetail);
                if (program == null)
                    return EmbedOutcome.Skip(substitutionDetail);

                substituted = true;
            }

            // ---- 2. is the program a format this step can embed correctly? ---------
            ProgramKind kind = ClassifyProgram(program, out string kindProblem);
            if (kind == ProgramKind.Unsupported)
                return EmbedOutcome.Skip(kindProblem);

            TrueTypeMetrics metrics = null;
            if (kind != ProgramKind.BareCff)
            {
                try
                {
                    metrics = PdfFontEmbedder.GetFontMetrics(program);
                }
                catch (Exception ex)
                {
                    AccessibilityDiagnostics.Report(Op,
                        "the supplied program for " + request + " could not be parsed", ex);
                    return EmbedOutcome.Skip(
                        "the supplied font program could not be parsed (" + ex.Message + ")");
                }
            }
            else if (site.Descriptor == null)
            {
                return EmbedOutcome.Skip(
                    "the supplied program is a bare CFF, whose metrics this step cannot read, and the " +
                    "font has no existing /FontDescriptor to take them from; supply an sfnt " +
                    "(TrueType/OpenType) program instead");
            }

            // ---- 3. plan the font-dictionary edits (widths / encoding) --------------
            SimpleFontPlan plan = null;
            if (!site.IsType0)
            {
                plan = PlanSimpleFont(doc, site, metrics);
                if (plan.SkipReason != null) return EmbedOutcome.Skip(plan.SkipReason);
            }
            else
            {
                // A composite font's codes are CIDs, and a CID selects a glyph by *index* into the
                // descendant's own program. There is no encoding layer to renegotiate and no
                // /Widths to preserve, so the safety a simple font gets from PlanSimpleFont has to
                // come from somewhere else — hence this.
                string compositeProblem = CheckCompositeBinding(site, kind, substituted);
                if (compositeProblem != null) return EmbedOutcome.Skip(compositeProblem);

                warnings.Add(
                    "Font " + request + " is a Type0 composite. Its character codes are CIDs that " +
                    "select glyphs by index in the CIDFont's own program (through /CIDToGIDMap, " +
                    "which defaults to /Identity), not through a Unicode cmap, and nothing in the " +
                    "file records which program the document was laid out against. The program you " +
                    "supplied was embedded on that assumption — if it is not that program, every " +
                    "glyph this font draws changes. Verify the output visually.");
            }

            // ---- 4. subset, when it is provably safe -------------------------------
            bool subsetted = false;
            if (options.SubsetEmbeddedFonts)
            {
                byte[] subset = TrySubset(program, kind, site, request, plan, out string subsetNote);
                if (subset != null)
                {
                    program   = subset;
                    subsetted = true;
                }
                else if (subsetNote != null)
                {
                    warnings.Add(subsetNote);
                }
            }

            // ---- 4b. when it could not be subsetted, at least prune it ---------------
            //
            // TrySubset declines three whole classes -- Type0 composites, CFF outlines, and any font
            // whose used-glyph set is not provably complete -- and rather than embed each of those
            // byte-for-byte, layout tables and all, the tables a PDF reader never consults are
            // dropped, which needs no knowledge of which glyphs are used.
            //
            // Core.Fonts.SfntTablePruner; Builder's resolver is its other caller.
            //
            // `subsetted` deliberately stays false. A pruned font is NOT a subset: it still contains
            // every glyph the original had, so tagging it `ABCDEF+` would claim a character-set
            // restriction that is not there. The subset tag is a statement about glyph coverage, not
            // about file size.
            if (!subsetted)
            {
                byte[] pruned = null;
                try { pruned = SfntTablePruner.Prune(program, out _, out _); }
                catch { pruned = null; }

                if (pruned != null && pruned.Length < program.Length)
                {
                    // Only accept bytes that still parse. A smaller program the reader cannot load is
                    // strictly worse than the working one it replaced, and this path exists to make
                    // an already-conservative fallback cheaper, never riskier.
                    bool usable;
                    try { usable = TrueTypeFontProgram.Parse(pruned) != null; }
                    catch { usable = false; }
                    if (usable) program = pruned;
                }
            }

            // ---- 5. everything that can still refuse, before an object number is spent --
            //
            // Allocation is not free to undo: a number handed out for a font file that is then
            // abandoned leaves an orphan stream in the appended revision. So the remaining refusals
            // are settled here, while nothing has been staged.

            // One /FontDescriptor can be named by two font dictionaries — the same face registered
            // twice under different /Encodings is a real and legal pattern — and one descriptor can
            // carry only one /FontFile. Revising it a second time would overwrite the first font's
            // program with this font's (a different subset, built for a different character set),
            // leaving font 1 drawing font 2's glyphs and font 1's stream referenced by nothing. So a
            // descriptor this pass has already put a program on is not revised again: this font gets
            // a descriptor of its own, cloned from the same original, and its owner points at that.
            bool descriptorTakenByAnotherFont =
                site.DescriptorObjectNumber > 0
                && modified.TryGetValue(site.DescriptorObjectNumber, out PdfObject stagedObject)
                && stagedObject is PdfDictionary stagedDescriptor
                && (stagedDescriptor[PdfName.FontFile]  != null
                 || stagedDescriptor[PdfName.FontFile2] != null
                 || stagedDescriptor[PdfName.FontFile3] != null);

            bool reviseExistingDescriptor = site.Descriptor != null
                                         && site.DescriptorObjectNumber > 0
                                         && !descriptorTakenByAnotherFont;

            if (!reviseExistingDescriptor)
            {
                string why = descriptorTakenByAnotherFont
                    ? "this font shares its /FontDescriptor with another font that already received " +
                      "a program in this pass, so it needs a descriptor of its own, but "
                    : "a /FontDescriptor has to be created for this font but ";

                if (metrics == null)
                    return EmbedOutcome.Skip(why + "the supplied program's metrics could not be read");

                if (site.DescriptorOwnerObjectNumber <= 0)
                    return EmbedOutcome.Skip(
                        why + "the dictionary that would own one is not an indirect object, so an " +
                        "incremental update cannot attach it");
            }

            // ---- 6. build the font-file stream and the /FontDescriptor ---------------
            string subsetPrefix = subsetted ? SubsetTag.Make(site.BaseFont, request.UsedText) : null;
            string fontName     = SubsetTag.Apply(site.BaseFont ?? metrics?.PostScriptName ?? "EmbeddedFont", subsetPrefix);

            int fontFileObjectNumber = index.AllocateObjectNumber();
            PdfStream fontFile = BuildFontFileStream(program, kind);
            fontFile.ObjectNumber     = fontFileObjectNumber;
            fontFile.GenerationNumber = 0;
            newObjects[fontFileObjectNumber] = fontFile;

            PdfDictionary descriptor;

            if (reviseExistingDescriptor)
            {
                // TakeClone, not CloneDictionary: another edit in this pass may already have staged
                // a clone of this object, and rebuilding it from the original would discard that.
                descriptor = TakeClone(modified, site.DescriptorObjectNumber, site.Descriptor);
                CompleteDescriptor(descriptor, metrics, site, fontName, overwriteFontName: subsetPrefix != null);
                modified[site.DescriptorObjectNumber] = descriptor;
            }
            else
            {
                // Cloned from the original when there is one (the shared-descriptor case above), so
                // the values the document already states — /Flags, /FontBBox, /MissingWidth — carry
                // over instead of being re-derived from the substitute. It never carries a
                // /FontFile: a site that already had one is AlreadyEmbedded and never reaches here.
                descriptor = CloneDictionary(site.Descriptor);
                descriptor.Set(PdfName.Type, PdfName.FontDescriptor);
                CompleteDescriptor(descriptor, metrics, site, fontName, overwriteFontName: true);

                int descriptorObjectNumber = index.AllocateObjectNumber();
                descriptor.ObjectNumber     = descriptorObjectNumber;
                descriptor.GenerationNumber = 0;
                newObjects[descriptorObjectNumber] = descriptor;

                PdfDictionary owner = TakeClone(modified, site.DescriptorOwnerObjectNumber, site.DescriptorOwner);
                owner.Set(PdfName.FontDescriptor, new PdfReference(descriptorObjectNumber, 0));
                modified[site.DescriptorOwnerObjectNumber] = owner;
            }

            descriptor.Set(FontFileKey(kind), new PdfReference(fontFileObjectNumber, 0));

            // ---- 7. apply the planned font-dictionary edits -------------------------
            // An indirect encoding dictionary is superseded on its own; a direct one is part of the
            // font dictionary and is rewritten with it below.
            PdfObject encodingEntry = site.FontDict[PdfName.Encoding];
            bool baseEncodingInFontDict = false;
            if (plan != null && plan.WriteBaseEncoding)
            {
                if (encodingEntry is PdfReference encodingReference && encodingReference.ReferenceObjectNumber > 0
                    && doc.ResolveObject(encodingReference) is PdfDictionary indirectEncoding)
                {
                    PdfDictionary encoding = TakeClone(modified, encodingReference.ReferenceObjectNumber, indirectEncoding);
                    encoding.Set(PdfName.BaseEncoding, PdfName.StandardEncoding);
                    modified[encodingReference.ReferenceObjectNumber] = encoding;
                }
                else baseEncodingInFontDict = true;

                warnings.Add("Font " + request + " has an /Encoding dictionary with no /BaseEncoding, whose " +
                             "implicit base is StandardEncoding while the font is not embedded and the program's " +
                             "own encoding once it is (ISO 32000-1 9.6.6.1); /BaseEncoding /StandardEncoding was " +
                             "written so that no code /Differences leaves unnamed changes glyph.");
            }

            if (plan != null && (plan.WidthsToWrite != null || plan.WriteStandardEncoding || subsetPrefix != null || baseEncodingInFontDict))
            {
                PdfDictionary fontDict = TakeClone(modified, site.FontObjectNumber, site.FontDict);

                if (plan.WidthsToWrite != null)
                {
                    fontDict.Set(PdfName.FirstChar, new PdfNumber(plan.FirstChar));
                    fontDict.Set(PdfName.LastChar,  new PdfNumber(plan.LastChar));
                    fontDict.Set(PdfName.Widths,    plan.WidthsToWrite);
                }

                if (plan.WriteStandardEncoding)
                {
                    fontDict.Set(PdfName.Encoding, PdfName.StandardEncoding);
                    warnings.Add("Font " + request + " declared no /Encoding, so the encoding it already " +
                                 "implied — StandardEncoding, the built-in table of a non-symbolic " +
                                 "Standard-14 font — was made explicit as /StandardEncoding when the " +
                                 "program was embedded. The synthesised /Widths were computed against that " +
                                 "same table, so no character code changes either the glyph it selects or " +
                                 "its advance width.");
                }

                if (subsetPrefix != null)
                    fontDict.Set(PdfName.BaseFont, new PdfName(fontName));

                if (baseEncodingInFontDict && doc.ResolveObject(fontDict[PdfName.Encoding]) is PdfDictionary directEncoding)
                {
                    PdfDictionary encoding = CloneDictionary(directEncoding);
                    encoding.Set(PdfName.BaseEncoding, PdfName.StandardEncoding);
                    fontDict.Set(PdfName.Encoding, encoding);
                }

                modified[site.FontObjectNumber] = fontDict;
            }

            // ---- 8. a Type 1 subset's /CharSet ---------------------------------------
            // ISO 19005-1 6.3.5: a Type 1 font subset's descriptor lists the character names in the
            // subset — the names the drawn codes select through the font's encoding, as written above.
            // Without it the PDF/A-1 converter after this pass would produce a file that fails rule 6.3.5-2.
            if (subsetPrefix != null && plan?.Encoding != null && (site.Subtype == "Type1" || site.Subtype == "MMType1"))
            {
                PdfDictionary written = modified.TryGetValue(site.FontObjectNumber, out PdfObject staged) && staged is PdfDictionary stagedFont
                    ? stagedFont : site.FontDict;
                // Through the objects this pass staged first: an indirect /Encoding superseded in step 7
                // carries the base the original does not.
                IReadOnlyDictionary<int, string> names = SimpleFontEncodingNames.ByCode(written,
                    o => o is PdfReference r && modified.TryGetValue(r.ReferenceObjectNumber, out PdfObject stagedObject)
                        ? stagedObject : doc.ResolveObject(o));
                var drawn = new HashSet<char>(request.UsedText);
                var charSet = new SortedSet<string>(StringComparer.Ordinal);
                var unnamed = new SortedSet<int>();
                foreach (var entry in plan.Encoding)
                {
                    if (!drawn.Contains(entry.Value)) continue;
                    if (names.TryGetValue(entry.Key, out string name) && name != EmbeddedGlyphSets.NotDefGlyph) charSet.Add(name);
                    else unnamed.Add(entry.Key);
                }
                if (unnamed.Count == 0 && charSet.Count > 0)
                    descriptor.Set(PdfName.CharSet, new PdfString("/" + string.Join("/", charSet)));
                else if (unnamed.Count > 0)
                    warnings.Add("Font " + request + " was embedded as a subset, but its /Encoding names no glyph for " +
                                 "code(s) " + string.Join(", ", unnamed) + " it draws, so no /CharSet was written; " +
                                 "ISO 19005-1 6.3.5 requires one of a Type 1 subset.");
            }

            if (substituted)
                warnings.Add("Font " + request + " was not supplied by FontEmbeddingOptions.FontProvider, " +
                             "so a metrically compatible font installed on this machine was embedded in " +
                             "its place. Embedding redistributes that font — make sure you hold the " +
                             "rights to do so.");

            return EmbedOutcome.Ok(subsetted);
        }
    }
}
