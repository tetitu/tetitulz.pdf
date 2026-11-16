// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;

namespace Tetitulz.Pdf.Core.Fonts
{
    /// <summary>
    /// The component records of a TrueType composite glyph (OpenType <c>glyf</c>, "Composite glyph
    /// description"): where each one's glyph index sits, so a caller can read it or renumber it, and
    /// the whole record — its arguments and its transform — so an outline reader can place it.
    /// </summary>
    /// <remarks>
    /// <para><b>One walk</b>, for Core's <c>TrueTypeSubsetter</c> and Base's <c>PdfFontSubsetter</c>
    /// (listing a glyph's components and renumbering them) and the Maintenance plugin's
    /// <c>TrueTypeGlyphPruner</c>. It stops at the glyph's own end: reading on while the
    /// <c>MORE_COMPONENTS</c> flag says so would walk a malformed last record into the next glyph
    /// and take its bytes for component records, and a subset would carry glyphs nothing draws.</para>
    /// <para>Every record is bounded by the glyph's end as the pruner's was: a record that does not
    /// fit ends the walk, and the records before it are kept. A record whose glyph index fits but
    /// whose arguments or transform do not is still yielded — the subsetters renumber it — and says
    /// so in <see cref="Component.Complete"/>, which an outline reader refuses.</para>
    /// <para><b>Public</b> because <c>Plugins.Maintenance</c> is one of the callers, the reason
    /// <see cref="SfntBuilder"/> is public.</para>
    /// </remarks>
    public static class CompositeGlyph
    {
        private const int ArgsAreWords    = 0x0001;
        private const int ArgsAreXyValues = 0x0002;
        private const int HaveScale       = 0x0008;
        private const int MoreComponents  = 0x0020;
        private const int HaveXAndYScale  = 0x0040;
        private const int HaveTwoByTwo    = 0x0080;

        /// <summary>One component record.</summary>
        public readonly struct Component
        {
            internal Component(int indexOffset, int glyphIndex, int flags, int argument1, int argument2,
                               double a, double b, double c, double d, bool complete)
            {
                IndexOffset = indexOffset; GlyphIndex = glyphIndex; Flags = flags;
                Argument1 = argument1; Argument2 = argument2;
                A = a; B = b; C = c; D = d;
                Complete = complete;
            }

            /// <summary>Where the record's glyph index sits in the data.</summary>
            public int IndexOffset { get; }

            /// <summary>The component's glyph index.</summary>
            public int GlyphIndex { get; }

            /// <summary>The record's flags.</summary>
            public int Flags { get; }

            /// <summary>The first argument: an x offset under <c>ARGS_ARE_XY_VALUES</c>, else the composite's point to match.</summary>
            public int Argument1 { get; }

            /// <summary>The second argument: a y offset under <c>ARGS_ARE_XY_VALUES</c>, else the component's point to match.</summary>
            public int Argument2 { get; }

            /// <summary>The transform's x scale (x' = A·x + C·y); 1 when the record has none.</summary>
            public double A { get; }

            /// <summary>The transform's y-from-x term (y' = B·x + D·y); 0 when the record has none.</summary>
            public double B { get; }

            /// <summary>The transform's x-from-y term; 0 when the record has none.</summary>
            public double C { get; }

            /// <summary>The transform's y scale; 1 when the record has none.</summary>
            public double D { get; }

            /// <summary>Whether the whole record — arguments and transform — lies within the glyph.</summary>
            public bool Complete { get; }
        }

        /// <summary>
        /// The component records of the glyph stored at <paramref name="start"/> up to
        /// <paramref name="end"/>; nothing for a simple glyph, an empty one, or one too short to hold
        /// a header.
        /// </summary>
        /// <param name="data">The bytes holding the glyph (a <c>glyf</c> table, a font, or the glyph alone).</param>
        /// <param name="start">The glyph's first byte.</param>
        /// <param name="end">One past the glyph's last byte; no record is read beyond it.</param>
        public static IEnumerable<Component> Components(byte[] data, int start, int end)
        {
            if (data == null || start < 0 || end > data.Length || end - start < 10) yield break;
            if ((short)((data[start] << 8) | data[start + 1]) >= 0) yield break;   // simple glyph

            int p = start + 10;   // numberOfContours and the bounding box
            while (p + 4 <= end)
            {
                int flags = (data[p] << 8) | data[p + 1];
                int glyphIndex = ReadIndex(data, p + 2);
                int args = (flags & ArgsAreWords) != 0 ? 4 : 2;
                int transform = (flags & HaveScale) != 0 ? 2
                              : (flags & HaveXAndYScale) != 0 ? 4
                              : (flags & HaveTwoByTwo) != 0 ? 8 : 0;
                int next = p + 4 + args + transform;

                if (next > end)
                {
                    yield return new Component(p + 2, glyphIndex, flags, 0, 0, 1, 0, 0, 1, complete: false);
                }
                else
                {
                    bool signed = (flags & ArgsAreXyValues) != 0;
                    int q = p + 4, argument1, argument2;
                    if (args == 4)
                    {
                        int w1 = ReadIndex(data, q), w2 = ReadIndex(data, q + 2);
                        argument1 = signed ? (short)w1 : w1;
                        argument2 = signed ? (short)w2 : w2;
                    }
                    else
                    {
                        argument1 = signed ? (sbyte)data[q] : data[q];
                        argument2 = signed ? (sbyte)data[q + 1] : data[q + 1];
                    }
                    q += args;

                    double a = 1, b = 0, c = 0, d = 1;
                    if (transform == 2) a = d = F2Dot14(data, q);
                    else if (transform == 4) { a = F2Dot14(data, q); d = F2Dot14(data, q + 2); }
                    else if (transform == 8)
                    {
                        a = F2Dot14(data, q); b = F2Dot14(data, q + 2);
                        c = F2Dot14(data, q + 4); d = F2Dot14(data, q + 6);
                    }
                    yield return new Component(p + 2, glyphIndex, flags, argument1, argument2, a, b, c, d, complete: true);
                }

                p = next;
                if ((flags & MoreComponents) == 0) yield break;
            }
        }

        /// <summary>
        /// The offset of each component record's glyph index, in <paramref name="data"/>, for the
        /// glyph stored at <paramref name="start"/> up to <paramref name="end"/> — <see cref="Components"/>'
        /// <see cref="Component.IndexOffset"/>s.
        /// </summary>
        /// <param name="data">The bytes holding the glyph (a <c>glyf</c> table, a font, or the glyph alone).</param>
        /// <param name="start">The glyph's first byte.</param>
        /// <param name="end">One past the glyph's last byte; no record is read beyond it.</param>
        public static IEnumerable<int> ComponentIndexOffsets(byte[] data, int start, int end)
        {
            foreach (Component component in Components(data, start, end)) yield return component.IndexOffset;
        }

        /// <summary>The glyph index at <paramref name="offset"/>, as <see cref="ComponentIndexOffsets"/> yields it.</summary>
        public static int ReadIndex(byte[] data, int offset) => (data[offset] << 8) | data[offset + 1];

        /// <summary>Writes <paramref name="glyphIndex"/> at <paramref name="offset"/>.</summary>
        public static void WriteIndex(byte[] data, int offset, int glyphIndex)
        {
            data[offset]     = (byte)(glyphIndex >> 8);
            data[offset + 1] = (byte)glyphIndex;
        }

        private static double F2Dot14(byte[] data, int at) => (short)ReadIndex(data, at) / 16384.0;
    }
}
