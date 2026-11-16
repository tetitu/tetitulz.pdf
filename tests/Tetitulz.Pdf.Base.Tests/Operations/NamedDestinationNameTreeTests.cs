// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Structure;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// Writing a named destination preserves the ones already in the document.
///
/// <para><b>Why this exists.</b> §7.9.6 lets a name tree hold its entries in leaves below a root,
/// which is the ordinary shape once a document has more than a handful of destinations — and such a
/// root carries no <c>/Names</c> array of its own. The read path
/// (<see cref="PdfNamedDestinations.ListDestinations"/>) walked <c>/Kids</c>; the write path used a
/// private leaf-only reader that did not. It therefore saw an empty tree, and rewrote the whole
/// <c>/Dests</c> node as a flat leaf containing only the entry being written — <b>silently deleting
/// every existing destination</b>. The output was a valid PDF with a valid name tree, so nothing
/// downstream complained.</para>
///
/// <para>Both writers shared that reader, so both had it.</para>
/// </summary>
public class NamedDestinationNameTreeTests
{
    /// <summary>Adding a destination to a two-level tree keeps the ones already there.</summary>
    [Fact]
    public void SetDestination_PreservesDestinationsStoredUnderKids()
    {
        byte[] pdf = TwoLevelDestsPdf();
        Assert.Equal(new[] { "Alpha", "Beta" }, Names(pdf));

        using var outMs = new MemoryStream();
        PdfNamedDestinations.SetDestination(new MemoryStream(pdf), outMs, "Zeta", 0);

        Assert.Equal(new[] { "Alpha", "Beta", "Zeta" }, Names(outMs.ToArray()));
    }

    /// <summary>Removing one destination from a two-level tree removes exactly one.</summary>
    [Fact]
    public void RemoveDestination_PreservesTheOtherDestinationsUnderKids()
    {
        byte[] pdf = TwoLevelDestsPdf();

        using var outMs = new MemoryStream();
        bool removed = PdfNamedDestinations.RemoveDestination(new MemoryStream(pdf), outMs, "Alpha");

        Assert.True(removed);
        Assert.Equal(new[] { "Beta" }, Names(outMs.ToArray()));
    }

    /// <summary>
    /// Replacing an existing destination still replaces it rather than appending a duplicate — the
    /// control, so the tests above cannot pass by the writer having stopped de-duplicating.
    /// </summary>
    [Fact]
    public void SetDestination_ReplacesAnExistingNameInPlace()
    {
        byte[] pdf = TwoLevelDestsPdf();

        using var outMs = new MemoryStream();
        PdfNamedDestinations.SetDestination(new MemoryStream(pdf), outMs, "Alpha", 0, left: 5, top: 6);

        Assert.Equal(new[] { "Alpha", "Beta" }, Names(outMs.ToArray()));
    }

    // ------------------------------------------------------------------

    private static string[] Names(byte[] pdf) =>
        PdfNamedDestinations.ListDestinations(new MemoryStream(pdf))
                            .Select(d => d.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();

    /// <summary>
    /// <c>/Names /Dests</c> is a ROOT node whose entries live in a <c>/Kids</c> leaf — the shape a
    /// leaf-only reader cannot see.
    /// </summary>
    private static byte[] TwoLevelDestsPdf()
    {
        var acc = new List<byte>();
        var offsets = new List<int>();
        void Emit(string s) => acc.AddRange(Encoding.Latin1.GetBytes(s));
        void Obj(string body) { offsets.Add(acc.Count); Emit(body); }

        Emit("%PDF-1.7\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R /Names << /Dests 5 0 R >> >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R >>\nendobj\n");
        Obj("4 0 obj\n<< /Length 3 >>\nstream\nq Q\nendstream\nendobj\n");
        Obj("5 0 obj\n<< /Kids [6 0 R] >>\nendobj\n");
        Obj("6 0 obj\n<< /Limits [(Alpha) (Beta)] /Names [(Alpha) [3 0 R /XYZ null null null] "
            + "(Beta) [3 0 R /XYZ null null null]] >>\nendobj\n");

        int xref = acc.Count;
        int size = offsets.Count + 1;
        Emit($"xref\n0 {size}\n0000000000 65535 f \n");
        foreach (int o in offsets) Emit($"{o:D10} 00000 n \n");
        Emit($"trailer\n<< /Size {size} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return acc.ToArray();
    }
}
