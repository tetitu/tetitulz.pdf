// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Docx.Tests;

/// <summary>
/// <see cref="PdfToDocxConverter.Convert(Stream)"/> reads a stream that cannot seek back: the table
/// pre-pass and the document each read from the start, so such a stream is copied once and both read
/// the copy. Read directly, the pre-pass drained it and the document opened nothing.
/// </summary>
public sealed class ConverterInputStreamTests
{
    [Fact]
    public void AStreamThatCannotSeek_ConvertsAsItsBytesDo()
    {
        byte[] pdf = Pdf("BT /F1 12 Tf 72 700 Td (Forward only) Tj ET");

        string[] fromBytes = Paragraphs(PdfToDocxConverter.Convert(pdf));
        string[] fromForwardOnly = Paragraphs(PdfToDocxConverter.Convert(new ForwardOnlyStream(pdf)));

        Assert.Contains("Forward only", fromBytes);
        Assert.Equal(fromBytes, fromForwardOnly);
        Assert.Null(PdfToDocxConverter.LastError);
    }

    private static string[] Paragraphs(byte[] docx)
    {
        using var word = WordprocessingDocument.Open(new MemoryStream(docx), false);
        return word.MainDocumentPart!.Document.Body!.Elements<Paragraph>().Select(p => p.InnerText).ToArray();
    }

    /// <summary>A read-only stream that answers no question about its position and cannot be sought.</summary>
    private sealed class ForwardOnlyStream : Stream
    {
        private readonly MemoryStream _inner;

        public ForwardOnlyStream(byte[] bytes) { _inner = new MemoryStream(bytes, writable: false); }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
    }

    private static byte[] Pdf(string content)
    {
        var acc = new List<byte>();
        var offsets = new List<int>();
        void Emit(string s) => acc.AddRange(Encoding.Latin1.GetBytes(s));
        void Obj(string body) { offsets.Add(acc.Count); Emit(body); }

        Emit("%PDF-1.7\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R "
            + "/Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n");
        Obj($"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");
        Obj("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

        int xref = acc.Count;
        int size = offsets.Count + 1;
        Emit($"xref\n0 {size}\n0000000000 65535 f \n");
        foreach (int o in offsets) Emit($"{o:D10} 00000 n \n");
        Emit($"trailer\n<< /Size {size} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return acc.ToArray();
    }
}
