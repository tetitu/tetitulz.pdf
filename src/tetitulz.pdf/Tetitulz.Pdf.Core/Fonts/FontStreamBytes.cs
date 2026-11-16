// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Fonts
{
    /// <summary>
    /// The decoded bytes of a font's own streams, each kind under its own ceiling: a CMap
    /// (<c>/ToUnicode</c>, an embedded <c>/Encoding</c>) under <see cref="PdfLimits.MaxCMapStreamBytes"/>
    /// and a font program (<c>/FontFile</c>, <c>/FontFile2</c>, <c>/FontFile3</c>) under
    /// <see cref="PdfLimits.MaxFontProgramBytes"/> — never the general
    /// <see cref="PdfLimits.MaxDecompressedBytes"/>, which is sized for page content and images.
    /// </summary>
    /// <remarks>
    /// <para><b>Every reader of those streams decodes them here</b> — the font model, both renderers'
    /// font cache, the SVG embedder, the font exporter, the conformance checkers, the accessibility
    /// checkers and remediator, the optimizer — so a bomb in one is refused at the same size wherever
    /// it is read. A stream of a few hundred kilobytes may inflate to the general ceiling, half a
    /// gigabyte, and a CMap's bytes are then also read as a string twice their size: per kind, the
    /// largest real one of each sets the ceiling, with room to spare.</para>
    /// <para><b>A refusal throws <see cref="PdfFilterException"/></b>, as a stream that fails to decode
    /// does, so each reader's own failure path — a font drawn by substitute, a check reported as not
    /// made — is the one it takes. The decode is cached on the stream, as <c>GetDecodedData</c>'s is.</para>
    /// </remarks>
    public static class FontStreamBytes
    {
        /// <summary>A CMap stream's decoded bytes, under <see cref="PdfLimits.MaxCMapStreamBytes"/>.</summary>
        /// <exception cref="PdfFilterException">The stream decodes past the ceiling, or does not decode.</exception>
        public static byte[] CMap(PdfStream stream) => Decode(stream, PdfLimits.MaxCMapStreamBytes, "CMap");

        /// <summary>A font program's decoded bytes, under <see cref="PdfLimits.MaxFontProgramBytes"/>.</summary>
        /// <exception cref="PdfFilterException">The stream decodes past the ceiling, or does not decode.</exception>
        public static byte[] Program(PdfStream stream) => Decode(stream, PdfLimits.MaxFontProgramBytes, "font program");

        private static byte[] Decode(PdfStream stream, long ceiling, string kind)
        {
            if (stream == null) return null;
            if (stream.TryGetDecodedData(ceiling, out byte[] data, out string reason)) return data;
            throw new PdfFilterException($"The {kind} stream was not decoded: {reason}");
        }
    }
}
