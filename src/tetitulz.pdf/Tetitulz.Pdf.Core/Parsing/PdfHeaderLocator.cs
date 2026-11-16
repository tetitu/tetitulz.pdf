// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using Tetitulz.Pdf.Core.IO;

namespace Tetitulz.Pdf.Core.Parsing
{
    /// <summary>
    /// Finds the <c>%PDF-</c> header — the point ISO 32000-2 §7.5.2 has every byte offset in a file
    /// count from: "The PDF file begins with the 5 characters “%PDF–” and byte offsets shall be
    /// calculated from the PERCENT SIGN (25h)." Its NOTE 1: "This provision allows for arbitrary
    /// bytes preceding the %PDF- without impacting the viability of the PDF file and its byte
    /// offsets."
    /// </summary>
    /// <remarks>
    /// <para><b>One search, two callers.</b> <see cref="PdfParser"/> reads a file's offsets from
    /// here; <c>Writing.IncrementalUpdateWriter</c> writes an appended revision's offsets from here
    /// and probes the original at positions counted from here. A second copy of the search is where
    /// the two would drift apart — a header the reader accepts and the writer cannot find, or the
    /// reverse — so the parser's version-parsing keeps only the version digits and takes the index
    /// from here.</para>
    /// <para><b>A leaf with no static fields</b>, in the style of <see cref="ObjectHeaderScanner"/>,
    /// so neither caller gains a type-initialization edge.</para>
    /// </remarks>
    internal static class PdfHeaderLocator
    {
        /// <summary>The marker that introduces the PDF version number.</summary>
        internal const string Marker = "%PDF-";

        /// <summary>
        /// How far into a file the header is looked for. PDF 32000-1:2008 §7.5.2 puts the header on
        /// the first line; producers that prepend bytes (an HTML preamble from a misconfigured web
        /// server, say) are still accepted as long as the marker lands inside this window.
        /// </summary>
        internal const int SearchWindowBytes = 1024;

        /// <summary>
        /// The search window: the first <see cref="SearchWindowBytes"/> of <paramref name="stream"/>,
        /// or all of it when it is shorter. The stream's position is restored.
        /// </summary>
        /// <param name="stream">A seekable stream.</param>
        /// <param name="count">How many bytes of the returned buffer are populated.</param>
        internal static byte[] ReadSearchWindow(Stream stream, out int count)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));

            long saved = stream.Position;
            try
            {
                stream.Position = 0;
                var buffer = new byte[(int)Math.Min(SearchWindowBytes, stream.Length)];
                // Stream.Read may return fewer bytes than requested even when more are
                // available; fill until the buffer is full or EOF.
                count = StreamBytes.Fill(stream, buffer, 0, buffer.Length);
                return buffer;
            }
            finally
            {
                stream.Position = saved;
            }
        }

        /// <summary>
        /// Index of the percent sign of the first <see cref="Marker"/> inside the search window of
        /// <c>buffer[0..count)</c>, or -1 when the window holds none. A marker that would run past
        /// the window is not found, exactly as a text search over the window would not find it.
        /// </summary>
        internal static int IndexOf(byte[] buffer, int count)
        {
            if (buffer == null) return -1;

            int limit = Math.Min(Math.Min(count, buffer.Length), SearchWindowBytes);
            for (int i = 0; i + Marker.Length <= limit; i++)
            {
                if (buffer[i] != (byte)'%') continue;
                if (buffer[i + 1] == (byte)'P' && buffer[i + 2] == (byte)'D' &&
                    buffer[i + 3] == (byte)'F' && buffer[i + 4] == (byte)'-')
                    return i;
            }

            return -1;
        }

        /// <summary>Stream position of the header's percent sign, or -1 when the search window holds none.</summary>
        internal static long IndexOf(Stream stream)
        {
            byte[] window = ReadSearchWindow(stream, out int count);
            return IndexOf(window, count);
        }

        /// <summary>
        /// The origin the file's byte offsets count from (§7.5.2): the header's position, or 0 when
        /// there is none. A stream with no header — a fragment, an extracted revision, an embedded
        /// PDF — keeps counting from byte 0, which is where its first object may well sit; an origin
        /// of "not found" would put that object one byte outside the file.
        /// </summary>
        internal static long OriginOf(byte[] buffer, int count) => Math.Max(0, IndexOf(buffer, count));

        /// <summary>The same origin, read from a seekable stream whose position is restored.</summary>
        internal static long OriginOf(Stream stream) => Math.Max(0, IndexOf(stream));
    }
}
