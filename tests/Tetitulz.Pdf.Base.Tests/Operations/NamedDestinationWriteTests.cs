// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Base.Operations.Structure;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <see cref="PdfNamedDestinations.SetDestination"/> and <see cref="PdfNamedDestinations.RemoveDestination"/>
/// rewrite the whole <c>/Dests</c> name tree as one leaf — which is safe only when the tree was read in
/// full, and valid only when the leaf's keys are in order.
/// </summary>
public class NamedDestinationWriteTests
{
    /// <summary>
    /// §7.9.6: a name tree's keys are in lexical order — a reader may binary-search them. The
    /// reader in this library does not care, so the order is read from the raw <c>/Names</c> array.
    /// </summary>
    [Fact]
    public void SettingADestinationWritesTheLeafInKeyOrder()
    {
        byte[] pdf = Build("/Names << /Dests 4 0 R >>",
            "<< /Names [(beta) [3 0 R /Fit] (delta) [3 0 R /Fit]] >>");

        byte[] written = Set(pdf, "alpha");
        written = Set(written, "gamma");

        using var doc = PdfDocument.Open(new MemoryStream(written));
        var names = (PdfDictionary)doc.ResolveObject(doc.Catalog.Dictionary[PdfName.Names])!;
        var leaf = (PdfDictionary)doc.ResolveObject(names[PdfName.Dests])!;
        var entries = (PdfArray)doc.ResolveObject(leaf[PdfName.Names])!;
        Assert.Equal(new[] { "alpha", "beta", "delta", "gamma" },
            Enumerable.Range(0, entries.Count / 2).Select(i => ((PdfString)entries[2 * i]).GetText()));
    }

    /// <summary>
    /// A tree read only in part — here cut off by the depth cap — is <b>not</b> rewritten by either
    /// writer: a flat leaf built from a partial read destroys every entry the read did not reach. Both
    /// refuse, for that reason, and write nothing.
    /// </summary>
    [Theory]
    [InlineData("set")]
    [InlineData("remove")]
    public void ATreeReadOnlyInPartIsNotRewritten(string operation)
    {
        byte[] pdf = DeepTree(PdfLimits.MaxTreeDepth + 20);

        using var output = new MemoryStream();
        var ex = Assert.Throws<Tetitulz.Pdf.Core.PdfException>(() =>
        {
            if (operation == "set")
                PdfNamedDestinations.SetDestination(new MemoryStream(pdf), output, "new", 0);
            else
                PdfNamedDestinations.RemoveDestination(new MemoryStream(pdf), output, "shallow");
        });

        Assert.Contains("could not be read in full", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, output.Length);
    }

    /// <summary>The control: the same shape, shallow enough to read, is rewritten with every entry kept.</summary>
    [Fact]
    public void ATreeReadInFullIsRewrittenWithEveryEntry()
    {
        byte[] written = Set(DeepTree(3), "new");

        Assert.Equal(new[] { "deep", "new", "shallow" },
            PdfNamedDestinations.ListDestinations(new MemoryStream(written)).Select(d => d.Name).OrderBy(n => n, StringComparer.Ordinal));
    }

    /// <summary>
    /// §12.3.2.3: a named destination's value may be a dictionary whose <c>/D</c> is the array —
    /// directly, or through a reference — as well as the array itself. Each form lists with the
    /// page it names; the bare array is the control.
    /// </summary>
    [Fact]
    public void ADestinationWrittenAsADictionaryIsListedWithItsPage()
    {
        byte[] pdf = FormPdfFixtures.Build(
            "<< /Type /Catalog /Pages 2 0 R /Names << /Dests 5 0 R >> >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            "<< /Names [(array) [3 0 R /Fit] (direct) << /D [4 0 R /Fit] >> (indirect) << /D 6 0 R >>] >>",
            "[4 0 R /XYZ 0 0 0]");

        var byName = PdfNamedDestinations.ListDestinations(new MemoryStream(pdf)).ToDictionary(d => d.Name, d => d.PageIndex);

        Assert.Equal(0, byName["array"]);
        Assert.Equal(1, byName["direct"]);
        Assert.Equal(1, byName["indirect"]);
    }

    // ------------------------------------------------------------------ helpers

    private static byte[] Set(byte[] pdf, string name)
    {
        using var output = new MemoryStream();
        PdfNamedDestinations.SetDestination(new MemoryStream(pdf), output, name, 0);
        return output.ToArray();
    }

    /// <summary>Objects: 1 catalog, 2 pages, 3 page, then <paramref name="rest"/> from 4.</summary>
    private static byte[] Build(string catalogEntry, params string[] rest)
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R " + catalogEntry + " >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
        };
        bodies.AddRange(rest);
        return FormPdfFixtures.Build(bodies.ToArray());
    }

    /// <summary>
    /// A <c>/Dests</c> tree whose root holds a shallow leaf <c>(shallow)</c> and a <c>/Kids</c> chain
    /// <paramref name="depth"/> nodes long ending in <c>(deep)</c>.
    /// </summary>
    private static byte[] DeepTree(int depth)
    {
        const int FirstChainNode = 6;
        var rest = new List<string>
        {
            "<< /Kids [5 0 R " + FirstChainNode + " 0 R] >>",
            "<< /Names [(shallow) [3 0 R /Fit]] >>",
        };
        for (int i = 0; i < depth - 1; i++)
            rest.Add("<< /Kids [" + (FirstChainNode + i + 1) + " 0 R] >>");
        rest.Add("<< /Names [(deep) [3 0 R /Fit]] >>");
        return Build("/Names << /Dests 4 0 R >>", rest.ToArray());
    }
}
