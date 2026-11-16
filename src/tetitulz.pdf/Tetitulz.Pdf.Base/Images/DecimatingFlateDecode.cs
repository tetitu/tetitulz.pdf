// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.IO.Compression;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Imaging.Jpx;
using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Base.Images
{
    /// <summary>
    /// A FlateDecode or (<see cref="TryDecodeLzw"/>) LZWDecode image's samples decimated as they are
    /// decoded — that filter alone, or after ASCII stages the renderer has run
    /// (<c>ContentStreamInterpreter.TryDecodeDecimatedFlate</c>): every row is decoded, and
    /// undone from its PNG or TIFF predictor when it has one (the TIFF one,
    /// <c>PredictorHelper.UndoTiffRow</c>), but only the rows and samples the raster
    /// renderer's nearest-neighbour decimation keeps are stored (<see cref="ImageDownsampling.TryRawDecimation"/>).
    /// </summary>
    /// <remarks>
    /// <para><b>Why.</b> With <c>PdfRenderOptions.DownsampleImagesToTarget</c> on, a large raw image
    /// drawn small was inflated whole and then decimated: a 4 000² RGB image drawn at 72 pt cost
    /// 182 ms and 100 MB (the 48 MB of samples and the inflater's segments), against a 7 ms and
    /// 0.3 MB floor (GAPS.md §13b). Here the inflate streams through two row buffers and the
    /// allocation is the decimated image's.</para>
    /// <para><b>The same samples as the full decode, or nothing.</b> The output is byte for byte what
    /// <c>FlateDecodeFilter</c>, <c>PredictorHelper</c> and the renderer's decimation of the decoded
    /// buffer give — one rule for the step and size, one PNG row undo — and wherever this cannot be
    /// sure of that it answers <c>null</c> and the caller takes the full path: a stream short of its
    /// rows or one the inflater refuses (the filter retries such a stream as raw deflate), a
    /// predictor other than PNG's or TIFF's, predictor parameters that do not describe the image's rows, an output
    /// past <see cref="PdfLimits.MaxDecompressedBytes"/> (the filter's refusal), and samples that open
    /// with a JPEG, PNG or JPEG 2000 signature, which the full path sniffs and decodes as such.</para>
    /// </remarks>
    internal static class DecimatingFlateDecode
    {
        /// <summary>
        /// The decimated samples of a <paramref name="width"/> × <paramref name="height"/> image of
        /// <paramref name="components"/> components at <paramref name="bitsPerComponent"/> (8 or 16),
        /// every <paramref name="step"/>-th sample of every <paramref name="step"/>-th row, as an
        /// <paramref name="outWidth"/> × <paramref name="outHeight"/> buffer; <c>null</c> to take the
        /// full path. <paramref name="decodedBefore"/> is what earlier stages of the stream's chain
        /// produced, counted as the filter pipeline counts it against
        /// <see cref="PdfLimits.MaxCumulativeOutputBytes"/>.
        /// </summary>
        internal static byte[] TryDecode(byte[] compressed, PdfDictionary decodeParms, int width, int height,
            int components, int bitsPerComponent, int step, int outWidth, int outHeight, long decodedBefore = 0)
        {
            if (compressed == null || compressed.Length < 2) return null;
            Rows rows = Rows.For(decodeParms, width, height, components, bitsPerComponent, step, outWidth, outHeight, decodedBefore);
            if (rows == null) return null;
            try
            {
                int offset = (compressed[0] & 0x0F) == 0x08 ? 2 : 0;   // a zlib header, as the filter reads one
                using var deflate = new DeflateStream(new MemoryStream(compressed, offset, compressed.Length - offset), CompressionMode.Decompress);
                var chunk = new byte[Math.Min(1 << 16, rows.InputRowBytes)];
                while (!rows.Done)
                {
                    int read = deflate.Read(chunk, 0, chunk.Length);
                    if (read <= 0) break;
                    rows.Feed(chunk, 0, read);
                }
                return rows.Result;
            }
            catch (Exception ex) when (ex is InvalidDataException || ex is IOException || ex is NotSupportedException)
            {
                return null;   // the filter retries a stream the inflater refuses; the full path does
            }
        }

        /// <summary>
        /// As <see cref="TryDecode"/>, for an LZWDecode image (§7.4.4): the codes decoded as they are read
        /// (<c>LzwDecodeFilter.DecodeTo</c>, the filter's own decoder) under its <c>/EarlyChange</c>, the
        /// rows assembled and decimated as they come; <c>null</c> where the full path decides — the
        /// conditions TryDecode has, and a corrupt code the filter's leniency would stop at short of the rows.
        /// </summary>
        internal static byte[] TryDecodeLzw(byte[] compressed, PdfDictionary decodeParms, int width, int height,
            int components, int bitsPerComponent, int step, int outWidth, int outHeight, long decodedBefore = 0)
        {
            if (compressed == null || compressed.Length == 0) return null;
            Rows rows = Rows.For(decodeParms, width, height, components, bitsPerComponent, step, outWidth, outHeight, decodedBefore);
            if (rows == null) return null;
            int earlyChange = decodeParms?.GetInteger(PdfName.EarlyChange) ?? 1;
            try
            {
                LzwDecodeFilter.DecodeTo(compressed, earlyChange, PdfLimits.MaxDecompressedBytes, rows.Feed);
                return rows.Result;
            }
            catch (InvalidDataException)
            {
                return null;   // past the ceiling: the full path refuses it as the filter does
            }
        }

        /// <summary>
        /// An image's rows assembled from decoded bytes in any chunks, each undone from its predictor
        /// and its kept samples stored — every <c>step</c>-th sample of every <c>step</c>-th row.
        /// <see cref="Result"/> is <c>null</c> until every row has come, and for samples that open with
        /// a codec's signature, which the full path sniffs and decodes as such.
        /// </summary>
        private sealed class Rows
        {
            private readonly bool _png, _tiff;
            private readonly int _height, _components, _bpc, _step, _outWidth, _bytesPerPixel, _rowBytes;
            private readonly byte[] _input;
            private readonly long _outBytes;
            private byte[] _row, _previous, _output;

            /// <summary>
            /// The decimated output's first room; it grows as rows arrive. Made whole up front, it was
            /// the image's declared size — 150 MB for a 10 000² 16-bit RGB one drawn at half size —
            /// before a byte had inflated, for a stream of a dozen bytes that then declined.
            /// </summary>
            private const int InitialOutputBytes = 64 * 1024;
            private int _filled, _y;
            private bool _refused;

            private Rows(bool png, bool tiff, int width, int height, int components, int bpc, int step, int outWidth, long outBytes)
            {
                _png = png; _tiff = tiff; _height = height; _components = components; _bpc = bpc; _step = step; _outWidth = outWidth;
                _bytesPerPixel = components * (bpc / 8);
                _rowBytes = width * _bytesPerPixel;
                _input = new byte[_rowBytes + (png ? 1 : 0)];
                _row = png || tiff ? new byte[_rowBytes] : _input;
                _previous = png ? new byte[_rowBytes] : null;   // row 0's predecessor is all zeros
                _outBytes = outBytes;
                _output = new byte[Math.Min(outBytes, InitialOutputBytes)];
            }

            /// <summary>Room in the output for <paramref name="need"/> bytes, doubled as rows arrive, never past the image's size.</summary>
            private void EnsureOutput(long need)
            {
                if (need <= _output.Length) return;
                Array.Resize(ref _output, (int)Math.Min(_outBytes, Math.Max(need, (long)_output.Length * 2)));
            }

            /// <summary>The rows of an image, or <c>null</c> for one the decimating decode does not take.</summary>
            internal static Rows For(PdfDictionary decodeParms, int width, int height, int components, int bitsPerComponent,
                int step, int outWidth, int outHeight, long decodedBefore)
            {
                if (bitsPerComponent != 8 && bitsPerComponent != 16) return null;
                if (components <= 0 || width <= 0 || height <= 0 || step < 2 || outWidth <= 0 || outHeight <= 0) return null;
                if (outWidth != (width + step - 1) / step || outHeight != (height + step - 1) / step) return null;

                bool png = false, tiff = false;
                if (decodeParms != null)
                {
                    int predictor = decodeParms.GetInteger(PdfName.Predictor) ?? 1;
                    if ((predictor >= 10 && predictor <= 15) || predictor == 2)
                    {
                        // The rows the full path undoes are the parameters' rows: only when they are the
                        // image's are its samples this image's samples.
                        if ((decodeParms.GetInteger(PdfName.Columns) ?? 1) != width
                            || (decodeParms.GetInteger(PdfName.Colors) ?? 1) != components
                            || (decodeParms.GetInteger(PdfName.BitsPerComponent) ?? 8) != bitsPerComponent)
                            return null;
                        png = predictor != 2;
                        tiff = predictor == 2;
                    }
                    else if (predictor > 1) return null;
                }

                int bytesPerPixel = components * (bitsPerComponent / 8);
                long rowLong = (long)width * bytesPerPixel;
                long decoded = (rowLong + (png ? 1 : 0)) * height;
                if (rowLong > int.MaxValue / 2 || decoded > PdfLimits.MaxDecompressedBytes) return null;
                // The pipeline counts every stage's output against one ceiling: the rows past it
                // with what the chain's earlier stages made are its refusal.
                if (decodedBefore + decoded > PdfLimits.MaxCumulativeOutputBytes) return null;
                long outLong = (long)outWidth * outHeight * bytesPerPixel;
                if (outLong > int.MaxValue) return null;
                return new Rows(png, tiff, width, height, components, bitsPerComponent, step, outWidth, outLong);
            }

            /// <summary>How many decoded bytes a row is, its PNG filter byte included.</summary>
            internal int InputRowBytes => _input.Length;

            /// <summary>Whether every row has come, or the samples were refused.</summary>
            internal bool Done => _y >= _height || _refused;

            /// <summary>The decimated samples once every row has come; <c>null</c> short of them or refused.</summary>
            internal byte[] Result
            {
                get
                {
                    if (_refused || _y < _height) return null;
                    if (_output.Length != _outBytes) Array.Resize(ref _output, (int)_outBytes);
                    return _output;
                }
            }

            /// <summary>Takes decoded bytes; those past the last row are passed over, as the full path's decimation never reads them.</summary>
            internal void Feed(byte[] buffer, int offset, int count)
            {
                while (count > 0 && !Done)
                {
                    int take = Math.Min(count, _input.Length - _filled);
                    Buffer.BlockCopy(buffer, offset, _input, _filled, take);
                    _filled += take; offset += take; count -= take;
                    if (_filled == _input.Length) { CompleteRow(); _filled = 0; }
                }
            }

            private void CompleteRow()
            {
                if (_png) PredictorHelper.UndoPngRow(_input[0], _input, 1, _row, 0, _rowBytes, _bytesPerPixel, _previous, 0);
                else if (_tiff) PredictorHelper.UndoTiffRow(_input, 0, _row, 0, _rowBytes, _components, _bpc);

                // The full path sniffs the decoded samples for a codec's signature before any
                // decimation; samples that open with one are its, not this.
                if (_y == 0 && OpensWithACodecSignature(_row, 0, _rowBytes)) { _refused = true; return; }

                if (_y % _step == 0)
                {
                    long dst = (long)(_y / _step) * _outWidth * _bytesPerPixel;
                    EnsureOutput(dst + (long)_outWidth * _bytesPerPixel);
                    for (int x = 0; x < _outWidth; x++)
                    {
                        Buffer.BlockCopy(_row, x * _step * _bytesPerPixel, _output, (int)dst, _bytesPerPixel);
                        dst += _bytesPerPixel;
                    }
                }

                if (_png) { byte[] t = _previous; _previous = _row; _row = t; }
                _y++;
            }
        }

        /// <summary>Whether the first decoded row opens as a JPEG 2000, JPEG or PNG would — what the full path tests the whole buffer for.</summary>
        private static bool OpensWithACodecSignature(byte[] row, int start, int length)
        {
            int n = Math.Min(length, 16);
            var head = new byte[n];
            Buffer.BlockCopy(row, start, head, 0, n);
            return JpxHeader.IsJpx(head) || Core.Imaging.RasterImageInspector.IsJpeg(head) || Core.Imaging.RasterImageInspector.IsPng(head);
        }
    }
}
