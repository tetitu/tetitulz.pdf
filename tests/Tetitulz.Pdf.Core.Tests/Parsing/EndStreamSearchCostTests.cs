// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Parsing;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Parsing;

/// <summary>
/// A stream whose <c>/Length</c> is unusable is measured by searching for <c>endstream</c>. The
/// search read its whole window (the rest of the file, up to 8 MiB) before looking, so a file of many
/// such streams cost each one the rest of the file: quadratic in its size. It now reads a chunk at a
/// time and stops at the match, and a span already searched without a match is not read again.
/// </summary>
public sealed class EndStreamSearchCostTests
{
    /// <summary>Counts the bytes a reader takes.</summary>
    private sealed class CountingStream : MemoryStream
    {
        public long BytesRead;
        public CountingStream(byte[] data) : base(data, writable: false) { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = base.Read(buffer, offset, count);
            BytesRead += n;
            return n;
        }
        public override int Read(Span<byte> buffer)
        {
            int n = base.Read(buffer);
            BytesRead += n;
            return n;
        }
    }

    private static byte[] Objects(int count, Func<int, string> body)
    {
        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new int[count + 1];
        offsets[1] = sb.Length; sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        offsets[2] = sb.Length; sb.Append("2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n");
        for (int n = 3; n <= count; n++)
        {
            offsets[n] = sb.Length;
            sb.Append(n).Append(" 0 obj\n").Append(body(n)).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(count + 1).Append("\n0000000000 65535 f \n");
        for (int n = 1; n <= count; n++) sb.Append(offsets[n].ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static long BytesReadEnumerating(byte[] pdf)
    {
        var counting = new CountingStream(pdf);
        var parser = new PdfParser(counting);
        parser.ParseCrossReferences();
        long before = counting.BytesRead;
        int seen = parser.EnumerateAllObjects().Count();
        Assert.True(seen > 2);
        return counting.BytesRead - before;
    }

    [Fact]
    public void AdversarialManyStreamsWithoutLength_ReadAboutTheFile_NotItsSquare()
    {
        byte[] pdf = Objects(2000, _ => "<< >>\nstream\nX\nendstream");
        long read = BytesReadEnumerating(pdf);
        Assert.True(read <= 8L * pdf.Length, $"read {read} bytes of a {pdf.Length}-byte file");
    }

    /// <summary>The control: the same streams with a correct <c>/Length</c>, which no search reads past.</summary>
    [Fact]
    public void ManyStreamsWithACorrectLength_ReadAboutTheFile()
    {
        byte[] pdf = Objects(2000, _ => "<< /Length 1 >>\nstream\nX\nendstream");
        long read = BytesReadEnumerating(pdf);
        Assert.True(read <= 8L * pdf.Length, $"read {read} bytes of a {pdf.Length}-byte file");
    }

    [Fact]
    public void AdversarialManyStreamsWithNoEndstream_ReadAboutTheFile_NotItsSquare()
    {
        byte[] pdf = Objects(2000, _ => "<< >>\nstream\nXYZ");
        long read = BytesReadEnumerating(pdf);
        Assert.True(read <= 8L * pdf.Length, $"read {read} bytes of a {pdf.Length}-byte file");
    }

    /// <summary>
    /// The measured length is the data before the keyword, its EOL stripped, wherever the keyword falls
    /// against the chunk boundary — before, across and after it.
    /// </summary>
    [Theory]
    [InlineData(10, "\n")]
    [InlineData(65_520, "\r\n")]
    [InlineData(65_530, "\r\n")]
    [InlineData(65_534, "\n")]
    [InlineData(65_536, "\r\n")]
    [InlineData(200_000, "\n")]
    public void TheMeasuredLength_IsTheDataBeforeTheKeyword(int dataLength, string eol)
    {
        string data = new string('Q', dataLength);
        byte[] pdf = Objects(3, _ => "<< >>\nstream\n" + data + eol + "endstream");
        var parser = new PdfParser(new MemoryStream(pdf));
        parser.ParseCrossReferences();
        var stream = Assert.IsType<PdfStream>(parser.GetObject(3));
        Assert.Equal(dataLength, stream.RawData.Length);
    }
}
