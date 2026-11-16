// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Linq;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Forms;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Forms;

/// <summary>
/// <see cref="FieldNames.Qualify"/> under <see cref="PdfLimits.MaxQualifiedFieldNameLength"/>: a
/// name at the ceiling is the name, a longer one its stand-in — the head, a <c>#</c> and the
/// SHA-256 of the whole name — so two names that differ anywhere stay two names, and a name the
/// file makes as long as it likes costs a fixed length.
/// </summary>
[Collection(Constants.PdfLimitsCollection.Name)]
public sealed class FieldNameCeilingTests
{
    private static readonly int StandInLength = FieldNames.StandInHeadLength + 1 + 64;

    [Fact]
    public void ANameAtTheCeiling_IsTheName_AndOnePast_IsItsStandIn()
    {
        int max = PdfLimits.DefaultMaxQualifiedFieldNameLength;
        string parent = new string('p', 100);
        string atCeiling = new string('a', max - parent.Length - 1);
        Assert.Equal(parent + "." + atCeiling, FieldNames.Qualify(parent, atCeiling));

        string past = FieldNames.Qualify(parent, atCeiling + "a");
        Assert.Equal(StandInLength, past.Length);
        Assert.StartsWith(parent.Substring(0, FieldNames.StandInHeadLength), past, StringComparison.Ordinal);
        Assert.Equal('#', past[FieldNames.StandInHeadLength]);
        Assert.All(past.Substring(FieldNames.StandInHeadLength + 1), c => Assert.Contains(c, "0123456789abcdef"));
    }

    [Fact]
    public void LongNamesDifferingOnlyAtTheirEnd_HaveDifferentStandIns_AndOneNameAlwaysTheSame()
    {
        string big = new string('x', 1 << 20);
        string a = FieldNames.Qualify(null, big + "a");
        string b = FieldNames.Qualify(null, big + "b");
        Assert.NotEqual(a, b);
        Assert.Equal(a, FieldNames.Qualify(null, string.Copy(big + "a")));   // another string, the same name
        Assert.Equal(StandInLength, a.Length);

        // The parent's side counts too: one partial under two parents that differ at their end.
        string under1 = FieldNames.Qualify(new string('q', 1000) + "1", big);
        string under2 = FieldNames.Qualify(new string('q', 1000) + "2", big);
        Assert.NotEqual(under1, under2);
    }

    [Fact]
    public void AStandIn_JoinsItsChildrenLikeAnyName_AndNeverCutsASurrogatePair()
    {
        string parent = FieldNames.Qualify(null, new string('n', 5000));
        Assert.Equal(parent + ".child", FieldNames.Qualify(parent, "child"));

        // A pair straddling the head's last place is left out whole.
        string paired = new string('h', FieldNames.StandInHeadLength - 1) + "\U0001F600" + new string('t', 5000);
        string standIn = FieldNames.Qualify(null, paired);
        Assert.Equal(new string('h', FieldNames.StandInHeadLength - 1) + "#", standIn.Substring(0, FieldNames.StandInHeadLength));
        Assert.False(standIn.Any(char.IsSurrogate));
    }

    [Fact]
    public void ALongPartialName_IsHashedOnce_HoweverManyFieldsItIsJoinedFor()
    {
        string shared = new string('s', 1 << 16);
        long before = FieldNames.PartialDigestsComputed;
        string first = FieldNames.Qualify("p0", shared);
        for (int i = 1; i < 100; i++) Assert.NotEqual(first, FieldNames.Qualify("p" + i, shared));
        Assert.Equal(1, FieldNames.PartialDigestsComputed - before);
    }

    [Fact]
    public void AFieldAnswersTheNameItJoined_UntilItsNameChanges()
    {
        var parentDict = new PdfDictionary { [PdfName.T] = new PdfString(new string('P', 5000)) };
        var kidDict = new PdfDictionary { [PdfName.T] = new PdfString("kid") };
        using var doc = MinimalDoc();
        var parent = new PdfFormField(parentDict, doc, null);
        var kid = new PdfFormField(kidDict, doc, parent);

        string name = kid.FullyQualifiedName;
        Assert.EndsWith(".kid", name, StringComparison.Ordinal);
        Assert.Same(name, kid.FullyQualifiedName);   // the memo: the same string, not a second join

        kidDict[PdfName.T] = new PdfString("renamed");
        Assert.EndsWith(".renamed", kid.FullyQualifiedName, StringComparison.Ordinal);
        parentDict[PdfName.T] = new PdfString("short");
        Assert.Equal("short.renamed", kid.FullyQualifiedName);
    }

    private static PdfDocument MinimalDoc()
    {
        string pdfText =
            "%PDF-1.4\n" +
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n" +
            "2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n" +
            "xref\n0 3\n0000000000 65535 f \n0000000009 00000 n \n0000000058 00000 n \n" +
            "trailer\n<< /Size 3 /Root 1 0 R >>\n" +
            "startxref\n107\n%%EOF\n";
        return PdfDocument.Open(new System.IO.MemoryStream(System.Text.Encoding.ASCII.GetBytes(pdfText)));
    }

    [Fact]
    public void TheCeilingIsTheSetting()
    {
        try
        {
            PdfLimits.MaxQualifiedFieldNameLength = 10;
            Assert.Equal("abc.defghi", FieldNames.Qualify("abc", "defghi"));
            Assert.Equal(StandInLength - (FieldNames.StandInHeadLength - 11), FieldNames.Qualify("abc", "defghij").Length);
        }
        finally
        {
            PdfLimits.ResetToDefaults();
        }
    }
}
