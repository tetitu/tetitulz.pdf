// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Document;

/// <summary>
/// A signature's dictionary is among the bytes it signs when the object holding it is the one the
/// excluded gap stands in: it starts ahead of the gap and has not ended by it.
/// </summary>
public sealed class SignedGapObjectTests
{
    private const string Dictionary = "6 0 obj\n<< /Type /Sig /ByteRange [0 0 0 0] /Contents ";

    private static MemoryStream File(string text) => new MemoryStream(Encoding.Latin1.GetBytes(text));

    [Fact]
    public void The_object_the_gap_stands_in_holds_it()
    {
        const string prefix = "%PDF-1.7\n";
        using MemoryStream file = File(prefix + Dictionary + "<00> >>\nendobj\n");

        Assert.True(SignedByteRanges.GapIsInObjectAt(file, prefix.Length, prefix.Length + Dictionary.Length));
    }

    [Fact]
    public void AdversarialObject_AfterTheGap_DoesNotHoldIt()
    {
        using MemoryStream file = File(Dictionary + "<00> >>\nendobj\n7 0 obj\n<< >>\nendobj\n");

        Assert.False(SignedByteRanges.GapIsInObjectAt(file, Dictionary.Length + 10, Dictionary.Length));
        Assert.False(SignedByteRanges.GapIsInObjectAt(file, Dictionary.Length, Dictionary.Length));
    }

    [Fact]
    public void AdversarialObject_EndedBeforeTheGap_DoesNotHoldIt()
    {
        const string earlier = "5 0 obj\n<< >>\nendobj\n";
        using MemoryStream file = File(earlier + Dictionary + "<00> >>\nendobj\n");

        Assert.False(SignedByteRanges.GapIsInObjectAt(file, 0, earlier.Length + Dictionary.Length));
    }

    [Fact]
    public void AdversarialObject_TooFarAheadOfTheGap_IsNotRead()
    {
        int padding = SignedByteRanges.MaxBytesBeforeContents + 1;
        using var file = new MemoryStream(new byte[padding + 16]);

        Assert.False(SignedByteRanges.GapIsInObjectAt(file, 0, padding));
        Assert.True(SignedByteRanges.GapIsInObjectAt(file, 1, padding));   // control: a byte less is read
    }

    [Fact]
    public void AdversarialGap_PastTheFile_DoesNotHoldIt()
    {
        using MemoryStream file = File(Dictionary);

        Assert.False(SignedByteRanges.GapIsInObjectAt(file, 0, Dictionary.Length + 100));
    }

    [Fact]
    public void The_files_position_is_put_back()
    {
        using MemoryStream file = File(Dictionary + "<00>");
        file.Position = 3;

        SignedByteRanges.GapIsInObjectAt(file, 0, Dictionary.Length);

        Assert.Equal(3, file.Position);
    }
}
