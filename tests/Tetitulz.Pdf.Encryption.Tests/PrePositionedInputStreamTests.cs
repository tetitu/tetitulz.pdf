// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using Tetitulz.Pdf.Base.Encryption;
using Tetitulz.Pdf.Encryption;
using Xunit;

namespace Tetitulz.Pdf.Encryption.Tests;

/// <summary>
/// A caller may hand in a seekable stream that is <b>not</b> positioned at 0 — a
/// <see cref="FileStream"/> a host already sniffed a header from is the ordinary case. Buffering it
/// must rewind first.
/// </summary>
/// <remarks>
/// <para><see cref="Stream.CopyTo(Stream)"/> starts at the <i>current</i> position. Three verbatim
/// copies of the buffering block — two in <c>PdfDecryptor</c>, one in <c>PdfEncryptor</c> — copied
/// without rewinding, so the buffer was missing exactly the bytes already consumed. The result is a
/// truncated and therefore corrupt document, and <c>PdfDocument.Open</c> then failed on a file that
/// is perfectly well formed on disk.</para>
///
/// <para><b>No downstream seek can recover this one</b>, which is what separates it from the sibling
/// defect in <c>PdfPublicKeyEncryptor</c>. There, the missing rewind was masked because
/// <c>PdfParser.ParseHeader</c> seeks absolutely; here the bytes are simply not in the buffer.</para>
///
/// <para><b>The fixture uses a non-<see cref="MemoryStream"/> deliberately.</b> The copies special-
/// cased <c>MemoryStream</c> and rewound it correctly, so a <c>MemoryStream</c> fixture passes with
/// the defect fully intact — it never reaches the branch. That is the shape of fixture this
/// repository keeps catching, so <see cref="AMemoryStreamFixtureCannotReachTheDefect"/> states it
/// rather than leaving the choice looking arbitrary.</para>
/// </remarks>
public class PrePositionedInputStreamTests
{
    /// <summary>
    /// <b>The defect.</b> Reading the encryption info off a seekable, non-<see cref="MemoryStream"/>
    /// input that has already been read from must give the same answer as reading it from the start.
    /// </summary>
    [Fact]
    public void GetEncryptionInfoReadsAPrePositionedFileStreamFromTheBeginning()
    {
        byte[] encrypted = EncryptedPdf();

        using TempFileStream stream = TempFileStream.Containing(encrypted);
        stream.ReadByte();                       // a host sniffing the '%' of "%PDF"
        Assert.Equal(1, stream.Position);

        var info = PdfDecryptor.GetEncryptionInfo(stream);

        Assert.NotNull(info);
        Assert.True(info.V > 0, "the /Encrypt dictionary really is there, whatever the read position");
    }

    /// <summary>The decrypt path, on the same input shape.</summary>
    [Fact]
    public void DecryptReadsAPrePositionedFileStreamFromTheBeginning()
    {
        byte[] encrypted = EncryptedPdf();

        using TempFileStream stream = TempFileStream.Containing(encrypted);
        stream.Position = 64;                    // deeper in, so a partial copy is definitely short

        using var output = new MemoryStream();
        PdfDecryptor.Decrypt(stream, output, Password);

        Assert.True(output.Length > 0);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(output.ToArray(), 0, 4));
    }

    /// <summary>
    /// The control that makes the two above mean something: from position 0 the same input has
    /// always worked, so neither test can be passing because the API accepts anything.
    /// </summary>
    [Fact]
    public void TheSameInputAtPositionZeroAlsoWorks()
    {
        byte[] encrypted = EncryptedPdf();

        using TempFileStream stream = TempFileStream.Containing(encrypted);

        var info = PdfDecryptor.GetEncryptionInfo(stream);

        Assert.NotNull(info);
        Assert.True(info.V > 0);
    }

    /// <summary>
    /// Records why the fixture is a <see cref="FileStream"/>: a pre-positioned
    /// <see cref="MemoryStream"/> takes the branch that was always correct, so it cannot detect the
    /// defect at all. Asserting that keeps the next reader from "simplifying" the fixture.
    /// </summary>
    [Fact]
    public void AMemoryStreamFixtureCannotReachTheDefect()
    {
        byte[] encrypted = EncryptedPdf();

        using var stream = new MemoryStream(encrypted, writable: false);
        stream.Position = 64;

        var info = PdfDecryptor.GetEncryptionInfo(stream);

        Assert.NotNull(info);
        Assert.True(info.V > 0);
    }

    // ------------------------------------------------------------------

    private const string Password = "pw";

    /// <summary>An RC4-encrypted one-page document, produced through the library's own encryptor.</summary>
    private static byte[] EncryptedPdf()
    {
        byte[] plain = TestHelpers.MinimalPdfBuilder.CreateMinimalPdf();

        using var input = new MemoryStream(plain);
        using var output = new MemoryStream();
        PdfEncryptor.Encrypt(input, output, new EncryptionParameters
        {
            UserPassword = Password,
            OwnerPassword = Password,
        });
        return output.ToArray();
    }

    /// <summary>
    /// A real seekable, non-<see cref="MemoryStream"/> stream over a temporary file, deleted on
    /// dispose.
    /// </summary>
    private sealed class TempFileStream : Stream
    {
        private readonly FileStream _inner;
        private readonly string _path;

        private TempFileStream(string path)
        {
            _path = path;
            _inner = new FileStream(path, FileMode.Open, FileAccess.Read);
        }

        public static TempFileStream Containing(byte[] bytes)
        {
            string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".pdf");
            File.WriteAllBytes(path, bytes);
            return new TempFileStream(path);
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] b, int o, int c) => _inner.Read(b, o, c);
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                try { File.Delete(_path); } catch (IOException) { }
            }
            base.Dispose(disposing);
        }
    }
}
