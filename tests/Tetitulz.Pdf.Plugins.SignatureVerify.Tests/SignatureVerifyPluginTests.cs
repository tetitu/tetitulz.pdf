// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Plugins;
using Tetitulz.Pdf.Plugins.SignatureVerify;
using Xunit;
using Tetitulz.Pdf.Signing.Plugins;

namespace Tetitulz.Pdf.Plugins.SignatureVerify.Tests;

/// <summary>
/// Unit tests for <see cref="SignatureVerifyPlugin"/>.
/// All tests use in-memory PDF bytes — no disk I/O or network required.
/// </summary>
public sealed class SignatureVerifyPluginTests
{
    // ── Metadata ─────────────────────────────────────────────────────────────

    [Fact]
    public void Name_IsExpected()
        => Assert.Equal("Tetitulz.Plugins.SignatureVerify", new SignatureVerifyPlugin().Name);

    [Fact]
    public void Description_IsNonEmpty()
        => Assert.False(string.IsNullOrWhiteSpace(new SignatureVerifyPlugin().Description));

    [Fact]
    public void Version_IsOneZeroZero()
        => Assert.Equal(new Version(1, 0, 0), new SignatureVerifyPlugin().Version);

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The plugin holds no state to set up, so what <c>Initialize</c> has to guarantee is that the
    /// instance is <b>usable afterwards</b> — and that a host which initialises twice (a common
    /// re-registration path) gets the same working plugin, not a second, broken one.
    ///
    /// <para>"Initialize did not throw" is true of an empty method and of one that quietly
    /// disabled the plugin; verifying through it afterwards is not.</para>
    /// </summary>
    [Fact]
    public void Initialize_LeavesThePluginAbleToVerify()
    {
        var plugin = new SignatureVerifyPlugin();

        plugin.Initialize();
        plugin.Initialize();   // repeatable

        using var ms = new MemoryStream(BuildMinimalPdf());
        Assert.Empty(plugin.VerifyAll(ms));   // an unsigned document really has no signatures
        Assert.Equal("Tetitulz.Plugins.SignatureVerify", plugin.Name);
    }

    /// <summary>
    /// <c>Shutdown</c> releases nothing this plugin owns, so it must not disable the instance:
    /// a host that stops and restarts the plugin gets identical behaviour back.
    /// </summary>
    [Fact]
    public void Shutdown_LeavesThePluginAbleToVerify()
    {
        var plugin = new SignatureVerifyPlugin();
        plugin.Initialize();
        plugin.Shutdown();

        plugin.Initialize();

        using var ms = new MemoryStream(BuildMinimalPdf());
        Assert.Empty(plugin.VerifyAll(ms));
        Assert.Equal(new Version(1, 0, 0), plugin.Version);
    }

    // ── VerifyAll null-guard ──────────────────────────────────────────────────

    [Fact]
    public void VerifyAll_NullStream_ThrowsArgumentNullException()
        => Assert.Throws<ArgumentNullException>(() => new SignatureVerifyPlugin().VerifyAll(null));

    // ── VerifyAll with an unsigned PDF ───────────────────────────────────────

    [Fact]
    public void VerifyAll_UnsignedPdf_ReturnsEmptyList()
    {
        using var ms = new MemoryStream(BuildMinimalPdf());
        var results = new SignatureVerifyPlugin().VerifyAll(ms);
        Assert.NotNull(results);
        Assert.Empty(results);
    }

    [Fact]
    public void VerifyAll_SeekableStream_DoesNotThrow()
    {
        using var ms = new MemoryStream(BuildMinimalPdf());
        var ex = Record.Exception(() => new SignatureVerifyPlugin().VerifyAll(ms));
        Assert.Null(ex);
    }

    [Fact]
    public void VerifyAll_NonSeekableStream_DoesNotThrow()
    {
        byte[] pdfBytes = BuildMinimalPdf();
        using var ms = new MemoryStream(pdfBytes);
        using var ns = new NonSeekableStream(ms);
        var ex = Record.Exception(() => new SignatureVerifyPlugin().VerifyAll(ns));
        Assert.Null(ex);
    }

    // ── IPdfPlugin contract ───────────────────────────────────────────────────

    [Fact]
    public void ImplementsIPdfPlugin()
        => Assert.IsAssignableFrom<IPdfPlugin>(new SignatureVerifyPlugin());

    [Fact]
    public void ImplementsISignatureVerifyPlugin()
        => Assert.IsAssignableFrom<ISignatureVerifyPlugin>(new SignatureVerifyPlugin());

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static byte[] BuildMinimalPdf()
    {
        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.4");
        sb.AppendLine("1 0 obj");
        sb.AppendLine("<< /Type /Catalog /Pages 2 0 R >>");
        sb.AppendLine("endobj");
        sb.AppendLine("2 0 obj");
        sb.AppendLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        sb.AppendLine("endobj");
        sb.AppendLine("3 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        sb.AppendLine("endobj");

        long xrefOffset = Encoding.Latin1.GetByteCount(sb.ToString());

        sb.AppendLine("xref");
        sb.AppendLine("0 4");
        sb.AppendLine("0000000000 65535 f ");
        sb.AppendLine("0000000009 00000 n ");
        sb.AppendLine("0000000062 00000 n ");
        sb.AppendLine("0000000119 00000 n ");
        sb.AppendLine("trailer");
        sb.AppendLine("<< /Size 4 /Root 1 0 R >>");
        sb.AppendLine("startxref");
        sb.AppendLine(xrefOffset.ToString());
        sb.AppendLine("%%EOF");

        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private sealed class NonSeekableStream(Stream inner) : Stream
    {
        public override bool CanRead  => true;
        public override bool CanSeek  => false;
        public override bool CanWrite => false;
        public override long Length   => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void  Flush()                          { }
        public override int   Read(byte[] buf, int off, int n) => inner.Read(buf, off, n);
        public override long  Seek(long offset, SeekOrigin o)  => throw new NotSupportedException();
        public override void  SetLength(long value)            => throw new NotSupportedException();
        public override void  Write(byte[] buf, int off, int n)=> throw new NotSupportedException();
    }
}
