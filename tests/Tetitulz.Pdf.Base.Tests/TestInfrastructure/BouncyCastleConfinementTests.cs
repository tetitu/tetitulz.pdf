// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.TestInfrastructure;

/// <summary>
/// BouncyCastle may appear in <c>Tetitulz.Pdf.Encryption</c> <b>only</b> where it is genuinely
/// needed — certificate (public-key) encryption. The password-based standard security handler must
/// stay dependency-free.
/// </summary>
/// <remarks>
/// <para><b>Why this matters.</b> The whole reason password-based encryption lived in its own
/// assembly is that BouncyCastle must not reach every consumer's package graph (CLAUDE.md →
/// Architecture Principles). But the standard security handler never needed it: it needs MD5,
/// SHA-2, RC4 and AES-CBC, and three of the four are in <c>System.Security.Cryptography</c> on
/// every target framework here — <c>netstandard2.0</c> included — while RC4 is twenty lines. Those
/// ciphers now live in <c>Core.Encryption.PdfCryptoPrimitives</c>, and on 2026-08-14 BouncyCastle
/// went from four files to one.</para>
/// <para><b>Only CMS enveloped-data recipients have a real claim on the dependency</b> — the
/// <c>/Adobe.PubSec</c> handler, in both directions. Anything else reaching for it is either
/// duplicating a primitive that already exists in Core — the divergence shape this repository keeps
/// finding defects in — or is quietly re-widening the dependency this separation exists to
/// contain.</para>
/// <para><b>The allow-list is a statement about which single FEATURE justifies the dependency, not
/// a burn-down list</b>, and adding an entry needs a reason in this comment. It gained its second
/// on 2026-09-01: <c>PdfPublicKeyDecryptor</c> is the reading half of the same feature — unwrapping
/// an enveloped-data recipient and building one are the same CMS claim in opposite directions, and
/// neither has a BCL equivalent that works on <c>netstandard2.0</c>.</para>
/// <para><b>A third entry, and a genuinely different feature: <c>PdfGcmCipher.cs</c>
/// (ISO/TS 32003, the <c>/CFM /AESV4</c> AES-GCM crypt filter).</b> Unlike RC4/AES-CBC — which had
/// full BCL equivalents once someone wrote the twenty lines RC4 needed — AES-GCM has <b>no</b> BCL
/// route on <c>netstandard2.0</c> at all: <c>System.Security.Cryptography.AesGcm</c> is net10.0-only
/// and is not part of the netstandard2.0 surface. net10.0 still uses that native class; only the
/// netstandard2.0 leg goes through BouncyCastle's <c>GcmBlockCipher</c>, for the same "no BCL
/// equivalent on every target framework" reason the public-key entries state — an owner decision
/// (see GAPS.md §22b), not a default.</para>
/// <para><b>A fourth entry, and a fourth distinct feature: <c>PdfMacToken.cs</c> (ISO/TS 32004,
/// standalone PDF MAC integrity tokens).</b> A CMS <c>AuthenticatedData</c> structure (RFC 5652) has
/// no BCL equivalent at all, on either target framework — unlike <c>PdfGcmCipher.cs</c>, whose
/// justification is netstandard2.0-specific, this one needs BouncyCastle's ASN.1/CMS model classes
/// on both legs, the same category of justification the two public-key entries have. A fifth entry
/// would have to argue the same absence afresh.</para>
/// </remarks>
public class BouncyCastleConfinementTests
{
    /// <summary>
    /// Four features with a genuine claim: CMS enveloped-data recipients (writing and unwrapping
    /// are the same claim in opposite directions), AES-GCM, and PDF MAC's CMS AuthenticatedData
    /// token — none of which has a BCL equivalent across every target framework this library builds
    /// for.
    /// </summary>
    private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        "PdfPublicKeyEncryptor.cs",
        "PdfPublicKeyDecryptor.cs",
        "PdfGcmCipher.cs",
        "PdfMacToken.cs",
    };

    [RepoSourceFact]
    public void OnlyPublicKeyEncryption_ReferencesBouncyCastle()
    {
        string encryption = Path.Combine(
            TestCorpus.RepoRoot!, "src", "tetitulz.pdf", "Tetitulz.Pdf.Encryption");

        Assert.True(Directory.Exists(encryption),
            $"The Encryption assembly was not found at {encryption}; this guard is scanning nothing.");

        var offenders = new List<string>();
        int scanned = 0;

        foreach (string file in Directory.EnumerateFiles(encryption, "*.cs", SearchOption.AllDirectories))
        {
            string norm = file.Replace('\\', '/');
            if (norm.Contains("/obj/") || norm.Contains("/bin/")) continue;

            scanned++;
            if (Allowed.Contains(Path.GetFileName(file))) continue;
            if (File.ReadAllText(file).Contains("Org.BouncyCastle", StringComparison.Ordinal))
                offenders.Add(Path.GetFileName(file));
        }

        // The scan must actually be reading files; a wrong path would make this green forever.
        Assert.True(scanned >= 10,
            $"Only {scanned} source files were scanned; the path is wrong, not the tree.");

        Assert.True(offenders.Count == 0,
            "BouncyCastle has spread back into the password-based encryption path: " +
            string.Join(", ", offenders.Distinct()) +
            ". The ciphers the standard security handler needs live in " +
            "Core.Encryption.PdfCryptoPrimitives and are BCL-only — use those. Only CMS " +
            "enveloped-data (certificate) encryption — the /Adobe.PubSec handler, either direction — has " +
            "a real claim on the dependency.");
    }

    /// <summary>
    /// The guard must be able to see an offender, or it is a scan that always passes.
    /// </summary>
    [RepoSourceFact]
    public void TheGuardItself_WouldSeeAFileThatReachesForBouncyCastle()
    {
        string encryption = Path.Combine(
            TestCorpus.RepoRoot!, "src", "tetitulz.pdf", "Tetitulz.Pdf.Encryption");

        // The allowed file really does contain the token — so the scan's matching works, and a
        // green run above means "no other file has it", not "the token is never found anywhere".
        string allowed = Path.Combine(encryption, "PdfPublicKeyEncryptor.cs");
        Assert.True(File.Exists(allowed),
            "The one allow-listed file is gone; either the allow-list or the guard is now wrong.");
        Assert.Contains("Org.BouncyCastle", File.ReadAllText(allowed), StringComparison.Ordinal);
    }
}
