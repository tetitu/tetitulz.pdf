// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Plugins.Maintenance;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <see cref="PdfRevisions.IsLinearized"/> — "was this file written for progressive access?",
/// which is the question a host must answer before a byte-range fetch is worth attempting.
/// </summary>
/// <remarks>
/// <para><b>Asserted against what this library's own linearizer writes</b>, so the predicate and the
/// producer cannot drift apart: <c>PdfLinearizer</c> emits all eleven parts of Annex F and its
/// output is checked against an independent checker and an independent implementation by its own suite. Here the only question is whether the
/// claim is recognised.</para>
/// <para>GAPS.md §23 is the standing context: the producing half is complete and the consuming half
/// does not exist. This is the first piece of the consuming half and is deliberately the smallest —
/// it reads a head, not a document.</para>
/// </remarks>
public class LinearizationPredicateTests
{
    private static byte[] Linearized()
    {
        using var input = new MemoryStream(MinimalPdfBuilder.CreateMultiPagePdf(3));
        using var output = new MemoryStream();
        PdfLinearizer.Linearize(input, output);
        return output.ToArray();
    }

    private static bool IsLinearized(byte[] pdf)
    {
        using var input = new MemoryStream(pdf, writable: false);
        return PdfRevisions.IsLinearized(input);
    }

    /// <summary>A file this library linearized is recognised as one.</summary>
    [Fact]
    public void ALinearizedFileIsRecognised()
    {
        Assert.True(IsLinearized(Linearized()));
    }

    /// <summary>
    /// An ordinary file is not — including the very file the linearizer was given, which is the
    /// control that makes the assertion above about linearization rather than about PDFs.
    /// </summary>
    [Fact]
    public void AnOrdinaryFileIsNot()
    {
        Assert.False(IsLinearized(MinimalPdfBuilder.CreateMultiPagePdf(3)));
    }

    /// <summary>
    /// <b>The head alone answers it.</b> Annex F.3.3 puts the dictionary in the first object and
    /// within the first 1024 bytes, so a download that has delivered only its opening can be
    /// classified — which is the whole reason a progressive reader asks this first.
    /// </summary>
    [Fact]
    public void TheHeadOfTheFileIsEnough()
    {
        byte[] whole = Linearized();
        byte[] head = whole.Take(PdfLimits.LinearizationDictionaryWindowBytes).ToArray();

        Assert.True(IsLinearized(head));
    }

    /// <summary>
    /// A file shorter than the window is read for what it has rather than refused — the same
    /// partially-present case, at the other extreme.
    /// </summary>
    [Fact]
    public void AFileShorterThanTheWindowIsStillAnswered()
    {
        byte[] head = Linearized().Take(300).ToArray();

        Assert.True(IsLinearized(head));
        Assert.False(IsLinearized(MinimalPdfBuilder.CreateMinimalPdf().Take(300).ToArray()));
    }

    /// <summary>
    /// <c>/Linearized</c> past the Annex F window is not a linearization dictionary. The rule is
    /// positional: a reader that has fetched only the head must be able to trust the answer, and it
    /// cannot if the keyword may appear anywhere.
    /// </summary>
    [Fact]
    public void TheKeywordPastTheWindowIsNotADictionary()
    {
        byte[] padded = PadFirstObject(Linearized(), PdfLimits.LinearizationDictionaryWindowBytes);

        Assert.False(IsLinearized(padded));
    }

    /// <summary>
    /// A file that merely <b>mentions</b> <c>/Linearized</c> before its first object is not
    /// linearized. Annex F puts the dictionary <i>in</i> the first object, so the search starts
    /// there — scanning from byte zero instead would let a comment, or a string in the header,
    /// promote an ordinary file.
    /// </summary>
    [Fact]
    public void AFileThatOnlyMentionsTheKeywordIsNotLinearized()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMultiPagePdf(2);
        string text = Encoding.Latin1.GetString(pdf);
        int firstLine = text.IndexOf('\n') + 1;

        byte[] mentioning = Encoding.Latin1.GetBytes(text.Insert(firstLine, "%/Linearized 1\n"));

        Assert.False(IsLinearized(mentioning));
    }

    /// <summary>A stream is required, and a null one is a programming error rather than "no".</summary>
    [Fact]
    public void ANullStreamIsRefused()
    {
        Assert.Throws<ArgumentNullException>(() => PdfRevisions.IsLinearized(null));
        Assert.Throws<ArgumentNullException>(() => PdfRevisions.TryReadLinearization(null, out _));
    }

    // ================================================================
    //  The parameter dictionary — Table F.1
    // ================================================================

    /// <summary>
    /// The parameters describe the file this library wrote: the length it had, the page count, the
    /// first page's object, and — the one that matters for a progressive fetch — where the hint
    /// stream is.
    /// </summary>
    /// <remarks>
    /// Asserted against the file's own bytes rather than against constants, so the test says "these
    /// parameters describe THIS file" instead of "these are the numbers we happened to write".
    /// </remarks>
    [Fact]
    public void TheParametersDescribeTheFileThatWasWritten()
    {
        byte[] pdf = Linearized();
        using var input = new MemoryStream(pdf, writable: false);

        Assert.True(PdfRevisions.TryReadLinearization(input, out PdfLinearizationInfo info));

        Assert.Equal(pdf.Length, info.FileLength);
        Assert.Equal(3, info.PageCount);
        Assert.True(info.FirstPageObjectNumber > 0, "no first-page object number");

        // /H points at a real stream inside the file, and /E and /T inside it too.
        Assert.InRange(info.HintStreamOffset, 1, pdf.Length - 1);
        Assert.InRange(info.HintStreamOffset + info.HintStreamLength, 1, pdf.Length);
        Assert.InRange(info.FirstPageEndOffset, 1, pdf.Length);
        Assert.InRange(info.MainCrossReferenceOffset, 1, pdf.Length);
    }

    /// <summary>
    /// The hint stream really is at the offset and length <c>/H</c> claims — which is the whole
    /// point of reporting them, since a progressive client fetches exactly that range and has no
    /// way to check it first.
    /// </summary>
    [Fact]
    public void TheHintStreamIsWhereTheParametersSayItIs()
    {
        byte[] pdf = Linearized();
        using var input = new MemoryStream(pdf, writable: false);
        Assert.True(PdfRevisions.TryReadLinearization(input, out PdfLinearizationInfo info));

        string atOffset = Encoding.Latin1.GetString(
            pdf, (int)info.HintStreamOffset, (int)Math.Min(info.HintStreamLength, 200));

        Assert.Contains("obj", atOffset, StringComparison.Ordinal);
        Assert.Contains("stream", atOffset, StringComparison.Ordinal);
    }

    /// <summary>An ordinary file has no parameters to read, and says so rather than inventing them.</summary>
    [Fact]
    public void AnOrdinaryFileHasNoParameters()
    {
        using var input = new MemoryStream(MinimalPdfBuilder.CreateMultiPagePdf(3), writable: false);

        Assert.False(PdfRevisions.TryReadLinearization(input, out PdfLinearizationInfo info));
        Assert.Null(info);
    }

    /// <summary>
    /// A head too short to hold the whole first object answers <c>false</c> rather than throwing —
    /// a caller asking this of a partial download is asking whether it has enough yet.
    /// </summary>
    [Fact]
    public void AHeadTooShortForTheObjectIsAnsweredNotThrown()
    {
        byte[] truncated = Linearized().Take(40).ToArray();
        using var input = new MemoryStream(truncated, writable: false);

        Assert.False(PdfRevisions.TryReadLinearization(input, out PdfLinearizationInfo info));
        Assert.Null(info);
    }

    /// <summary>
    /// Pushes the first object past <paramref name="by"/> bytes by inserting a comment after the
    /// header — the file stays well-formed and its dictionary simply moves out of the window.
    /// </summary>
    private static byte[] PadFirstObject(byte[] pdf, int by)
    {
        string text = Encoding.Latin1.GetString(pdf);
        int firstLine = text.IndexOf('\n') + 1;
        string padding = "%" + new string('p', by) + "\n";

        return Encoding.Latin1.GetBytes(text.Insert(firstLine, padding));
    }
}
