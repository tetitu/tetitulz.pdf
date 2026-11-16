// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.TestInfrastructure;

/// <summary>
/// Fails the build if any C# source file under <c>src/</c> contains a raw <c>NUL</c> (<c>0x00</c>)
/// byte. Write <c>"\0"</c> / <c>'\0'</c> instead — the escape is byte-identical at runtime.
///
/// <para><b>Why this exists, and it is not a style rule.</b> A file containing a NUL is treated as
/// <b>binary</b> by ripgrep and by <c>git diff</c>. It does not appear in content searches at all —
/// not as a non-match, but as no result. Three files in this repository were in that state, and the
/// way it was found was a grep that answered <c>binary file matches</c> and returned nothing.</para>
///
/// <para>That matters here more than it would elsewhere. This codebase's stated method for
/// preventing its most-repeated defect — two implementations of one spec rule drifting apart — is to
/// search the tree for the other copy. CLAUDE.md's guards (<c>NoInlineCmykConversionTests</c>,
/// <c>NoRedundantPdfNameAllocationTests</c>) are themselves text scans over sources. A file that is
/// invisible to a text scan is silently exempt from every one of them, and nothing reports that it
/// was skipped. It is the same shape as banned test pattern 2: a check that quietly does not run
/// looks exactly like a check that ran and found nothing.</para>
///
/// <para><b>The three that were fixed were all deliberate uses, not corruption.</b>
/// <c>PdfTemplateEngine.PositionKey</c> and <c>PdfResourceCollector</c> used NUL as a composite-key
/// separator — a good technique, since it cannot occur in the text being joined — and
/// <c>PsFontMapper</c> used it for the undefined slots of the WinAnsi 0x80-0x9F table. The idiom was
/// right; writing the byte literally rather than escaping it was what cost the searchability.</para>
/// </summary>
public sealed class NoRawNulBytesInSourceTests
{
    [RepoSourcesFact]
    public void NoSourceFileContainsARawNulByte()
    {
        string root = RepoSources.SrcRoot!;

        var offenders = new List<string>();
        foreach (string path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            if (RepoSources.IsGenerated(path)) continue;

            int count = CountNulBytes(File.ReadAllBytes(path));
            if (count > 0)
                offenders.Add($"{Path.GetRelativePath(root, path)} ({count})");
        }

        Assert.True(offenders.Count == 0,
            $"{offenders.Count} source file(s) contain a raw NUL byte, which makes them binary to " +
            "ripgrep and git diff — so they silently vanish from every content search, including " +
            "the source-scanning guards in this folder. Write \"\\0\" or '\\0' instead; the escape " +
            "is byte-identical at runtime.\n  " + string.Join("\n  ", offenders.OrderBy(x => x, StringComparer.Ordinal)));
    }

    /// <summary>
    /// The floor: proves the detector fires, so the test above cannot be passing because it looks
    /// at nothing. It validates the detector against synthetic bytes rather than asserting that
    /// some real file is clean — a file-based fixture would have to *contain* the byte this guard
    /// bans, which is a contradiction.
    /// </summary>
    [Fact]
    public void TheDetectorFiresOnANulByteAndNotOnOrdinarySource()
    {
        Assert.Equal(0, CountNulBytes(new byte[] { (byte)'a', (byte)'\\', (byte)'0', (byte)'b' }));
        Assert.Equal(2, CountNulBytes(new byte[] { (byte)'a', 0x00, (byte)'b', 0x00 }));
    }

    private static int CountNulBytes(byte[] bytes)
    {
        int n = 0;
        for (int i = 0; i < bytes.Length; i++)
            if (bytes[i] == 0) n++;
        return n;
    }
}
