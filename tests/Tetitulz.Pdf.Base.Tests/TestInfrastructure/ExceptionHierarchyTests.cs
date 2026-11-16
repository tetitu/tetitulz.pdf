// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Tetitulz.Pdf.Core;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.TestInfrastructure;

/// <summary>
/// Every public exception this library throws derives from <see cref="PdfException"/>, so
/// <c>catch (PdfException)</c> means what a caller expects it to.
/// </summary>
/// <remarks>
/// <para><b>This guard is reflection because the API baseline structurally cannot do it.</b>
/// <c>PublicApiSnapshot</c> records members and signatures, not base types, so a new exception
/// deriving straight from <c>Exception</c> is invisible to it — the snapshot would show the type
/// arriving and say nothing about what it descends from. Five types had already slipped through
/// that way.</para>
///
/// <para><b>Why it matters beyond tidiness.</b> A host wrapping a parse in
/// <c>catch (PdfException)</c> was not catching a filter bomb or a work-budget refusal, because
/// <c>PdfFilterException</c> and <c>WorkBudgetExceededException</c> sat outside the hierarchy — so
/// a hostile document escaped the handler written to contain it and reached the host's top level.
/// The alternative is every caller enumerating the library's exception types, which is a contract
/// nobody can keep in step.</para>
///
/// <para><b>It scans the loaded assemblies rather than the source</b>, so a type added in any
/// package is covered without this file naming it, and a rename cannot make the guard stop
/// matching.</para>
///
/// <para><b>One package is out of reach, and says so rather than being quietly missed:</b>
/// <c>WinForms.PdfViewer</c> targets <c>net462</c> / <c>net10.0-windows</c>, so this
/// <c>net10.0</c> project cannot reference it and the sweep never loads it.
/// <c>PdfPasswordRequiredException</c> is asserted in that package's own test project instead.
/// A guard with a hole is only dangerous when the hole is undocumented.</para>
/// </remarks>
public class ExceptionHierarchyTests
{
    /// <summary>
    /// Exception types that legitimately stay outside the hierarchy, with the reason.
    /// </summary>
    /// <remarks>
    /// <b>A statement about which types are not library errors, not a burn-down list.</b> Adding an
    /// entry needs a reason written here. It is currently empty, and the five that used to belong
    /// in it were rebased instead — a work-budget refusal, a filter failure, an HTML conversion
    /// failure, an unrenderable-text refusal and a password prompt are all "this library could not
    /// do what you asked", which is precisely what <c>PdfException</c> means.
    /// </remarks>
    private static readonly HashSet<string> OutsideTheHierarchy = new(StringComparer.Ordinal);

    /// <summary>
    /// One type from each package whose exceptions this must see.
    /// </summary>
    /// <remarks>
    /// <b>The sweep reads the loaded assemblies, and .NET loads one the first time a type in it is
    /// touched</b> — so without naming a type per package, a package whose exceptions nothing else
    /// in the run happened to mention is simply absent, and the guard reports a clean sheet over a
    /// tree it never looked at. Found by the floor test below, which noticed the sweep could not
    /// see <c>UnrenderableTextException</c> at all.
    /// </remarks>
    private static readonly Type[] PackageAnchors =
    {
        typeof(PdfException),                                        // Core
        typeof(Tetitulz.Pdf.Base.Operations.PdfActionDispatcher),    // Base
        typeof(Tetitulz.Pdf.Encryption.PdfDecryptor),                // Encryption
        typeof(Tetitulz.Pdf.Builder.Export.UnrenderableTextException),   // Builder
        typeof(Tetitulz.Pdf.HtmlToPdf.HtmlToPdfException),               // HtmlToPdf
    };

    private static IEnumerable<Type> LibraryExceptionTypes()
    {
        // Touching the array is what forces each anchor's assembly to load.
        Assert.NotEmpty(PackageAnchors);

        return AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a.GetName().Name?.StartsWith("Tetitulz.Pdf", StringComparison.Ordinal) == true)
            .Where(a => a.GetName().Name?.EndsWith(".Tests", StringComparison.Ordinal) != true)
            .SelectMany(SafeTypes)
            .Where(t => t.IsPublic && typeof(Exception).IsAssignableFrom(t))
            .Where(t => t != typeof(PdfException));
    }

    private static IEnumerable<Type> SafeTypes(Assembly a)
    {
        try { return a.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null); }
    }

    /// <summary>Every public exception type in a shipped package derives from `PdfException`.</summary>
    [Fact]
    public void EveryPublicExceptionDerivesFromPdfException()
    {
        var offenders = LibraryExceptionTypes()
            .Where(t => !typeof(PdfException).IsAssignableFrom(t))
            .Where(t => !OutsideTheHierarchy.Contains(t.FullName ?? t.Name))
            .Select(t => $"{t.FullName} : {t.BaseType?.Name}")
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        Assert.True(offenders.Count == 0,
            "These public exception types do not derive from PdfException, so a caller's " +
            "catch (PdfException) will not see them:" + Environment.NewLine +
            string.Join(Environment.NewLine, offenders) + Environment.NewLine +
            "Derive from PdfException, or add the type to OutsideTheHierarchy with a reason.");
    }

    /// <summary>
    /// The sweep actually saw exception types — a floor, so a scan that loads nothing cannot read
    /// as a clean bill.
    /// </summary>
    /// <remarks>
    /// The guard's whole value is in the <i>absence</i> of offenders, and an absence is what a
    /// broken scan also produces. The five that prompted this file are named individually below, so
    /// the floor cannot be met by counting unrelated types either.
    /// </remarks>
    [Fact]
    public void TheSweepSeesTheExceptionTypesItIsSupposedTo()
    {
        var seen = LibraryExceptionTypes().Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

        Assert.True(seen.Count >= 5, $"the sweep found only {seen.Count} exception types");

        foreach (string expected in new[]
                 {
                     "PdfFilterException",
                     "WorkBudgetExceededException",
                     "UnrenderableTextException",
                     "HtmlToPdfException",
                 })
        {
            Assert.Contains(expected, seen);
        }
    }

    /// <summary>
    /// The four reachable of the five the pen test named are in the hierarchy now, by name.
    /// </summary>
    /// <remarks>
    /// The sweep above would go quiet if one of these were deleted or made internal; naming them
    /// keeps the specific finding closed rather than merely unreported.
    /// </remarks>
    [Fact]
    public void TheOnesThatBypassedItAreInTheHierarchy()
    {
        Assert.True(typeof(PdfException).IsAssignableFrom(typeof(Tetitulz.Pdf.Core.Util.WorkBudgetExceededException)));
        Assert.True(typeof(PdfException).IsAssignableFrom(typeof(Tetitulz.Pdf.Core.IO.Filters.PdfFilterException)));
        Assert.True(typeof(PdfException).IsAssignableFrom(typeof(Tetitulz.Pdf.HtmlToPdf.HtmlToPdfException)));
        Assert.True(typeof(PdfException).IsAssignableFrom(typeof(Tetitulz.Pdf.Builder.Export.UnrenderableTextException)));
    }

    /// <summary>
    /// <b>The behaviour a caller actually gets:</b> one <c>catch</c> sees them all.
    /// </summary>
    /// <remarks>
    /// The assertions above are about metadata; this is about the language. A base-type change is
    /// only worth anything if <c>catch</c> follows it, and that is a property of the runtime rather
    /// than of <c>IsAssignableFrom</c>.
    /// </remarks>
    [Fact]
    public void OneCatchOfPdfExceptionSeesThemAll()
    {
        var thrown = new Exception[]
        {
            new Tetitulz.Pdf.Core.Util.WorkBudgetExceededException("budget"),
            new Tetitulz.Pdf.Core.IO.Filters.PdfFilterException("filter"),
            new Tetitulz.Pdf.HtmlToPdf.HtmlToPdfException("html"),
            new Tetitulz.Pdf.Builder.Export.UnrenderableTextException("text"),
        };

        foreach (Exception e in thrown)
        {
            bool caught = false;
            try { throw e; }
            catch (PdfException) { caught = true; }
            catch (Exception) { /* deliberately second: this arm must not be the one that runs */ }

            Assert.True(caught, $"{e.GetType().FullName} escaped catch (PdfException)");
        }
    }
}
