// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Text;
using Xunit;

namespace Tetitulz.Pdf.Encryption.Tests;

/// <summary>
/// <see cref="PdfDecryptor.LastObjectStreamRejection"/> is the caller's only way to tell a
/// decrypted document that is <i>complete</i> from one that is syntactically valid and
/// <i>eviscerated</i> — an object stream that could not be expanded takes every object it
/// contained out of the output, and <c>Decrypt</c> still returns normally.
/// </summary>
/// <remarks>
/// <para>The diagnostic shipped with no test at all (GAPS.md §16 item 2), and the obvious way to
/// reach it does not work: a truncated <c>/ObjStm</c> index is swallowed because
/// <c>PdfTokenizer.ReadInteger</c> returns 0 rather than throwing. The route that does work is a
/// <c>/Filter</c> array longer than <c>PdfLimits.MaxFilterChainLength</c>, which
/// <c>FilterPipeline</c> refuses <b>before decoding a byte</b> — see
/// <see cref="TestPdfs.LegacyRc4.BuildWithUnexpandableObjectStream"/> for why the fixture also
/// needs a cross-reference stream.</para>
/// </remarks>
public class ObjectStreamDropDiagnosticTests
{
    private static readonly byte[] FileId =
    {
        0x01, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD, 0xEF,
        0xFE, 0xDC, 0xBA, 0x98, 0x76, 0x54, 0x32, 0x10
    };

    [Fact]
    public void Decrypt_ObjectStreamThatCannotBeExpanded_ReportsWhatWasDropped()
    {
        byte[] pdf = TestPdfs.LegacyRc4.BuildWithUnexpandableObjectStream(FileId);

        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfDecryptor.Decrypt(input, output, password: "");

        string reason = PdfDecryptor.LastObjectStreamRejection;
        Assert.NotNull(reason);
        // The message must name the stream that failed and say what the consequence was —
        // "something went wrong" would not let a caller decide whether to use the output.
        Assert.Contains("Object stream 6", reason!);
        Assert.Contains("absent from the output", reason!);

        // The decrypt still succeeded: the point of the diagnostic is that this output looks
        // perfectly valid, which is exactly why the loss has to be reported rather than inferred.
        Assert.True(output.Length > 0);
        Assert.Contains(TestPdfs.ContentMarker, Encoding.ASCII.GetString(output.ToArray()));
    }

    /// <summary>
    /// The control: the same fixture family with nothing wrong with it reports nothing. Without
    /// this the test above would pass against a slot that is simply always set.
    /// </summary>
    [Fact]
    public void Decrypt_IntactDocument_ReportsNoDrop()
    {
        byte[] pdf = TestPdfs.LegacyRc4.Build(FileId);

        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfDecryptor.Decrypt(input, output, password: "");

        Assert.Null(PdfDecryptor.LastObjectStreamRejection);
    }
}
