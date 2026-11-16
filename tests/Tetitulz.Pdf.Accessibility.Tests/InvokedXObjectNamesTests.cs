// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Tetitulz.Pdf.Accessibility.Internal;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// <see cref="ContentScanResult.InvokedXObjectNames"/> — the distinct XObjects a content stream
/// invokes with <c>Do</c>.
/// </summary>
/// <remarks>
/// <para>The method is the merge of two character-identical private copies,
/// <c>ContentStreamTagger.CollectInvokedXObjectNames</c> and
/// <c>FormXObjectInventory.InvokedNames</c>. Mutating the survivor to return nothing reddens 24
/// existing tests, so it is thoroughly reached — but mutating it to read
/// <c>Operands[0]</c> instead of <c>Operands[Count - 1]</c> reddened <b>nothing</b>, because every
/// fixture in the suite invokes <c>Do</c> with exactly one operand and the two indices coincide.
/// That is the accidentally-equivalent case rather than a coverage gap, and this file closes it:
/// the end-read is a real decision about malformed input and now has a test that can see it.</para>
/// </remarks>
public class InvokedXObjectNamesTests
{
    private static ContentScanResult Scan(string content) =>
        ContentStreamScanner.ScanDetailed(Encoding.ASCII.GetBytes(content));

    [Fact]
    public void TheInvokedNamesAreReturnedInFirstUseOrder()
    {
        var names = Scan("/Fm0 Do /Fm1 Do /Fm2 Do").InvokedXObjectNames();
        Assert.Equal(new[] { "Fm0", "Fm1", "Fm2" }, names);
    }

    /// <summary>A name invoked twice is listed once — the callers key resource lookups off it.</summary>
    [Fact]
    public void ARepeatedInvocationIsListedOnce()
    {
        var names = Scan("/Fm0 Do /Fm1 Do /Fm0 Do").InvokedXObjectNames();
        Assert.Equal(new[] { "Fm0", "Fm1" }, names);
    }

    /// <summary>
    /// <b>The operand is read from the end of the stack, not the start.</b> A content stream is
    /// postfix, so <c>Do</c> takes the name immediately before it; anything else left on the stack
    /// by a malformed producer precedes that name and is not the operand.
    /// </summary>
    /// <remarks>
    /// This is the only case that distinguishes the two readings, which is why it exists. Reading
    /// <c>Operands[0]</c> here yields <c>"Junk"</c> — a name no resource dictionary contains — so a
    /// tagger would look up the wrong XObject and an inventory would report one that is not drawn,
    /// while every single-operand fixture in the suite carried on passing.
    /// </remarks>
    [Fact]
    public void AStrayOperandBeforeTheNameDoesNotDisplaceIt()
    {
        var names = Scan("/Junk /Fm0 Do").InvokedXObjectNames();
        Assert.Equal(new[] { "Fm0" }, names);
    }

    /// <summary>A <c>Do</c> with no operand at all is skipped rather than throwing.</summary>
    [Fact]
    public void AnOperandlessDoIsIgnored()
    {
        var names = Scan("Do /Fm0 Do").InvokedXObjectNames();
        Assert.Equal(new[] { "Fm0" }, names);
    }

    /// <summary>
    /// The control: content that invokes nothing yields nothing. Without it, a method that always
    /// returned an empty list would pass the operandless and stray-operand cases above.
    /// </summary>
    [Fact]
    public void ContentThatInvokesNoXObjectYieldsNoNames()
    {
        Assert.Empty(Scan("BT /F1 12 Tf (hi) Tj ET").InvokedXObjectNames());
    }
}
