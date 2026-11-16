// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Core.Plugins;
using Tetitulz.Pdf.Plugins.Conformance;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Conformance.Tests;

/// <summary>
/// Reaching the PDF/A and PDF/X converters from <c>PdfOperations</c>.
/// </summary>
/// <remarks>
/// <para><b>Why this needed a contract at all.</b> The converters live in a plugin that references
/// <c>Base</c>, so <c>Base</c> cannot reference them back — dispatch through <c>PdfPluginHost</c> is
/// the only direction that does not close a cycle. Until <see cref="IConformanceConversionPlugin"/>
/// existed they were reachable only by taking a direct dependency on this package and naming
/// <c>PdfAConverter</c> by hand, which works and which nothing in the facade could tell you about.
/// GAPS.md recorded that as a decline; §8d reclassified it as ordinary work once the same dispatch
/// pattern had been established for validation.</para>
///
/// <para><b>These register plugins, so the class joins <c>PluginHostSerialCollection</c></b> — this
/// project's own serial collection, because there are no cross-project test dependencies and
/// <c>Base.Tests</c>' equivalent is unreachable from here. The same dependency rule that shapes the
/// production code shapes where its test can live.</para>
/// </remarks>
[Collection(nameof(PluginHostSerialCollection))]
public class ConformanceConversionPluginTests : IDisposable
{
    public ConformanceConversionPluginTests() => PdfPluginHost.Instance.ResetForTesting();
    public void Dispose() => PdfPluginHost.Instance.ResetForTesting();

    /// <summary>
    /// <b>With nothing registered, the facade says so rather than returning a quiet null.</b>
    /// </summary>
    /// <remarks>
    /// A null with no reason is indistinguishable from "the document needed no conversion", which is
    /// the failure this slot exists to prevent — the same one the validation path was given a
    /// <c>LastValidationError</c> for.
    /// </remarks>
    [Fact]
    public void WithNoPluginRegisteredTheFacadeSaysWhyRatherThanReturningAQuietNull()
    {
        using var input = new MemoryStream(MinimalPdf());
        using var output = new MemoryStream();

        var result = PdfOperations.ConvertToConformance(input, output, "PDF/A-2b");

        Assert.Null(result);
        Assert.NotNull(PdfOperations.LastConversionError);
        Assert.Contains("No conformance-conversion plugin is registered",
            PdfOperations.LastConversionError, StringComparison.Ordinal);
        Assert.Empty(PdfOperations.GetSupportedConversionTargets());
    }

    /// <summary>
    /// <b>An unclaimed target names the ones that <i>are</i> registered</b>, so a typo is a
    /// diagnosable mistake rather than a silent no-op.
    /// </summary>
    [Fact]
    public void AnUnclaimedTargetNamesTheTargetsThatAreRegistered()
    {
        PdfPluginHost.Instance.Register(new ConformanceConversionPlugin("PDF/A-2b"));
        using var input = new MemoryStream(MinimalPdf());
        using var output = new MemoryStream();

        var result = PdfOperations.ConvertToConformance(input, output, "PDF/A-9z");

        Assert.Null(result);
        Assert.Contains("PDF/A-2b", PdfOperations.LastConversionError, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Every advertised target converts and reports its own name back.</b>
    /// </summary>
    /// <remarks>
    /// The target is asserted on the result rather than only on the registration, because the
    /// PDF/X branch sets the converter's <c>Level</c> from the target string — so a mis-wired switch
    /// would convert to the wrong level while the registration still looked right.
    /// </remarks>
    [Theory]
    [InlineData("PDF/A-1b")]
    [InlineData("PDF/A-2b")]
    [InlineData("PDF/A-3b")]
    [InlineData("PDF/X-1a")]
    [InlineData("PDF/X-3")]
    [InlineData("PDF/X-4")]
    public void EveryAdvertisedTargetConvertsAndReportsItsOwnName(string target)
    {
        ConformanceConversionPlugin.RegisterAll(PdfPluginHost.Instance);
        using var input = new MemoryStream(MinimalPdf());
        using var output = new MemoryStream();

        var result = PdfOperations.ConvertToConformance(input, output, target);

        Assert.NotNull(result);
        Assert.Equal(target, result.TargetConformanceLevel);
        Assert.True(result.OutputWritten, "nothing was written to the output stream");
        Assert.True(output.Length > 0, "OutputWritten was true but the stream is empty");
        Assert.Null(PdfOperations.LastConversionError);
    }

    /// <summary>
    /// <b>`RegisterAll` advertises exactly what it can produce</b>, and the facade lists it.
    /// </summary>
    /// <remarks>
    /// Advertising a target nothing converts to would be discoverable through
    /// <c>GetSupportedConversionTargets</c> and then fail at the call — which is why PDF/A-4 is
    /// absent from the list even though <c>PdfAConformancePart</c> has a <c>Part4</c>.
    /// </remarks>
    [Fact]
    public void RegisterAllAdvertisesExactlyWhatItCanProduce()
    {
        ConformanceConversionPlugin.RegisterAll(PdfPluginHost.Instance);

        var advertised = PdfOperations.GetSupportedConversionTargets();

        Assert.Equal(ConformanceConversionPlugin.SupportedTargets.OrderBy(s => s, StringComparer.Ordinal),
                     advertised.OrderBy(s => s, StringComparer.Ordinal));
        Assert.DoesNotContain("PDF/A-4", advertised);
    }

    /// <summary>
    /// <b>A target the plugin cannot produce is refused at construction, by name.</b>
    /// </summary>
    [Fact]
    public void AnUnsupportedTargetIsRefusedAtConstruction()
    {
        var ex = Assert.Throws<ArgumentException>(() => new ConformanceConversionPlugin("PDF/A-4"));

        Assert.Contains("PDF/A-2b", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>The target spelling is case-insensitive, at construction and at dispatch.</summary>
    [Fact]
    public void TheTargetSpellingIsCaseInsensitive()
    {
        PdfPluginHost.Instance.Register(new ConformanceConversionPlugin("pdf/a-2B"));
        using var input = new MemoryStream(MinimalPdf());
        using var output = new MemoryStream();

        var result = PdfOperations.ConvertToConformance(input, output, "PDF/A-2b");

        Assert.NotNull(result);
        // Canonicalised, so a caller comparing it against a validator's level matches.
        Assert.Equal("PDF/A-2b", result.TargetConformanceLevel);
    }

    /// <summary>
    /// <b>Convert, then validate the output with the same string</b> — the sequence the contract's
    /// shared vocabulary exists for.
    /// </summary>
    /// <remarks>
    /// A converter's own <c>IsConformant</c> is its opinion of its own work, which is CLAUDE.md's
    /// third testing blind spot: a round trip through one implementation cannot see an error that
    /// implementation makes consistently. The validator is a separately written second opinion, and
    /// the two vocabularies were made identical so a caller can reach it without a lookup table.
    /// This asserts the two answers *exist and agree in shape*, not that a minimal fixture becomes
    /// conformant — it cannot, having no fonts to embed or profile to declare.
    /// </remarks>
    [Fact]
    public void TheConvertedOutputCanBeValidatedWithTheSameLevelString()
    {
        ConformanceConversionPlugin.RegisterAll(PdfPluginHost.Instance);
        PdfXValidationPlugin.RegisterAll(PdfPluginHost.Instance);

        using var input = new MemoryStream(MinimalPdf());
        using var converted = new MemoryStream();

        var conversion = PdfOperations.ConvertToConformance(input, converted, "PDF/X-4");
        Assert.NotNull(conversion);

        converted.Position = 0;
        var validation = PdfOperations.Validate(converted, "PDF/X-4");

        Assert.NotNull(validation);
        Assert.Equal("PDF/X-4", validation.ConformanceLevel);
        // Both answered about the same level; whether the document conforms is the document's
        // business and this fixture's answer would be a fixture fact, not a contract fact.
        Assert.Equal(conversion.TargetConformanceLevel, validation.ConformanceLevel);
    }

    /// <summary>
    /// <b>The level actually produced follows the target, not just the label reported back.</b>
    /// </summary>
    /// <remarks>
    /// <para><b>This is the assertion the first version of these tests was missing.</b> They checked
    /// <c>result.TargetConformanceLevel</c>, which is the <i>advertised</i> name — so wiring every
    /// PDF/A target to <c>ConvertToPdfA1b</c> changed nothing any of them looked at, and the
    /// mutation survived. The label is the plugin repeating its own input back.</para>
    ///
    /// <para>ISO 19005 puts the part number in the XMP as <c>pdfaid:part</c>, and requires that
    /// stream to be readable without decoding — so the produced bytes carry the answer.</para>
    /// </remarks>
    /// <para><b>The fixture needs <c>DeclareConformanceRegardless</c> to reach the declaration.</b>
    /// A minimal PDF has no ICC profile to build an output intent from, so the converter withholds
    /// the identifier rather than stamping a claim it cannot support — correctly, and it means the
    /// obvious fixture never reaches the line under test. Supplying the option through the
    /// plugin's options constructor is what puts the part number in the output.</para>
    [Theory]
    [InlineData("PDF/A-1b", "1")]
    [InlineData("PDF/A-2b", "2")]
    [InlineData("PDF/A-3b", "3")]
    public void ThePdfAPartInTheOutputFollowsTheTarget(string target, string expectedPart)
    {
        PdfPluginHost.Instance.Register(new ConformanceConversionPlugin(target,
            new PdfAConversionOptions { DeclareConformanceRegardless = true }, null));
        using var input = new MemoryStream(MinimalPdf());
        using var output = new MemoryStream();

        PdfOperations.ConvertToConformance(input, output, target);

        string produced = Encoding.UTF8.GetString(output.ToArray());
        var m = System.Text.RegularExpressions.Regex.Match(produced, @"pdfaid:part[^0-9]{0,24}(\d)");
        Assert.True(m.Success, "the output declares no pdfaid:part at all");
        Assert.Equal(expectedPart, m.Groups[1].Value);
    }

    /// <summary>
    /// The same for PDF/X, whose level lands in <c>GTS_PDFXVersion</c>.
    /// </summary>
    /// <remarks>
    /// The plugin sets the converter's <c>Level</c> <i>from</i> the target string rather than
    /// reading it off the options, so the two cannot disagree — and this is what proves it.
    /// </remarks>
    [Theory]
    [InlineData("PDF/X-1a", "PDF/X-1a")]
    [InlineData("PDF/X-3", "PDF/X-3")]
    [InlineData("PDF/X-4", "PDF/X-4")]
    public void ThePdfXVersionInTheOutputFollowsTheTarget(string target, string expectedToken)
    {
        PdfPluginHost.Instance.Register(new ConformanceConversionPlugin(target, null,
            new PdfXConversionOptions { DeclareConformanceRegardless = true }));
        using var input = new MemoryStream(MinimalPdf());
        using var output = new MemoryStream();

        PdfOperations.ConvertToConformance(input, output, target);

        string produced = Encoding.UTF8.GetString(output.ToArray());
        Assert.Contains("GTS_PDFXVersion", produced, StringComparison.Ordinal);
        Assert.Contains(expectedToken, produced, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>A success clears the previous failure's reason.</b>
    /// </summary>
    /// <remarks>
    /// The slot is <c>[ThreadStatic]</c> and these run in a serial collection, so a reason left
    /// behind is read by the next call as its own. The first assertion is the potency half:
    /// without it, "the reason is null" is equally satisfied by a slot that was never set.
    /// </remarks>
    [Fact]
    public void ASuccessClearsThePreviousFailuresReason()
    {
        using var first = new MemoryStream(MinimalPdf());
        using var firstOut = new MemoryStream();
        Assert.Null(PdfOperations.ConvertToConformance(first, firstOut, "PDF/A-2b"));
        Assert.NotNull(PdfOperations.LastConversionError);

        ConformanceConversionPlugin.RegisterAll(PdfPluginHost.Instance);
        using var second = new MemoryStream(MinimalPdf());
        using var secondOut = new MemoryStream();

        Assert.NotNull(PdfOperations.ConvertToConformance(second, secondOut, "PDF/A-2b"));
        Assert.Null(PdfOperations.LastConversionError);
    }

    /// <summary>
    /// <b>The facade matches a target case-insensitively against whatever a plugin advertises.</b>
    /// </summary>
    /// <remarks>
    /// <c>ConformanceConversionPlugin</c> canonicalises its own target, which makes the facade's
    /// comparison redundant <i>for that plugin</i> — so a test using it cannot see the facade's
    /// rule at all. This registers a plugin that advertises a non-canonical spelling, which is what
    /// any third-party implementation of the contract is free to do.
    /// </remarks>
    [Fact]
    public void TheFacadeMatchesATargetCaseInsensitivelyAgainstWhateverAPluginAdvertises()
    {
        PdfPluginHost.Instance.Register(new OddlyCasedConversionPlugin());
        using var input = new MemoryStream(MinimalPdf());
        using var output = new MemoryStream();

        var result = PdfOperations.ConvertToConformance(input, output, "PDF/A-2b");

        Assert.NotNull(result);
        Assert.Equal("pDf/a-2B", result.TargetConformanceLevel);
    }

    /// <summary>A contract implementation that advertises a target in an unusual case.</summary>
    private sealed class OddlyCasedConversionPlugin : IConformanceConversionPlugin
    {
        public string TargetConformanceLevel => "pDf/a-2B";
        public string Name        => "Test.OddlyCasedConversion";
        public string Description => "Advertises a non-canonical target spelling.";
        public Version Version    => new Version(1, 0, 0);
        public void Initialize() { }
        public void Shutdown()   { }

        public PluginConversionResult Convert(Stream input, Stream output)
            => new PluginConversionResult
            {
                TargetConformanceLevel = TargetConformanceLevel,
                OutputWritten          = false,
            };
    }

    // ------------------------------------------------------------------
    //  Fixture
    // ------------------------------------------------------------------

    private static byte[] MinimalPdf()
    {
        var bodies = new List<string>
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>\nendobj\n",
        };

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        foreach (string b in bodies) { offsets.Add(sb.Length); sb.Append(b); }

        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n")
          .Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
