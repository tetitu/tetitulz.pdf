// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.TestInfrastructure;

/// <summary>
/// Guards against the two habits that made the suite unrunnable by anyone but its
/// author, and unable to report the truth about itself:
///
///   1. <b>Machine-specific absolute paths.</b> Four tests hardcoded
///      <c>c:\myapp\tetitulz\tetitulz.pdf\...</c> and then hard-asserted the file
///      existed, so they failed on every machine — including this one, since that
///      directory does not exist here either.
///   2. <b>Fake skips.</b> <c>if (!File.Exists(path)) return;</c> reports *Passed*
///      while executing nothing, so a test written that way can never go red.
///      Use <see cref="CorpusFactAttribute"/> / <see cref="CorpusTheoryAttribute"/>
///      instead — those report *Skipped*, which is the honest outcome.
///
/// These run over the test sources on disk, so they only have teeth when the repo
/// is present; they skip (visibly) when it is not, e.g. when the assemblies are
/// executed from somewhere else.
///
/// <para><b>2 is checked twice, and the second check exists because the first one has a hole the
/// shape of one identifier.</b> <see cref="NoTestSilentlyReturnsWhenItsInputFileIsMissing"/> is a
/// regex over the file text: it sees <c>File.Exists</c>/<c>Directory.Exists</c> written
/// <i>inline</i>, wherever they appear, helper methods included. What it cannot see is the same
/// bail-out behind an indirection — <c>if (!HasHebrewFont()) return;</c> — which is how two Hebrew
/// font tests reported <i>Passed</i> while executing nothing for their whole lives.
/// <see cref="NoTestSilentlyReturnsFromAGuardedEarlyReturn"/> closes that by asking a structural
/// question instead: does this <c>[Fact]</c>'s own body end at a predicate, however that predicate
/// is spelled. Neither subsumes the other, so both stay.</para>
/// </summary>
public sealed class NoHardcodedPathsTests
{
    // Source scanning (repo-root resolution, the obj/bin exclusion, and the offset-preserving
    // comment stripper) lives in TestSourceScanner so this guard and
    // NoUnfalsifiableAssertionsTests cannot drift on what counts as a test source.
    private static string? TestsRoot => TestSourceScanner.TestsRoot;

    private static IEnumerable<string> TestSources() => TestSourceScanner.TestSources();

    private static bool IsGenerated(string path) => TestSourceScanner.IsGenerated(path);

    private static string StripComments(string src) => TestSourceScanner.StripComments(src);

    /// <summary>
    /// No test source may embed an absolute path that points at a checkout of this
    /// repository. Unrelated absolute literals are fine and deliberately allowed —
    /// e.g. <c>C:\Program Files\LibreOffice\program\soffice.exe</c> (an option-value
    /// test) or <c>Z:\definitely\missing\...</c> (a negative-path test). Only paths
    /// that bake in where *this repo* lives on someone's disk are rejected.
    /// </summary>
    [RepoSourceFact]
    public void NoTestEmbedsAnAbsolutePathIntoThisRepository()
    {

        // Drive-letter (or UNC) literal that also names this project — i.e. someone's
        // local checkout location, which is never valid on another machine.
        var offender = new Regex(
            @"@?""(?:[A-Za-z]:\\|\\\\)[^""]*tetitulz[^""]*""",
            RegexOptions.IgnoreCase);

        var hits = new List<string>();
        foreach (var file in TestSources())
        {
            if (IsGenerated(file)) continue;
            var text = StripComments(File.ReadAllText(file));
            foreach (Match m in offender.Matches(text))
            {
                int line = text[..m.Index].Split('\n').Length;
                hits.Add($"{Path.GetRelativePath(TestsRoot, file)}:{line}  {m.Value}");
            }
        }

        Assert.True(hits.Count == 0,
            "Test sources must not hardcode a path into a local checkout of this repo. " +
            "Resolve corpus files through Tetitulz.Pdf.TestHelpers.TestCorpus and gate the " +
            "test with [CorpusFact]/[CorpusTheory] instead.\n  " + string.Join("\n  ", hits));
    }

    /// <summary>
    /// A test that early-returns when its input file is missing reports Passed while
    /// executing nothing. That is how ~40 results in this suite stayed green
    /// regardless of what the renderer did. Corpus gating belongs in the attribute.
    /// </summary>
    [RepoSourceFact]
    public void NoTestSilentlyReturnsWhenItsInputFileIsMissing()
    {

        // `if (!File.Exists(x)) return;` and the brace-wrapped form, allowing the
        // System.IO. prefix and arbitrary whitespace/comments before the return.
        var offender = new Regex(
            @"if\s*\(\s*!\s*(?:System\.IO\.)?(?:File\.Exists|Directory\.Exists)\s*\([^)]*\)\s*\)\s*(?:\{[^}]*?)?\breturn\s*;",
            RegexOptions.Singleline);

        var hits = new List<string>();
        foreach (var file in TestSources())
        {
            if (IsGenerated(file)) continue;
            var text = StripComments(File.ReadAllText(file));
            foreach (Match m in offender.Matches(text))
            {
                int line = text[..m.Index].Split('\n').Length;
                hits.Add($"{Path.GetRelativePath(TestsRoot, file)}:{line}");
            }
        }

        Assert.True(hits.Count == 0,
            "These tests silently pass when their input is missing, so they can never fail. " +
            "Gate them with [CorpusFact]/[CorpusTheory] so they report as Skipped instead.\n  " +
            string.Join("\n  ", hits));
    }

    /// <summary>
    /// The same defect, structurally: a <c>[Fact]</c> / <c>[Theory]</c> whose <b>own body</b> ends
    /// at <c>if (SomePredicate(…)) return;</c>. The test reports <i>Passed</i> having executed
    /// nothing, and the name above it goes on claiming a behaviour nobody checked.
    ///
    /// <para><b>Why the sibling regex could not find these.</b> It matches the condition it was
    /// written for — an inline <c>File.Exists</c> — so wrapping the same question in a helper hides
    /// it completely — a test doing that is green on every machine that lacks what it needs (a Hebrew
    /// font, say). This check does not look at the condition's
    /// <i>subject</i> at all; it looks at where the <c>return</c> is.</para>
    ///
    /// <para><b>What it deliberately does not flag,</b> because these are not the defect:
    /// a <c>return</c> inside a <b>lambda</b> (the thread-filtered diagnostics handlers this suite
    /// is full of — <c>if (Environment.CurrentManagedThreadId != ownThread) return;</c> — end a
    /// callback, not a test) or inside a <b>local function</b> (recursive tree walks bottom out
    /// with <c>if (depth > 32) return;</c>). Both are severed at their opening brace, so only the
    /// method's own statements are considered. And the condition must contain a <b>call</b>: a
    /// predicate the test asks, rather than an ordinary <c>if</c> over data it already has.</para>
    ///
    /// <para>The remedy is never to delete the condition. It is to move it into an attribute that
    /// reports <b>Skipped</b> — <see cref="CorpusFactAttribute"/>, <see cref="SystemFontFactAttribute"/>,
    /// or a per-project one modelled on them — or to remove the host dependency altogether by
    /// injecting the fixture the test needs.</para>
    /// </summary>
    [RepoSourceFact]
    public void NoTestSilentlyReturnsFromAGuardedEarlyReturn()
    {
        var hits = new List<string>();
        foreach (var test in TestSourceScanner.EnumerateTestMethods())
            if (FindGuardedEarlyReturns(test.Body).Count > 0)
                hits.Add(test.ToString());

        Assert.True(hits.Count == 0,
            "These tests return early from their own body when a predicate says no, which reports " +
            "Passed while executing nothing — so they can never go red, whatever their name claims. " +
            "Gate them with an attribute that reports Skipped ([CorpusFact], [SystemFontFact], or a " +
            "per-project equivalent), or inject the fixture so the dependency disappears.\n  " +
            string.Join("\n  ", hits));
    }

    // ------------------------------------------------------------------ the detector

    /// <summary>An identifier immediately followed by '(' — i.e. the condition asks something.</summary>
    private static readonly Regex ConditionContainsCall =
        new(@"[A-Za-z_]\w*\s*\(", RegexOptions.Compiled);

    /// <summary>
    /// Offsets, within <paramref name="methodBody"/>, of every <c>if (…) return;</c> that ends the
    /// <b>test method itself</b>. Exposed for
    /// <c>GuardedEarlyReturnDetectorTests</c>, which is what proves this fires on the hidden shape
    /// and stays quiet on the legitimate ones — a guard nothing tests is indistinguishable from one
    /// that was deleted.
    /// </summary>
    internal static IReadOnlyList<int> FindGuardedEarlyReturns(string methodBody)
    {
        var hits = new List<int>();
        if (string.IsNullOrEmpty(methodBody)) return hits;

        int depth = 0;
        int severedAt = -1;           // brace depth at which a lambda / local function was entered
        var openParens = new Stack<int>();
        int lastGroupOpen = -1, lastGroupClose = -1;

        for (int i = 0; i < methodBody.Length; i++)
        {
            if (SkipLiteral(methodBody, ref i)) continue;

            char c = methodBody[i];

            if (c == '(') { openParens.Push(i); continue; }
            if (c == ')')
            {
                lastGroupOpen = openParens.Count > 0 ? openParens.Pop() : -1;
                lastGroupClose = i;
                continue;
            }
            if (c == '{')
            {
                depth++;
                if (severedAt < 0 && OpensNonMethodScope(methodBody, i, lastGroupOpen, lastGroupClose))
                    severedAt = depth;
                continue;
            }
            if (c == '}')
            {
                if (severedAt == depth) severedAt = -1;
                depth--;
                continue;
            }

            if (severedAt >= 0) continue;
            if (c != 'i' || !IsWordAt(methodBody, i, "if")) continue;

            int open = SkipSpace(methodBody, i + 2);
            if (open >= methodBody.Length || methodBody[open] != '(') continue;
            int close = MatchParen(methodBody, open);
            if (close < 0) continue;

            int keyword = GuardedReturnKeyword(methodBody, close + 1);
            if (keyword < 0) continue;

            // `return;` only. `return value;` cannot occur in a void test, and a bail-out that
            // produces a value is a helper's business, not a test's.
            int semi = SkipSpace(methodBody, keyword + "return".Length);
            if (semi >= methodBody.Length || methodBody[semi] != ';') continue;

            if (!ConditionContainsCall.IsMatch(methodBody.Substring(open + 1, close - open - 1))) continue;

            hits.Add(i);
        }

        return hits;
    }

    /// <summary>
    /// Index of the <c>return</c> keyword this <c>if</c> guards, or -1. Accepts both the bare form
    /// and a brace block, in which case anything may precede the <c>return</c> as long as the block
    /// does not close or nest first — the same latitude the sibling regex allows.
    /// </summary>
    private static int GuardedReturnKeyword(string src, int afterCondition)
    {
        int k = SkipSpace(src, afterCondition);
        if (k >= src.Length) return -1;
        if (src[k] != '{') return IsWordAt(src, k, "return") ? k : -1;

        for (int m = k + 1; m < src.Length; m++)
        {
            if (SkipLiteral(src, ref m)) continue;
            if (src[m] == '{' || src[m] == '}') return -1;
            if (src[m] == 'r' && IsWordAt(src, m, "return")) return m;
        }
        return -1;
    }

    private static readonly string[] BlockKeywords =
    {
        "if", "else", "for", "foreach", "while", "switch", "using", "lock", "fixed",
        "catch", "try", "finally", "do", "unsafe", "checked", "unchecked",
    };

    /// <summary>
    /// True when the brace at <paramref name="brace"/> opens something whose <c>return</c> is not
    /// the test's own: a lambda body, a local function, or an object/collection initialiser.
    /// </summary>
    /// <param name="lastGroupOpen">Index of the '(' matching the most recently closed ')'.</param>
    /// <param name="lastGroupClose">Index of that ')'. Tracked during the forward walk rather than
    /// re-matched backwards, because a backwards scan cannot tell a parenthesis inside a string
    /// literal from a real one.</param>
    private static bool OpensNonMethodScope(string src, int brace, int lastGroupOpen, int lastGroupClose)
    {
        int i = brace - 1;
        while (i >= 0 && char.IsWhiteSpace(src[i])) i--;
        if (i < 0) return false;                                     // the method body's own brace

        if (src[i] == '>' && i > 0 && src[i - 1] == '=') return true;      // `=> { … }`
        if (src[i] == ';' || src[i] == '{' || src[i] == '}' || src[i] == ':') return false;

        if (src[i] == ')')
        {
            // The token before the parameter list decides. `if (…) {` is a block; `void Walk(…) {`
            // is a local function, and its `return` ends the walk rather than the test.
            if (i != lastGroupClose || lastGroupOpen < 0) return true;
            int j = lastGroupOpen - 1;
            while (j >= 0 && char.IsWhiteSpace(src[j])) j--;
            return !EndsWithBlockKeyword(src, j);
        }

        // `try {`, `else {`, `do {`, `finally {`, `unsafe {` …
        if (EndsWithBlockKeyword(src, i)) return false;

        // `= new X { … }`, `new[] { … }`, a switch arm: data, not statements.
        return true;
    }

    private static bool EndsWithBlockKeyword(string src, int lastCharIndex)
    {
        if (lastCharIndex < 0 || !IsIdentifierChar(src[lastCharIndex])) return false;
        int start = lastCharIndex;
        while (start > 0 && IsIdentifierChar(src[start - 1])) start--;
        string word = src.Substring(start, lastCharIndex - start + 1);
        foreach (string k in BlockKeywords)
            if (string.Equals(word, k, StringComparison.Ordinal)) return true;
        return false;
    }

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static bool IsWordAt(string src, int i, string word) =>
        i >= 0 && i + word.Length <= src.Length &&
        string.CompareOrdinal(src, i, word, 0, word.Length) == 0 &&
        (i == 0 || !IsIdentifierChar(src[i - 1])) &&
        (i + word.Length >= src.Length || !IsIdentifierChar(src[i + word.Length]));

    private static int SkipSpace(string src, int i)
    {
        while (i < src.Length && char.IsWhiteSpace(src[i])) i++;
        return i;
    }

    private static int MatchParen(string src, int openParen)
    {
        int depth = 0;
        for (int i = openParen; i < src.Length; i++)
        {
            if (SkipLiteral(src, ref i)) continue;
            if (src[i] == '(') depth++;
            else if (src[i] == ')' && --depth == 0) return i;
        }
        return -1;
    }

    /// <summary>
    /// When <paramref name="i"/> is at the start of a string, verbatim-string, raw-string or
    /// character literal, advances it to that literal's <b>last</b> character and returns true.
    ///
    /// <para>A near-copy of <c>TestSourceScanner</c>'s own private literal skip, and deliberately
    /// not a call into it: that one serves the walk that finds where a method <i>ends</i>, and is
    /// private to it. Both exist for the same reason — a brace or parenthesis inside a literal is
    /// data, and counting it as syntax truncates the walk at the wrong place. Any test whose
    /// fixture is PostScript, JSON or a PDF dictionary contains one.</para>
    /// </summary>
    private static bool SkipLiteral(string src, ref int i)
    {
        char c = src[i];
        char next = i + 1 < src.Length ? src[i + 1] : '\0';

        if (c == '"' && next == '"' && i + 2 < src.Length && src[i + 2] == '"')
        {
            int fence = 0;
            while (i + fence < src.Length && src[i + fence] == '"') fence++;
            int j = i + fence;
            while (j < src.Length)
            {
                if (src[j] == '"')
                {
                    int run = 0;
                    while (j + run < src.Length && src[j + run] == '"') run++;
                    if (run >= fence) { i = j + run - 1; return true; }
                    j += run;
                    continue;
                }
                j++;
            }
            i = src.Length - 1;
            return true;
        }

        if (c == '@' && next == '"')
        {
            i += 2;
            while (i < src.Length)
            {
                if (src[i] == '"')
                {
                    if (i + 1 < src.Length && src[i + 1] == '"') { i += 2; continue; }
                    return true;
                }
                i++;
            }
            i = src.Length - 1;
            return true;
        }

        if (c == '"' || c == '\'')
        {
            char quote = c;
            i++;
            while (i < src.Length && src[i] != quote)
            {
                if (src[i] == '\\') i++;
                i++;
            }
            if (i >= src.Length) i = src.Length - 1;
            return true;
        }

        return false;
    }
}
