// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Metadata;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// A Reader-Extended document's UR3 usage rights must be reported whichever way the two keys that
/// gate the walk are spelled.
/// </summary>
/// <remarks>
/// <para><b>Both nulls fall the same way, and it is the expensive way for a caller.</b>
/// <c>/FT</c> gates whether a field is examined at all, and <c>/TransformMethod</c> gates whether a
/// signature-reference dictionary is recognised as UR3. Read as absent, either one makes
/// <c>GetUsageRights</c> report <b>no rights</b> for a document that grants them — and tooling
/// gated on that answer refuses work the document permits. "No rights" is also the correct answer
/// for almost every PDF, which is exactly why the failure is invisible.</para>
///
/// <para>Both were the only unresolved reads in methods that resolve everything around them:
/// <c>/V</c> and <c>/Kids</c> sit three and twelve lines from <c>/FT</c>, and
/// <c>/Reference</c>, each of its elements and <c>/TransformParams</c> all surround
/// <c>/TransformMethod</c>.</para>
///
/// <para><b>Asserted on the parsed entry and its permission bits.</b> "Read returned a list" is
/// satisfied by the empty list the defect produces.</para>
/// </remarks>
public class IndirectUsageRightsReadTests
{
    /// <summary>How the fixture writes the two keys that gate the walk.</summary>
    public enum Spelling
    {
        /// <summary>Both written inline — the control, which always worked.</summary>
        Direct,

        /// <summary><c>/FT 9 0 R</c>.</summary>
        IndirectFieldType,

        /// <summary><c>/TransformMethod 10 0 R</c>.</summary>
        IndirectTransformMethod,

        /// <summary>
        /// <c>/Name 11 0 R</c> — one of the three reads that only reach the reported entry rather
        /// than gating the walk. Included because a mutation of that read <b>survived</b> the first
        /// version of this test, and correctly so: the fixture wrote <c>/Name</c> inline, making the
        /// mutation equivalent. A family fixed together needs a case per member, or the quiet ones
        /// are fixed on faith.
        /// </summary>
        IndirectSignerName,
    }

    [Theory]
    [InlineData(Spelling.Direct)]
    [InlineData(Spelling.IndirectFieldType)]
    [InlineData(Spelling.IndirectTransformMethod)]
    [InlineData(Spelling.IndirectSignerName)]
    public void UsageRightsAreReported_WhicheverWayTheGatesAreSpelled(Spelling spelling)
    {
        using var ms = new MemoryStream(UsageRightsPdf(spelling));
        IReadOnlyList<UsageRightsEntry> entries = PdfUsageRightsReader.Read(ms);

        Assert.NotNull(entries);
        var entry = Assert.Single(entries);

        // The permission actually granted, not merely that an entry exists.
        Assert.True(entry.HasAnnotation, "annotation rights were not reported: " + entry.Rights);
        Assert.Equal("Rights Granter", entry.SignerName);
    }

    /// <summary>
    /// The floor: a document with no UR3 transform yields no entries, so the assertion above cannot
    /// be met by a reader that reports one unconditionally.
    /// </summary>
    [Fact]
    public void ADocumentWithoutUsageRights_ReportsNone()
    {
        using var ms = new MemoryStream(UsageRightsPdf(Spelling.Direct, withUr3: false));
        IReadOnlyList<UsageRightsEntry> entries = PdfUsageRightsReader.Read(ms);

        Assert.NotNull(entries);
        Assert.Empty(entries);
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// A document whose AcroForm holds one signature field carrying a UR3 transform granting
    /// annotation rights. Objects 9 and 10 hold the two gate values; <paramref name="spelling"/>
    /// selects which one the document points at instead of writing inline.
    /// </summary>
    private static byte[] UsageRightsPdf(Spelling spelling, bool withUr3 = true)
    {
        string ft = spelling == Spelling.IndirectFieldType ? "9 0 R" : "/Sig";
        string method = spelling == Spelling.IndirectTransformMethod ? "10 0 R" : "/UR3";
        string name = spelling == Spelling.IndirectSignerName ? "11 0 R" : "(Rights Granter)";
        string reference = withUr3 ? " /Reference [7 0 R]" : "";

        return Build(
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R] /SigFlags 3 >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R >>",
            "<< /Length 0 >>\nstream\n\nendstream",
            "<< /Type /Annot /Subtype /Widget /FT " + ft + " /T (SigUR) /Rect [0 0 10 10]"
                + " /V 6 0 R >>",
            "<< /Type /Sig /Filter /Adobe.PPKLite /SubFilter /adbe.pkcs7.detached"
                + " /Name " + name + reference + " >>",
            "<< /Type /SigRef /TransformMethod " + method + " /TransformParams 8 0 R"
                + " /DigestMethod /SHA256 >>",
            "<< /Type /TransformParams /V /2.2 /Annots [/Create /Modify] >>",
            "/Sig",
            "/UR3",
            "(Rights Granter)");
    }

    /// <summary>Assembles objects 1..n with a correct xref table. Bodies must be ASCII.</summary>
    private static byte[] Build(params string[] objects)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");

        var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }

        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.Append("trailer\n<< /Size ").Append(objects.Length + 1).Append(" /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
