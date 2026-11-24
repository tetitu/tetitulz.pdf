// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Plugins.PostScript.Interpreter;
using Xunit;

namespace Tetitulz.Pdf.Plugins.PostScript.Tests;

/// <summary>
/// A dictionary key's hash is keyed, so a program cannot choose keys that share one bucket and make
/// every <c>def</c> walk them all; and keys that <c>eq</c> calls equal still hash alike.
/// </summary>
public class DictionaryKeyHashTests
{
    /// <summary>Reals whose two halves XOR to one value, so they share one <c>double.GetHashCode</c>.</summary>
    private static List<double> RealsSharingDoubleGetHashCode(int count)
    {
        var values = new List<double>();
        for (uint lo = 1; lo <= count; lo++)
            values.Add(BitConverter.Int64BitsToDouble((long)(((ulong)(0x3FF00000u ^ lo) << 32) | lo)));
        return values;
    }

    /// <summary>
    /// 4 096 real keys that share one <c>double.GetHashCode</c> take nearly as many hashes as keys, and
    /// a dictionary holding them all finds each one's value.
    /// </summary>
    [Fact]
    public void RealKeysSharingDoubleGetHashCode_SpreadAcrossBuckets()
    {
        List<double> reals = RealsSharingDoubleGetHashCode(4096);
        Assert.Single(reals.Select(v => v.GetHashCode()).Distinct());

        int distinct = reals.Select(v => PsObject.Real(v).GetHashCode()).Distinct().Count();
        Assert.True(distinct >= 4090, $"{distinct} distinct hashes for 4096 real keys");

        var dict = new PsDict();
        for (int i = 0; i < reals.Count; i++) dict.Put(PsObject.Real(reals[i]), PsObject.Int(i));
        Assert.Equal(4096, dict.Count);
        Assert.True(dict.TryGet(PsObject.Real(reals[4095]), out PsObject last));
        Assert.Equal(4095, last.AsInt());
    }

    /// <summary>Keys <c>eq</c> calls equal hash alike: an integer and the equal real, 0 and −0, a name and a string of its characters.</summary>
    [Fact]
    public void EqualKeys_HashAlike()
    {
        Assert.True(PsObject.Int(7).Equals(PsObject.Real(7.0)));
        Assert.Equal(PsObject.Int(7).GetHashCode(), PsObject.Real(7.0).GetHashCode());
        Assert.Equal(PsObject.Real(0.0).GetHashCode(), PsObject.Real(-0.0).GetHashCode());

        PsObject name = PsObject.LitName("abc");
        PsObject text = PsObject.Str(new PsString(Encoding.ASCII.GetBytes("abc")));
        Assert.True(name.Equals(text));
        Assert.Equal(name.GetHashCode(), text.GetHashCode());

        Assert.NotEqual(PsObject.Int(7).GetHashCode(), PsObject.Int(8).GetHashCode());
    }
}
