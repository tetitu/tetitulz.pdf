// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.TestInfrastructure;

/// <summary>
/// Every <c>Last*</c> diagnostic slot must be <b>cleared somewhere</b>, not only written on failure —
/// and must be <b>written somewhere</b>, not only cleared.
/// </summary>
/// <remarks>
/// <para>
/// CLAUDE.md states the rule: <i>"A slot that a caller reads after an operation must be reset at the
/// start of that operation, on every path, not only where it is written."</i>
/// <c>[ThreadStatic]</c> alone is per-thread storage, not per-document scope. What a slot with
/// <b>no clear at all</b> does:
/// </para>
/// <list type="bullet">
///   <item><c>PdfParser.LastObjectParseError</c> documents itself as "null when the last GetObject
///   call on this thread succeeded", which cannot be true without a reset: one bad object would be
///   reported against every later success, and a caller handed <c>PdfNull</c> blame the wrong
///   object.</item>
///   <item><c>DssBuilder.LastAiaParseError</c> runs per-certificate over a chain, so one
///   unparseable AIA extension would be attributed to every certificate after it.</item>
///   <item><c>PdfMdpVerifier.LastDocMdpUsedDefaultP</c>, a <c>bool</c> feeding security reports,
///   would hold the previous document's verdict when opening a malformed file threw.</item>
///   <item><c>CMapParser.LastUsecmapName</c> would leak across an empty-input early return.</item>
/// </list>
/// <para>
/// <b>What this can and cannot check.</b> Proving the reset happens <i>at the start, on every path</i>
/// needs flow analysis. This asserts the weaker, mechanical property — the slot is cleared
/// <i>somewhere</i> in its own file — which is precisely what each of those would fail. A slot that
/// clears itself in the wrong place still passes here, so read a green run as "no slot is
/// write-only", not as proof of correct scoping.
/// </para>
/// <para>
/// <b>And the mirror.</b> "Is it cleared?" is satisfied by a slot that is <i>nothing but</i> a
/// clear: a property whose only assignment is its reset can never be non-null, the diagnostic it
/// documents is unreachable, and tests asserting it is null pass vacuously — while a mutation
/// deleting the reset is killed by <i>this</i> test, which makes a dead slot look like a covered
/// one. <see cref="HasNonNeutralWrite"/> asks the other half.
/// </para>
/// </remarks>
public class DiagnosticSlotResetTests
{
    /// <summary>A <c>Last*</c> property or field declaration, e.g. <c>public static string LastFoo</c>.</summary>
    private static readonly Regex Declaration = new(
        @"^\s*(?:public|internal|private)\s+static\s+[A-Za-z0-9_<>,\.\?\[\]]+\s+(?<name>Last[A-Za-z0-9]+)\s*(?:\{|=>|;)",
        RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>
    /// Slots whose clear is real but unprovable by pattern-matching. <b>One entry, with its
    /// reason</b> — this is not a place to silence a finding.
    /// </summary>
    private static readonly HashSet<string> Exempt = new(StringComparer.Ordinal)
    {
        // Cleared by PASSING null: NoteProfileConsulted(reason) is called on every consultation,
        // including the successful ones, so the slot is overwritten with null on success. The
        // clearing therefore lives at the call sites, not in this file, and no regex over this file
        // can see it. Verified by reading NoteProfileConsulted's callers.
        "IccTransformProvider.cs:LastUnsupportedProfile",
    };

    /// <summary>The neutral values an assignment may carry and still count only as a <i>clear</i>.</summary>
    private static readonly Regex NeutralRightHandSide = new(
        @"^(null|false|0(?![\dxX\.])|string\.Empty|""""|default|new\s|[A-Za-z0-9_]+\.(None|Empty|Unknown|Ok|Success))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Whether anything in <paramref name="text"/> assigns <paramref name="slot"/> a value that is
    /// <b>not</b> neutral — that is, whether the slot can ever carry a diagnostic at all.
    /// </summary>
    /// <remarks>
    /// <para><b>The mirror of the question above, and the one that went unasked.</b> A slot that is
    /// only ever <i>cleared</i> can never be non-null, so the diagnostic it exists to carry is
    /// unreachable and every <c>Assert.Null</c> on it passes with the feature deleted.
    /// <c>PdfLayoutAnalyzer.LastAnalysisWarning</c> was exactly that: the ceiling it reported went
    /// away in a rewrite and the reset stayed behind as the field's only assignment. This guard ran
    /// green over it for as long as it existed, because "is it cleared?" is satisfied by a slot that
    /// is *nothing but* a clear.</para>
    ///
    /// <para><b>A plain assignment is only one of the four ways a slot is written</b>, and the first
    /// run of this check reported five false positives — one per shape it did not model. A counting
    /// slot is advanced (<c>LastUnchangedSubtreeCount++</c>); a lazily-created one is compound-assigned
    /// (<c>_lastRowRecovered ??= …</c>); a collection-backed one is created and then added to
    /// (<c>(_issues ??= new List&lt;string&gt;()).Add(issue)</c>), so the only mention of the field
    /// is that <c>??=</c>; and a backing field spelled <c>_lastFoo</c> never matches a pattern built
    /// from the property name <c>LastFoo</c> without <see cref="RegexOptions.IgnoreCase"/>.</para>
    ///
    /// <para>Assignment only, never comparison: the right-hand side may not begin with <c>=</c>, so
    /// <c>slot == x</c> is excluded, and <c>!=</c>, <c>&lt;=</c> and <c>&gt;=</c> all put a character
    /// between the name and the <c>=</c> that <c>\s*</c> will not cross.</para>
    /// </remarks>
    private static bool HasNonNeutralWrite(string text, string slot)
    {
        string bare = Regex.Escape(slot.TrimStart('_'));

        // Advanced, not assigned: a counter has no right-hand side to classify.
        if (Regex.IsMatch(text, $@"\b_?{bare}\b\s*(\+\+|--)", RegexOptions.IgnoreCase)) return true;

        // Compound assignment reads the old value, so its result can never be a clear. ??= is the
        // common one: it is how a lazily-created slot is both made and kept.
        if (Regex.IsMatch(text, $@"\b_?{bare}\b\s*(\?\?|[+\-*/|&^]|<<|>>)=", RegexOptions.IgnoreCase))
            return true;

        // A collection-backed slot is CREATED empty and then added to, and an empty collection is
        // genuinely a cleared state — so `= new List<string>()` is rightly neutral and the content
        // arrives through a member call instead. Reading only assignments calls such a slot dead.
        if (Regex.IsMatch(
                text,
                $@"\b_?{bare}\b\s*\??\.\s*(Add|AddRange|Append|Insert|Push|Enqueue|TryAdd|Union\w*)\s*\(",
                RegexOptions.IgnoreCase))
            return true;

        // =(?![>=]) — an expression-bodied property is NOT an assignment, and reading it as one is
        // how the first version of this check passed over the very slot it was written for:
        // `LastAnalysisWarning => _lastAnalysisWarning;` is "name, space, =", whose right-hand side
        // then reads as "> _lastAnalysisWarning" — not in the neutral set, so every forwarder
        // property counted as a write and no slot could ever be reported dead.
        var assignment = new Regex(
            $@"\b_?{bare}\b\s*=(?![>=])\s*(?<rhs>[^=;][^;]*)", RegexOptions.IgnoreCase);

        foreach (Match a in assignment.Matches(text))
            if (!NeutralRightHandSide.IsMatch(a.Groups["rhs"].Value.TrimStart()))
                return true;

        return false;
    }

    [RepoSourceFact]
    public void Every_Last_diagnostic_slot_is_cleared_somewhere_in_its_own_file()
    {
        string repo = TestCorpus.RepoRoot!;
        string src = Path.Combine(repo, "src");

        var offenders = new List<string>();
        int slotsChecked = 0;

        foreach (string file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            string norm = file.Replace('\\', '/');
            if (norm.Contains("/obj/") || norm.Contains("/bin/")) continue;

            string text = File.ReadAllText(file);
            if (!text.Contains("Last")) continue;

            foreach (Match m in Declaration.Matches(text))
            {
                string name = m.Groups["name"].Value;

                // An expression-bodied getter owns no state of its own — the slot is whatever it
                // forwards to. Resolve that target and check IT is cleared, because the backing
                // field is usually named differently (LastError => _lastXmpWriteError) and checking
                // the property name would report every such slot as an offender.
                // \s* after => so a body wrapped onto the next line still resolves (that shape
                // alone produced two false positives when this was written).
                var forwarder = new Regex(
                    $@"\b{Regex.Escape(name)}\s*=>\s*\(?[A-Za-z<>,\[\]\s]*\)?\s*(?<target>[A-Za-z_][A-Za-z0-9_\.]*)");
                var fwd = forwarder.Match(text);

                // A forwarder to another type (Other.LastError) delegates ownership entirely.
                if (fwd.Success && fwd.Groups["target"].Value.Contains('.')) continue;

                string slot = fwd.Success ? fwd.Groups["target"].Value : name;

                // A slot is "cleared" if anything in the file assigns it a neutral value. Both the
                // property and its backing field are accepted, since either may carry the reset,
                // and an enum sentinel (CffFailure.None) counts as much as null does.
                //
                // 0 is in the list because a COUNTING slot's neutral value is zero and nothing else.
                // Every slot this guard had seen was a reference or a bool, so the vocabulary simply
                // had no word for one — PdfTaggedTextExtractor.LastEdgesExamined, a slot-count probe
                // that IS reset at the top of its operation, was reported as write-only. Widening the
                // set of values that count as neutral is not a relaxation: the rule is still that the
                // slot is assigned a cleared state somewhere in its own file, and `slot = 0` is that
                // assignment for an int in the way `slot = null` is for a string.
                var cleared = new Regex(
                    $@"\b_?{Regex.Escape(slot.TrimStart('_'))}\b\s*=\s*" +
                    // 0(?![\dxX.]) so a literal that merely STARTS with a zero — 0x1F, 0.5, 007 —
                    // does not pass as a clear.
                    @"(null|false|0(?![\dxX\.])|string\.Empty|""""|default|new |[A-Za-z0-9_]+\.(None|Empty|Unknown|Ok|Success))",
                    RegexOptions.IgnoreCase);

                slotsChecked++;
                if (Exempt.Contains($"{Path.GetFileName(file)}:{name}")) continue;
                if (!cleared.IsMatch(text))
                    offenders.Add($"{Rel(repo, file)}: {name} is written but never cleared.");
                else if (!HasNonNeutralWrite(text, slot))
                    offenders.Add(
                        $"{Rel(repo, file)}: {name} is cleared but never written — it can never " +
                        "report anything, so every assertion that it is null passes vacuously.");
            }
        }

        // The scan must actually be finding slots; a regex that silently stops matching would
        // otherwise turn this guard green forever.
        Assert.True(slotsChecked >= 20,
            $"Only {slotsChecked} Last* slots were found; the declaration pattern is wrong, not the tree.");

        Assert.True(offenders.Count == 0,
            "A Last* diagnostic that is never cleared reports a stale value after a later success; " +
            "one that is never written can report nothing at all:\n  " +
            string.Join("\n  ", offenders.Distinct()));
    }

    private static string Rel(string repo, string file) =>
        file.Substring(repo.Length).TrimStart('\\', '/').Replace('\\', '/');
}
