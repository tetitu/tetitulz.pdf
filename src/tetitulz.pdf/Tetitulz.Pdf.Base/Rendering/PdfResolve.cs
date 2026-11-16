// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Base.Rendering
{
    /// <summary>
    /// Reference-following dictionary reads for the renderers.
    ///
    /// <para><b>Why this exists.</b> <see cref="PdfDictionary"/>'s indexer and its
    /// <c>Get*</c> helpers return the <i>stored</i> value without following an indirect
    /// reference, so the idiom <c>dict[PdfName.X] as PdfArray</c> silently evaluates to
    /// <c>null</c> — indistinguishable from "the key is absent" — whenever a producer writes
    /// <c>X</c> as <c>12 0 R</c> instead of inline. Both spellings are legal for essentially every
    /// value in the spec, so each such read was a latent "feature silently missing on some files"
    /// defect, so every read resolves.</para>
    ///
    /// <para><b>Strictly a superset of a non-resolving read.</b>
    /// <see cref="PdfDocument.ResolveObject"/> returns a non-reference unchanged and yields
    /// <c>PdfNull</c> (never the wrong object) for a broken or cyclic reference, so a direct value
    /// reads as it is.</para>
    ///
    /// <para>A null <c>doc</c> degrades to the non-resolving read rather than
    /// throwing, which keeps the helpers usable from the few paths that have no document.</para>
    /// </summary>
    internal static class PdfResolve
    {
        /// <summary>Follows indirect references; returns <paramref name="obj"/> for a direct value.</summary>
        /// <remarks>
        /// <para><b>This is a strict superset of <c>doc.ResolveObject(obj)</c>, which is worth
        /// stating because ten call sites hedged as if it were not.</b> They read
        /// <c>doc.ResolveObject(x) as PdfArray ?? PdfResolve.Array(doc, …)</c> — or the same pair
        /// the other way round — and in every case one operand was dead: this method already is
        /// <c>ResolveObject(obj) ?? obj</c>, so it returns whatever <c>ResolveObject</c> would and
        /// additionally covers the direct value it declines. A <c>??</c> whose operands are ordered
        /// superset-first can never evaluate its right side, and one ordered subset-first is simply
        /// the superset written twice.</para>
        ///
        /// <para>The hedge looked like defence in depth against the very defect this class exists to
        /// prevent — an unresolved reference reading as an absent key — which is exactly why it
        /// is easy to keep: a redundant guard against a real hazard is the hardest kind to see
        /// through. If you find yourself adding a fallback beside one of these
        /// helpers, the fallback is already inside it.</para>
        /// </remarks>
        internal static PdfObject Value(PdfDocument doc, PdfObject obj)
        {
            if (obj == null) return null;
            if (doc == null) return obj;
            return doc.ResolveObject(obj) ?? obj;
        }

        // ---- resolve an already-fetched object ----

        internal static PdfArray      Array (PdfDocument doc, PdfObject obj) => Value(doc, obj) as PdfArray;
        internal static PdfDictionary Dict  (PdfDocument doc, PdfObject obj) => Value(doc, obj) as PdfDictionary;
        internal static PdfName       Name  (PdfDocument doc, PdfObject obj) => Value(doc, obj) as PdfName;
        internal static PdfNumber     Number(PdfDocument doc, PdfObject obj) => Value(doc, obj) as PdfNumber;
        internal static PdfStream     Stream(PdfDocument doc, PdfObject obj) => Value(doc, obj) as PdfStream;
        internal static PdfString     Str   (PdfDocument doc, PdfObject obj) => Value(doc, obj) as PdfString;
        internal static PdfBoolean    Bool  (PdfDocument doc, PdfObject obj) => Value(doc, obj) as PdfBoolean;

        // ---- fetch a key and resolve it ----

        internal static PdfArray Array(PdfDocument doc, PdfDictionary dict, PdfName key)
            => dict == null ? null : Array(doc, dict[key]);

        internal static PdfDictionary Dict(PdfDocument doc, PdfDictionary dict, PdfName key)
            => dict == null ? null : Dict(doc, dict[key]);

        internal static PdfName Name(PdfDocument doc, PdfDictionary dict, PdfName key)
            => dict == null ? null : Name(doc, dict[key]);

        internal static PdfNumber Number(PdfDocument doc, PdfDictionary dict, PdfName key)
            => dict == null ? null : Number(doc, dict[key]);

        /// <summary>
        /// An integer image parameter, accepting the full key or its abbreviated inline-image
        /// spelling (§8.9.7 Table 93: <c>/Width</c> ≡ <c>/W</c>, <c>/Height</c> ≡ <c>/H</c>,
        /// <c>/BitsPerComponent</c> ≡ <c>/BPC</c>), and resolving either.
        /// </summary>
        /// <remarks>
        /// <para><b>Both renderers read these raw, and they did not even agree about it.</b> The
        /// raster path used <c>PdfDictionary.GetInteger</c> and the SVG path a private
        /// <c>GetStreamInt</c>; neither follows a reference, so <c>/Width 12 0 R</c> — legal under
        /// §7.3.10 — fell to the default. For an image XObject that default is <b>1</b>, so the
        /// image drew as a single pixel stretched over its placement rectangle. Meanwhile the SVG
        /// soft-mask path <i>did</i> resolve (<c>PdfResolve.Number</c>), so the same file gave the
        /// two renderers different mask geometry: exactly the one-renderer-lags-the-other
        /// divergence this helper's own file exists to prevent.</para>
        ///
        /// <para>One implementation, reached from both, because the alternative is two private
        /// helpers that agree today.</para>
        ///
        /// <para><b>A number outside the <c>int</c> range saturates</b> (<see cref="Saturate"/>).
        /// This was <c>(int)</c> of <see cref="PdfNumber.LongValue"/>, which keeps the low 32 bits:
        /// <c>/Width 4294967297</c> read as 1, and <c>1e30</c> — saturated to <c>long.MaxValue</c>
        /// first — as -1. A size check after it then saw a small or negative number where the file
        /// said an enormous one.</para>
        /// </remarks>
        internal static int Int(PdfDocument doc, PdfDictionary dict, PdfName key, PdfName abbreviation, int fallback)
        {
            var n = Number(doc, dict, key) ?? Number(doc, dict, abbreviation);
            return n == null ? fallback : Saturate(n.DoubleValue, fallback);
        }

        /// <summary>An integer with no abbreviated spelling.</summary>
        internal static int Int(PdfDocument doc, PdfDictionary dict, PdfName key, int fallback)
        {
            var n = Number(doc, dict, key);
            return n == null ? fallback : Saturate(n.DoubleValue, fallback);
        }

        /// <summary>
        /// A file-supplied number as an <c>int</c>: truncated toward zero inside the range, the
        /// nearer bound outside it, and <paramref name="fallback"/> for NaN.
        /// </summary>
        /// <remarks><see cref="SaturatingCast.ToInt"/>, the one rule on every runtime, with NaN the
        /// caller's fallback rather than 0.</remarks>
        internal static int Saturate(double value, int fallback)
            => double.IsNaN(value) ? fallback : SaturatingCast.ToInt(value);

        internal static PdfStream Stream(PdfDocument doc, PdfDictionary dict, PdfName key)
            => dict == null ? null : Stream(doc, dict[key]);

        internal static PdfString Str(PdfDocument doc, PdfDictionary dict, PdfName key)
            => dict == null ? null : Str(doc, dict[key]);

        internal static PdfBoolean Bool(PdfDocument doc, PdfDictionary dict, PdfName key)
            => dict == null ? null : Bool(doc, dict[key]);

        /// <summary>
        /// One numeric element of a <b>document</b> array — a <c>/Rect</c>, a page box, a
        /// <c>/BBox</c> — following an indirect reference on the element itself. 0 when the index is
        /// out of range or the element is not a number.
        /// </summary>
        /// <remarks>
        /// <para><b>Not a duplicate of <c>Core.Text.ContentOperand.ArrayFloat</c>, and the
        /// difference is the whole point.</b> That one reads a <i>content-stream operand</i>, where
        /// an indirect reference cannot appear at all (§7.8.2: a content stream's operands are
        /// direct objects), so it correctly does not resolve. Both renderers reached for it to read
        /// an annotation's <c>/Rect</c> as well — a dictionary value, where §7.3.10 permits an
        /// indirect element — and an unresolved element reads as <b>0</b>, which is a plausible
        /// coordinate rather than a detectable failure.</para>
        /// <para><b>The damage was a disagreement between what is drawn and what is reported.</b>
        /// For <c>/Rect [12 0 R 700 200 720]</c> with object 12 = 100,
        /// <c>Base.Operations.Extraction.PdfLinkExtractor</c> resolves the element and reports the
        /// clickable box as x 100..200, while both renderers computed x 0..200 — so the appearance
        /// stream was fitted into a box twice as wide, 100 pt to the left of the rectangle the host
        /// makes clickable. Neither answer looks wrong on its own; only comparing the two shows it,
        /// which is why it survived.</para>
        /// <para>Both renderers read a <c>/BBox</c>, a <c>/Matrix</c>, an annotation's geometry,
        /// <c>/QuadPoints</c>, border, colour and dash, a shading's numbers, a CIE space's
        /// parameters, an <c>/Indexed</c> Lab range, a soft mask's <c>/BC</c> and a JPEG 2000
        /// <c>/Decode</c> through here, and <c>PdfPage</c> hands out its boxes with their numbers in
        /// place. <c>ContentOperand</c>'s element readers are for content-stream operands, which hold
        /// no references; an array a dictionary holds is read here.</para>
        /// </remarks>
        internal static float ArrayFloat(PdfDocument doc, PdfArray arr, int index)
            => (float)ArrayDouble(doc, arr, index);

        /// <summary>
        /// The same rule at <c>double</c> precision, for the operations side, which measures
        /// rectangles rather than painting them.
        /// </summary>
        /// <remarks>
        /// <b>Two byte-identical copies existed</b>, as <c>Num</c> on <c>PdfFormReader</c> and on
        /// <c>PdfFormFiller</c> — the same expression under the same doc comment, which is the
        /// strongest tell this project has for a duplication. Neither bounds-checked, so a
        /// <c>/Rect</c> with fewer than four entries threw <see cref="System.IndexOutOfRangeException"/>
        /// out of a public form call; here a short array reads 0, matching every other element
        /// reader in this class.
        /// </remarks>
        internal static double ArrayDouble(PdfDocument doc, PdfArray arr, int index)
            => ArrayDouble(doc, arr, index, 0.0);

        /// <summary>
        /// The same, reading <paramref name="fallback"/> for a missing or non-numeric element — a
        /// matrix's 1 on its diagonal.
        /// </summary>
        internal static double ArrayDouble(PdfDocument doc, PdfArray arr, int index, double fallback)
        {
            if (arr == null || index < 0 || index >= arr.Count) return fallback;
            return Value(doc, arr[index]) is PdfNumber n ? n.DoubleValue : fallback;
        }
    }
}
