// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Document;

/// <summary>
/// The one <c>/RoleMap</c> reader (§14.7.3 / §14.8.4), shared by three of the four readers that
/// used to exist across the accessibility feature and the Conformance plugin.
/// </summary>
public class PdfRoleMapTests
{
    /// <summary>
    /// <c>/RoleMap</c> and its values are routinely written indirectly, and
    /// <c>PdfDictionary</c>'s indexer does not follow a reference — unresolved, the map reads as
    /// <i>absent</i> and every custom role becomes an unknown structure type.
    /// </summary>
    [Fact]
    public void AnIndirectRoleMapAndAnIndirectTargetAreBothFollowed()
    {
        var roleMap = new PdfDictionary();
        roleMap.Set(new PdfName("Chapter"), new PdfReference(9, 0));
        roleMap.Set(new PdfName("Bullet"), new PdfName("LI"));

        var root = new PdfDictionary();
        root.Set(PdfName.RoleMap, new PdfReference(8, 0));

        var objects = new Dictionary<int, PdfObject> { [8] = roleMap, [9] = new PdfName("H1") };

        var map = PdfRoleMap.Build(root, o => Resolve(objects, o));

        Assert.Equal("H1", map["Chapter"]);
        Assert.Equal("LI", map["Bullet"]);
    }

    /// <summary>
    /// An entry whose value is not a name is kept with a <c>null</c> value rather than dropped.
    /// The deep checker's UA13 reports it as <c>"X → (not a name)"</c>, and dropping it would make
    /// an unresolvable role indistinguishable from an unmapped one — a different violation.
    /// </summary>
    [Fact]
    public void AnEntryWhoseTargetIsNotANameIsKeptWithANullValue()
    {
        var roleMap = new PdfDictionary();
        roleMap.Set(new PdfName("Broken"), new PdfNumber(7));
        roleMap.Set(new PdfName("Fine"), new PdfName("P"));

        var root = new PdfDictionary();
        root.Set(PdfName.RoleMap, roleMap);

        var map = PdfRoleMap.Build(root, o => o);

        Assert.True(map.ContainsKey("Broken"));
        Assert.Null(map["Broken"]);
        Assert.Equal("P", map["Fine"]);
    }

    [Fact]
    public void NoStructTreeOrNoRoleMapYieldsAnEmptyMap()
    {
        Assert.Empty(PdfRoleMap.Build(null, o => o));
        Assert.Empty(PdfRoleMap.Build(new PdfDictionary(), o => o));
    }

    [Fact]
    public void AChainIsFollowedToItsTerminus()
    {
        var map = Map(("Bullet", "ListItem"), ("ListItem", "LI"));

        Assert.Equal("LI", PdfRoleMap.Resolve("Bullet", map));
        Assert.Equal("LI", PdfRoleMap.Resolve("ListItem", map));
        Assert.Equal("Unmapped", PdfRoleMap.Resolve("Unmapped", map));
    }

    /// <summary>
    /// A cycle resolves to the role the chain re-enters — which is the role you started from — and
    /// that is a property of the <b>visited set</b>, not of the hop cap.
    /// </summary>
    /// <remarks>
    /// <para><b>This is the assertion that separates the three copies this merge replaced.</b> Two
    /// of them had a hop cap and no visited set, so on a three-node cycle they stopped wherever
    /// 16 mod 3 landed — one hop in, i.e. <c>B</c> for a walk starting at <c>A</c> — while the
    /// third stopped at the first repeat and returned <c>A</c>. Same file, two answers.</para>
    ///
    /// <para>A <b>two</b>-node cycle cannot tell them apart: 16 is even, so a cap-only walk also
    /// lands back on its starting role. The only in-tree cycle fixture before this was two-node.</para>
    /// </remarks>
    [Fact]
    public void AThreeNodeCycleResolvesToTheRoleTheChainReEnters()
    {
        var map = Map(("A", "B"), ("B", "C"), ("C", "A"));

        Assert.Equal("A", PdfRoleMap.Resolve("A", map));
        Assert.Equal("B", PdfRoleMap.Resolve("B", map));
        Assert.Equal("C", PdfRoleMap.Resolve("C", map));
    }

    [Fact]
    public void ASelfMapAndANullTargetBothEndTheChain()
    {
        var selfMap = Map(("Loop", "Loop"));
        Assert.Equal("Loop", PdfRoleMap.Resolve("Loop", selfMap));

        var broken = new Dictionary<string, string> { ["Custom"] = "Middle", ["Middle"] = null };
        // Stops at the unusable entry rather than walking into it —
        // Dictionary.TryGetValue(null) throws, so this is a correctness guard, not a nicety.
        Assert.Equal("Middle", PdfRoleMap.Resolve("Custom", broken));
    }

    /// <summary>
    /// <c>Means</c> tests the raw name as well as the resolved one, so a file cannot turn a check
    /// off by remapping the standard type the check looks for.
    /// </summary>
    [Fact]
    public void MeansIsNotDefeatedByRemappingTheStandardTypeItLooksFor()
    {
        var attack = Map(("Figure", "Div"));

        // A real <Figure> still means Figure, even though the map says otherwise.
        Assert.True(PdfRoleMap.Means("Figure", attack, "Figure"));

        // And a custom role that maps onto Figure means Figure too.
        var custom = Map(("Illustration", "Figure"));
        Assert.True(PdfRoleMap.Means("Illustration", custom, "Figure"));

        Assert.False(PdfRoleMap.Means("Table", custom, "Figure"));
        Assert.False(PdfRoleMap.Means(null, custom, "Figure"));
    }

    private static Dictionary<string, string> Map(params (string from, string to)[] entries)
    {
        var map = new Dictionary<string, string>(System.StringComparer.Ordinal);
        foreach (var (from, to) in entries) map[from] = to;
        return map;
    }

    private static PdfObject Resolve(Dictionary<int, PdfObject> objects, PdfObject obj)
        => obj is PdfReference r && objects.TryGetValue(r.ReferenceObjectNumber, out PdfObject target)
            ? target
            : obj;
}
