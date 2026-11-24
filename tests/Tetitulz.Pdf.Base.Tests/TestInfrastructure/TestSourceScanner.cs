// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.TestHelpers;

namespace Tetitulz.Pdf.Base.Tests.TestInfrastructure;

/// <summary>
/// One test method found in a source file on disk.
/// </summary>
/// <param name="RelativePath">Path relative to <c>tests/</c>, forward-slashed, stable across machines.</param>
/// <param name="Name">The method name.</param>
/// <param name="Body">The method body, braces included, with comments blanked out.</param>
/// <param name="Line">1-based line of the method signature.</param>
internal readonly record struct TestMethodSource(string RelativePath, string Name, string Body, int Line)
{
    /// <summary>
    /// The stable identity used by allow-lists: <c>Relative/Path.cs::MethodName</c>. Deliberately
    /// not the line number, which changes on every unrelated edit above it.
    /// </summary>
    public string Id => $"{RelativePath}::{Name}";

    public override string ToString() => $"{RelativePath}:{Line}  {Name}";
}

/// <summary>
/// Shared source-scanning for the test-infrastructure guards
/// (<see cref="NoHardcodedPathsTests"/>, <see cref="NoUnfalsifiableAssertionsTests"/>).
///
/// <para>These guards read the test sources <b>on disk</b>, so they only have teeth when the repo
/// is present. <see cref="TestsRoot"/> returning <c>null</c> is the signal that it is not — e.g.
/// when the assemblies are executed from somewhere else — and the guards must then report
/// <b>Skipped</b>, via <c>[RepoSourceFact]</c>; <c>if (TestsRoot == null) return;</c> would report
/// <b>Passed</b>, and a guard reporting green when it could not run is banned test pattern 2.</para>
///
/// <para>Shared so the two guards cannot drift on what counts as a test source, how comments
/// are stripped, or where a method body ends.</para>
/// </summary>
internal static class TestSourceScanner
{
    /// <summary>The repository's <c>tests/</c> directory, or <c>null</c> when the repo is not present.</summary>
    public static string? TestsRoot =>
        TestCorpus.RepoRoot is { } repo && Directory.Exists(Path.Combine(repo, "tests"))
            ? Path.Combine(repo, "tests")
            : null;

    /// <summary>Every non-generated test source file, or empty when the repo is not present.</summary>
    public static IEnumerable<string> TestSources() =>
        TestsRoot == null
            ? Array.Empty<string>()
            : Directory.EnumerateFiles(TestsRoot, "*.cs", SearchOption.AllDirectories);

    public static bool IsGenerated(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
        path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}");

    public static string RelativePath(string absolutePath) =>
        TestsRoot == null
            ? absolutePath
            : Path.GetRelativePath(TestsRoot, absolutePath).Replace('\\', '/');

    /// <summary>1-based line number of <paramref name="index"/> within <paramref name="text"/>.</summary>
    public static int LineOf(string text, int index)
    {
        int line = 1;
        for (int i = 0; i < index && i < text.Length; i++)
            if (text[i] == '\n') line++;
        return line;
    }

    /// <summary>
    /// Blanks out comments <b>preserving offsets</b>, so reported line numbers stay correct — the
    /// guards' own doc comments quote the patterns they forbid, and would otherwise flag
    /// themselves. String and character literals are left intact, because a banned pattern
    /// inside a literal is still a banned pattern.
    /// </summary>
    public static string StripComments(string src)
    {
        var buf = src.ToCharArray();
        bool inString = false, inChar = false, inVerbatim = false;

        for (int i = 0; i < buf.Length; i++)
        {
            char c = buf[i];
            char next = i + 1 < buf.Length ? buf[i + 1] : '\0';

            if (inVerbatim)
            {
                if (c == '"' && next == '"') { i++; continue; }
                if (c == '"') inVerbatim = false;
                continue;
            }
            if (inString)
            {
                if (c == '\\') { i++; continue; }
                if (c == '"') inString = false;
                continue;
            }
            if (inChar)
            {
                if (c == '\\') { i++; continue; }
                if (c == '\'') inChar = false;
                continue;
            }

            if (c == '@' && next == '"') { inVerbatim = true; i++; continue; }
            if (c == '"') { inString = true; continue; }
            if (c == '\'') { inChar = true; continue; }

            if (c == '/' && next == '/')
            {
                while (i < buf.Length && buf[i] != '\n') buf[i++] = ' ';
            }
            else if (c == '/' && next == '*')
            {
                while (i < buf.Length && !(buf[i] == '*' && i + 1 < buf.Length && buf[i + 1] == '/'))
                {
                    if (buf[i] != '\n') buf[i] = ' ';
                    i++;
                }
                if (i < buf.Length) { buf[i] = ' '; if (i + 1 < buf.Length) buf[i + 1] = ' '; i++; }
            }
        }
        return new string(buf);
    }

    /// <summary>
    /// When <paramref name="i"/> is at the start of a string, verbatim-string, raw-string or
    /// character literal, advances it to that literal's <b>last</b> character and returns true; the
    /// caller's own <c>i++</c> then steps past it. Otherwise leaves <paramref name="i"/> alone and
    /// returns false.
    ///
    /// <para><b>Why every bracket walk below needs this.</b> <see cref="StripComments"/> tracks
    /// literals, but only so that a <c>//</c> inside one is not mistaken for a comment — it
    /// deliberately leaves literals intact. The walks then counted <i>every</i> brace character,
    /// including those inside literals, so a test whose body contains a string like
    /// <c>"1 2 add}"</c> appeared to end at that brace. The body was truncated to a prefix holding
    /// no assertions, and <see cref="NoUnfalsifiableAssertionsTests"/> reported it as asserting
    /// nothing. Any test for a brace-using syntax — PostScript, JSON, a C# code generator — hits
    /// this.</para>
    ///
    /// <para>Truncation can only ever <i>add</i> false positives, never hide a real offender: a
    /// prefix of a body has no more assertions than the body.</para>
    /// </summary>
    private static bool TrySkipLiteral(string src, ref int i)
    {
        char c = src[i];
        char next = i + 1 < src.Length ? src[i + 1] : '\0';

        // Raw string literal: three or more quotes, closed by the same number.
        if (c == '"' && next == '"' && i + 2 < src.Length && src[i + 2] == '"')
        {
            int fenceLength = 0;
            while (i + fenceLength < src.Length && src[i + fenceLength] == '"') fenceLength++;

            int j = i + fenceLength;
            while (j < src.Length)
            {
                if (src[j] == '"')
                {
                    int run = 0;
                    while (j + run < src.Length && src[j + run] == '"') run++;
                    if (run >= fenceLength) { i = j + run - 1; return true; }
                    j += run;
                    continue;
                }
                j++;
            }
            i = src.Length - 1;   // unterminated
            return true;
        }

        // Verbatim string: @"…", where "" is an escaped quote.
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

    /// <summary>
    /// Index just past the ')' that closes the parameter list opened at <paramref name="openParen"/>.
    /// Counts nesting so a default value containing parentheses does not end the list early, and
    /// skips literals so a ')' inside one does not either.
    /// </summary>
    private static int FindParameterListEnd(string src, int openParen)
    {
        if (openParen < 0 || openParen >= src.Length || src[openParen] != '(') return -1;

        int depth = 0;
        for (int i = openParen; i < src.Length; i++)
        {
            if (TrySkipLiteral(src, ref i)) continue;

            if (src[i] == '(') depth++;
            else if (src[i] == ')')
            {
                depth--;
                if (depth == 0) return i + 1;
            }
        }
        return -1;
    }

    /// <summary>
    /// Index of the ';' ending an expression-bodied member, skipping semicolons nested inside
    /// parentheses, braces, brackets (a lambda body can contain them) or literals.
    /// </summary>
    private static int FindStatementEnd(string src, int from)
    {
        int paren = 0, brace = 0, bracket = 0;
        for (int i = from; i < src.Length; i++)
        {
            if (TrySkipLiteral(src, ref i)) continue;

            char c = src[i];
            if (c == '(') paren++;
            else if (c == ')') paren--;
            else if (c == '{') brace++;
            else if (c == '}') brace--;
            else if (c == '[') bracket++;
            else if (c == ']') bracket--;
            else if (c == ';' && paren <= 0 && brace <= 0 && bracket <= 0) return i;
        }
        return -1;
    }

    private static readonly Regex FactOrTheory = new(@"\[(Fact|Theory)[^\]]*\]", RegexOptions.Compiled);

    // The method signature that follows the attribute. Deliberately permissive about the return
    // type (async Task, ValueTask, void) and tolerant of attributes sitting between the two.
    private static readonly Regex MethodSignature =
        new(@"public\s+(?:async\s+)?[\w<>\[\],\s\?\.]+?\s+(\w+)\s*\(", RegexOptions.Compiled);

    /// <summary>
    /// Every <c>[Fact]</c> / <c>[Theory]</c> method in every test source, with its body.
    ///
    /// <para>Bodies are located by brace matching rather than by regex, because a method body
    /// contains braces (object initialisers, nested lambdas, PDF dictionaries built inline) and
    /// no regex can find its end. A method whose braces cannot be balanced — which would mean the
    /// file does not compile — is skipped rather than guessed at.</para>
    ///
    /// <para>The walk skips string and character literals (<see cref="TrySkipLiteral"/>): a brace
    /// inside a literal is data, not syntax, and counting it truncated the body of any test whose
    /// fixture contains one.</para>
    /// </summary>
    public static IEnumerable<TestMethodSource> EnumerateTestMethods()
    {
        foreach (var file in TestSources())
        {
            if (IsGenerated(file)) continue;

            string src;
            try { src = StripComments(File.ReadAllText(file)); }
            catch (IOException) { continue; }

            string relative = RelativePath(file);

            foreach (Match attribute in FactOrTheory.Matches(src))
            {
                var signature = MethodSignature.Match(src, attribute.Index);
                if (!signature.Success) continue;

                // Find whichever comes first after the parameter list: a block body '{' or an
                // expression body "=>". Without this an expression-bodied test —
                //   [Fact] public void X_Throws() => Assert.Throws<T>(() => …);
                // — has no brace of its own, so the walk below would run on to the NEXT method's
                // block and classify this test by a body that is not its own. Several perfectly
                // good `Assert.Throws` tests were mis-flagged that way.
                int afterParams = FindParameterListEnd(src, signature.Index + signature.Length - 1);
                if (afterParams < 0) continue;

                int open = src.IndexOf('{', afterParams);
                int arrow = src.IndexOf("=>", afterParams, StringComparison.Ordinal);

                int close;
                if (arrow >= 0 && (open < 0 || arrow < open))
                {
                    // Expression body: everything up to the terminating semicolon at depth 0.
                    close = FindStatementEnd(src, arrow);
                    if (close < 0) continue;
                    open = arrow;
                }
                else
                {
                    if (open < 0) continue;

                    int depth = 0;
                    close = -1;
                    for (int i = open; i < src.Length; i++)
                    {
                        if (TrySkipLiteral(src, ref i)) continue;

                        if (src[i] == '{') depth++;
                        else if (src[i] == '}')
                        {
                            depth--;
                            if (depth == 0) { close = i; break; }
                        }
                    }
                    if (close < 0) continue;
                }

                yield return new TestMethodSource(
                    relative,
                    signature.Groups[1].Value,
                    src.Substring(open, close - open + 1),
                    LineOf(src, signature.Index));
            }
        }
    }
}
