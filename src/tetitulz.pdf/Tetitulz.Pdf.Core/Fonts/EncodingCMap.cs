// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;

namespace Tetitulz.Pdf.Core.Fonts
{
    /// <summary>
    /// An encoding CMap read from its own program text (Adobe Technical Note #5014's format, as an
    /// embedded CMap stream holds it, ISO 32000-2 §9.7.5.3): the codespace ranges that split a string
    /// into codes and the <c>cidrange</c> / <c>cidchar</c> mappings that give each code its CID; a
    /// <c>usecmap</c> takes the named CMap's mappings after its own.
    /// </summary>
    /// <remarks>
    /// <b>Public</b> because <c>Plugins.PostScript</c> holds CMaps a program builds with the
    /// <c>CIDInit</c> operators — per conversion, never in the process-wide predefined registry — and
    /// the codec is internal. The splitting and the mapping are the reader's own, so a code is shown
    /// as the reader will decode the CMap the converter embeds.
    /// </remarks>
    public sealed class EncodingCMap
    {
        private readonly CMapCodec _codec;

        private EncodingCMap(CMapCodec codec) => _codec = codec;

        /// <summary>
        /// The CMap <paramref name="program"/> is, parsed once; a <c>usecmap</c> it makes is
        /// answered by <paramref name="parents"/> — a CMap's program by its name, or null — before
        /// the predefined CMaps, under <see cref="Constants.PdfLimits.MaxCMapUsecmapDepth"/>.
        /// </summary>
        /// <param name="program">The CMap's program text.</param>
        /// <param name="parents">The programs of the CMaps it may use, by name; null for the predefined ones alone.</param>
        public static EncodingCMap Parse(byte[] program, System.Func<string, byte[]> parents = null)
            => new EncodingCMap(CMapCodec.Parse(program, parents));

        /// <summary>
        /// <paramref name="bytes"/> as the codes this CMap splits it into, each with its CID and its
        /// text in the collection <paramref name="ordering"/> (<see cref="CidCollectionText.Decode(string, string, byte[])"/>'s
        /// lookup); empty for no bytes.
        /// </summary>
        /// <param name="ordering">The character collection's ordering (<c>Japan1</c>, <c>GB1</c>, …).</param>
        /// <param name="bytes">The shown string.</param>
        public IReadOnlyList<CidCollectionText.CodeText> Decode(string ordering, byte[] bytes)
            => CidCollectionText.Decode(_codec, ordering, bytes ?? new byte[0]);
    }
}
