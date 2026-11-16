// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <see cref="PdfAttachmentManager.SanitizeFilename"/> and the containment check above it — the
/// path an attacker-supplied name takes on its way to <c>File.WriteAllBytes</c>.
/// </summary>
/// <remarks>
/// <para><b>Neither had a single test.</b> The containment is two independent layers — strip the
/// name down, then verify the resolved path is still under the target directory — and a silent
/// regression in either leaves the other looking like it works. Writing an attachment out is the
/// one operation here that touches a path a document chose.</para>
///
/// <para><b>The reserved-device-name concern was measured rather than assumed, and it did not
/// survive.</b> On Windows 11 with .NET 10 <i>and</i> .NET Framework 4.8,
/// <c>Path.GetFullPath</c> leaves <c>CON</c>, <c>AUX</c>, <c>PRN</c> and <c>NUL.txt</c> as ordinary
/// paths inside the directory, and writing to them produces ordinary 25-byte files that read back.
/// Only a bare <c>NUL</c> still resolves to the device — as <c>\\.\NUL</c>, which escapes the
/// target directory and so is already refused by the check below. Rejecting the device names would
/// therefore rename a legitimate attachment called <c>CON.pdf</c> to fix nothing.</para>
/// </remarks>
public class AttachmentFilenameContainmentTests
{
    // ================================================================ the name is stripped down

    /// <summary>
    /// Directory components go, in both separator conventions, and the leaf survives intact.
    /// </summary>
    /// <remarks>
    /// Asserting only "the result contains no separator" would pass for a method that returned the
    /// fallback name for everything, so each case names the leaf it must keep.
    /// </remarks>
    [Theory]
    [InlineData("../../../etc/passwd", "passwd")]
    [InlineData("..\\..\\Windows\\System32\\evil.dll", "evil.dll")]
    [InlineData("/absolute/path/report.pdf", "report.pdf")]
    [InlineData("subdir/nested/data.csv", "data.csv")]
    [InlineData("plain.txt", "plain.txt")]
    public void DirectoryComponentsAreStrippedAndTheLeafIsKept(string raw, string expected)
    {
        Assert.Equal(expected, PdfAttachmentManager.SanitizeFilename(raw));
    }

    /// <summary>A drive-relative prefix is dropped along with everything before the colon.</summary>
    /// <remarks>
    /// <c>C:evil.txt</c> is not a directory traversal but it is not a filename either — on Windows
    /// it names a path relative to the current directory <i>of that drive</i>.
    /// </remarks>
    [Theory]
    [InlineData("C:evil.txt", "evil.txt")]
    [InlineData("D:\\rooted\\thing.bin", "thing.bin")]
    public void ADriveRelativePrefixIsDropped(string raw, string expected)
    {
        Assert.Equal(expected, PdfAttachmentManager.SanitizeFilename(raw));
    }

    /// <summary>NUL bytes and control characters are removed rather than passed through.</summary>
    [Fact]
    public void NulAndControlCharactersAreRemoved()
    {
        string raw = "re" + '\0' + "po" + '\u0007' + "rt" + '\u001F' + ".pdf";

        Assert.Equal("report.pdf", PdfAttachmentManager.SanitizeFilename(raw));
    }

    /// <summary>
    /// A name that sanitises down to nothing meaningful becomes the stated fallback, not an empty
    /// string.
    /// </summary>
    /// <remarks>
    /// An empty name reaching <c>Path.Combine</c> yields the directory itself, and writing to that
    /// is a different failure entirely.
    /// </remarks>
    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("/")]
    [InlineData("....")]
    [InlineData("   ")]
    public void ANameThatSanitisesToNothingBecomesTheFallback(string raw)
    {
        Assert.Equal("attachment.bin", PdfAttachmentManager.SanitizeFilename(raw));
    }

    /// <summary>
    /// Trailing dots go, because Windows silently drops them and two attachments would collide.
    /// </summary>
    [Fact]
    public void TrailingDotsAreTrimmed()
    {
        Assert.Equal("report", PdfAttachmentManager.SanitizeFilename("report..."));
        Assert.Equal("report.pdf", PdfAttachmentManager.SanitizeFilename("report.pdf."));
    }

    /// <summary>
    /// <b>The measured behaviour for reserved device names: they are ordinary filenames.</b>
    /// </summary>
    /// <remarks>
    /// This is here so the concern is not re-raised from a survey. Sanitising keeps them, and that
    /// is correct: an attachment genuinely called <c>CON.pdf</c> keeps its name, and a bare
    /// <c>NUL</c> is stopped one layer up by the containment check rather than by renaming.
    /// </remarks>
    [Theory]
    [InlineData("CON")]
    [InlineData("NUL.txt")]
    [InlineData("PRN")]
    [InlineData("COM1.dat")]
    public void ReservedDeviceNamesAreKeptAsOrdinaryNames(string raw)
    {
        Assert.Equal(raw, PdfAttachmentManager.SanitizeFilename(raw));
    }

    // ================================================================ and then contained

    /// <summary>A PDF carrying one attachment under <paramref name="name"/>.</summary>
    private static byte[] WithAttachment(string name, string content)
    {
        using var input = new MemoryStream(MinimalPdfBuilder.CreateMinimalPdf());
        using var output = new MemoryStream();
        PdfOperations.AddAttachment(input, output, name, Encoding.ASCII.GetBytes(content));
        return output.ToArray();
    }

    /// <summary>A scratch directory under the OS temp path, removed afterwards.</summary>
    private sealed class ScratchDirectory : IDisposable
    {
        internal string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tetitulz-attach-" + Guid.NewGuid().ToString("N"));

        internal ScratchDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>
    /// <b>An attachment whose name is a traversal lands inside the target directory.</b>
    /// </summary>
    /// <remarks>
    /// The end-to-end property, and the one that matters: the two layers are only worth having if
    /// the file ends up where the caller asked. Asserting the returned path is not enough — the
    /// bytes are read back off disk, and the parent directory is checked to be empty of the
    /// traversal target.
    /// </remarks>
    [Fact]
    public void ATraversalNamedAttachmentIsWrittenInsideTheTargetDirectory()
    {
        const string payload = "attachment-payload";
        using var scratch = new ScratchDirectory();

        byte[] pdf = WithAttachment("../../escaped.txt", payload);

        using var input = new MemoryStream(pdf);
        string written = PdfAttachmentManager.ExtractAttachmentToDirectory(
            input, "../../escaped.txt", scratch.Path);

        Assert.NotNull(written);
        Assert.Equal(scratch.Path, Path.GetDirectoryName(written));
        Assert.Equal("escaped.txt", Path.GetFileName(written));
        Assert.Equal(payload, File.ReadAllText(written));

        // And nothing landed two levels up, where the name asked to go.
        string wouldHaveBeen = Path.GetFullPath(
            Path.Combine(scratch.Path, "..", "..", "escaped.txt"));
        Assert.False(File.Exists(wouldHaveBeen), $"the traversal reached {wouldHaveBeen}");
    }

    /// <summary>An ordinary name round-trips through the directory unchanged.</summary>
    [Fact]
    public void AnOrdinaryAttachmentRoundTripsThroughTheDirectory()
    {
        const string payload = "ordinary-payload";
        using var scratch = new ScratchDirectory();

        using var input = new MemoryStream(WithAttachment("notes.txt", payload));
        string written = PdfAttachmentManager.ExtractAttachmentToDirectory(input, "notes.txt", scratch.Path);

        Assert.Equal(Path.Combine(scratch.Path, "notes.txt"), written);
        Assert.Equal(payload, File.ReadAllText(written));
    }

    /// <summary>
    /// A target directory written with a trailing separator is the same directory: the attachment
    /// is written into it, where a prefix test over the unnormalised root refuses every name.
    /// </summary>
    [Fact]
    public void ATargetDirectoryWithATrailingSeparatorIsTheSameDirectory()
    {
        const string payload = "trailing-separator-payload";
        using var scratch = new ScratchDirectory();

        using var input = new MemoryStream(WithAttachment("notes.txt", payload));
        string written = PdfAttachmentManager.ExtractAttachmentToDirectory(
            input, "notes.txt", scratch.Path + Path.DirectorySeparatorChar);

        Assert.Equal(Path.Combine(scratch.Path, "notes.txt"), written);
        Assert.Equal(payload, File.ReadAllText(written));
    }

    /// <summary>
    /// <b>A bare <c>NUL</c> is refused by the containment check, not written to the null device.</b>
    /// </summary>
    /// <remarks>
    /// The one reserved name that still resolves to a device. <c>Path.GetFullPath</c> turns
    /// <c>&lt;dir&gt;\NUL</c> into <c>\\.\NUL</c>, which is outside the target directory — so the
    /// second layer catches what the first deliberately does not rename. Measured, then asserted:
    /// writing to that path really does discard the bytes, which is why the refusal matters.
    /// </remarks>
    [Fact]
    public void ABareNulNameIsRefusedRatherThanDiscarded()
    {
        using var scratch = new ScratchDirectory();

        using var input = new MemoryStream(WithAttachment("NUL", "would-vanish"));

        var ex = Assert.Throws<Tetitulz.Pdf.Core.PdfException>(
            () => PdfAttachmentManager.ExtractAttachmentToDirectory(input, "NUL", scratch.Path));

        Assert.Contains("escapes target directory", ex.Message);
        Assert.Empty(Directory.GetFiles(scratch.Path));
    }

    /// <summary>Asking for an attachment that is not there yields null rather than an empty file.</summary>
    [Fact]
    public void AnAbsentAttachmentWritesNothing()
    {
        using var scratch = new ScratchDirectory();

        using var input = new MemoryStream(WithAttachment("present.txt", "x"));
        string written = PdfAttachmentManager.ExtractAttachmentToDirectory(input, "absent.txt", scratch.Path);

        Assert.Null(written);
        Assert.Empty(Directory.GetFiles(scratch.Path));
    }

    [Fact]
    public void NullArgumentsAreRefused()
    {
        using var scratch = new ScratchDirectory();
        using var input = new MemoryStream(WithAttachment("a.txt", "x"));

        Assert.Throws<ArgumentNullException>(
            () => PdfAttachmentManager.ExtractAttachmentToDirectory(null, "a.txt", scratch.Path));
        Assert.Throws<ArgumentNullException>(
            () => PdfAttachmentManager.ExtractAttachmentToDirectory(input, null, scratch.Path));
        Assert.Throws<ArgumentNullException>(
            () => PdfAttachmentManager.ExtractAttachmentToDirectory(input, "a.txt", null));
    }
}
