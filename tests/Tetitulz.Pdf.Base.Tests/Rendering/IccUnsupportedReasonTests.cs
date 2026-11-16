// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Text;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// An unsupported ICC profile's reason must name <b>what would actually have to change</b>, not the
/// first true thing the parser noticed.
/// </summary>
/// <remarks>
/// <para><b>Two rounds of the same lesson.</b> The PCS check once ran before the data-space
/// switch, so a CMYK profile — which almost always declares a <c>Lab</c> PCS — reported <i>"PCS is
/// 'Lab '; only an XYZ PCS is evaluated"</i>: true, and the less useful of the two true things,
/// since it implies an XYZ-PCS CMYK profile would work and none would. That was fixed by naming
/// the <c>A2B0</c> lut as the real blocker. <b>The lut is now read</b> (<see cref="IccLut"/>), so
/// the message that replaced it — "evaluating it needs a full CMM" — became the stale one in its
/// turn, and these cases were rewritten again rather than being left to assert a decline that no
/// longer happens.</para>
/// <para>This matters more than a wording nit because the fallback is <b>silent in the pixels</b>:
/// a profile that is present but unevaluated renders identically to no profile at all, so the reason
/// string is the only thing distinguishing them. That is why <c>IccTransformFailed</c> and
/// <c>IccTransform.LastUnsupportedProfile</c> exist.</para>
/// </remarks>
public class IccUnsupportedReasonTests
{
    /// <summary>
    /// A Lab-PCS profile whose <c>A2B0</c> is a stub names the <b>tag</b>, and — the part that
    /// carries the lesson — does <i>not</i> blame the PCS. A Lab PCS is now decoded, so a message
    /// mentioning it would send a caller after the one thing about this file that is fine.
    /// </summary>
    [Fact]
    public void ALabPcsProfileWithAnUnreadableLut_NamesTheTagAndNotThePcs()
    {
        byte[] profile = BuildProfile(dataSpace: "CMYK", pcs: "Lab ", withA2B0: true);

        string reason = ReasonFor(profile);

        Assert.Contains("A2B0", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("PCS", reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// With no <c>A2B0</c> and no matrix form, the message names the data space — the one thing a
    /// caller could act on — and still names the missing lut, because "this space needs one" is
    /// what would have to change. The guard against "improving" every message into the same vague
    /// paragraph is the <c>DoesNotContain</c> above, not this one.
    /// </summary>
    [Fact]
    public void ALabPcsProfileWithNoLut_NamesTheSpaceAndTheMissingLut()
    {
        byte[] profile = BuildProfile(dataSpace: "RGB ", pcs: "Lab ", withA2B0: false);

        string reason = ReasonFor(profile);

        Assert.Contains("RGB", reason, StringComparison.Ordinal);
        Assert.Contains("A2B0", reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// A PCS that is neither <c>'XYZ '</c> nor <c>'Lab '</c> is not a PCS at all, and that is the
    /// one remaining case where naming the PCS is the right answer. It is kept precisely because
    /// the two cases above assert the opposite: without it, "never mention the PCS" would be an
    /// equally passing implementation.
    /// </summary>
    [Fact]
    public void AProfileWithNeitherPcsEncoding_StillNamesThePcs()
    {
        byte[] profile = BuildProfile(dataSpace: "RGB ", pcs: "YCbr", withA2B0: true);

        string reason = ReasonFor(profile);

        Assert.Contains("PCS", reason, StringComparison.Ordinal);
        Assert.Contains("YCbr", reason, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// The reason a caller would read, via the public reporting surface rather than an internal.
    /// </summary>
    private static string ReasonFor(byte[] profile)
    {
        // The real consultation path, not a bespoke probe: LastUnsupportedProfile is refreshed on
        // EVERY consultation (its own doc says so), so reading it straight after describes this call
        // and needs no reset -- which is just as well, since the slot is read-only by design.
        IccColorTransform.TryTransform(profile, new float[] { 0f, 0f, 0f, 0f }, out _);
        string reason = IccTransform.LastUnsupportedProfile;
        Assert.False(string.IsNullOrEmpty(reason),
            "no reason was recorded, so nothing tells a caller why the profile was ignored");
        return reason;
    }

    /// <summary>
    /// A minimal but structurally valid ICC profile: 128-byte header, a tag table, and optionally an
    /// <c>A2B0</c> entry. Only the header fields and tag signatures matter to the parse under test.
    /// </summary>
    private static byte[] BuildProfile(string dataSpace, string pcs, bool withA2B0)
    {
        var tags = new List<string> { "desc" };
        if (withA2B0) tags.Add("A2B0");

        int headerBytes = 128;
        int tableBytes = 4 + tags.Count * 12;
        int payloadAt = headerBytes + tableBytes;
        int payloadBytes = tags.Count * 16;                 // a token body per tag
        var b = new byte[payloadAt + payloadBytes];

        WriteU32(b, 0, (uint)b.Length);                     // declared size
        WriteSig(b, 12, "mntr");                            // device class
        WriteSig(b, 16, dataSpace);
        WriteSig(b, 20, pcs);
        WriteSig(b, 36, "acsp");                            // file signature

        WriteU32(b, headerBytes, (uint)tags.Count);
        for (int i = 0; i < tags.Count; i++)
        {
            int e = headerBytes + 4 + i * 12;
            WriteSig(b, e, tags[i]);
            WriteU32(b, e + 4, (uint)(payloadAt + i * 16));
            WriteU32(b, e + 8, 16);
        }

        return b;
    }

    private static void WriteSig(byte[] b, int at, string sig)
    {
        byte[] s = Encoding.ASCII.GetBytes(sig.PadRight(4).Substring(0, 4));
        Array.Copy(s, 0, b, at, 4);
    }

    private static void WriteU32(byte[] b, int at, uint v)
    {
        b[at] = (byte)(v >> 24); b[at + 1] = (byte)(v >> 16);
        b[at + 2] = (byte)(v >> 8); b[at + 3] = (byte)v;
    }
}
