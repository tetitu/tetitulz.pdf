// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Base.Tests.TestInfrastructure;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.TestHelpers;
using Xunit;
using Xunit.Abstractions;

namespace Tetitulz.Pdf.Base.Tests;

/// <summary>
/// Measures how often a real field tree's <c>/Parent</c> back-pointer agrees with the
/// <c>/Kids</c> edge it was reached through — the number the whole "which chain is authoritative"
/// argument has been conducted without.
/// </summary>
/// <remarks>
/// <para>Core resolves an inheritable attribute up the chain a field was <b>descended</b> through;
/// the Conformance walk and the annotation renderer climb the <c>/Parent</c> key. On a conformant
/// file those are the two directions of one edge — Table 220 defines <c>/Parent</c> as "the field
/// … whose <c>Kids</c> array includes this field" — so they cannot reach different nodes, and every
/// shape that separates them is malformed. What nobody knew is <b>which malformations actually
/// occur</b>, and in what proportion.</para>
///
/// <para><b>The assertion is the exhaustiveness of the classification, not the counts.</b> Every
/// node reached by a <c>/Kids</c> edge lands in exactly one bucket and the buckets sum to the walked
/// count — that is falsifiable and stays true as the corpus changes. The counts themselves go to
/// test output and into a commit message, because a corpus-dependent number asserted as a
/// threshold is a test that fails when someone adds a PDF.</para>
///
/// <para><b>It cannot veto a rule, and is not meant to.</b> A resolver that tries <c>/Parent</c>
/// first and falls back to the arrival chain returns a value whenever either chain does, so no
/// count can make it lose an answer. What the number tells a reader is how load-bearing the
/// fallback half is: if <c>ParentAbsent</c> is zero across a real corpus, the fallback is defensive;
/// if it dominates, the <c>/Parent</c>-first preference is close to a no-op and the argument for it
/// is weaker than it sounds.</para>
/// </remarks>
public sealed class CorpusFieldParentAgreementTests
{
    private readonly ITestOutputHelper _out;

    public CorpusFieldParentAgreementTests(ITestOutputHelper output) => _out = output;

    private sealed class Tally
    {
        internal int Agrees;          // /Parent names the node we descended from
        internal int Absent;          // no /Parent at all — only the arrival chain can answer
        internal int Different;       // /Parent names some OTHER node — the chains disagree
        internal int RootWithParent;  // a /Fields entry carrying a /Parent, which Table 220 forbids
        internal int Walked;          // nodes reached by a /Kids edge (excludes /Fields roots)
        internal int Documents;
        internal int WithForms;
    }

    [CorpusFact]
    public void EveryFieldReachedByAKidsEdgeFallsInExactlyOneAgreementBucket()
    {
        var t = new Tally();

        foreach (string path in TestCorpus.Pdfs())
        {
            t.Documents++;
            try { Measure(path, t); }
            catch (Exception ex) { _out.WriteLine($"skipped {Path.GetFileName(path)}: {ex.GetType().Name}"); }
        }

        _out.WriteLine($"documents scanned : {t.Documents} ({t.WithForms} with an AcroForm)");
        _out.WriteLine($"nodes via /Kids   : {t.Walked}");
        _out.WriteLine($"  /Parent agrees  : {t.Agrees}");
        _out.WriteLine($"  /Parent absent  : {t.Absent}");
        _out.WriteLine($"  /Parent differs : {t.Different}");
        _out.WriteLine($"roots with /Parent: {t.RootWithParent}");

        // The classification is total: three mutually exclusive buckets, nothing uncounted.
        Assert.Equal(t.Walked, t.Agrees + t.Absent + t.Different);

        // And the walk reached something, or the sum above is 0 == 0 and says nothing.
        Assert.True(t.Documents > 0, "the corpus resolved to no documents");
    }

    /// <summary>
    /// The instrument proves itself on a tree built here, because the corpus may not contain one —
    /// and on the corpus this was written against, it does not.
    /// </summary>
    /// <remarks>
    /// <para><b>Measured on that corpus: 20 documents, 2 with an <c>/AcroForm</c>, and ZERO nodes
    /// reached by a <c>/Kids</c> edge.</b> Both forms are entirely flat — every field is a root of
    /// <c>/Fields</c> with no children — so the corpus classification above is <c>0 == 0</c> and
    /// says nothing at all about the chain question.</para>
    ///
    /// <para>That is exactly why this case exists. Without it the whole file is a test that cannot
    /// fail: the sum of three zeroes is zero however the classifier is written, so a classifier
    /// that mislabelled every node would pass. Here all three buckets are exercised on a tree whose
    /// answers are known, so when a corpus with nested forms does arrive the counts it produces can
    /// be believed.</para>
    /// </remarks>
    [Fact]
    public void TheClassifierSeparatesAllThreeBucketsOnATreeWithKnownAnswers()
    {
        var t = new Tally();
        string path = Path.Combine(Path.GetTempPath(), "acroform-agreement-" + Guid.NewGuid().ToString("N") + ".pdf");
        File.WriteAllBytes(path, ThreeBucketPdf());
        try { Measure(path, t); }
        finally { try { File.Delete(path); } catch (IOException) { } }

        Assert.Equal(3, t.Walked);
        Assert.Equal(1, t.Agrees);      // kid 7 points back at its parent 6
        Assert.Equal(1, t.Absent);      // kid 8 carries no /Parent
        Assert.Equal(1, t.Different);   // kid 9 points at 10, which is not where it was reached from
        Assert.Equal(t.Walked, t.Agrees + t.Absent + t.Different);
    }

    /// <summary>
    /// Object 6 is the only <c>/Fields</c> root; its three kids realise one bucket each.
    /// </summary>
    private static byte[] ThreeBucketPdf()
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [6 0 R] >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources 5 0 R >>",
            "<< /Length 0 >>\nstream\n\nendstream",
            "<< >>",
            "<< /T (root) /FT /Tx /Kids [7 0 R 8 0 R 9 0 R] >>",
            "<< /T (agrees) /Parent 6 0 R >>",
            "<< /T (absent) >>",
            "<< /T (differs) /Parent 10 0 R >>",
            "<< /T (elsewhere) >>",
        };

        var sb = new System.Text.StringBuilder();
        sb.Append("%PDF-1.7\n");
        var offsets = new int[bodies.Count];
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets[i] = sb.Length;
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture))
              .Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }

        int xref = sb.Length;
        sb.Append("xref\n0 ").Append((bodies.Count + 1).ToString(CultureInfo.InvariantCulture))
          .Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", o);
        sb.Append("trailer\n<< /Size ").Append((bodies.Count + 1).ToString(CultureInfo.InvariantCulture))
          .Append(" /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);
        return System.Text.Encoding.Latin1.GetBytes(sb.ToString());
    }

    // ------------------------------------------------------------------

    private static void Measure(string path, Tally t)
    {
        using var fs = File.OpenRead(path);
        using var doc = PdfDocument.Open(fs);

        var catalog = doc.Catalog?.Dictionary;
        var acroForm = catalog == null
            ? null
            : doc.ResolveObject(catalog[PdfName.AcroForm]) as PdfDictionary;
        var fields = acroForm == null
            ? null
            : doc.ResolveObject(acroForm[PdfName.Fields]) as PdfArray;
        if (fields == null || fields.Count == 0) return;

        t.WithForms++;

        // Reference identity, because a parser caches objects: two reads of `7 0 R` give the same
        // instance, so `ReferenceEquals` is the right test for "is this the node I came from".
        var seen = new HashSet<PdfObject>(ReferenceComparer.Instance);

        for (int i = 0; i < fields.Count; i++)
        {
            if (!(doc.ResolveObject(fields[i]) is PdfDictionary root)) continue;
            if (doc.ResolveObject(root[PdfName.Parent]) is PdfDictionary) t.RootWithParent++;
            Descend(root, doc, t, seen, 0);
        }
    }

    private static void Descend(PdfDictionary node, PdfDocument doc, Tally t,
                                HashSet<PdfObject> seen, int depth)
    {
        if (depth >= PdfLimits.MaxTreeDepth) return;
        if (!seen.Add(node)) return;

        if (!(doc.ResolveObject(node[PdfName.Kids]) is PdfArray kids)) return;

        for (int i = 0; i < kids.Count; i++)
        {
            if (!(doc.ResolveObject(kids[i]) is PdfDictionary kid)) continue;

            t.Walked++;
            var declared = doc.ResolveObject(kid[PdfName.Parent]) as PdfDictionary;
            if (declared == null) t.Absent++;
            else if (ReferenceEquals(declared, node)) t.Agrees++;
            else t.Different++;

            Descend(kid, doc, t, seen, depth + 1);
        }
    }

    private sealed class ReferenceComparer : IEqualityComparer<PdfObject>
    {
        internal static readonly ReferenceComparer Instance = new ReferenceComparer();
        public bool Equals(PdfObject a, PdfObject b) => ReferenceEquals(a, b);
        public int GetHashCode(PdfObject o) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o);
    }
}
