// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.TestInfrastructure;

/// <summary>
/// Enforces the ring model described in CLAUDE.md against the <b>shipped</b>
/// <c>ProjectReference</c> graph.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> Every other invariant in this repository that mattered got a
/// build-failing scanner — <c>NoInlineCmykConversionTests</c>,
/// <c>NoRedundantPdfNameAllocationTests</c>, <c>NoUnfalsifiableAssertionsTests</c>,
/// <c>PublicApiSurfaceTests</c> — but the architecture itself was doc-and-review enforced only.
/// The cost of that showed up in the 2026-08-12 boundary review (GAPS.md §15): a
/// <c>Plugins.Conformance</c> → <c>Plugins.Maintenance</c> reference had been sitting in the tree
/// unnoticed, in violation of a rule stated in two places; <c>Base</c> had accumulated a full
/// JavaScript interpreter; and CLAUDE.md's own rule text had drifted away from what the graph
/// actually did. All three were mechanically detectable.
/// </para>
/// <para>
/// <b>The invariant is acyclicity, not direction.</b> <c>Plugins.Docx</c> →
/// <c>Tetitulz.Pdf.DocxToPdf</c> and <c>Plugins.Html</c> → <c>Tetitulz.Pdf.HtmlToPdf</c> are
/// plugin → feature edges and are <i>correct</i>, because neither converter references a plugin.
/// So this guard checks cycles plus the specific hard invariants, rather than banning a direction
/// wholesale.
/// </para>
/// </remarks>
public class AssemblyLayeringTests
{
    private sealed record Proj(string Name, string Ring, IReadOnlyList<string> ProjectRefs,
                               IReadOnlyList<string> PackageRefs, string Dir);

    private static string? SrcRoot =>
        TestCorpus.RepoRoot is { } repo && Directory.Exists(Path.Combine(repo, "src"))
            ? Path.Combine(repo, "src")
            : null;

    private static List<Proj> LoadProjects()
    {
        var result = new List<Proj>();
        foreach (var csproj in Directory.EnumerateFiles(SrcRoot!, "*.csproj", SearchOption.AllDirectories))
        {
            string norm = csproj.Replace('\\', '/');
            if (norm.Contains("/obj/") || norm.Contains("/bin/")) continue;

            var doc = XDocument.Load(csproj);
            var projRefs = doc.Descendants("ProjectReference")
                .Select(e => (string?)e.Attribute("Include"))
                .Where(s => !string.IsNullOrEmpty(s))
                .Select(s => Path.GetFileNameWithoutExtension(s!.Replace('\\', '/')))
                .ToList();
            var pkgRefs = doc.Descendants("PackageReference")
                .Select(e => (string?)e.Attribute("Include"))
                .Where(s => !string.IsNullOrEmpty(s))
                .Select(s => s!)
                .ToList();

            string ring =
                norm.Contains("/src/plugins/") ? "plugin" :
                norm.Contains("/src/features/") ? "feature" : "core-ring";

            result.Add(new Proj(Path.GetFileNameWithoutExtension(csproj), ring,
                                projRefs, pkgRefs, Path.GetDirectoryName(csproj)!));
        }
        return result;
    }

    /// <summary>
    /// The project-reference graph must be a DAG. A cycle is not merely untidy: it does not build,
    /// and the near-misses this repo has had (Accessibility ↔ Conformance) were each argued about
    /// in prose rather than checked.
    /// </summary>
    [RepoSourceFact]
    public void Project_reference_graph_has_no_cycles()
    {
        var projects = LoadProjects();
        var byName = projects.ToDictionary(p => p.Name, StringComparer.Ordinal);
        var state = new Dictionary<string, int>(StringComparer.Ordinal); // 0=unseen 1=onstack 2=done
        var cycles = new List<string>();

        void Visit(string name, List<string> stack)
        {
            if (!byName.TryGetValue(name, out var proj)) return;   // outside src/, e.g. a test project
            state.TryGetValue(name, out int s);
            if (s == 2) return;
            if (s == 1)
            {
                int at = stack.IndexOf(name);
                cycles.Add(string.Join(" -> ", stack.Skip(at < 0 ? 0 : at)) + " -> " + name);
                return;
            }
            state[name] = 1;
            stack.Add(name);
            foreach (var r in proj.ProjectRefs) Visit(r, stack);
            stack.RemoveAt(stack.Count - 1);
            state[name] = 2;
        }

        foreach (var p in projects) Visit(p.Name, new List<string>());

        Assert.True(projects.Count >= 20,
            $"Only {projects.Count} src/ projects were found; the scan is wrong, not the tree.");
        Assert.True(cycles.Count == 0,
            "The project-reference graph must be acyclic:\n  " + string.Join("\n  ", cycles.Distinct()));
    }

    /// <summary>
    /// Two plugins are peers, so an edge between them makes each other's package graph
    /// unpredictable and there is always a downward home for the shared code. Live prey when this
    /// was written: <c>Plugins.Conformance</c> → <c>Plugins.Maintenance</c>, closed by moving
    /// <c>PdfSanitizer</c> down, to <c>Core.Document</c> (GAPS.md §15a).
    /// </summary>
    [RepoSourceFact]
    public void No_plugin_references_another_plugin()
    {
        var projects = LoadProjects();
        var plugins = projects.Where(p => p.Ring == "plugin").ToList();
        var pluginNames = plugins.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

        var offences = plugins
            .SelectMany(p => p.ProjectRefs.Where(pluginNames.Contains).Select(r => $"{p.Name} -> {r}"))
            .ToList();

        Assert.True(plugins.Count >= 10,
            $"Only {plugins.Count} plugins were found; the scan is wrong, not the tree.");
        Assert.True(offences.Count == 0,
            "Plugin -> plugin references are forbidden; move the shared code down into a ring both " +
            "already reference (see Core.Writing.IncrementalUpdateWriter and " +
            "Core.Document.PdfSanitizer for the precedent):\n  " +
            string.Join("\n  ", offences));
    }

    /// <summary>
    /// The core ring is the bottom of the graph: it may never reference a feature or a plugin. This
    /// is the ring inversion that the plugin-SDK move closed, and nothing had been watching it since.
    /// </summary>
    [RepoSourceFact]
    public void Core_ring_references_no_feature_and_no_plugin()
    {
        var projects = LoadProjects();
        var byName = projects.ToDictionary(p => p.Name, StringComparer.Ordinal);

        var offences = new List<string>();
        foreach (var p in projects.Where(p => p.Ring == "core-ring"))
            foreach (var r in p.ProjectRefs)
                if (byName.TryGetValue(r, out var target) && target.Ring != "core-ring")
                    offences.Add($"{p.Name} -> {r} ({target.Ring})");

        Assert.True(offences.Count == 0,
            "A core-ring assembly (src/tetitulz.pdf/) must not reference a feature or a plugin:\n  " +
            string.Join("\n  ", offences));
    }

    /// <summary>
    /// The hard invariants that keep the sanctioned exceptions from becoming cycles:
    /// <c>Builder</c> references no other feature; <c>Plugins.Conformance</c> references no feature
    /// (because <c>Accessibility</c> → <c>Conformance</c> already exists); <c>Plugins.PostScript</c>
    /// references no feature (because <c>Printing</c>, in tetitulz.pdf-apps, references it).
    /// </summary>
    [RepoSourceFact]
    public void Sanctioned_exceptions_keep_their_hard_invariants()
    {
        var projects = LoadProjects();
        var byName = projects.ToDictionary(p => p.Name, StringComparer.Ordinal);
        var offences = new List<string>();

        var builder = byName.GetValueOrDefault("Tetitulz.Pdf.Builder");
        Assert.NotNull(builder);
        foreach (var r in builder!.ProjectRefs)
            if (byName.TryGetValue(r, out var t) && t.Ring == "feature")
                offences.Add($"Builder -> {r}: Builder must never reference another feature.");

        var conformance = byName.GetValueOrDefault("Tetitulz.Pdf.Plugins.Conformance");
        Assert.NotNull(conformance);
        foreach (var r in conformance!.ProjectRefs)
            if (byName.TryGetValue(r, out var t) && t.Ring == "feature")
                offences.Add($"Conformance -> {r}: Conformance must never reference a feature " +
                             "(Accessibility -> Conformance exists, so this would cycle).");

        var postScript = byName.GetValueOrDefault("Tetitulz.Pdf.Plugins.PostScript");
        Assert.NotNull(postScript);
        foreach (var r in postScript!.ProjectRefs)
            if (byName.TryGetValue(r, out var t) && t.Ring == "feature")
                offences.Add($"PostScript -> {r}: PostScript must never reference a feature " +
                             "(Printing, in tetitulz.pdf-apps, references PostScript).");

        Assert.True(offences.Count == 0, string.Join("\n  ", offences));
    }

    /// <summary>
    /// Dependencies that must not reappear where they were deliberately removed. Each entry is a
    /// decision with a written reason, not a style preference.
    /// </summary>
    [RepoSourceFact]
    public void Banned_packages_are_absent_from_the_projects_they_were_removed_from()
    {
        var banned = new (string Project, string Package, string Why)[]
        {
            // Core is UI-free and renderer-free by definition (CLAUDE.md, "Core ring").
            ("Tetitulz.Pdf.Core", "SkiaSharp",
             "Core must not use SkiaSharp."),
            // GAPS.md §15b: a full ECMAScript interpreter in Base put itself into the package graph
            // of every consumer. The engine lives in Tetitulz.Pdf.Plugins.JavaScript.
            ("Tetitulz.Pdf.Base", "Jint",
             "the JavaScript engine is Tetitulz.Pdf.Plugins.JavaScript (GAPS.md §15b)."),
            // CLAUDE.md: removed with its only consumer on 2026-08-07 and must not come back -
            // a PDF reader must not shape text.
            ("Tetitulz.Pdf.Base", "SkiaSharp.HarfBuzz",
             "neither renderer shapes text; see the shaping box in CLAUDE.md."),
        };

        var projects = LoadProjects();
        var offences = new List<string>();
        foreach (var (name, package, why) in banned)
        {
            var proj = projects.SingleOrDefault(p => p.Name == name);
            Assert.True(proj != null, $"Project {name} was not found; the scan is wrong, not the tree.");
            if (proj!.PackageRefs.Contains(package, StringComparer.OrdinalIgnoreCase))
                offences.Add($"{name} must not reference {package}: {why}");
        }

        Assert.True(offences.Count == 0, string.Join("\n  ", offences));
    }

    /// <summary>
    /// A project that <c>using</c>s a third-party namespace must declare that package itself, rather
    /// than relying on it arriving transitively through a ProjectReference. A transitive dependency
    /// is invisible in the csproj and silently changes when the intermediate project's references
    /// change.
    /// </summary>
    /// <remarks>
    /// The <c>Plugins.Maintenance</c> csproj carries a comment recording this exact fix for
    /// SkiaSharp, which is why the rule is precedented rather than invented here.
    /// </remarks>
    [RepoSourceFact]
    public void Third_party_namespaces_are_declared_by_the_project_that_uses_them()
    {
        // Curated: only namespaces whose owning package is unambiguous.
        var namespaceToPackage = new (string Ns, string Package)[]
        {
            ("Jint",                    "Jint"),
            ("SkiaSharp",               "SkiaSharp"),
            ("Tesseract",               "Tesseract"),
            ("AngleSharp",              "AngleSharp"),
            ("Microsoft.Playwright",    "Microsoft.Playwright"),
            ("DocumentFormat.OpenXml",  "DocumentFormat.OpenXml"),
            ("Nethereum",               "Nethereum.Web3"),
        };

        var offences = new List<string>();
        foreach (var proj in LoadProjects())
        {
            var sources = Directory.EnumerateFiles(proj.Dir, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Replace('\\', '/').Contains("/obj/") &&
                            !f.Replace('\\', '/').Contains("/bin/"))
                .ToList();

            foreach (var (ns, package) in namespaceToPackage)
            {
                var pattern = new Regex($@"^\s*using\s+(static\s+)?{Regex.Escape(ns)}\s*[;.]",
                                        RegexOptions.Multiline);
                bool used = sources.Any(f => pattern.IsMatch(File.ReadAllText(f)));
                if (used && !proj.PackageRefs.Contains(package, StringComparer.OrdinalIgnoreCase))
                    offences.Add($"{proj.Name} uses namespace '{ns}' but does not declare " +
                                 $"PackageReference '{package}' (it arrives transitively).");
            }
        }

        Assert.True(offences.Count == 0, string.Join("\n  ", offences));
    }
}
