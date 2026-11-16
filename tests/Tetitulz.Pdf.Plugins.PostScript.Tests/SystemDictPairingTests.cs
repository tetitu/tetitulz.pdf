// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using System.Linq;
using Tetitulz.Pdf.Plugins.PostScript.Interpreter;
using Tetitulz.Pdf.Plugins.PostScript.Operators;
using Xunit;

namespace Tetitulz.Pdf.Plugins.PostScript.Tests;

/// <summary>
/// Every <c>set<i>X</i></c> the system dictionary registers has a <c>current<i>X</i></c> partner,
/// and every <c>current<i>X</i></c> has a <c>set<i>X</i></c>.
/// </summary>
/// <remarks>
/// <para><b>This invariant is stated in the source and was, until now, maintained by hand.</b>
/// <c>PsSystemDict.RegisterKnownUnimplemented</c>'s own comment says it: <i>"Every set*/current*
/// pair here must be COMPLETE. Five partners were missing until 2026-08-19 —
/// <c>currentcolorscreen</c>, <c>currentcolortransfer</c>, <c>currentsmoothness</c>,
/// <c>currentcachelimit</c> and <c>currentvmthreshold</c> — while their set* halves were all
/// present, which is the one asymmetry that reads as an oversight rather than a decision."</i></para>
///
/// <para><b>Those five were then covered by a hand-written list of names</b> — one
/// <c>[InlineData]</c> row apiece in <c>MissingSystemNameTests</c>. That pins the five that were
/// found; it cannot notice a sixth. This test asks the question the comment actually poses, over the
/// registered names themselves, so a new <c>set*</c> with no readback fails the build the day it is
/// added rather than the day a real file trips over it.</para>
///
/// <para><b>Why a missing half is expensive out of proportion to its size.</b> A program that sets a
/// parameter and reads it back is doing the most ordinary thing there is — and an unrecognised name
/// raises <c>undefined</c>, which costs the interpreter the whole enclosing top-level object, not
/// just the one operator. Half a pair is a silently truncated page.</para>
/// </remarks>
public class SystemDictPairingTests
{
    /// <summary>
    /// Names whose partner does not exist in PostScript at all. <b>This list may only shrink.</b>
    /// Each entry is a fact about the PLRM, not a gap in this interpreter.
    /// </summary>
    private static readonly Dictionary<string, string> Unpaired = new(StringComparer.Ordinal)
    {
        // A glyph declares its bounding box to the font machinery; nothing reads it back.
        ["setbbox"] = "PLRM: a Type 3 glyph operator, with no readback",

        // The readback is spelled `fileposition`, not `currentfileposition` — the file operators
        // predate the set/current convention and were never renamed into it.
        ["setfileposition"] = "PLRM: its partner is `fileposition`, not `current…`",

        // A pattern is installed as the current COLOUR, so it reads back through
        // currentcolor/currentcolorspace. There is no `currentpattern`.
        ["setpattern"] = "PLRM: reads back through currentcolor/currentcolorspace",

        // The current input file is a property of execution, not a parameter anything sets.
        ["currentfile"] = "PLRM: there is no `setfile`",

        // The rest of the Type 3 glyph family, alongside setbbox: a glyph declares its metrics to
        // the font machinery and nothing reads them back.
        ["setcachedevice"]  = "PLRM: a Type 3 glyph operator, with no readback",
        ["setcachedevice2"] = "PLRM: a Type 3 glyph operator, with no readback",
        ["setcharwidth"]    = "PLRM: a Type 3 glyph operator, with no readback",

        // The current dictionary is changed by begin/end, not by a `setdict`.
        ["currentdict"] = "PLRM: the dictionary stack is driven by begin/end, not by a setter",

        // The current point is a property of the path, moved by moveto/lineto/curveto.
        ["currentpoint"] = "PLRM: set by the path operators, not by a `setpoint`",
    };

    private static HashSet<string> RegisteredNames()
    {
        PsDict system = PsSystemDict.Build();

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (KeyValuePair<PsObject, PsObject> entry in system.Entries)
        {
            if (entry.Key.Type != PsType.Name) continue;
            string name = entry.Key.AsName();
            if (name != null) names.Add(name);
        }
        return names;
    }

    [Fact]
    public void EverySetOperatorHasAReadback()
    {
        HashSet<string> names = RegisteredNames();

        var lonely = names.Where(n => n.StartsWith("set", StringComparison.Ordinal))
                          .Where(n => !names.Contains("current" + n.Substring(3)))
                          .Where(n => !Unpaired.ContainsKey(n))
                          .OrderBy(n => n, StringComparer.Ordinal)
                          .ToList();

        Assert.True(lonely.Count == 0,
            "These set* operators have no current* readback, so a program that sets the parameter " +
            "and reads it back loses its whole top-level object to `undefined`:\n  " +
            string.Join("\n  ", lonely) +
            "\n\nRegister the partner rather than adding it to Unpaired — that list is for names " +
            "PostScript itself does not pair.");
    }

    [Fact]
    public void EveryReadbackHasASetOperator()
    {
        HashSet<string> names = RegisteredNames();

        var lonely = names.Where(n => n.StartsWith("current", StringComparison.Ordinal))
                          .Where(n => !names.Contains("set" + n.Substring(7)))
                          .Where(n => !Unpaired.ContainsKey(n))
                          .OrderBy(n => n, StringComparer.Ordinal)
                          .ToList();

        Assert.True(lonely.Count == 0,
            "These current* operators have no set* partner:\n  " + string.Join("\n  ", lonely));
    }

    /// <summary>
    /// A name on the exception list that has since gained a partner must come off it, or the list
    /// stops describing PostScript and starts excusing this interpreter.
    /// </summary>
    [Fact]
    public void TheUnpairedListHasNoStaleEntries()
    {
        HashSet<string> names = RegisteredNames();

        var stale = Unpaired.Keys
            .Where(n => n.StartsWith("set", StringComparison.Ordinal)
                          ? names.Contains("current" + n.Substring(3))
                          : names.Contains("set" + n.Substring(7)))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.True(stale.Count == 0,
            "These now have a partner and must be removed from Unpaired:\n  " + string.Join("\n  ", stale));
    }

    /// <summary>
    /// A floor for the guard itself: if the name scan came back empty or tiny, both tests above
    /// would pass while checking nothing — banned pattern 3 wearing a dictionary walk.
    /// </summary>
    [Fact]
    public void TheScanReadsARealSystemDictionary()
    {
        HashSet<string> names = RegisteredNames();

        Assert.True(names.Count > 80, $"only {names.Count} names found; the scan is not working");
        Assert.Contains("setlinewidth", names);
        Assert.Contains("currentlinewidth", names);

        // …and the five the source comment records as once-missing are present, which is the
        // specific regression this invariant exists to prevent recurring.
        foreach (string name in new[]
                 {
                     "currentcolorscreen", "currentcolortransfer",
                     "currentsmoothness", "currentcachelimit", "currentvmthreshold",
                 })
            Assert.Contains(name, names);
    }
}
