// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Reflection;
using System.Text;
using Tetitulz.Pdf.Plugins.CMap;
using Tetitulz.Pdf.Plugins.PostScript;
using Tetitulz.Pdf.Plugins.PostScript.Interpreter;

namespace Tetitulz.Pdf.Plugins.PostScript.Tests;

/// <summary>
/// Runs the scanner and parser over the ~700 genuine Adobe CMap resources embedded in
/// <c>Tetitulz.Pdf.Plugins.CMap</c>.
///
/// <para>A CMap resource <i>is</i> a PostScript program — <c>%!PS-Adobe-3.0 Resource-CMap</c>,
/// with real prologues, <c>begin</c>/<c>end</c>, procedures, hex strings, dictionaries and
/// comments — so this is a large corpus of hand-written and generated PostScript that nobody wrote
/// with this parser in mind. It costs nothing to keep: the files are embedded resources, so no test
/// reads from disk and the repo's self-contained-tests rule holds.</para>
///
/// <para>They are scanned and <b>parsed</b>, never executed: they call <c>findresource</c> and
/// <c>defineresource</c>, which are out of scope, and executing them would be testing something
/// else.</para>
/// </summary>
public sealed class PsScannerCorpusTests
{
    private static IReadOnlyList<(string Name, byte[] Bytes)> LoadCorpus()
    {
        var asm = typeof(AdobeCMapPlugin).Assembly;
        var items = new List<(string, byte[])>();

        foreach (string name in asm.GetManifestResourceNames())
        {
            using var stream = asm.GetManifestResourceStream(name);
            if (stream == null) continue;
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            items.Add((name, ms.ToArray()));
        }

        return items;
    }

    /// <summary>
    /// The corpus must be there for the rest of this class to mean anything — if the CMap plugin
    /// ever stops embedding its resources, these tests would silently pass over nothing.
    /// </summary>
    [Fact]
    public void Corpus_IsNotEmpty()
    {
        var corpus = LoadCorpus();

        Assert.True(corpus.Count > 100,
            $"expected the CMap plugin to embed hundreds of PostScript resources, found {corpus.Count}");
    }

    [Fact]
    public void Corpus_ScansWithoutThrowing_AndEveryResourceYieldsTokens()
    {
        var empty = new List<string>();

        foreach (var (name, bytes) in LoadCorpus())
        {
            PostScriptDiagnostics.Clear();
            var tokens = new PsScanner(bytes).ReadAll();
            if (tokens.Count == 0) empty.Add(name);
        }

        Assert.Empty(empty);
    }

    [Fact]
    public void Corpus_ParsesWithoutThrowing_AndEveryResourceYieldsObjects()
    {
        var empty = new List<string>();

        foreach (var (name, bytes) in LoadCorpus())
        {
            PostScriptDiagnostics.Clear();
            var parser = new PsParser(new PsScanner(bytes));
            var objects = parser.ReadAll();
            if (objects.Count == 0) empty.Add(name);
        }

        Assert.Empty(empty);
    }

    /// <summary>
    /// Every CMap resource contains the executable name <c>begincmap</c>. Finding it as a *token of
    /// the right kind* — rather than as a substring — is what proves the scanner segmented the file
    /// correctly rather than producing plausible-looking garbage.
    /// </summary>
    [Fact]
    public void Corpus_BeginCmapIsScannedAsAnExecutableName()
    {
        var missing = new List<string>();
        int checkedCount = 0;

        foreach (var (name, bytes) in LoadCorpus())
        {
            // usecmap-only stubs legitimately lack begincmap; only check files that contain it.
            if (Encoding.ASCII.GetString(bytes).IndexOf("begincmap", StringComparison.Ordinal) < 0)
                continue;

            checkedCount++;
            PostScriptDiagnostics.Clear();
            var tokens = new PsScanner(bytes).ReadAll();

            bool found = tokens.Any(t => t.Kind == PsTokenKind.ExecutableName
                                      && t.Text == "begincmap");
            if (!found) missing.Add(name);
        }

        Assert.True(checkedCount > 100,
            $"expected hundreds of resources to contain begincmap, checked {checkedCount}");
        Assert.Empty(missing);
    }

    /// <summary>
    /// CMaps are full of <c>&lt;hex&gt;</c> codespace and cid ranges. Every one must scan as a
    /// String token — if any came back as a dictionary delimiter or a name, the &lt;&lt;-vs-&lt;
    /// disambiguation is wrong.
    /// </summary>
    [Fact]
    public void Corpus_HexStringsScanAsStringsNotAsDictDelimiters()
    {
        int hexTokens = 0;
        int dictOpens = 0;

        foreach (var (_, bytes) in LoadCorpus())
        {
            PostScriptDiagnostics.Clear();
            foreach (var t in new PsScanner(bytes).ReadAll())
            {
                if (t.Kind == PsTokenKind.String) hexTokens++;
                else if (t.Kind == PsTokenKind.DictOpen) dictOpens++;
            }
        }

        // The corpus is overwhelmingly hex ranges; a handful of resources do use << >> for their
        // CIDSystemInfo dict, so both counts are non-zero and the strings vastly outnumber them.
        Assert.True(hexTokens > 10_000, $"only {hexTokens} string tokens across the corpus");
        Assert.True(hexTokens > dictOpens * 10,
            $"{dictOpens} dict-opens vs {hexTokens} strings — '<' disambiguation looks wrong");
    }
}
