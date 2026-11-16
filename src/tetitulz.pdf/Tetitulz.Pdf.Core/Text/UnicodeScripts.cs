// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;

namespace Tetitulz.Pdf.Core.Text
{
    /// <summary>
    /// The OpenType script tag a font registers its features under for a character's script —
    /// Unicode's Script property (<c>Scripts.txt</c>) by range, each script's ISO 15924 code as the
    /// tag, with the tags that predate the codes and the Indic second-generation tags; the table is
    /// generated (<c>tools/unicode-scripts</c>), never typed.
    /// </summary>
    /// <remarks>
    /// <para><b>A run's script decides which lookups run.</b> A font lists its features per script,
    /// and many list a script's features under that script alone: a Thai or Devanagari font's mark
    /// positioning, a Cyrillic face's kerning. Answering Hebrew, Arabic or Latin and nothing else
    /// would give every other script Latin's lookups, or the default script's, or none. The fallback chain is a shaper's (<see cref="Fallbacks"/>): the script's tag, the
    /// first-generation tag of an Indic script, then <c>DFLT</c>, <c>dflt</c> and <c>latn</c>, so a
    /// font that registers only Latin still kerns the Cyrillic its cmap covers.</para>
    /// <para>Common, Inherited and Unknown characters (digits, punctuation, combining marks) have
    /// no script of their own and decide nothing; a run of them alone is Latin's.</para>
    /// </remarks>
    internal static partial class UnicodeScripts
    {
        /// <summary>The tag a run with no scripted character takes.</summary>
        internal const string LatinTag = "latn";

        /// <summary>The first-generation tag of each Indic script's second-generation one (<c>dev2</c> → <c>deva</c>).</summary>
        private static readonly Dictionary<string, string> FirstGeneration = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["bng2"] = "beng", ["dev2"] = "deva", ["gjr2"] = "gujr", ["gur2"] = "guru", ["knd2"] = "knda",
            ["mlm2"] = "mlym", ["ory2"] = "orya", ["tml2"] = "taml", ["tel2"] = "telu", ["mym2"] = "mymr",
        };

        /// <summary>
        /// The OpenType script tag for code point <paramref name="cp"/>, or <c>null</c> for a
        /// character of no script (Common, Inherited, unassigned). A run is cut into segments of one
        /// script by <c>TextRunComposer</c>, a glyph of no script taking its neighbour's, and each
        /// segment's lookups are taken under its own tag with <see cref="Fallbacks"/>; there is no
        /// one tag for a run, since a Latin word beside Hebrew is kerned under <c>latn</c> and the
        /// Hebrew pointed under <c>hebr</c>.
        /// </summary>
        internal static string TagOf(int cp)
        {
            int lo = 0, hi = Starts.Length - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (cp < Starts[mid]) hi = mid - 1;
                else if (cp > Ends[mid]) lo = mid + 1;
                else return Tags[TagIndex[mid]];
            }
            return null;
        }

        /// <summary>
        /// The script tags to look a run's features up under, in order: <paramref name="tag"/>, an
        /// Indic script's first-generation tag, then <c>DFLT</c>, <c>dflt</c> and <c>latn</c> — a
        /// shaper's chain, each once.
        /// </summary>
        internal static IEnumerable<string> Fallbacks(string tag)
        {
            if (tag != null) yield return tag;
            if (tag != null && FirstGeneration.TryGetValue(tag, out string first)) yield return first;
            if (tag != "DFLT") yield return "DFLT";
            if (tag != "dflt") yield return "dflt";
            if (tag != LatinTag) yield return LatinTag;
        }
    }
}
