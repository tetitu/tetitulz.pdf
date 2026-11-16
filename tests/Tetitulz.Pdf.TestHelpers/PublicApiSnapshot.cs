// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace Tetitulz.Pdf.TestHelpers;

/// <summary>
/// Renders an assembly's <b>exported</b> surface as a sorted, line-per-member text document, and
/// compares it against a checked-in baseline.
///
/// <para><b>Why this exists.</b> Before it, nothing in <c>tests/</c> snapshotted exported types —
/// the suite had rendering-parity and diagnostic-surface tests, but no way to answer "did that
/// refactor change the public API?". Every "provably behaviour-preserving" claim about a file
/// split, a type promotion or a namespace move was therefore an assertion rather than a test
/// result. During a large reorganisation that is the single most useful guard available: a
/// mechanical move that changes nothing shows an empty diff, and a move that changes something
/// shows exactly what.</para>
///
/// <para><b>It is deliberately a two-way check.</b> An accidental break fails; so does an
/// <i>intended</i> break until the baseline is updated in the same commit. That is the point —
/// the baseline diff becomes the migration table, written by the compiler rather than by hand.</para>
///
/// <para><b>Protected members are included.</b> They are part of the contract for anyone
/// subclassing an exported type, so dropping one is a break even though it is not <c>public</c>.
/// Property and event accessors (<c>get_</c>/<c>set_</c>/<c>add_</c>/<c>remove_</c>) are excluded
/// because the property or event line already covers them; listing both doubles the file for no
/// added signal.</para>
/// </summary>
public static class PublicApiSnapshot
{
    /// <summary>Where the baselines live, relative to the repository root.</summary>
    private const string BaselineDirectory = "tests/ApiSnapshots";

    /// <summary>
    /// The exported surface of <paramref name="assembly"/>, one member per line, sorted with
    /// <see cref="StringComparer.Ordinal"/> so the file is stable across runs, machines and
    /// reflection orderings (which are not specified).
    /// </summary>
    public static string Render(Assembly assembly)
    {
        var lines = new List<string>();

        foreach (var type in assembly.GetExportedTypes())
        {
            // A nested type is reached through its declaring type's listing, and reflection
            // reports it separately as well; skip the duplicate.
            if (type.IsNested) continue;
            AppendType(lines, type);
        }

        lines.Sort(StringComparer.Ordinal);
        var sb = new StringBuilder();
        foreach (string line in lines) sb.Append(line).Append('\n');
        return sb.ToString();
    }

    private static void AppendType(List<string> lines, Type type)
    {
        lines.Add($"{Kind(type)} {Name(type)}");

        foreach (var member in type.GetMembers(BindingFlags.Public | BindingFlags.NonPublic |
                                               BindingFlags.Instance | BindingFlags.Static |
                                               BindingFlags.DeclaredOnly))
        {
            if (!IsExported(member)) continue;

            string? rendered = member switch
            {
                ConstructorInfo c => $"{Name(type)}::.ctor({Parameters(c.GetParameters())})",
                MethodInfo m when !IsAccessor(m) =>
                    $"{Name(type)}::{m.Name}{Generics(m)}({Parameters(m.GetParameters())}) : {Name(m.ReturnType)}",
                PropertyInfo p =>
                    $"{Name(type)}::{p.Name}{Indexer(p)} : {Name(p.PropertyType)} {{ {Accessors(p)} }}",
                FieldInfo f => $"{Name(type)}::{f.Name} : {Name(f.FieldType)}{Constant(f)}",
                EventInfo e => $"{Name(type)}::{e.Name} : {Name(e.EventHandlerType!)} (event)",
                Type nested => null,   // handled by the recursion below
                _ => null,
            };

            if (rendered != null) lines.Add(rendered);
            if (member is Type nestedType && IsExported(nestedType)) AppendType(lines, nestedType);
        }
    }

    // ------------------------------------------------------------------ visibility

    private static bool IsExported(MemberInfo member) => member switch
    {
        // `IsFamilyAndAssembly` is `private protected` — invisible outside the assembly, so not
        // part of the surface. `IsFamilyOrAssembly` is `protected internal`, which a subclass in
        // another assembly *can* see, so it counts.
        ConstructorInfo c => c.IsPublic || c.IsFamily || c.IsFamilyOrAssembly,
        MethodInfo m => m.IsPublic || m.IsFamily || m.IsFamilyOrAssembly,
        FieldInfo f => f.IsPublic || f.IsFamily || f.IsFamilyOrAssembly,
        PropertyInfo p => IsExportedAccessor(p.GetMethod) || IsExportedAccessor(p.SetMethod),
        EventInfo e => IsExportedAccessor(e.AddMethod),
        Type t => t.IsPublic || t.IsNestedPublic || t.IsNestedFamily || t.IsNestedFamORAssem,
        _ => false,
    };

    private static bool IsExportedAccessor(MethodInfo? m) =>
        m != null && (m.IsPublic || m.IsFamily || m.IsFamilyOrAssembly);

    private static bool IsAccessor(MethodInfo m) =>
        m.IsSpecialName &&
        (m.Name.StartsWith("get_", StringComparison.Ordinal) ||
         m.Name.StartsWith("set_", StringComparison.Ordinal) ||
         m.Name.StartsWith("add_", StringComparison.Ordinal) ||
         m.Name.StartsWith("remove_", StringComparison.Ordinal));

    // ------------------------------------------------------------------ rendering

    private static string Kind(Type t) =>
        t.IsEnum ? "enum" :
        t.IsInterface ? "interface" :
        t.IsValueType ? "struct" :
        "class";

    /// <summary>
    /// A stable, namespace-qualified name. Generic arity is spelled out (<c>List&lt;T&gt;</c>
    /// rather than <c>List`1</c>) so a signature change is legible in the diff rather than
    /// encoded.
    /// </summary>
    private static string Name(Type t)
    {
        if (t.IsByRef) return Name(t.GetElementType()!) + "&";
        if (t.IsArray) return Name(t.GetElementType()!) + "[" + new string(',', t.GetArrayRank() - 1) + "]";
        if (t.IsGenericParameter) return t.Name;

        if (t.IsGenericType)
        {
            string bare = t.GetGenericTypeDefinition().FullName ?? t.Name;
            int tick = bare.IndexOf('`');
            if (tick >= 0) bare = bare.Substring(0, tick);
            return bare + "<" + string.Join(", ", t.GetGenericArguments().Select(Name)) + ">";
        }

        return t.FullName ?? t.Name;
    }

    private static string Generics(MethodInfo m) =>
        m.IsGenericMethodDefinition
            ? "<" + string.Join(", ", m.GetGenericArguments().Select(a => a.Name)) + ">"
            : "";

    private static string Parameters(ParameterInfo[] ps) =>
        string.Join(", ", ps.Select(p =>
            (p.IsOut ? "out " : p.ParameterType.IsByRef ? "ref " : "") +
            Name(p.ParameterType) +
            (p.HasDefaultValue ? " = default" : "")));

    private static string Indexer(PropertyInfo p)
    {
        var ps = p.GetIndexParameters();
        return ps.Length == 0 ? "" : "[" + Parameters(ps) + "]";
    }

    private static string Accessors(PropertyInfo p)
    {
        var parts = new List<string>(2);
        if (IsExportedAccessor(p.GetMethod)) parts.Add("get");
        if (IsExportedAccessor(p.SetMethod)) parts.Add("set");
        return string.Join("; ", parts);
    }

    /// <summary>
    /// A literal constant's value is part of the contract — it is baked into every consumer at
    /// their compile time, so changing one silently desynchronises already-built callers.
    /// </summary>
    private static string Constant(FieldInfo f)
    {
        if (!f.IsLiteral) return "";
        object? v = f.GetRawConstantValue();
        return " = " + (v is string s ? "\"" + s + "\"" : Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture));
    }

    // ------------------------------------------------------------------ baseline comparison

    /// <summary>The checked-in baseline for <paramref name="assemblyName"/>, or <c>null</c> outside a checkout.</summary>
    public static string? BaselinePath(string assemblyName) =>
        TestCorpus.RepoRoot is { } repo
            ? Path.Combine(repo, BaselineDirectory.Replace('/', Path.DirectorySeparatorChar), assemblyName + ".txt")
            : null;

    /// <summary>
    /// Compares <paramref name="assembly"/> against its baseline. Returns <c>null</c> when they
    /// match, else a human-readable report naming every added and removed line.
    ///
    /// <para>On mismatch the actual surface is written beside the baseline as
    /// <c>&lt;assembly&gt;.actual.txt</c>, so accepting an intended change is a file copy rather
    /// than a hand transcription — and so the diff that becomes a migration table is generated,
    /// not typed.</para>
    /// </summary>
    public static string? Compare(Assembly assembly)
    {
        string name = assembly.GetName().Name!;
        string? baseline = BaselinePath(name);
        if (baseline == null) return null;   // outside a checkout; the caller's attribute skips

        string actual = Render(assembly);

        if (!File.Exists(baseline))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(baseline)!);
            File.WriteAllText(baseline + ".actual", actual);
            return $"No baseline for {name}. The current surface was written to " +
                   $"'{baseline}.actual'; review it and rename it to '{Path.GetFileName(baseline)}'.";
        }

        // Normalise line endings: the repo has core.autocrlf=true, so a checked-in LF baseline
        // arrives in the worktree as CRLF and a byte comparison would fail for no reason.
        var expectedLines = Split(File.ReadAllText(baseline));
        var actualLines = Split(actual);

        var added = actualLines.Except(expectedLines, StringComparer.Ordinal).ToList();
        var removed = expectedLines.Except(actualLines, StringComparer.Ordinal).ToList();
        if (added.Count == 0 && removed.Count == 0) return null;

        File.WriteAllText(baseline + ".actual", actual);

        var sb = new StringBuilder();
        sb.Append($"The exported surface of {name} differs from its baseline (");
        sb.Append($"{removed.Count} removed, {added.Count} added).\n");
        sb.Append($"If the change is intended, replace '{Path.GetFileName(baseline)}' with the ");
        sb.Append($"'.actual' file written beside it, in the SAME commit as the change.\n");
        foreach (string line in removed.Take(40)) sb.Append("  - ").Append(line).Append('\n');
        if (removed.Count > 40) sb.Append($"  … and {removed.Count - 40} more removed\n");
        foreach (string line in added.Take(40)) sb.Append("  + ").Append(line).Append('\n');
        if (added.Count > 40) sb.Append($"  … and {added.Count - 40} more added\n");
        return sb.ToString();
    }

    private static string[] Split(string text) =>
        text.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
}
