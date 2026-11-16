// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Tetitulz.Pdf.Base.Encryption;
using Tetitulz.Pdf.Encryption;
using Tetitulz.Pdf.Signing;
using Tetitulz.Pdf.Signing.Timestamping;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Diagnostics;

/// <summary>
/// Locks in the contract of the <c>Last*</c> diagnostic properties that live outside Core —
/// <c>Tetitulz.Pdf.Encryption</c> and <c>Tetitulz.Pdf.Signing</c>. These pair with the Core-side
/// coverage in <c>tests/Tetitulz.Pdf.Core.Tests/Diagnostics/DiagnosticSurfaceTests.cs</c>.
///
/// <para><b>What this file used to be.</b> Every test here read a property into a local and threw
/// it away — <c>_ = aia;</c> — or asserted <c>v == null || v is string</c>, which the compiler
/// already guarantees. Four tests, none of which could go red. They are rewritten in two shapes:
/// the decryptor's slots are <b>driven</b> (encrypt, decrypt, read the reason back), and the
/// assembly-wide <c>[ThreadStatic]</c> rule CLAUDE.md states for these slots is enforced by
/// <b>reflection over every one of them</b> rather than by naming four by hand.</para>
/// </summary>
public class BaseDiagnosticSurfaceTests
{
    // ==================================================================
    //  PdfDecryptor — driven, not merely read.
    // ==================================================================

    private static byte[] Encrypt(EncryptionAlgorithm algorithm)
    {
        using var input  = new MemoryStream(MinimalPdfBuilder.CreateMinimalPdf("diag"));
        using var output = new MemoryStream();
        PdfEncryptor.Encrypt(input, output, new EncryptionParameters
        {
            UserPassword  = "pw",
            OwnerPassword = "owner",
            Algorithm     = algorithm,
            Permissions   = PdfPermissions.All
        });
        return output.ToArray();
    }

    private static void Decrypt(byte[] encrypted)
    {
        using var input  = new MemoryStream(encrypted);
        using var output = new MemoryStream();
        PdfDecryptor.Decrypt(input, output, "pw");
    }

    /// <summary>
    /// <c>/Perms</c> validation (ISO 32000-2 §7.6.4.3.4, Algorithm 11) applies only to V=5/R=6.
    /// Both documented outcomes are exercised from a real file: AES-256 must report <c>"OK"</c>,
    /// and an RC4 file — which has no <c>/Perms</c> to check — must report
    /// <c>"SkippedNonAes256Key"</c>. Reading the slot after a decryption is the only thing that
    /// distinguishes a working validator from one that never runs.
    ///
    /// <para>The predecessor asserted that whatever value happened to be lying in the slot was one
    /// of the documented strings — <i>if</i> it was non-null. On a fresh thread it is null, so the
    /// body reduced to <c>if (false)</c>.</para>
    /// </summary>
    [Fact]
    public void PdfDecryptor_LastPermsValidation_ReportsTheOutcomeOfThisDecryption()
    {
        Decrypt(Encrypt(EncryptionAlgorithm.AES256));
        Assert.Equal("OK", PdfDecryptor.LastPermsValidation);

        Decrypt(Encrypt(EncryptionAlgorithm.RC4_128));
        Assert.Equal("SkippedNonAes256Key", PdfDecryptor.LastPermsValidation);
    }

    /// <summary>
    /// The crypt-filter slot describes how this document's <c>/StmF</c> and <c>/StrF</c> resolved.
    /// A file this library wrote itself names a real filter for both, so there is nothing to
    /// describe and the honest value is <c>null</c> — falsifiable in the direction that matters: a
    /// decryptor that reported a finding about well-formed input fails here.
    /// </summary>
    /// <remarks>
    /// <b>This summary used to say the slot "exists to say the file's <c>/CF</c> did not resolve and
    /// I guessed".</b> That was the property's own contract until 2026-08-19, when it also began
    /// reporting <c>/Identity</c> and <c>/CFM /None</c> — configurations where nothing fell back —
    /// and began accumulating both keys' findings rather than holding the most recent. The property
    /// was corrected; this copy of the old contract was left behind in the test suite, where the
    /// assertion below stays green either way, so nothing would ever have surfaced it. See
    /// <c>PdfDecryptor.LastCryptFilterFallback</c>'s remarks for what non-null now means.
    /// </remarks>
    [Theory]
    [InlineData(EncryptionAlgorithm.AES128)]
    [InlineData(EncryptionAlgorithm.AES256)]
    public void PdfDecryptor_LastCryptFilterFallback_IsNullForAWellFormedFile(
        EncryptionAlgorithm algorithm)
    {
        Decrypt(Encrypt(algorithm));

        Assert.Null(PdfDecryptor.LastCryptFilterFallback);
    }

    // ==================================================================
    //  The [ThreadStatic] rule, enforced over the whole surface.
    // ==================================================================

    /// <summary>
    /// CLAUDE.md's Diagnostics Pattern makes one hard rule about these slots: <b>every</b> one is
    /// backed by a <c>[ThreadStatic]</c> field, because a plain process-wide static hands a caller
    /// a reason belonging to some other thread's document. Naming four properties by hand — which
    /// is what this file used to do — cannot enforce a rule about all of them: the failure mode is
    /// a <i>newly added</i> diagnostic that forgets the attribute, and a hand-written list never
    /// mentions the property that does not exist yet.
    ///
    /// <para>So: reflect over every public static <c>Last*</c> property in both assemblies, find
    /// its backing field, and require the attribute. Drop one <c>[ThreadStatic]</c> anywhere in
    /// Signing or Encryption and this goes red naming it.</para>
    /// </summary>
    [Theory]
    [InlineData(typeof(DssBuilder))]     // anchors Tetitulz.Pdf.Signing
    [InlineData(typeof(PdfDecryptor))]   // anchors Tetitulz.Pdf.Encryption
    public void EveryLastStarDiagnostic_IsBackedByAThreadStaticField(Type assemblyAnchor)
    {
        var offenders = new List<string>();
        int examined = 0;

        foreach (Type type in assemblyAnchor.Assembly.GetTypes())
        {
            foreach (PropertyInfo prop in type.GetProperties(
                         BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (!prop.Name.StartsWith("Last", StringComparison.Ordinal)) continue;
                if (prop.GetMethod == null) continue;

                FieldInfo backing = BackingFieldOf(type, prop);
                if (backing == null)
                {
                    offenders.Add($"{type.Name}.{prop.Name}: no backing field found");
                    continue;
                }

                examined++;
                if (!backing.IsDefined(typeof(ThreadStaticAttribute), inherit: false))
                    offenders.Add(
                        $"{type.Name}.{prop.Name} (field {backing.Name}) is not [ThreadStatic]");
            }
        }

        // Guard the guard: if discovery ever stops finding properties the sweep would pass
        // vacuously — exactly the failure this file is being rewritten to remove.
        Assert.True(examined > 0,
            $"no Last* diagnostics discovered in {assemblyAnchor.Assembly.GetName().Name}");
        Assert.Empty(offenders);
    }

    /// <summary>
    /// Resolves the static field a <c>Last*</c> property reads. Handles both idioms in the tree:
    /// an explicit <c>_lastXxx</c> field, and a compiler-generated auto-property backing field
    /// (which can never carry <c>[ThreadStatic]</c>, so finding one <i>is</i> the finding).
    /// </summary>
    private static FieldInfo BackingFieldOf(Type type, PropertyInfo prop)
    {
        FieldInfo[] fields = type.GetFields(
            BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly);

        string expected = "_" + char.ToLowerInvariant(prop.Name[0]) + prop.Name.Substring(1);
        return fields.FirstOrDefault(f => f.Name == expected)
            ?? fields.FirstOrDefault(f => f.Name == $"<{prop.Name}>k__BackingField");
    }

    /// <summary>
    /// The sweep is only worth running if a violation is detectable, so this pins the detector
    /// against a deliberately non-conforming stand-in. Without it, a <see cref="BackingFieldOf"/>
    /// that returned <c>null</c> for everything would report a clean bill of health over nothing.
    /// </summary>
    [Fact]
    public void ThreadStaticSweep_DetectsANonThreadStaticSlot()
    {
        PropertyInfo prop = typeof(NotIsolated).GetProperty(nameof(NotIsolated.LastThing));
        FieldInfo backing = BackingFieldOf(typeof(NotIsolated), prop);

        Assert.NotNull(backing);
        Assert.False(backing!.IsDefined(typeof(ThreadStaticAttribute), inherit: false));
    }

    private static class NotIsolated
    {
        public static string LastThing { get; set; }
    }

    // ==================================================================
    //  TsaClient.LastVerifiedNonce — the one slot needing a network TSA.
    // ==================================================================

    /// <summary>
    /// Populating this needs a live RFC 3161 round-trip, so its <i>value</i> is out of reach here;
    /// the sweep above already covers its isolation. What is checkable offline is the shape callers
    /// depend on: an audit pipeline compares it against the nonce it sent, so it must expose the
    /// bytes and must have <b>no public setter</b> — a settable slot would let the conclusion
    /// "the response was bound to my request" be forged by any code in the process.
    /// </summary>
    [Fact]
    public void TsaClient_LastVerifiedNonce_IsAReadOnlyByteArraySurface()
    {
        PropertyInfo prop = typeof(TsaClient).GetProperty(nameof(TsaClient.LastVerifiedNonce),
            BindingFlags.Public | BindingFlags.Static);

        Assert.NotNull(prop);
        Assert.Equal(typeof(byte[]), prop!.PropertyType);
        Assert.Null(prop.GetSetMethod(nonPublic: false));
    }
}
