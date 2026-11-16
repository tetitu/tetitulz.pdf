// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Plugins.Conformance
{
    /// <summary>
    /// A <c>/Metadata</c> stream's decoded bytes for a checker that reads its XMP, refused past the
    /// XMP packet ceiling (<see cref="Base.Operations.Metadata.PdfDeclarationsWriter.MaxExistingPacketBytes"/>,
    /// Core's 2 MB) before the decode passes it.
    /// </summary>
    /// <remarks>
    /// Every checker decoded the packet with <c>GetDecodedData</c>, bounded only by the 512 MB
    /// per-stream ceiling sized for images, and loaded it into an <c>XmlDocument</c>: a few kilobytes
    /// of flate made half a gigabyte of bytes and several gigabytes of DOM. An XMP packet is
    /// metadata. The refusal is a <see cref="PdfException"/>, which each checker's handler for a
    /// packet that will not decode already reports. Core's readers ask the same ceiling through
    /// <c>XmpPacketDom.TryReadPacketBytes</c>, which this plugin, outside Core's
    /// <c>InternalsVisibleTo</c>, cannot see.
    /// </remarks>
    internal static class XmpStreamBytes
    {
        internal static byte[] Read(PdfStream metadata)
        {
            long ceiling = Base.Operations.Metadata.PdfDeclarationsWriter.MaxExistingPacketBytes;
            if (metadata.TryGetDecodedData(ceiling, out byte[] bytes, out string reason)) return bytes;
            throw new PdfException("the XMP metadata stream could not be read: " +
                                   (reason ?? "it would not decode").TrimEnd('.') + ".");
        }
    }
}
