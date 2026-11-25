// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Builder.Model;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// The loose-list-item marker and the first-strong sampling the Auto text direction reads: a
/// numbered item's marker, the empty samples of an
/// absent paragraph or section, and a list item's text reaching the sample of its section.
/// </summary>
public class ModelRulesSampleTests
{
    [Fact]
    public void ALooseNumberedItem_IsMarkedWithItsLevelAndADot()
    {
        Assert.Equal("1. ", ModelRules.ListMarker(new ListItem { IsNumbered = true, Level = 0 }));
        Assert.Equal("3. ", ModelRules.ListMarker(new ListItem { IsNumbered = true, Level = 2 }));
    }

    [Fact]
    public void ALooseBulletItem_IsMarkedWithABullet()
    {
        Assert.Equal("• ", ModelRules.ListMarker(new ListItem { IsNumbered = false, Level = 0 }));
    }

    [Fact]
    public void AnExplicitMarker_WinsOverTheDerivedOne()
    {
        Assert.Equal("→", ModelRules.ListMarker(new ListItem { IsNumbered = true, Marker = "→" }));
    }

    [Fact]
    public void AnAbsentParagraph_SamplesNothing()
    {
        Assert.Equal("", ModelRules.SampleText((Paragraph?)null));
    }

    [Fact]
    public void AnAbsentSection_SamplesNothing()
    {
        Assert.Equal("", ModelRules.SampleText((Section?)null));
    }

    [Fact]
    public void ASectionSamplesItsListItemText()
    {
        var section = new Section();
        section.Children.Add(new ListItem { Text = "Hello" });

        Assert.Contains("Hello", ModelRules.SampleText(section), System.StringComparison.Ordinal);
    }

    /// <summary>
    /// A section's direction is sampled only when it is Auto, and then from a prefix of its blocks:
    /// the PDF exporter asks once per box and table, and a section of ten thousand empty boxes was
    /// walked to its end at every question, whatever its direction.
    /// </summary>
    [Fact]
    public void AdversarialSectionOfTextFreeBlocks_IsSampledByAPrefix()
    {
        Section Boxes(TextDirection direction)
        {
            var s = new Section { Direction = direction };
            for (int i = 0; i < 10_000; i++) { s.Children.Add(new BoxStart()); s.Children.Add(new BoxEnd()); }
            return s;
        }

        ModelRules.SampledBlocks = 0;
        Section ltr = Boxes(TextDirection.LeftToRight);
        for (int i = 0; i < 100; i++) Assert.False(ModelRules.IsRightToLeft(ltr));
        Assert.Equal(0, ModelRules.SampledBlocks);

        Section auto = Boxes(TextDirection.Auto);
        for (int i = 0; i < 100; i++) Assert.False(ModelRules.IsRightToLeft(auto));
        Assert.InRange(ModelRules.SampledBlocks, 1, 100 * 512);   // was 100 × 20 000
    }

    /// <summary>The control: an Auto section still resolves from its first strong character, and a declared one by its declaration.</summary>
    [Fact]
    public void ASectionsDirection_StillResolves()
    {
        var auto = new Section { Direction = TextDirection.Auto };
        auto.Children.Add(new Paragraph { Runs = { new TextRun { Text = "שלום" } } });
        Assert.True(ModelRules.IsRightToLeft(auto));
        Assert.True(ModelRules.IsRightToLeft(new Section { Direction = TextDirection.RightToLeft }));
        Assert.False(ModelRules.IsRightToLeft(new Section { Direction = TextDirection.LeftToRight }));
    }
}
