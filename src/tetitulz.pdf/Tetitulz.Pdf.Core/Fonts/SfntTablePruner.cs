// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;

namespace Tetitulz.Pdf.Core.Fonts
{
    /// <summary>
    /// Removes sfnt tables a PDF reader never consults, keeping the outlines untouched. Works for
    /// TrueType (<c>glyf</c>) and OpenType/CFF (<c>CFF </c>) alike, because it does not look at
    /// them.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this is worth doing, and why it is the safe half of subsetting.</b> Every CFF
    /// font this library embeds is embedded <i>whole</i> — <c>PdfFontSubsetter</c> is TrueType-only
    /// — so a document carrying one modern Hebrew, Arabic or CJK OpenType face ships every byte of
    /// it. Layout tables are usually the largest thing in such a font after the outlines, and this
    /// removes them without touching a single charstring. It cannot produce a wrong glyph, because
    /// it never rewrites one.</para>
    ///
    /// <para><b>A blacklist, not a whitelist, and that is a deliberate trade.</b> A whitelist saves
    /// more — every unknown vendor table would go — but it breaks a font the moment some reader
    /// needs a table nobody here thought of. A blacklist can only ever fail to save space. For an
    /// output-size optimisation that is the right direction to be wrong in.</para>
    ///
    /// <para><b>Why each dropped table is safe:</b></para>
    /// <list type="bullet">
    ///   <item><c>GSUB</c>, <c>GPOS</c>, <c>GDEF</c>, <c>BASE</c>, <c>JSTF</c>, <c>MATH</c> —
    ///     OpenType Layout. <b>A PDF reader must not shape</b>: ISO 32000-1 §9.4 defines showing
    ///     text as character codes → glyphs with advances from <c>/Widths</c>, so the producer
    ///     already chose both. This repository's shaping ruling says the same thing and is why no
    ///     shaping package is referenced. These tables are dead weight in an embedded font, and
    ///     they are typically the biggest dead weight there is.</item>
    ///   <item><c>DSIG</c> — <b>must go, and for correctness rather than size</b>. It is a digital
    ///     signature over the font file; any modification invalidates it, so carrying a stale one
    ///     into a rewritten font is worse than carrying none.</item>
    ///   <item><c>kern</c> — the legacy kerning table. The producer applied kerning when it
    ///     positioned the text; a reader honouring it again would double-apply.</item>
    ///   <item><c>hdmx</c>, <c>VDMX</c>, <c>LTSH</c>, <c>gasp</c> — device metrics and grid-fitting
    ///     hints for specific ppem sizes on screen. A PDF is rasterised at arbitrary scale.</item>
    ///   <item><c>PCLT</c>, <c>meta</c>, <c>vhea</c>-adjacent vendor blobs — metadata no renderer
    ///     reads.</item>
    /// </list>
    ///
    /// <para><b>What is deliberately kept:</b> <c>glyf</c>/<c>loca</c>/<c>CFF </c> (the outlines),
    /// <c>head</c>/<c>hhea</c>/<c>hmtx</c>/<c>maxp</c>/<c>OS/2</c>/<c>post</c>/<c>name</c> (an sfnt
    /// is not valid without them), <c>cmap</c> (a simple-encoded font is looked up through it), and
    /// <c>vhea</c>/<c>vmtx</c>/<c>VORG</c> (vertical writing, which CJK documents use).</para>
    ///
    /// <para><b>Variable fonts are refused outright.</b> A font with <c>fvar</c> carries its
    /// variation data across a family of tables (<c>gvar</c>, <c>HVAR</c>, <c>avar</c>,
    /// <c>CFF2</c>…) whose interdependencies this pruner does not model. Refusing is honest;
    /// guessing would produce a font that renders at the wrong weight.</para>
    /// </remarks>
    internal static class SfntTablePruner
    {
        /// <summary>Tables a PDF reader never consults. See the class remarks for why each is safe.</summary>
        private static readonly HashSet<string> Droppable = new HashSet<string>(StringComparer.Ordinal)
        {
            "GSUB", "GPOS", "GDEF", "BASE", "JSTF", "MATH",   // OpenType Layout — a reader must not shape
            "DSIG",                                            // invalidated by any rewrite; must go
            "kern",                                            // producer already applied kerning
            "hdmx", "VDMX", "LTSH", "gasp",                    // ppem-specific screen hinting
            "PCLT", "meta",                                    // metadata no renderer reads
        };

        /// <summary>Presence of any of these means a variable font, which is refused.</summary>
        private static readonly HashSet<string> VariationMarkers = new HashSet<string>(StringComparer.Ordinal)
        {
            "fvar", "gvar", "CFF2", "HVAR", "VVAR", "MVAR", "avar",
        };

        /// <summary>
        /// Returns <paramref name="fontBytes"/> with droppable tables removed, or <c>null</c> when
        /// the font cannot be pruned — with <paramref name="reason"/> saying why.
        /// </summary>
        /// <remarks>
        /// <b>Returns null rather than the input when nothing can be dropped</b>, so a caller can
        /// tell "pruned to the same size" from "not pruned" without comparing byte arrays. A null
        /// with a reason is the honest answer; handing back the original would read as a successful
        /// prune that happened to save nothing.
        /// </remarks>
        internal static byte[] Prune(byte[] fontBytes, out string reason, out int bytesSaved)
        {
            reason = null;
            bytesSaved = 0;

            if (fontBytes == null || fontBytes.Length < 12)
            {
                reason = "the font is empty or shorter than an sfnt header";
                return null;
            }

            if (SfntTableDirectory.IsCollection(fontBytes))
            {
                reason = SfntTableDirectory.CollectionRefusal;
                return null;
            }

            uint version = SfntTableDirectory.ReadVersion(fontBytes);
            if (version != SfntBuilder.VersionTrueType &&
                version != SfntBuilder.VersionOpenTypeCff &&
                version != SfntTableDirectory.VersionAppleTrueType)
            {
                reason = "unrecognised sfnt version 0x" + version.ToString("X8");
                return null;
            }

            // The directory-fits check, the per-table range check and the tag rules are
            // SfntTableDirectory's — the one reader shared by the three places that re-emit a font.
            // It reads a tag as its bytes; Encoding.ASCII would substitute '?' for any byte above
            // 0x7E and so silently rename such a table on the way back out.
            if (!SfntTableDirectory.TryRead(fontBytes, out var records, out reason))
                return null;

            var kept = new List<(string tag, byte[] data)>(records.Count);
            var dropped = new List<string>();

            foreach (SfntTableDirectory.TableRecord record in records)
            {
                string tag = record.Tag;

                if (VariationMarkers.Contains(tag))
                {
                    reason = $"the font is variable (it carries \"{tag}\"), and its variation tables " +
                             "are interdependent in ways this pruner does not model";
                    return null;
                }

                if (Droppable.Contains(tag))
                {
                    dropped.Add(tag);
                    continue;
                }

                kept.Add((tag, SfntTableDirectory.Payload(fontBytes, record)));
            }

            if (dropped.Count == 0)
            {
                reason = "the font carries none of the tables a PDF reader can do without";
                return null;
            }

            byte[] pruned = SfntBuilder.Build(version, kept);
            if (pruned == null)
            {
                reason = "every table was dropped, which cannot be a font";
                return null;
            }

            bytesSaved = fontBytes.Length - pruned.Length;
            if (bytesSaved <= 0)
            {
                // Possible in principle: a font whose directory was already tightly packed and whose
                // dropped tables were tiny can round up to the same size once re-padded. Reporting
                // it as a saving would be a lie a caller might act on.
                reason = $"dropping {string.Join(", ", dropped)} did not reduce the font";
                return null;
            }

            return pruned;
        }
    }
}
