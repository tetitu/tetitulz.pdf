// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Base.Operations.Forms
{
    /// <summary>
    /// The one mapping between <see cref="FormDataAnnotation"/> and an annotation dictionary.
    /// </summary>
    /// <remarks>
    /// <para><b>Three callers need it and they must agree.</b> The FDF serialiser writes these
    /// dictionaries into an exchange file, reads them back out, and <c>PdfAnnotationWriter</c>
    /// writes them into a document. A key spelled one way on the write side and another on the read
    /// side produces a file that round-trips through this library and loses data through anything
    /// else — the failure that only an external reader can see, and the reason this is one table.</para>
    ///
    /// <para><b><c>/Page</c> belongs to FDF and not to a PDF.</b> In an exchange file the
    /// annotation carries the page it goes on; in a document the page carries the annotation and
    /// <c>/P</c> points back the other way. Writing <c>/Page</c> into a document's annotation is
    /// harmless but wrong, and omitting it from an FDF loses the placement entirely — so the caller
    /// says which it wants rather than the mapping guessing.</para>
    ///
    /// <para><b>Zero-based, both formats.</b> FDF's <c>/Page</c> and XFDF's <c>page</c> both count
    /// from zero. Nothing here adjusts it.</para>
    ///
    /// <para><b>The vocabulary was taken from a file another implementation wrote</b>, not from
    /// recollection: an independent implementation was asked to export the same six annotation kinds, and the
    /// keys below are the ones it emitted — including that <c>/Page</c> is zero-based, that a
    /// <c>Line</c> carries <c>/L</c> as well as <c>/Rect</c>, and that <c>/InkList</c> is an array
    /// of arrays rather than a flat one.</para>
    /// </remarks>
    internal static class AnnotationDictionaryMap
    {
        /// <summary>Keys this mapping owns; everything else in a dictionary goes to <c>Extras</c>.</summary>
        private static readonly HashSet<string> Known = new HashSet<string>(StringComparer.Ordinal)
        {
            "Type", "Subtype", "Page", "Rect", "C", "IC", "Contents", "T", "Subj", "NM",
            "M", "CreationDate", "CA", "F", "QuadPoints", "Vertices", "L", "InkList", "BS",
        };

        /// <summary>
        /// Unrecognised keys never written back, whatever they hold. An FDF is untrusted input:
        /// actions run (<c>/A</c>, <c>/AA</c>, a Link's <c>/PA</c>); an appearance is a stream and the
        /// state that selects one; the rest link into the document the annotation came from, and
        /// mean nothing — or something else — in another one; a payload is a stream.
        /// </summary>
        private static readonly HashSet<string> NeverCarried = new HashSet<string>(StringComparer.Ordinal)
        {
            "A", "AA", "PA",
            "AP", "AS",
            "P", "Popup", "Parent", "IRT", "OC", "StructParent", "Dest",
            "FS", "Sound", "Movie",
        };

        /// <summary>How deep a carried value may nest: well past <c>/BE</c>, <c>/LE</c> or <c>/RD</c>, and a bound on a hostile cycle of references.</summary>
        internal const int MaxCarriedDepth = 8;

        /// <summary>How many objects one carried value may hold.</summary>
        internal const int MaxCarriedObjects = 4096;

        /// <summary>
        /// The annotation as a dictionary.
        /// </summary>
        /// <param name="a">The annotation.</param>
        /// <param name="includePage">
        /// Write <c>/Page</c>. True for an FDF file, false for a document — see the remarks.
        /// </param>
        internal static PdfDictionary ToDictionary(FormDataAnnotation a, bool includePage)
        {
            if (a == null) throw new ArgumentNullException(nameof(a));

            var d = new PdfDictionary();
            d.Set(PdfName.Type, PdfName.Annot);
            d.Set(PdfName.Subtype, new PdfName(a.Subtype));

            if (includePage) d.Set(PdfName.Page, new PdfNumber(a.PageIndex));

            if (a.Rect != null && a.Rect.Length == 4) d.Set(PdfName.Rect, Numbers(Normalised(a.Rect)));
            if (a.Color != null) d.Set(PdfName.C, Numbers(a.Color));
            if (a.InteriorColor != null) d.Set(PdfName.IC, Numbers(a.InteriorColor));

            if (a.Contents != null) d.Set(PdfName.Contents, new PdfString(a.Contents));
            if (a.Title != null) d.Set(PdfName.T, new PdfString(a.Title));
            if (a.Subject != null) d.Set(PdfName.Subj, new PdfString(a.Subject));
            if (a.Name != null) d.Set(PdfName.NM, new PdfString(a.Name));
            if (a.ModifiedDate != null) d.Set(PdfName.M, new PdfString(a.ModifiedDate));
            if (a.CreationDate != null) d.Set(PdfName.CreationDate, new PdfString(a.CreationDate));

            if (a.Opacity.HasValue) d.Set(PdfName.CA, new PdfNumber(a.Opacity.Value));
            if (a.Flags.HasValue) d.Set(PdfName.F, new PdfNumber(a.Flags.Value));

            if (a.QuadPoints != null) d.Set(PdfName.QuadPoints, Numbers(a.QuadPoints));
            if (a.Vertices != null) d.Set(PdfName.Vertices, Numbers(a.Vertices));
            if (a.Line != null && a.Line.Length == 4) d.Set(PdfName.L, Numbers(a.Line));

            if (a.InkList.Count > 0)
            {
                var strokes = new PdfArray();
                foreach (double[] stroke in a.InkList)
                    if (stroke != null) strokes.Add(Numbers(stroke));
                d.Set(PdfName.InkList, strokes);
            }

            if (a.BorderWidth.HasValue)
            {
                var bs = new PdfDictionary();
                bs.Set(PdfName.W, new PdfNumber(a.BorderWidth.Value));
                d.Set(PdfName.BS, bs);
            }

            // The plain entries the file carried that the model does not name. The reader admits no
            // key the model sets and none it refuses (FromDictionary), so each goes in as it is — a
            // fresh copy, so two writes of one annotation never share an object.
            foreach (var kvp in a.Carried)
            {
                int objects = 0;
                d.Set(new PdfName(kvp.Key), PlainCopy(kvp.Value, o => o, 0, ref objects));
            }

            return d;
        }

        /// <summary>
        /// The annotation an <c>/Annots</c> entry describes, or <see langword="null"/> when it
        /// carries no usable subtype.
        /// </summary>
        /// <param name="d">The annotation dictionary.</param>
        /// <param name="resolve">
        /// Follows an indirect reference. FDF files reference their annotation arrays and colours
        /// indirectly as often as not, and a direct read would answer <c>null</c> — which is
        /// indistinguishable from "the key is absent".
        /// </param>
        internal static FormDataAnnotation FromDictionary(PdfDictionary d, Func<PdfObject, PdfObject> resolve)
        {
            if (d == null) return null;
            resolve = resolve ?? (o => o);

            if (!(resolve(d[PdfName.Subtype]) is PdfName subtype)) return null;
            if (string.IsNullOrEmpty(subtype.Value)) return null;

            var a = new FormDataAnnotation(subtype.Value);

            if (resolve(d[PdfName.Page]) is PdfNumber page) a.PageIndex = page.IntValue;

            a.Rect = Doubles(resolve(d[PdfName.Rect]), resolve);
            a.Color = Doubles(resolve(d[PdfName.C]), resolve);
            a.InteriorColor = Doubles(resolve(d[PdfName.IC]), resolve);
            a.QuadPoints = Doubles(resolve(d[PdfName.QuadPoints]), resolve);
            a.Vertices = Doubles(resolve(d[PdfName.Vertices]), resolve);
            a.Line = Doubles(resolve(d[PdfName.L]), resolve);

            a.Contents = Text(resolve(d[PdfName.Contents]));
            a.Title = Text(resolve(d[PdfName.T]));
            a.Subject = Text(resolve(d[PdfName.Subj]));
            a.Name = Text(resolve(d[PdfName.NM]));
            a.ModifiedDate = Text(resolve(d[PdfName.M]));
            a.CreationDate = Text(resolve(d[PdfName.CreationDate]));

            if (resolve(d[PdfName.CA]) is PdfNumber ca) a.Opacity = ca.DoubleValue;
            if (PdfAnnotationFlags.FromNumber(resolve(d[PdfName.F])) is int f) a.Flags = f;

            if (resolve(d[PdfName.InkList]) is PdfArray ink)
                for (int i = 0; i < ink.Count; i++)
                {
                    double[] stroke = Doubles(resolve(ink[i]), resolve);
                    if (stroke != null) a.InkList.Add(stroke);
                }

            if (resolve(d[PdfName.BS]) is PdfDictionary bs && resolve(bs[PdfName.W]) is PdfNumber w)
                a.BorderWidth = w.DoubleValue;

            foreach (var kvp in d)
            {
                if (kvp.Key == null || Known.Contains(kvp.Key.Value)) continue;
                a.Extras[kvp.Key.Value] = PdfWriterText.Serialize(kvp.Value);

                if (NeverCarried.Contains(kvp.Key.Value)) continue;
                int objects = 0;
                PdfObject plain = PlainCopy(kvp.Value, resolve, 0, ref objects);
                if (plain != null && !(plain is PdfNull)) a.Carried[kvp.Key.Value] = plain;
            }

            return a;
        }

        /// <summary>
        /// A fresh copy of <paramref name="obj"/> made only of plain direct values — null, booleans,
        /// numbers (an integer kept an integer), names, strings (their bytes, hex or literal) and
        /// arrays and dictionaries of them — with every reference followed through
        /// <paramref name="resolve"/>; or <see langword="null"/> when any part is not one: a stream, a
        /// reference that does not resolve, an object past <see cref="MaxCarriedDepth"/> or the
        /// <see cref="MaxCarriedObjects"/>-th. All or nothing, so a value is never written half.
        /// </summary>
        private static PdfObject PlainCopy(PdfObject obj, Func<PdfObject, PdfObject> resolve, int depth, ref int objects)
        {
            if (depth > MaxCarriedDepth || ++objects > MaxCarriedObjects) return null;
            if (obj is PdfReference)
            {
                obj = resolve(obj);
                if (obj == null || obj is PdfReference) return null;
            }

            switch (obj)
            {
                case PdfStream _: return null;
                case PdfNull _: return PdfNull.Instance;
                case PdfBoolean b: return b.Value ? PdfBoolean.True : PdfBoolean.False;
                case PdfNumber n: return n.IsInteger ? new PdfNumber(n.LongValue) : new PdfNumber(n.DoubleValue);
                case PdfName name: return new PdfName(name.Value);
                case PdfString s: return new PdfString((byte[])s.RawBytes.Clone(), s.IsHex);
                case PdfArray array:
                {
                    var copy = new PdfArray();
                    for (int i = 0; i < array.Count; i++)
                    {
                        PdfObject item = PlainCopy(array[i], resolve, depth + 1, ref objects);
                        if (item == null) return null;
                        copy.Add(item);
                    }
                    return copy;
                }
                case PdfDictionary dict:
                {
                    var copy = new PdfDictionary();
                    foreach (var kvp in dict)
                    {
                        if (kvp.Key == null) continue;
                        PdfObject value = PlainCopy(kvp.Value, resolve, depth + 1, ref objects);
                        if (value == null) return null;
                        copy.Set(new PdfName(kvp.Key.Value), value);
                    }
                    return copy;
                }
                default: return null;
            }
        }

        /// <summary>
        /// §12.5.2: a rectangle is stored with its corners in either order and read normalised.
        /// </summary>
        /// <remarks>
        /// Applied on the way <i>out</i> only. The model keeps what the file said, so a caller can
        /// still see that a producer wrote its corners backwards; what leaves here is what a reader
        /// should be given.
        /// </remarks>
        private static double[] Normalised(double[] rect)
            => new[]
            {
                Math.Min(rect[0], rect[2]), Math.Min(rect[1], rect[3]),
                Math.Max(rect[0], rect[2]), Math.Max(rect[1], rect[3]),
            };

        private static PdfArray Numbers(double[] values)
        {
            var a = new PdfArray();
            foreach (double v in values) a.Add(new PdfNumber(v));
            return a;
        }

        private static double[] Doubles(PdfObject obj, Func<PdfObject, PdfObject> resolve)
        {
            if (!(obj is PdfArray array)) return null;

            var values = new double[array.Count];
            for (int i = 0; i < array.Count; i++)
            {
                if (!(resolve(array[i]) is PdfNumber n)) return null;
                values[i] = n.DoubleValue;
            }
            return values;
        }

        private static string Text(PdfObject obj) => obj is PdfString s ? s.GetText() : null;

        /// <summary>
        /// Renders an object back to the token text <see cref="FormDataAnnotation.Extras"/> keeps.
        /// </summary>
        /// <remarks>
        /// Deliberately shallow and deliberately lossy for a stream: an <c>Extras</c> value exists
        /// so a key this model does not understand survives a read-modify-write, and a stream's
        /// bytes are not something to smuggle through a string. A stream becomes its dictionary,
        /// which is what a reader can act on.
        /// </remarks>
        internal static class PdfWriterText
        {
            internal static string Serialize(PdfObject obj)
            {
                switch (obj)
                {
                    case null: return "null";
                    case PdfNull _: return "null";
                    case PdfBoolean b: return b.Value ? "true" : "false";
                    // The integer branch states the intent — §7.3.3 has no exponent form, so an
                    // Extras value must never come back as "1E+16" — but no value can tell the two
                    // renderings apart: a PdfNumber holds a double, and for every integral double
                    // inside long's range the round-trip format prints the same digits. A mutation
                    // sweep reports this line as a survivor and it is accidentally equivalent, not
                    // untested. It is kept because the guarantee is about the format, and the day
                    // that format changes the branch is what keeps the token legal.
                    case PdfNumber n:
                        return n.IsInteger
                            ? n.LongValue.ToString(CultureInfo.InvariantCulture)
                            : n.DoubleValue.ToString("R", CultureInfo.InvariantCulture);
                    case PdfName nm: return "/" + nm.Value;
                    case PdfString s: return "(" + s.GetText() + ")";
                    case PdfArray a:
                    {
                        var parts = new List<string>(a.Count);
                        for (int i = 0; i < a.Count; i++) parts.Add(Serialize(a[i]));
                        return "[" + string.Join(" ", parts) + "]";
                    }
                    case PdfDictionary d:
                    {
                        var parts = new List<string>();
                        foreach (var kvp in d) parts.Add("/" + kvp.Key.Value + " " + Serialize(kvp.Value));
                        return "<< " + string.Join(" ", parts) + " >>";
                    }
                    default: return obj.ToString();
                }
            }
        }
    }
}
