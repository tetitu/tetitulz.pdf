// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Forms;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Plugins.Conformance;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Conformance.Tests;

/// <summary>
/// §12.7.3.1's terminal-field question, asked of the two implementations that answer it:
/// <c>Core.Forms.PdfFormField.IsTerminal</c> and <c>Conformance.AcroFormFieldWalk</c>.
/// </summary>
/// <remarks>
/// <para>The spec gives <b>one</b> definition — a field whose children are fields is non-terminal,
/// and a kid that is a widget annotation is part of its parent's presentation rather than a field
/// in its own right — so two implementations of it may not disagree. CLAUDE.md already records the
/// checker and the fixer having drifted over this exact question once; sharing
/// <c>AcroFormFieldWalk</c> is what stopped <i>those two</i> drifting again, and it does nothing
/// about Core, which decides the same thing for form filling.</para>
///
/// <para><b>They discriminate a widget kid from a field kid by different tests.</b> Core asks for a
/// non-empty <c>/T</c>. The walk asks for <c>/FT</c> <i>or</i> <c>/T</c> <i>or</i> <c>/Kids</c>. The
/// cases below are the inputs that separates those two rules, and each is a shape a real generator
/// emits.</para>
/// </remarks>
public class AcroFormTerminalParityTests
{
    private static PdfString Str(string s) => new PdfString(Encoding.UTF8.GetBytes(s));

    private static PdfArray Array(params PdfObject[] items)
    {
        var a = new PdfArray();
        foreach (var i in items) a.Add(i);
        return a;
    }

    private static PdfDictionary CatalogWithFields(params PdfObject[] fields)
    {
        var acroForm = new PdfDictionary();
        acroForm[PdfName.Fields] = Array(fields);

        var catalog = new PdfDictionary();
        catalog[PdfName.AcroForm] = acroForm;
        return catalog;
    }

    private static AcroFormResolver Direct => obj => obj;

    /// <summary>A named group whose single kid is built by the caller.</summary>
    private static PdfDictionary GroupWithKid(PdfDictionary kid)
    {
        var group = new PdfDictionary();
        group[PdfName.T] = Str("grp");
        group[PdfName.Kids] = Array(kid);
        return group;
    }

    // ==================================================================

    /// <summary>
    /// <b>A bare widget kid.</b> The uncontroversial case, and the control: a radio group's kid
    /// carries an appearance and nothing else, so the group is terminal in both. Without this the
    /// theory below could pass with either implementation stuck at a constant.
    /// </summary>
    [Fact]
    public void BareWidgetKid_LeavesTheGroupTerminal_InBoth()
    {
        var kid = new PdfDictionary();
        kid[PdfName.Subtype] = PdfName.Widget;

        Assert.True(CoreSaysTerminal(GroupWithKid(kid)));
        Assert.True(WalkSaysTerminal(GroupWithKid(kid)));
    }

    /// <summary>
    /// <b>A named kid.</b> The other control: a kid with its own partial name is a field by every
    /// reading of §12.7.3.1, so the group is non-terminal in both.
    /// </summary>
    [Fact]
    public void NamedKid_MakesTheGroupNonTerminal_InBoth()
    {
        var kid = new PdfDictionary();
        kid[PdfName.T] = Str("kid");

        Assert.False(CoreSaysTerminal(GroupWithKid(kid)));
        Assert.False(WalkSaysTerminal(GroupWithKid(kid)));
    }

    /// <summary>
    /// <b>The three shapes that separated the two implementations</b>, each asserted against the
    /// answer §12.7.3.1 requires — not merely against each other.
    /// </summary>
    /// <remarks>
    /// Asserting agreement alone would become a tautology the moment both call one predicate, which
    /// is the whole point of the merge; it would then pass just as happily if the shared rule were
    /// wrong. The expected column is what keeps it falsifiable, and it is not one side's answer:
    /// <b>Core was right about the first two and the walk about the third.</b>
    /// </remarks>
    [Theory]
    // A widget that also carries a field type. Producers that merge a single widget into its field
    // and then wrap the result in a group emit this. /FT is inheritable (Table 220), so carrying one
    // says nothing about whether this node is a field: the kid is a widget, the group is terminal.
    // The walk used to call it a field, so the group escaped UA08 and the fixer wrote the /TU onto
    // the widget, where — /TU not being inheritable — it names nothing a user reaches.
    [InlineData("ft", true)]
    // /T present but empty, what a generator that writes every key unconditionally emits. An empty
    // partial name contributes nothing to a fully qualified name (§12.7.3.2), so it is not a field.
    [InlineData("emptyT", true)]
    // Kids of its own, no name, no type. A widget annotation has no children at all, so this is a
    // field node whatever else it omits — the one shape Core got wrong, and it mattered:
    // WidgetDictionaries handed the node back as a dictionary to draw an appearance into, skipping
    // the real widgets beneath it.
    [InlineData("kids", false)]
    public void CoreAndTheWalk_AgreeOnWhetherTheGroupIsTerminal(string shape, bool expectedTerminal)
    {
        PdfDictionary Kid()
        {
            var k = new PdfDictionary();
            k[PdfName.Subtype] = PdfName.Widget;
            switch (shape)
            {
                case "ft": k[PdfName.FT] = new PdfName(PdfFieldTypes.Text); break;
                case "emptyT": k[PdfName.T] = Str(""); break;
                case "kids": k[PdfName.Kids] = Array(new PdfDictionary()); break;
            }
            return k;
        }

        // Both in one assertion, as a tuple, so that a mutation of the shared predicate reports
        // BOTH sides having moved rather than stopping at whichever is written first. That is the
        // difference between evidence the merge is live on two paths and evidence it is live on one.
        Assert.Equal(
            (Core: expectedTerminal, Walk: expectedTerminal),
            (Core: CoreSaysTerminal(GroupWithKid(Kid())), Walk: WalkSaysTerminal(GroupWithKid(Kid()))));
    }

    /// <summary>
    /// <b>The consequence of the <c>/FT</c> shape, at the level a user feels it.</b> With the group
    /// terminal, Core reads the merged widget's <c>/FT</c> through
    /// <c>PdfFormField.ResolveFieldType</c>'s downward step and types the group as a text field, so
    /// a tooltip written onto the group names something. That is what makes "terminal" the useful
    /// answer here rather than merely the permissive one, and it is the half no boolean above can
    /// express.
    /// </summary>
    [Fact]
    public void MergedWidgetsFieldType_ReachesTheGroup()
    {
        var kid = new PdfDictionary();
        kid[PdfName.Subtype] = PdfName.Widget;
        kid[PdfName.FT] = new PdfName(PdfFieldTypes.Text);

        var group = new PdfFormField(GroupWithKid(kid), null, null);

        Assert.True(group.IsTerminal);
        Assert.Equal(PdfFieldType.Text, group.FieldType);
    }

    // ==================================================================

    /// <summary>
    /// Core's answer. The document is null on purpose: the graph is entirely direct, so nothing
    /// needs resolving, and this keeps the fixture a hand-built object graph rather than a file.
    /// </summary>
    private static bool CoreSaysTerminal(PdfDictionary group)
        => new PdfFormField(group, null, null).IsTerminal;

    /// <summary>The walk's answer for the same group.</summary>
    private static bool WalkSaysTerminal(PdfDictionary group)
    {
        var walk = AcroFormFieldWalk.Walk(CatalogWithFields(group), Direct);
        return walk.Fields.Single(f => f.Dictionary == group).IsTerminal;
    }
}
