// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Tetitulz.Pdf.Core.Fonts
{
    /// <summary>
    /// The ISO 32000-1 §9.6.4 subset tag: six uppercase letters and a <c>'+'</c> prefixed to the
    /// name of a font whose embedded program holds only some of the font's glyphs
    /// ("ABCDEF+LiberationSans").
    ///
    /// <para>
    /// <b>Why this is a type rather than two private methods.</b> Two independent subsetting paths
    /// ship here — <see cref="Tetitulz.Pdf.Core.Writing.Type0FontBuilder"/> when a document is
    /// written, and the Accessibility feature's font remediator when one is repaired — and the tag
    /// rule has one implementation both reach, so the same subsetted program is labelled the same
    /// way whichever door it comes through, which is the same "share downward into a leaf"
    /// move the two renderers' colour code made.
    /// </para>
    ///
    /// <para>
    /// <b>A tag is a claim about the program, and both directions of getting it wrong are defects.</b>
    /// An untagged subset is a file that says it carries a complete font while shipping part of one;
    /// a tag on a program that was embedded whole is the same lie reversed, and it is the more
    /// insidious of the two, because a consumer that trusts §9.6.4 will refuse to merge or reuse
    /// that program and a validator will read two files sharing a tag with different glyph sets as a
    /// conflict. So callers apply the tag only where subsetting is <i>known</i> to have succeeded —
    /// not where it was merely attempted.
    /// </para>
    ///
    /// <para>
    /// This class holds no static state, only pure functions, so it has no type initializer and is a
    /// leaf by construction. That matters for a helper this widely shared: a static-initialization
    /// edge added by a "harmless" shared static is what once crashed a test host mid-run.
    /// </para>
    /// </summary>
    /// <remarks>
    /// <b>Public because two plugins need <see cref="Strip"/>.</b> Core's
    /// <c>InternalsVisibleTo</c> names no plugin, and <c>Plugins.PostScript</c> and
    /// <c>Plugins.Conformance</c> both already reference Core directly — so widening this adds no
    /// reference, no plugin-to-plugin edge and no cycle. It is a stateless leaf with no static
    /// fields, hence no type initializer, which is what makes it safe to call from anywhere (see
    /// CLAUDE.md on the sideways-static crash).
    /// </remarks>
    public static class SubsetTag
    {
        /// <summary>Letters in the tag itself, not counting the <c>'+'</c> (§9.6.4).</summary>
        private const int TagLength = 6;

        /// <summary>Separator between the tag and the font name (§9.6.4).</summary>
        private const char Separator = '+';

        /// <summary>
        /// A deterministic tag for a subset containing exactly <paramref name="usedGlyphIds"/>.
        ///
        /// <para>
        /// The glyph ids normally arrive as some dictionary's key collection, whose enumeration
        /// order is <b>unspecified</b>. Hashing them in arrival order would give one font a
        /// different tag from one run to the next, which breaks byte-identical output and the
        /// idempotency the templating and remediation pipelines promise. They are therefore sorted
        /// and de-duplicated first — the same ordering the <c>/W</c> array is built in, so the tag
        /// and the widths describe the glyph set the same way round.
        /// </para>
        /// </summary>
        internal static string Make(string baseFont, IEnumerable<ushort> usedGlyphIds)
        {
            var sb = new StringBuilder();
            if (usedGlyphIds != null)
            {
                foreach (ushort gid in usedGlyphIds.Distinct().OrderBy(g => g))
                    sb.Append(gid.ToString(CultureInfo.InvariantCulture)).Append(',');
            }
            return Make(baseFont, sb.ToString());
        }

        /// <summary>
        /// A deterministic tag for a subset identified by an arbitrary <paramref name="discriminator"/>
        /// — the text a simple font actually draws, say, where the caller has characters rather than
        /// glyph ids. Two different glyph sets of one font must not share a tag, so whatever the
        /// caller passes has to distinguish them.
        ///
        /// <para>FNV-1a over the name, a separator, and the discriminator, folded into six letters.
        /// The hash is not a security boundary — it exists so that re-running the same operation on
        /// the same document produces the same bytes.</para>
        /// </summary>
        internal static string Make(string baseFont, string discriminator)
        {
            unchecked
            {
                uint hash = 2166136261;
                foreach (char c in baseFont ?? string.Empty) { hash ^= c; hash *= 16777619; }
                hash ^= '|'; hash *= 16777619;
                foreach (char c in discriminator ?? string.Empty) { hash ^= c; hash *= 16777619; }

                var tag = new char[TagLength];
                for (int i = 0; i < TagLength; i++)
                {
                    tag[i] = (char)('A' + (hash % 26));
                    hash /= 26;
                    // Six divisions by 26 exhaust a 32-bit hash, and a zero hash would spell
                    // "AAAAAA" for every font from that point on. Re-seed instead, per position, so
                    // the remaining letters still vary.
                    if (hash == 0) hash = 2166136261 + (uint)i;
                }
                return new string(tag);
            }
        }

        /// <summary>
        /// Prefixes <paramref name="name"/> with <paramref name="tag"/>, replacing any tag the name
        /// already carries. A null or empty tag returns the name untouched, so a caller that decided
        /// not to subset can pass the result of its decision straight through.
        ///
        /// <para>The strip is not defensive noise: a font program's own PostScript name can already
        /// be tagged (it may itself be a subset that some earlier tool produced), and PDF name
        /// sanitisation keeps <c>'+'</c>, so prefixing blindly yields "GHIJKL+ABCDEF+Arial" — a name
        /// no reader will match back to Arial.</para>
        /// </summary>
        internal static string Apply(string name, string tag)
            => string.IsNullOrEmpty(tag) ? name : tag + Separator + Strip(name);

        /// <summary>
        /// The font name without its subset tag, or the name unchanged when it carries none.
        ///
        /// <para>
        /// The shape is checked, not assumed: §9.6.4 spells the tag as six <b>uppercase</b> letters
        /// followed by <c>'+'</c> followed by the name, so "abcdef+X", "AB+X" and a name that merely
        /// happens to contain a plus are all left alone. The length test is <c>&gt; TagLength + 1</c>
        /// rather than <c>&gt;=</c> because a bare "ABCDEF+" names nothing — stripping it would
        /// return the empty string, and a font whose name is empty is worse than one whose name
        /// looks odd.
        /// </para>
        /// </summary>
        public static string Strip(string name)
        {
            if (name == null || name.Length <= TagLength + 1 || name[TagLength] != Separator)
                return name;
            for (int i = 0; i < TagLength; i++)
                if (name[i] < 'A' || name[i] > 'Z') return name;
            return name.Substring(TagLength + 1);
        }
    }
}
