// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Accessibility;
using Tetitulz.Pdf.Accessibility.Internal;
using Tetitulz.Pdf.Accessibility.Models;
using Tetitulz.Pdf.Accessibility.Tagging;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// <c>AutoTagOptions.FigureAlternateText</c> — an image the caller can describe is wrapped in a
/// <c>/Figure</c> carrying that description as <c>/Alt</c>.
/// </summary>
/// <remarks>
/// <para><b>The register said this was blocked on two things and both were wrong.</b> "Nothing
/// records the <c>Do</c> operator's byte offsets" was false — <c>ContentToken</c> carries a start
/// and an end for every token — and "emitting <c>/Figure</c> without an <c>/Alt</c> manufactures a
/// UA07 finding" is a reason to <i>ask the caller first</i>, not a reason to stop. What was actually
/// left was a reading-order merge and a resource read.</para>
///
/// <para><b>Declining is the load-bearing case, so it has more tests than accepting.</b> A
/// <c>/Figure</c> with no usable <c>/Alt</c> is worse than no <c>/Figure</c>: it reports as tagged,
/// reads as nothing, and is a finding this pass would have created.
/// <see cref="AnInferredFigurePassesThisLibrarysOwnImageChecks"/> is the test that closes that
/// loop.</para>
/// </remarks>
public sealed class FigureInferenceTests
{
    private const string Alt = "A bar chart comparing regional sales for the year.";

    /// <summary>An image the caller describes becomes a Figure carrying that description.</summary>
    [Fact]
    public void AnImageTheCallerDescribesBecomesAFigure()
    {
        Tree tree = Tag(OneImageAndAParagraph(), c => Alt);

        Assert.Equal(1, tree.Count("Figure"));
        Assert.Equal(new[] { Alt }, tree.AltTextOfEvery("Figure"));
    }

    /// <summary>Without a callback the same page has no Figure at all.</summary>
    /// <remarks>
    /// <b>The acceptance control for the whole file and for the default.</b> Existing callers must
    /// see exactly what they saw before, so supplying the callback has to be the only thing that
    /// changes the answer — not the presence of an image on the page.
    /// </remarks>
    [Fact]
    public void TheSamePageWithoutACallbackHasNoFigure()
    {
        Tree tree = Tag(OneImageAndAParagraph(), callback: null);

        Assert.Equal(0, tree.Count("Figure"));
        Assert.Equal(1, tree.Count("P"));
    }

    /// <summary>An image the caller cannot describe is left untagged, and counted.</summary>
    /// <remarks>
    /// <b>The decision the whole feature rests on.</b> ISO 14289-1 §7.3 requires a <c>/Figure</c> to
    /// carry an <c>/Alt</c>; emitting one anyway would trade an untagged image for a non-conformant
    /// tagged one. The count is what stops the loss being silent — an image nobody could describe is
    /// still an image a screen reader will not reach.
    /// </remarks>
    [Fact]
    public void AnImageTheCallerDeclinesIsLeftUntaggedAndCounted()
    {
        AutoTagResult result = Result(OneImageAndAParagraph(), c => null);

        Assert.Equal(0, result.FiguresTagged);
        Assert.Equal(1, result.FiguresDeclined);
        Assert.Equal(0, new Tree(TaggedBytes(OneImageAndAParagraph(), c => null)).Count("Figure"));
    }

    /// <summary>Empty alternate text is a decline, not an empty Alt.</summary>
    /// <remarks>
    /// An empty <c>/Alt</c> is its own UA15 finding — "only valid for a purely decorative figure,
    /// which should be an artifact instead" — so treating <c>""</c> as an answer would produce
    /// exactly the shape the checker rejects. The callback has one way to say no and both spellings
    /// of it mean the same thing.
    /// </remarks>
    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void EmptyAlternateTextIsADecline(string answer)
    {
        AutoTagResult result = Result(OneImageAndAParagraph(), c => answer);

        Assert.Equal(0, result.FiguresTagged);
        Assert.Equal(1, result.FiguresDeclined);
    }

    /// <summary>A declined image is warned about, not only counted.</summary>
    /// <remarks>
    /// <b>The count alone cannot reach a caller using the facade.</b>
    /// <c>PdfAccessibility.RemediateWithReport</c> surfaces each step's <em>warnings</em>, not its
    /// <c>AutoTagResult</c> — so without this the one outcome the feature is careful about, an
    /// image left unreachable, would be invisible to the entry point most callers use.
    /// <para>
    /// It cannot fire on conformant input: <c>FiguresDeclined</c> only moves when a callback was
    /// supplied <em>and</em> returned nothing for an image it was offered. The second assertion is
    /// what pins that — a channel that fires when nobody asked for the feature is one callers
    /// learn to ignore.
    /// </para>
    /// </remarks>
    [Fact]
    public void ADeclinedImageIsWarnedAboutAndNotOnlyCounted()
    {
        AutoTagResult declined = Result(OneImageAndAParagraph(), c => null);

        Assert.Contains(declined.Warnings,
            w => w.Contains("left untagged", StringComparison.Ordinal)
              && w.Contains("FigureAlternateText", StringComparison.Ordinal));

        // The acceptance control: a caller who never asked for figures is not warned about them.
        AutoTagResult untouched = Result(OneImageAndAParagraph(), callback: null);
        Assert.DoesNotContain(untouched.Warnings,
            w => w.Contains("left untagged", StringComparison.Ordinal));
    }

    /// <summary>
    /// A shared form is one decision but several elements, and the two counters say so.
    /// </summary>
    /// <remarks>
    /// <b>The pair does not sum to "images seen", and the docs used to imply it did.</b> A Form
    /// XObject drawn on two pages is tagged once and its bytes shared by both copies, so the
    /// callback is asked <em>once</em> while <em>two</em> <c>/Figure</c> elements are written.
    /// <c>FiguresTagged</c> counts elements; <c>FiguresDeclined</c> counts decisions. Both numbers
    /// are the useful one for a different question, and neither is derivable from the other.
    /// </remarks>
    [Fact]
    public void ASharedFormIsOneDecisionButSeveralElements()
    {
        var asked = new List<FigureCandidate>();

        using var input = new MemoryStream(AFormSharedByTwoPages());
        using var output = new MemoryStream();
        AutoTagResult result = PdfAutoTagger.AutoTag(
            input, output,
            new AutoTagOptions
            {
                DuplicateSharedFormXObjects = true,
                FigureAlternateText = c => { asked.Add(c); return Alt; },
            });

        Assert.Single(asked);
        Assert.Equal(2, result.FiguresTagged);
        Assert.Equal(2, new Tree(output.ToArray()).Count("Figure"));
    }

    /// <summary>The counts report both halves.</summary>
    [Fact]
    public void TheResultReportsTaggedAndDeclinedSeparately()
    {
        // Two images; the caller describes the first and declines the second.
        AutoTagResult result = Result(
            TwoImages(), c => string.Equals(c.XObjectName, "Im0", StringComparison.Ordinal) ? Alt : null);

        Assert.Equal(1, result.FiguresTagged);
        Assert.Equal(1, result.FiguresDeclined);
    }

    /// <summary>A Form XObject is not an image and is never offered.</summary>
    /// <remarks>
    /// <b>The refusal the resource read exists for.</b> The tagger is handed raw content bytes, so
    /// <c>/Fm0 Do</c> and <c>/Im0 Do</c> are the same three tokens to it; only the page's
    /// <c>/Resources /XObject</c> says which is a picture. Without that read, a form containing a
    /// page of text would be wrapped in a <c>/Figure</c> and described as an image.
    /// </remarks>
    [Fact]
    public void AFormXObjectIsNeverOfferedAsAFigure()
    {
        var offered = new List<string>();
        Tree tree = Tag(AFormAndAnImage(), c => { offered.Add(c.XObjectName); return Alt; });

        Assert.Equal(new[] { "Im0" }, offered);
        Assert.Equal(1, tree.Count("Figure"));
    }

    /// <summary>The candidate carries the name, the page and the pixel dimensions.</summary>
    /// <remarks>
    /// Those four values are the whole contract: they are what a lookup table, a filename convention
    /// or a vision service keys on. The dimensions in particular are how a caller recognises a
    /// spacer or a hairline rule it would rather decline than describe.
    /// </remarks>
    [Fact]
    public void TheCandidateCarriesTheImagesIdentityAndSize()
    {
        var seen = new List<FigureCandidate>();
        Result(OneImageAndAParagraph(), c => { seen.Add(c); return Alt; });

        FigureCandidate only = Assert.Single(seen);
        Assert.Equal("Im0", only.XObjectName);
        Assert.Equal(0, only.PageIndex);
        Assert.Equal(new[] { 0 }, only.PageIndexes);
        Assert.Equal(8, only.PixelWidth);
        Assert.Equal(4, only.PixelHeight);
    }

    /// <summary>
    /// An inline image (<c>BI…EI</c>) the caller describes becomes a Figure too, read where it is
    /// drawn — the owner's decision of 2026-10-18: inline images are offered as image XObjects are.
    /// </summary>
    [Fact]
    public void AnInlineImageTheCallerDescribesBecomesAFigure()
    {
        Tree tree = Tag(AnInlineImageAndAParagraph(), c => c.IsInlineImage ? Alt : null);

        Assert.Equal(1, tree.Count("Figure"));
        Assert.Equal(new[] { Alt }, tree.AltTextOfEvery("Figure"));
        Assert.Equal(new[] { "Figure", "P" }, tree.RootRoles);
    }

    /// <summary>
    /// The inline candidate names the image by its place among the stream's inline images, with
    /// no XObject name, and carries its size from the abbreviated keys (§8.9.7, Table 92).
    /// </summary>
    [Fact]
    public void TheInlineCandidateCarriesItsPlaceAndSize()
    {
        var seen = new List<FigureCandidate>();
        Result(AnInlineImageAndAParagraph(), c => { seen.Add(c); return Alt; });

        FigureCandidate only = Assert.Single(seen);
        Assert.True(only.IsInlineImage);
        Assert.Equal(0, only.InlineImageIndex);
        Assert.Null(only.XObjectName);
        Assert.Equal(new[] { 0 }, only.PageIndexes);
        Assert.Equal(4, only.PixelWidth);
        Assert.Equal(2, only.PixelHeight);
    }

    /// <summary>
    /// Two inline images are asked about by their indexes, in drawing order; the one declined is left
    /// untagged and counted, the other tagged.
    /// </summary>
    [Fact]
    public void TwoInlineImages_AreAskedByIndex_AndADeclineIsCounted()
    {
        var asked = new List<int>();
        string content = InlineImage(600) + InlineImage(400) + "BT /F1 12 Tf 72 300 Td (Two pictures above.) Tj ET\n";
        AutoTagResult result = Result(content, c => { asked.Add(c.InlineImageIndex); return c.InlineImageIndex == 1 ? Alt : null; });

        Assert.Equal(new[] { 0, 1 }, asked);
        Assert.Equal(1, result.FiguresTagged);
        Assert.Equal(1, result.FiguresDeclined);

        // The declined one has no element at all — not a /Figure without /Alt.
        Tree tree = Tag(content, c => c.InlineImageIndex == 1 ? Alt : null);
        Assert.Equal(1, tree.Count("Figure"));
        Assert.Equal(new[] { Alt }, tree.AltTextOfEvery("Figure"));
    }

    /// <summary>
    /// An inline image already inside a marked-content sequence — here an <c>/Artifact</c>, which
    /// declares it decoration — is not offered: it is someone's content already.
    /// </summary>
    [Fact]
    public void AnInlineImageInsideAnArtifactSequenceIsNotOffered()
    {
        var asked = new List<int>();
        var options = new PageTagOptions
        {
            PageHeight                 = 792f,
            AdoptExistingMarkedContent = true,
            InlineImageAlternateText   = (index, parameters) => { asked.Add(index); return Alt; },
        };

        PageTagResult result = ContentStreamTagger.TagPage(Encoding.Latin1.GetBytes(
            "/Artifact BMC " + InlineImage(600) + "EMC\n" + InlineImage(300) +
            "BT /F1 12 Tf 72 500 Td (A paragraph between the pictures.) Tj ET\n"), options);

        Assert.Null(result.SkipReason);
        Assert.Equal(new[] { 1 }, asked);                       // the second, outside the artifact, is offered
        Assert.Equal(1, result.Runs.Count(r => r.Role == "Figure"));
    }

    /// <summary>
    /// An inline image a producer drew inside a text object (which §8.2 does not allow, and some
    /// producers do) is not offered: the text object's bytes are cut into runs already, and a
    /// sequence around the image could not be guaranteed to nest.
    /// </summary>
    [Fact]
    public void AnInlineImageInsideATextObjectIsNotOffered()
    {
        var asked = new List<int>();
        var options = new PageTagOptions
        {
            PageHeight               = 792f,
            InlineImageAlternateText = (index, parameters) => { asked.Add(index); return Alt; },
        };

        PageTagResult result = ContentStreamTagger.TagPage(Encoding.Latin1.GetBytes(
            "BT /F1 12 Tf 72 500 Td (Text and a picture.) Tj BI /W 4 /H 2 /CS /G /BPC 8 ID ABCDEFGH EI ET\n" +
            InlineImage(300)), options);

        Assert.Null(result.SkipReason);
        Assert.Equal(new[] { 1 }, asked);                       // the one outside the text object is offered
        Assert.Equal(1, result.Runs.Count(r => r.Role == "Figure"));
    }

    /// <summary>An inline figure is reported clean by this library's own image checks, as an XObject figure is.</summary>
    [Fact]
    public void AnInlineFigurePassesThisLibrarysOwnImageChecks()
    {
        byte[] pdf = TaggedBytes(AnInlineImageAndAParagraph(), c => Alt);
        Assert.Equal(1, new Tree(pdf).Count("Figure"));

        using var ms = new MemoryStream(pdf);
        AccessibilityAuditReport report = PdfAccessibility.Audit(ms);
        Assert.Empty(report.Issues.Where(
            i => string.Equals(i.Code, "UA07", StringComparison.Ordinal)
              || string.Equals(i.Code, "UA15", StringComparison.Ordinal)));
    }

    /// <summary>An image drawn above a paragraph is read before it.</summary>
    /// <remarks>
    /// <b>The claim the unconditional sort exists for, and the one nothing else in this file can
    /// see.</b> Figures are collected after every text object has been walked, so without ordering
    /// the run list by byte offset a picture at the top of the page would be announced after all of
    /// its text. Counting roles cannot detect that; only the order of <c>/Document</c>'s kids can.
    /// </remarks>
    [Fact]
    public void AnImageAboveAParagraphIsReadBeforeIt()
    {
        Tree tree = Tag(OneImageAndAParagraph(), c => Alt);

        Assert.Equal(new[] { "Figure", "P" }, tree.RootRoles);
    }

    /// <summary>And an image drawn below the text is read after it.</summary>
    /// <remarks>
    /// The other half, because "always first" would pass the test above just as well as "in content
    /// order" does. A sort is only demonstrated by two inputs that order differently.
    /// </remarks>
    [Fact]
    public void AnImageBelowAParagraphIsReadAfterIt()
    {
        Tree tree = Tag(AParagraphThenAnImage(), c => Alt);

        Assert.Equal(new[] { "P", "Figure" }, tree.RootRoles);
    }

    /// <summary>The same image drawn twice is two figures with two MCIDs.</summary>
    /// <remarks>
    /// <b>Asked once per occurrence, not once per resource.</b> A logo in a header and again in a
    /// footer may want different text, or one of them may want to be declined; deduplicating by
    /// resource name would take that choice away. Two distinct MCIDs is what makes them two things a
    /// reader can reach separately.
    /// </remarks>
    [Fact]
    public void TheSameImageDrawnTwiceIsTwoFigures()
    {
        var offered = new List<string>();
        byte[] pdf = TaggedBytes(OneImageDrawnTwice(), c => { offered.Add(c.XObjectName); return Alt; });
        var tree = new Tree(pdf);

        Assert.Equal(new[] { "Im0", "Im0" }, offered);
        Assert.Equal(2, tree.Count("Figure"));
        Assert.Equal(2, tree.McidsOfEvery("Figure").Distinct().Count());
    }

    /// <summary>An inferred figure is reported clean by this library's own image checks.</summary>
    /// <remarks>
    /// <b>The test the feature is measured by.</b> Every other assertion here says the tree has the
    /// shape this pass intended; this one says the shape is the one the standard wants, judged by
    /// the checker that would report it — UA07 for a missing <c>/Alt</c>, UA15 for an empty or
    /// placeholder one. Remediation that manufactures a finding is worse than remediation that does
    /// nothing, and only running the checker tells the two apart.
    /// </remarks>
    [Fact]
    public void AnInferredFigurePassesThisLibrarysOwnImageChecks()
    {
        byte[] pdf = TaggedBytes(OneImageAndAParagraph(), c => Alt);

        using var ms = new MemoryStream(pdf);
        AccessibilityAuditReport report = PdfAccessibility.Audit(ms);

        Assert.Empty(report.Issues.Where(
            i => string.Equals(i.Code, "UA07", StringComparison.Ordinal)
              || string.Equals(i.Code, "UA15", StringComparison.Ordinal)));
    }

    /// <summary>A callback that throws costs its own image and nothing else.</summary>
    /// <remarks>
    /// <b>A caller's callback may reach a network, a model or a database.</b> The page is mid-splice
    /// when it runs, so letting the exception out would abandon every paragraph on the page as well
    /// as the image — turning a failed description into lost text. It is reported rather than
    /// swallowed.
    /// </remarks>
    [Fact]
    public void AnAdversarialCallbackThatThrowsCostsOnlyItsOwnImage()
    {
        Tree tree = Tag(OneImageAndAParagraph(),
                        c => throw new InvalidOperationException("the vision service is down"));

        Assert.Equal(0, tree.Count("Figure"));
        Assert.Equal(1, tree.Count("P"));
    }

    /// <summary>An XObject with no readable /Subtype is not treated as an image.</summary>
    /// <remarks>
    /// The fail-safe direction: leaving content untagged is recoverable, describing a page of text
    /// as a picture is not. A missing <c>/Subtype</c> is <em>not</em> an image.
    /// </remarks>
    [Fact]
    public void AMalformedXObjectWithNoSubtypeIsNotOffered()
    {
        var offered = new List<string>();
        Tag(AnXObjectWithNoSubtype(), c => { offered.Add(c.XObjectName); return Alt; });

        Assert.Empty(offered);
    }

    /// <summary>The spliced bytes still draw the image.</summary>
    /// <remarks>
    /// <para><b>The defect this exists for is invisible to every other test in this file.</b> A
    /// token's byte range starts at its first <em>operand</em>, so a figure wraps
    /// <c>/Im0 Do</c>. Start it two bytes earlier instead — at the <c>D</c> of <c>Do</c> — and the
    /// splice produces <c>/Im0 /Figure &lt;&lt;/MCID 0&gt;&gt; BDC Do EMC</c>: the <c>BDC</c> eats
    /// its own two operands, <c>/Im0</c> is left stranded on the stack, and <c>Do</c> runs with no
    /// operand. The page no longer draws the picture — and the structure tree is <b>identical</b>,
    /// so a mutation doing exactly that survived the whole suite.</para>
    ///
    /// <para><b>Where a writer emits raw bytes, something has to read raw bytes.</b> This re-scans
    /// the output stream and asserts the <c>Do</c> still owns its name operand, and that the
    /// sequence opens before it and closes after it. Asserting the tree cannot reach any of that;
    /// the tree is a claim about content the content no longer contains.</para>
    /// </remarks>
    [Fact]
    public void TheSplicedStreamStillDrawsTheImageInsideTheSequence()
    {
        byte[] pdf = TaggedBytes(OneImageAndAParagraph(), c => Alt);
        IReadOnlyList<ContentToken> tokens = ScanPageContent(pdf);

        int open = IndexOfOperator(tokens, "BDC", "Figure");
        int draw = IndexOfOperator(tokens, "Do", null);
        int close = IndexOfOperator(tokens, "EMC", null);

        Assert.True(open >= 0, "no /Figure BDC was emitted");
        Assert.True(draw >= 0, "the Do operator did not survive the splice");
        Assert.True(close >= 0, "no EMC was emitted");

        // The picture is drawn between the two, which is what makes the MCID describe it.
        Assert.True(open < draw && draw < close,
            $"expected BDC({open}) < Do({draw}) < EMC({close})");

        // And Do still owns its operand: this is the half a structural assertion cannot see.
        ContentToken invoke = tokens[draw];
        PdfObject last = invoke.Operands != null && invoke.Operands.Count > 0
            ? invoke.Operands[invoke.Operands.Count - 1]
            : null;
        Assert.Equal("Im0", Assert.IsType<PdfName>(last).Value);
    }

    /// <summary>Only the names the caller called images are offered.</summary>
    /// <remarks>
    /// <b>Exercised at the tagger's own seam, because the facade cannot reach it.</b>
    /// <c>PdfAutoTagger</c> wraps the caller's callback in one that looks the name up in the page's
    /// resources and returns null for anything absent — so through the facade the resource read
    /// filters twice and deleting the tagger's own check changes nothing. The check is the
    /// <em>tagger's</em> contract: <c>ImageXObjectNames</c> says which names are images, and a
    /// callback willing to answer for any name must not turn a Form XObject into a picture.
    /// </remarks>
    [Fact]
    public void TheTaggerOffersOnlyTheNamesItWasToldAreImages()
    {
        var asked = new List<string>();

        var options = new PageTagOptions
        {
            PageHeight          = 792f,
            ImageXObjectNames   = new HashSet<string>(new[] { "Im0" }, StringComparer.Ordinal),
            FigureAlternateText = name => { asked.Add(name); return Alt; },
        };

        PageTagResult result = ContentStreamTagger.TagPage(
            Encoding.Latin1.GetBytes(AFormAndAnImage()), options);

        Assert.Null(result.SkipReason);
        Assert.Equal(new[] { "Im0" }, asked);
        Assert.Equal(1, result.Runs.Count(r => r.Role == "Figure"));
    }

    /// <summary>The XObject name is read from the end of the operand list.</summary>
    /// <remarks>
    /// <b>A content stream is postfix</b>, so <c>/Name Do</c> puts the name immediately before the
    /// operator. A malformed stream that left an extra operand on the stack — which is legal to
    /// scan and happens in the wild — would otherwise yield whichever stale value happened to be
    /// first, and the image would go untagged. Every ordinary fixture has exactly one operand, so
    /// front and end agree and the rule is untestable through them.
    /// </remarks>
    [Fact]
    public void TheImageNameIsReadFromTheEndOfAMalformedOperandStack()
    {
        var options = new PageTagOptions
        {
            PageHeight          = 792f,
            ImageXObjectNames   = new HashSet<string>(new[] { "Im0" }, StringComparer.Ordinal),
            FigureAlternateText = name => Alt,
        };

        // A stray number left on the stack before the name.
        PageTagResult result = ContentStreamTagger.TagPage(
            Encoding.Latin1.GetBytes(
                "q 200 0 0 100 72 600 cm 0 /Im0 Do Q\n" +
                "BT /F1 12 Tf 72 500 Td (Below.) Tj ET\n"),
            options);

        Assert.Null(result.SkipReason);
        Assert.Equal(1, result.Runs.Count(r => r.Role == "Figure"));
    }

    /// <summary>An image drawn inside a text object is never wrapped.</summary>
    /// <remarks>
    /// <b>A text object's bytes are already being cut into runs by offset</b>, and a second
    /// sequence overlapping them could not be guaranteed properly nested — ISO 32000-1 §8.4.1
    /// requires marked content to nest, and the splice inserts by offset without knowing what
    /// else claims the same range. Leaving the image untagged is the recoverable outcome; an
    /// improperly nested stream is not.
    /// </remarks>
    [Fact]
    public void AnImageDrawnInsideATextObjectIsNotWrapped()
    {
        var asked = new List<string>();

        var options = new PageTagOptions
        {
            PageHeight          = 792f,
            ImageXObjectNames   = new HashSet<string>(new[] { "Im0" }, StringComparer.Ordinal),
            FigureAlternateText = name => { asked.Add(name); return Alt; },
        };

        PageTagResult result = ContentStreamTagger.TagPage(
            Encoding.Latin1.GetBytes(
                "BT /F1 12 Tf 72 700 Td (Text around a picture.) Tj /Im0 Do ET\n"),
            options);

        Assert.Empty(asked);
        Assert.Equal(0, result.Runs.Count(r => r.Role == "Figure"));
    }

    /// <summary>A page whose resources are inherited still offers its images.</summary>
    /// <remarks>
    /// <b>The defect this closes shipped for one commit.</b> §7.7.3.4 makes <c>/Resources</c>
    /// inheritable, and the first version read <c>page.Dictionary[/Resources]</c> directly — so a
    /// page carrying no key of its own, with the dictionary on the <c>/Pages</c> node above it,
    /// resolved to nothing and every image on it went unoffered. <c>PdfPage.Resources</c> is the
    /// accessor that walks the inheritance, and every fixture in this file except this one has the
    /// key on the page, so nothing else can see the difference.
    /// </remarks>
    [Fact]
    public void APageWithInheritedResourcesStillOffersItsImages()
    {
        var offered = new List<string>();

        using var input = new MemoryStream(PageWithInheritedResources());
        using var output = new MemoryStream();
        AutoTagResult result = PdfAutoTagger.AutoTag(
            input, output,
            new AutoTagOptions
            {
                FigureAlternateText = c => { offered.Add(c.XObjectName); return Alt; },
            });

        Assert.True(result.ContentMarked);
        Assert.Equal(new[] { "Im0" }, offered);
        Assert.Equal(1, result.FiguresTagged);
    }

    /// <summary>An image drawn inside a Form XObject becomes a Figure.</summary>
    /// <remarks>
    /// <b>Resolved against the form's own <c>/Resources</c>, not the page's.</b> The bytes being
    /// tagged are the form's content stream, so its <c>/ImF Do</c> means whatever the form's
    /// dictionary says — and the page around it may have a completely different <c>/ImF</c>, or
    /// none. The fixture gives the form an image name the page does not have, so a seam wired to
    /// the page's resources offers nothing and this test fails.
    /// </remarks>
    [Fact]
    public void AnImageInsideAFormXObjectBecomesAFigure()
    {
        var offered = new List<string>();

        using var input = new MemoryStream(PageInvokingAFormWithAnImage());
        using var output = new MemoryStream();
        AutoTagResult result = PdfAutoTagger.AutoTag(
            input, output,
            new AutoTagOptions
            {
                FigureAlternateText = c => { offered.Add(c.XObjectName); return Alt; },
            });

        Assert.True(result.ContentMarked);
        Assert.Equal(new[] { "ImF" }, offered);

        var tree = new Tree(output.ToArray());
        Assert.Equal(1, tree.Count("Figure"));
        Assert.Equal(new[] { Alt }, tree.AltTextOfEvery("Figure"));
    }

    /// <summary>A form drawn on two pages reports no page rather than the wrong one.</summary>
    /// <remarks>
    /// <b>The form is tagged once and its bytes shared by every copy</b>, so there is no single
    /// page to name. Naming the first invoking page would hand a caller keying a cache on
    /// (page, name) the wrong page for all but one copy; <c>PageIndex == -1</c> says the value is
    /// unavailable instead of saying something false. The picture is still described and still
    /// reachable, which is the outcome that matters.
    /// <para>
    /// The <c>Figure</c> count is 2 — one per copy — because each copy is its own stream with its
    /// own MCID space, and the caller was asked once for the form rather than once per copy.
    /// </para>
    /// </remarks>
    [Fact]
    public void AFormSharedByTwoPagesReportsNoPageRatherThanTheWrongOne()
    {
        var seen = new List<FigureCandidate>();

        using var input = new MemoryStream(AFormSharedByTwoPages());
        using var output = new MemoryStream();
        AutoTagResult result = PdfAutoTagger.AutoTag(
            input, output,
            new AutoTagOptions
            {
                DuplicateSharedFormXObjects = true,
                FigureAlternateText = c => { seen.Add(c); return Alt; },
            });

        Assert.True(result.ContentMarked);

        FigureCandidate only = Assert.Single(seen);
        Assert.Equal("ImF", only.XObjectName);
        Assert.Equal(-1, only.PageIndex);
        Assert.Equal(new[] { 0, 1 }, only.PageIndexes);   // every page that draws it, which -1 leaves out

        var tree = new Tree(output.ToArray());
        Assert.Equal(2, tree.Count("Figure"));
        Assert.Equal(4, tree.Count("P"));
    }

    /// <summary>
    /// A shared form with no resources of its own is offered an image every drawing page binds to
    /// the same object.
    /// </summary>
    /// <remarks>
    /// §8.10.1 makes a form without <c>/Resources</c> resolve names against the page that draws it.
    /// Here both pages bind <c>/Im0</c> to one image object, so there is exactly one answer;
    /// refusing the form whole would leave this, the common shape, untagged.
    /// </remarks>
    [Fact]
    public void ASharedFormWithNoResources_IsOfferedAnImageEveryPageBindsAlike()
    {
        var seen = new List<FigureCandidate>();

        using var input = new MemoryStream(ASharedFormWithNoResources());
        using var output = new MemoryStream();
        AutoTagResult result = PdfAutoTagger.AutoTag(
            input, output,
            new AutoTagOptions
            {
                DuplicateSharedFormXObjects = true,
                FigureAlternateText = c => { seen.Add(c); return Alt; },
            });

        Assert.True(result.ContentMarked);
        FigureCandidate only = Assert.Single(seen);
        Assert.Equal("Im0", only.XObjectName);
        Assert.Equal(-1, only.PageIndex);
        Assert.Equal(new[] { 0, 1 }, only.PageIndexes);

        var tree = new Tree(output.ToArray());
        Assert.Equal(2, tree.Count("Figure"));
        Assert.Equal(4, tree.Count("P"));
    }

    /// <summary>
    /// A name two drawing pages bind to different images is refused and reported; a name they bind
    /// alike, in the same form, is still offered.
    /// </summary>
    /// <remarks>
    /// <b>A genuine ambiguity, not a reporting problem.</b> The form draws <c>/Im0</c> and
    /// <c>/Im1</c>; both pages bind <c>/Im0</c> to one object, and <c>/Im1</c> to a different image
    /// each, so a description of either page's <c>/Im1</c> would be false on the other. The
    /// paragraph count and the offered <c>/Im0</c> are what stop this passing for the wrong reason
    /// — a change refusing the whole form would otherwise look identical.
    /// </remarks>
    [Fact]
    public void ASharedFormWithNoResources_RefusesANameThePagesBindToDifferentImages()
    {
        var offered = new List<string>();
        var reported = new List<string>();
        int thread = System.Threading.Thread.CurrentThread.ManagedThreadId;
        void Sink(string d) { if (System.Threading.Thread.CurrentThread.ManagedThreadId == thread) reported.Add(d); }

        using var input = new MemoryStream(ASharedFormWhosePagesDisagree());
        using var output = new MemoryStream();
        AutoTagResult result;
        PdfAccessibility.DiagnosticReported += Sink;
        try
        {
            result = PdfAutoTagger.AutoTag(
                input, output,
                new AutoTagOptions
                {
                    DuplicateSharedFormXObjects = true,
                    FigureAlternateText = c => { offered.Add(c.XObjectName); return Alt; },
                });
        }
        finally { PdfAccessibility.DiagnosticReported -= Sink; }

        Assert.True(result.ContentMarked);
        Assert.Equal(new[] { "Im0" }, offered);
        Assert.Contains(reported, d => d.Contains("bind /Im1 to different images", StringComparison.Ordinal));

        var tree = new Tree(output.ToArray());
        Assert.Equal(2, tree.Count("Figure"));   // /Im0 in each copy; /Im1 in neither
        Assert.Equal(4, tree.Count("P"));
    }

    /// <summary>
    /// <b>Adversarial: three hundred pages inheriting one resource dictionary of three hundred
    /// images.</b> Each page read every image of it — pages × images resolves, 4 × 10⁸ from a few
    /// megabytes at twenty thousand of each; the dictionary is read once a run now, and each page is
    /// still asked about the image it draws, under its own index and with its size.
    /// </summary>
    [Fact]
    public void AdversarialPagesSharingManyImages_ReadTheImagesOnce()
    {
        const int pages = 300, images = 300;
        var bodies = new List<string>();
        var kids = new StringBuilder();
        var xobjects = new StringBuilder();
        int firstImage = 4, firstPage = 4 + images, content = 4 + images + pages;
        for (int p = 0; p < pages; p++) kids.Append(firstPage + p).Append(" 0 R ");
        for (int i = 0; i < images; i++) xobjects.Append("/Im").Append(i).Append(' ').Append(firstImage + i).Append(" 0 R ");
        bodies.Add("<< /Type /Catalog /Pages 2 0 R >>");
        bodies.Add($"<< /Type /Pages /Kids [{kids}] /Count {pages} /MediaBox [0 0 612 792] /Resources 3 0 R >>");
        bodies.Add($"<< /XObject << {xobjects}>> /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >>");
        for (int i = 0; i < images; i++)
            bodies.Add($"<< /Type /XObject /Subtype /Image /Width {i + 1} /Height 2 /ColorSpace /DeviceGray /BitsPerComponent 8 /Length 1 >>\nstream\na\nendstream");
        for (int p = 0; p < pages; p++) bodies.Add($"<< /Type /Page /Parent 2 0 R /Contents {content} 0 R >>");
        string text = "BT /F1 12 Tf 72 700 Td (Hello) Tj ET q 10 0 0 10 0 0 cm /Im7 Do Q";
        bodies.Add($"<< /Length {text.Length} >>\nstream\n{text}\nendstream");

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Count; i++) { offsets.Add(sb.Length); sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n"); }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.Append(o.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");

        var asked = new List<FigureCandidate>();
        using var input = new MemoryStream(Encoding.ASCII.GetBytes(sb.ToString()));
        using var output = new MemoryStream();
        AutoTagResult result = PdfAutoTagger.AutoTag(input, output,
            new AutoTagOptions { FigureAlternateText = c => { asked.Add(c); return Alt; } });

        Assert.Equal(1, result.ImageCatalogsRead);
        Assert.Equal(pages, result.FiguresTagged);
        Assert.Equal(Enumerable.Range(0, pages), asked.Select(c => c.PageIndex));
        Assert.All(asked, c => { Assert.Equal("Im7", c.XObjectName); Assert.Equal(8, c.PixelWidth); Assert.Equal(2, c.PixelHeight); });
    }

    /// <summary>
    /// The agreement rule itself: the same reference, or the one inherited entry, agrees; a name
    /// missing from a page, or bound to another object, does not.
    /// </summary>
    [Fact]
    public void ANameAgreesOnlyWhenEveryPageBindsTheOneObject()
    {
        using var doc = Tetitulz.Pdf.Core.Document.PdfDocument.Open(new MemoryStream(ASharedFormWhosePagesDisagree()));
        var compared = new PdfAutoTagger.ImageCatalogs();
        var agreed = PdfAutoTagger.ImagesEveryPageAgreesOn(doc, doc.Pages, new List<int> { 0, 1 }, compared);
        Assert.Equal(new[] { "Im0" }, agreed.Names);
        Assert.Equal(2, compared.EntriesCompared);   // two dictionaries: each name looked up

        // Pages reaching one /XObject dictionary agree on every name without a lookup.
        var same = new PdfAutoTagger.ImageCatalogs();
        Assert.Equal(new[] { "Im0", "Im1" },
            PdfAutoTagger.ImagesEveryPageAgreesOn(doc, doc.Pages, new List<int> { 0, 0 }, same).Names.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(0, same.EntriesCompared);

        var onePage = PdfAutoTagger.ImagesEveryPageAgreesOn(doc, doc.Pages, new List<int> { 1 }, new PdfAutoTagger.ImageCatalogs());
        Assert.Equal(new[] { "Im0", "Im1" }, onePage.Names.OrderBy(k => k, StringComparer.Ordinal));

        // A page that has no /Im1 at all leaves the name without an answer too.
        using var lacking = Tetitulz.Pdf.Core.Document.PdfDocument.Open(new MemoryStream(ASharedFormWhosePagesDisagree(secondPageLacksIm1: true)));
        var withoutIm1 = PdfAutoTagger.ImagesEveryPageAgreesOn(lacking, lacking.Pages, new List<int> { 0, 1 }, new PdfAutoTagger.ImageCatalogs());
        Assert.Equal(new[] { "Im0" }, withoutIm1.Names);
    }

    /// <summary>
    /// A dictionary other than the first page's is compared once, however many drawing pages reach
    /// it: its agreement with the first page cannot change, and pages alternating between two
    /// dictionaries would otherwise cost the pages × the names.
    /// </summary>
    [Fact]
    public void AdversarialPagesSharingASecondDictionary_CompareItOnce()
    {
        using var doc = Tetitulz.Pdf.Core.Document.PdfDocument.Open(new MemoryStream(ASharedFormWhosePagesDisagree()));
        var drawnOn = new List<int> { 0 };
        for (int i = 0; i < 1000; i++) drawnOn.Add(1);

        var compared = new PdfAutoTagger.ImageCatalogs();
        var agreed = PdfAutoTagger.ImagesEveryPageAgreesOn(doc, doc.Pages, drawnOn, compared);

        Assert.Equal(new[] { "Im0" }, agreed.Names);   // the answer two pages give
        Assert.Equal(2, compared.EntriesCompared);     // the second dictionary's two names, once
    }

    /// <summary>A form with no resources of its own resolves against the page's.</summary>
    /// <remarks>
    /// <b>§8.10.1 makes a Form XObject's <c>/Resources</c> optional</b>, and a form that omits it
    /// resolves names against the resource dictionary in effect where it is drawn. That is not a
    /// guess or a lenience — it is what a conformant consumer does, and producers that emit one
    /// resource dictionary per page rely on it. Every other form fixture here carries its own
    /// dictionary, so the fallback is invisible to them: deleting it left the whole suite green.
    /// </remarks>
    [Fact]
    public void AFormWithNoResourcesOfItsOwnUsesThePages()
    {
        var offered = new List<string>();

        using var input = new MemoryStream(AFormWithNoResourcesOfItsOwn());
        using var output = new MemoryStream();
        AutoTagResult result = PdfAutoTagger.AutoTag(
            input, output,
            new AutoTagOptions
            {
                FigureAlternateText = c => { offered.Add(c.XObjectName); return Alt; },
            });

        Assert.True(result.ContentMarked);

        // The page never draws /Im0 itself — only the form does — so this is the fallback or
        // nothing.
        Assert.Equal(new[] { "Im0" }, offered);
        Assert.Equal(1, new Tree(output.ToArray()).Count("Figure"));
    }

    // ================================================================
    //  Fixtures
    // ================================================================

    private static string OneImageAndAParagraph() =>
        "q 200 0 0 100 72 600 cm /Im0 Do Q\n" +
        "BT /F1 12 Tf 72 500 Td (A paragraph below the picture.) Tj ET\n";

    /// <summary>A 4 × 2 grey inline image, its samples eight ASCII bytes, drawn at <paramref name="y"/>.</summary>
    private static string InlineImage(int y) =>
        "q 200 0 0 100 72 " + y.ToString(CultureInfo.InvariantCulture) + " cm BI /W 4 /H 2 /CS /G /BPC 8 ID ABCDEFGH EI Q\n";

    private static string AnInlineImageAndAParagraph() =>
        InlineImage(600) +
        "BT /F1 12 Tf 72 500 Td (A paragraph below the picture.) Tj ET\n";

    private static string AParagraphThenAnImage() =>
        "BT /F1 12 Tf 72 700 Td (A paragraph above the picture.) Tj ET\n" +
        "q 200 0 0 100 72 400 cm /Im0 Do Q\n";

    private static string OneImageDrawnTwice() =>
        "q 200 0 0 100 72 600 cm /Im0 Do Q\n" +
        "BT /F1 12 Tf 72 500 Td (Between the two.) Tj ET\n" +
        "q 200 0 0 100 72 300 cm /Im0 Do Q\n";

    private static string TwoImages() =>
        "q 200 0 0 100 72 600 cm /Im0 Do Q\n" +
        "q 200 0 0 100 72 400 cm /Im1 Do Q\n" +
        "BT /F1 12 Tf 72 300 Td (Two pictures above.) Tj ET\n";

    private static string AFormAndAnImage() =>
        "q 1 0 0 1 72 600 cm /Fm0 Do Q\n" +
        "q 200 0 0 100 72 400 cm /Im0 Do Q\n" +
        "BT /F1 12 Tf 72 300 Td (A form and a picture above.) Tj ET\n";

    private static string AnXObjectWithNoSubtype() =>
        "q 200 0 0 100 72 600 cm /Ix0 Do Q\n" +
        "BT /F1 12 Tf 72 500 Td (An unreadable XObject above.) Tj ET\n";

    /// <summary>
    /// One page that declares no <c>/Resources</c> of its own — the dictionary sits on the
    /// <c>/Pages</c> node, which §7.7.3.4 makes it inherit.
    /// </summary>
    private static byte[] PageWithInheritedResources()
    {
        string content = "q 200 0 0 100 72 600 cm /Im0 Do Q\n" +
                         "BT /F1 12 Tf 72 500 Td (Below the picture.) Tj ET\n";

        return Build(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",

            // The resources live HERE, not on the page.
            "<< /Type /Pages /Kids [3 0 R] /Count 1"
            + " /Resources << /Font << /F1 5 0 R >> /XObject << /Im0 6 0 R >> >> >>",

            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R >>",
            Stream(content),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            ImageObject(),
        });
    }

    /// <summary>
    /// A page invoking a Form XObject that draws its own image, under a name the page's resources
    /// do not have.
    /// </summary>
    /// <remarks>
    /// <para>The form carries text as well as the image, because a form with no text is skipped
    /// before tagging begins — a fixture without it would exercise nothing whatever the seam
    /// did.</para>
    ///
    /// <para><b>And so does the page.</b> A page whose content is only <c>/Fm0 Do</c> has no
    /// text-showing operator of its own, so the tagger skips it and reports as much; the first
    /// draft of this fixture did exactly that and failed on <c>ContentMarked</c> before reaching
    /// anything under test.</para>
    /// </remarks>
    private static byte[] PageInvokingAFormWithAnImage()
    {
        string pageContent = "BT /F1 12 Tf 72 700 Td (On the page itself.) Tj ET\n"
                           + "q 1 0 0 1 72 600 cm /Fm0 Do Q\n";

        return Build(new[]
        {
            /* 1 */ "<< /Type /Catalog /Pages 2 0 R >>",
            /* 2 */ "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            /* 3 */ "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R"
                    + " /Resources << /Font << /F1 5 0 R >> /XObject << /Fm0 6 0 R >> >> >>",
            /* 4 */ Stream(pageContent),
            /* 5 */ "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            /* 6 */ FormObject(imageObjectNumber: 7),
            /* 7 */ ImageObject(),
        });
    }

    /// <summary>
    /// A page invoking a Form XObject that declares no <c>/Resources</c> and draws an image the
    /// PAGE's resources name.
    /// </summary>
    /// <remarks>
    /// The page's own content stream never draws <c>/Im0</c>, so the only way for it to be offered
    /// is through the form's content resolving against the page's dictionary.
    /// </remarks>
    private static byte[] AFormWithNoResourcesOfItsOwn()
    {
        string pageContent = "BT /F1 12 Tf 72 700 Td (On the page itself.) Tj ET\n"
                           + "q 1 0 0 1 72 600 cm /Fm0 Do Q\n";
        string formContent = "BT /F1 10 Tf 0 30 Td (Text inside the form.) Tj ET\n"
                           + "q 50 0 0 25 0 0 cm /Im0 Do Q\n";

        return Build(new[]
        {
            /* 1 */ "<< /Type /Catalog /Pages 2 0 R >>",
            /* 2 */ "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            /* 3 */ "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R"
                    + " /Resources << /Font << /F1 5 0 R >>"
                    + " /XObject << /Fm0 6 0 R /Im0 7 0 R >> >> >>",
            /* 4 */ Stream(pageContent),
            /* 5 */ "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",

            // No /Resources at all.
            /* 6 */ "<< /Type /XObject /Subtype /Form /BBox [0 0 200 60] /Length "
                    + formContent.Length.ToString(CultureInfo.InvariantCulture)
                    + " >>\nstream\n" + formContent + "\nendstream",

            /* 7 */ ImageObject(),
        });
    }

    /// <summary>
    /// The same form drawn from two pages, declaring no <c>/Resources</c> of its own — so the image
    /// name it draws would resolve differently depending on which page is asked.
    /// </summary>
    private static byte[] ASharedFormWithNoResources()
    {
        string formContent = "BT /F1 10 Tf 0 30 Td (Text inside the form.) Tj ET\n"
                           + "q 50 0 0 25 0 0 cm /Im0 Do Q\n";

        string PageObject(int contents) =>
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents "
            + contents.ToString(CultureInfo.InvariantCulture) + " 0 R"
            + " /Resources << /Font << /F1 5 0 R >>"
            + " /XObject << /Fm0 6 0 R /Im0 7 0 R >> >> >>";

        string Content(string which) =>
            "BT /F1 12 Tf 72 700 Td (" + which + ") Tj ET\n"
            + "q 1 0 0 1 72 600 cm /Fm0 Do Q\n";

        return Build(new[]
        {
            /* 1 */ "<< /Type /Catalog /Pages 2 0 R >>",
            /* 2 */ "<< /Type /Pages /Kids [3 0 R 8 0 R] /Count 2 >>",
            /* 3 */ PageObject(4),
            /* 4 */ Stream(Content("The first page.")),
            /* 5 */ "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            /* 6 */ "<< /Type /XObject /Subtype /Form /BBox [0 0 200 60] /Length "
                    + formContent.Length.ToString(CultureInfo.InvariantCulture)
                    + " >>\nstream\n" + formContent + "\nendstream",
            /* 7 */ ImageObject(),
            /* 8 */ PageObject(9),
            /* 9 */ Stream(Content("The second page.")),
        });
    }

    /// <summary>
    /// A form with no <c>/Resources</c> drawing <c>/Im0</c> and <c>/Im1</c> from two pages that bind
    /// <c>/Im0</c> to one image (7) and <c>/Im1</c> to a different image each (10 and 11).
    /// </summary>
    private static byte[] ASharedFormWhosePagesDisagree(bool secondPageLacksIm1 = false)
    {
        string formContent = "BT /F1 10 Tf 0 30 Td (Text inside the form.) Tj ET\n"
                           + "q 50 0 0 25 0 0 cm /Im0 Do Q\n"
                           + "q 50 0 0 25 60 0 cm /Im1 Do Q\n";

        string PageObject(int contents, int im1) =>
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents "
            + contents.ToString(CultureInfo.InvariantCulture) + " 0 R"
            + " /Resources << /Font << /F1 5 0 R >>"
            + " /XObject << /Fm0 6 0 R /Im0 7 0 R"
            + (im1 > 0 ? " /Im1 " + im1.ToString(CultureInfo.InvariantCulture) + " 0 R" : "") + " >> >> >>";

        string Content(string which) =>
            "BT /F1 12 Tf 72 700 Td (" + which + ") Tj ET\n"
            + "q 1 0 0 1 72 600 cm /Fm0 Do Q\n";

        return Build(new[]
        {
            /* 1 */ "<< /Type /Catalog /Pages 2 0 R >>",
            /* 2 */ "<< /Type /Pages /Kids [3 0 R 8 0 R] /Count 2 >>",
            /* 3 */ PageObject(4, im1: 10),
            /* 4 */ Stream(Content("The first page.")),
            /* 5 */ "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            /* 6 */ "<< /Type /XObject /Subtype /Form /BBox [0 0 200 60] /Length "
                    + formContent.Length.ToString(CultureInfo.InvariantCulture)
                    + " >>\nstream\n" + formContent + "\nendstream",
            /* 7 */ ImageObject(),
            /* 8 */ PageObject(9, im1: secondPageLacksIm1 ? 0 : 11),
            /* 9 */ Stream(Content("The second page.")),
            /* 10 */ ImageObject(),
            /* 11 */ ImageObject(),
        });
    }

    /// <summary>The same form, drawn from two pages that each have their own content.</summary>
    private static byte[] AFormSharedByTwoPages()
    {
        string first  = "BT /F1 12 Tf 72 700 Td (The first page.) Tj ET\n"
                      + "q 1 0 0 1 72 600 cm /Fm0 Do Q\n";
        string second = "BT /F1 12 Tf 72 700 Td (The second page.) Tj ET\n"
                      + "q 1 0 0 1 72 600 cm /Fm0 Do Q\n";

        string PageObject(int contents) =>
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents "
            + contents.ToString(CultureInfo.InvariantCulture) + " 0 R"
            + " /Resources << /Font << /F1 5 0 R >> /XObject << /Fm0 6 0 R >> >> >>";

        return Build(new[]
        {
            /* 1 */ "<< /Type /Catalog /Pages 2 0 R >>",
            /* 2 */ "<< /Type /Pages /Kids [3 0 R 8 0 R] /Count 2 >>",
            /* 3 */ PageObject(4),
            /* 4 */ Stream(first),
            /* 5 */ "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            /* 6 */ FormObject(imageObjectNumber: 7),
            /* 7 */ ImageObject(),
            /* 8 */ PageObject(9),
            /* 9 */ Stream(second),
        });
    }

    /// <summary>
    /// A Form XObject with its own resources: a font, and an image under a name no page uses.
    /// </summary>
    private static string FormObject(int imageObjectNumber)
    {
        string content = "BT /F1 10 Tf 0 30 Td (Text inside the form.) Tj ET\n"
                       + "q 50 0 0 25 0 0 cm /ImF Do Q\n";

        return "<< /Type /XObject /Subtype /Form /BBox [0 0 200 60]"
             + " /Resources << /Font << /F1 5 0 R >> /XObject << /ImF "
             + imageObjectNumber.ToString(CultureInfo.InvariantCulture) + " 0 R >> >>"
             + " /Length " + content.Length.ToString(CultureInfo.InvariantCulture)
             + " >>\nstream\n" + content + "\nendstream";
    }

    /// <summary>An 8x4 DeviceGray image object with real samples.</summary>
    private static string ImageObject()
    {
        byte[] samples = new byte[8 * 4];
        for (int i = 0; i < samples.Length; i++) samples[i] = (byte)(i * 7);

        return "<< /Type /XObject /Subtype /Image /Width 8 /Height 4 /ColorSpace /DeviceGray"
             + " /BitsPerComponent 8 /Length " + samples.Length.ToString(CultureInfo.InvariantCulture)
             + " >>\nstream\n" + Encoding.Latin1.GetString(samples) + "\nendstream";
    }

    private static string Stream(string content) =>
        "<< /Length " + content.Length.ToString(CultureInfo.InvariantCulture)
        + " >>\nstream\n" + content + "\nendstream";

    /// <summary>Assembles numbered objects into a cross-referenced file.</summary>
    private static byte[] Build(string[] objects)
    {
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();

        foreach (string body in objects)
        {
            offsets.Add(sb.Length);
            sb.Append(offsets.Count).Append(" 0 obj\n").Append(body).Append("\nendobj\n");
        }

        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(offsets.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int offset in offsets)
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", offset);

        sb.Append("trailer\n<< /Size ").Append(offsets.Count + 1)
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");

        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    /// <summary>The tokens of the tagged document's first page content stream.</summary>
    private static IReadOnlyList<ContentToken> ScanPageContent(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        using PdfDocument doc = PdfDocument.Open(ms);

        PdfPage page = doc.Pages[0];
        byte[] content = ContentStreamTagger.ConcatenatePageContent(doc, page.Dictionary, 1 << 20);
        Assert.NotNull(content);

        return ContentStreamScanner.ScanDetailed(content).Tokens;
    }

    /// <summary>
    /// Index of the first token with this operator, optionally requiring its first operand to be a
    /// name with the given value. -1 when there is none.
    /// </summary>
    private static int IndexOfOperator(
        IReadOnlyList<ContentToken> tokens, string op, string firstOperandName)
    {
        for (int i = 0; i < tokens.Count; i++)
        {
            if (!string.Equals(tokens[i].Operator, op, StringComparison.Ordinal)) continue;
            if (firstOperandName == null) return i;

            if (tokens[i].Operands != null && tokens[i].Operands.Count > 0
                && tokens[i].Operands[0] is PdfName name
                && string.Equals(name.Value, firstOperandName, StringComparison.Ordinal))
                return i;
        }
        return -1;
    }

    private static Tree Tag(string content, Func<FigureCandidate, string> callback)
        => new Tree(TaggedBytes(content, callback));

    private static AutoTagResult Result(string content, Func<FigureCandidate, string> callback)
    {
        using var input = new MemoryStream(Page(content));
        using var output = new MemoryStream();
        return PdfAutoTagger.AutoTag(
            input, output, new AutoTagOptions { FigureAlternateText = callback });
    }

    private static byte[] TaggedBytes(string content, Func<FigureCandidate, string> callback)
    {
        using var input = new MemoryStream(Page(content));
        using var output = new MemoryStream();

        AutoTagResult result = PdfAutoTagger.AutoTag(
            input, output, new AutoTagOptions { FigureAlternateText = callback });

        Assert.True(result.ContentMarked,
            "the tagger marked nothing, so the tree under test was never built"
            + (result.SkippedPageReasons.Count > 0
                   ? ": " + result.SkippedPageReasons[0]
                   : string.Empty));

        return output.ToArray();
    }

    /// <summary>
    /// One page carrying the given content stream, with three XObject resources: an 8×4 image
    /// (<c>/Im0</c>), a second image (<c>/Im1</c>), a Form XObject (<c>/Fm0</c>) and an XObject with
    /// no <c>/Subtype</c> (<c>/Ix0</c>).
    /// </summary>
    /// <remarks>
    /// All four are declared on every page, so the fixtures differ only in what the content stream
    /// draws — which is the variable under test. The image samples are real (an 8×4 DeviceGray
    /// bitmap), so the page is one a renderer would accept rather than a dictionary shaped like one.
    /// </remarks>
    private static byte[] Page(string content)
    {
        byte[] bytes = Encoding.Latin1.GetBytes(content);
        byte[] samples = new byte[8 * 4];
        for (int i = 0; i < samples.Length; i++) samples[i] = (byte)(i * 7);

        const string FormContent = "BT /F1 9 Tf 0 0 Td (inside the form) Tj ET\n";

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();

        void Obj(string body)
        {
            offsets.Add(sb.Length);
            sb.Append(offsets.Count).Append(" 0 obj\n").Append(body).Append("\nendobj\n");
        }

        string Image() =>
            "<< /Type /XObject /Subtype /Image /Width 8 /Height 4 /ColorSpace /DeviceGray"
            + " /BitsPerComponent 8 /Length " + samples.Length.ToString(CultureInfo.InvariantCulture)
            + " >>\nstream\n" + Encoding.Latin1.GetString(samples) + "\nendstream";

        Obj("<< /Type /Catalog /Pages 2 0 R >>");
        Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R"
            + " /Resources << /Font << /F1 5 0 R >>"
            + " /XObject << /Im0 6 0 R /Im1 7 0 R /Fm0 8 0 R /Ix0 9 0 R >> >> >>");
        Obj("<< /Length " + bytes.Length.ToString(CultureInfo.InvariantCulture)
            + " >>\nstream\n" + Encoding.Latin1.GetString(bytes) + "\nendstream");
        Obj("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        Obj(Image());
        Obj(Image());
        Obj("<< /Type /XObject /Subtype /Form /BBox [0 0 100 50]"
            + " /Resources << /Font << /F1 5 0 R >> >>"
            + " /Length " + FormContent.Length.ToString(CultureInfo.InvariantCulture)
            + " >>\nstream\n" + FormContent + "\nendstream");

        // No /Subtype: the fail-safe case. Everything else about it looks like an image.
        Obj("<< /Type /XObject /Width 8 /Height 4 /ColorSpace /DeviceGray /BitsPerComponent 8"
            + " /Length " + samples.Length.ToString(CultureInfo.InvariantCulture)
            + " >>\nstream\n" + Encoding.Latin1.GetString(samples) + "\nendstream");

        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(offsets.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int offset in offsets)
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", offset);

        sb.Append("trailer\n<< /Size ").Append(offsets.Count + 1)
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");

        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    /// <summary>The structure tree of a tagged document, read back.</summary>
    private sealed class Tree
    {
        private readonly PdfDocument _doc;
        private readonly List<string> _roles = new List<string>();
        private readonly List<(string Role, PdfDictionary Dict)> _elements =
            new List<(string, PdfDictionary)>();

        internal List<string> RootRoles { get; } = new List<string>();

        internal Tree(byte[] pdf)
        {
            var ms = new MemoryStream(pdf);
            _doc = PdfDocument.Open(ms);

            var root = _doc.ResolveObject(_doc.Catalog.Dictionary.Get(PdfName.StructTreeRoot))
                       as PdfDictionary;
            if (root == null) return;

            void Visit(PdfObject node, int depth)
            {
                if (depth > 40) return;
                if (_doc.ResolveObject(node) is not PdfDictionary dict) return;

                string role = RoleOf(node);
                if (role != null) { _roles.Add(role); _elements.Add((role, dict)); }

                foreach (PdfObject kid in KidsOf(dict)) Visit(kid, depth + 1);
            }

            foreach (PdfObject documentElement in KidsOf(root))
            {
                if (_doc.ResolveObject(documentElement) is not PdfDictionary document) continue;

                foreach (PdfObject kid in KidsOf(document))
                {
                    string role = RoleOf(kid);
                    if (role != null) RootRoles.Add(role);
                    Visit(kid, 0);
                }
            }
        }

        private string RoleOf(PdfObject node)
            => _doc.ResolveObject(node) is PdfDictionary d
               && _doc.ResolveObject(d.Get(PdfName.S)) is PdfName s ? s.Value : null;

        private List<PdfObject> KidsOf(PdfDictionary dict)
        {
            PdfObject k = _doc.ResolveObject(dict.Get(PdfName.K));
            if (k is PdfArray array) return array.ToList();
            return k == null ? new List<PdfObject>() : new List<PdfObject> { k };
        }

        internal int Count(string role) => _roles.Count(r => r == role);

        internal List<string> AltTextOfEvery(string role)
        {
            var found = new List<string>();
            foreach ((string r, PdfDictionary dict) in _elements)
            {
                if (r != role) continue;
                if (_doc.ResolveObject(dict.Get(PdfName.Alt)) is PdfString alt) found.Add(alt.GetText());
            }
            return found;
        }

        internal List<int> McidsOfEvery(string role)
        {
            var mcids = new List<int>();
            foreach ((string r, PdfDictionary dict) in _elements)
            {
                if (r != role) continue;
                foreach (PdfObject kid in KidsOf(dict))
                    if (_doc.ResolveObject(kid) is PdfNumber n) mcids.Add((int)n.DoubleValue);
            }
            return mcids;
        }
    }
}
