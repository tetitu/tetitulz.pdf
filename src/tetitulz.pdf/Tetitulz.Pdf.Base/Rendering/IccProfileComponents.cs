// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Base.Rendering
{
    /// <summary>
    /// Answers one question about a raw ICC profile byte stream: how many colour components does
    /// its <b>data colour space</b> (ICC.1 header offset 16, ICC.1:2004-10 §6.1.6) actually carry?
    /// </summary>
    /// <remarks>
    /// <para><b>Public, unlike <see cref="IccProfile"/>.</b> <c>IccProfile</c> is <c>internal</c> —
    /// it builds a full evaluation model (matrix/TRC or LUT) and refuses whole classes of profile
    /// (device-link, named-colour, an unreadable PCS) that this question does not care about at
    /// all. A caller asking "how many components does this profile's colour model have" needs the
    /// header field alone, not an evaluator; <c>Tetitulz.Pdf.Plugins.Conformance</c> is exactly
    /// such a caller (ISO 19005-1:2005 rule 6.2.3.2-2: an ICCBased colour space's declared
    /// <c>/N</c> must match this number) and cannot see Base's internals — Base declares no
    /// <c>InternalsVisibleTo</c> for any plugin, on purpose.</para>
    /// <para><b>Not <see cref="IccProfile.ComponentCount"/>.</b> That property answers 0 for every
    /// <see cref="IccModel.Unsupported"/> profile, including a perfectly well-formed one this
    /// evaluator simply does not implement arithmetic for (a device-link profile, say) — the
    /// header's data colour space signature is still readable in that case, and rule 6.2.3.2-2 has
    /// no reason to go blind just because <c>IccColorTransform</c> would.</para>
    /// </remarks>
    public static class IccProfileComponents
    {
        /// <summary>
        /// The component count implied by <paramref name="profileData"/>'s data colour space
        /// signature, or <see langword="false"/> when the header cannot be read or the signature is
        /// not one of the ICC.1 Table 19 spaces <c>Core.Imaging.IccDataColourSpaces</c> recognises.
        /// </summary>
        public static bool TryGetDataColorSpaceComponentCount(byte[] profileData, out int componentCount)
        {
            componentCount = Core.Imaging.IccDataColourSpaces.ComponentCount(IccProfile.Parse(profileData).DataColorSpace);
            return componentCount > 0;
        }

        /// <summary>
        /// The three header fields ISO 19005-1 constrains for an ICCBased profile (rule
        /// 6.2.3.2-1: the device class, the data colour space, and the version's major number),
        /// read from the header alone. <see langword="false"/> when the bytes are not an ICC
        /// header — the same gate <c>IccProfile.Parse</c> applies, so a conformance check cannot
        /// call something a profile that the evaluator would refuse as none.
        /// </summary>
        /// <param name="profileData">The raw profile bytes.</param>
        /// <param name="deviceClass">Header offset 12, four characters — <c>"mntr"</c>, <c>"prtr"</c>, <c>"scnr"</c>, <c>"spac"</c>, <c>"link"</c>, <c>"abst"</c>, <c>"nmcl"</c>.</param>
        /// <param name="dataColorSpace">Header offset 16, four characters — <c>"RGB "</c>, <c>"CMYK"</c>, <c>"GRAY"</c>, <c>"Lab "</c>, …</param>
        /// <param name="majorVersion">Header byte 8: 2 for an ICC v2 profile, 4 for v4.</param>
        public static bool TryGetHeaderFields(
            byte[] profileData, out string deviceClass, out string dataColorSpace, out int majorVersion)
        {
            if (!IccProfile.TryReadHeader(profileData, out IccProfile.IccHeader header, out _))
            {
                deviceClass = null;
                dataColorSpace = null;
                majorVersion = 0;
                return false;
            }

            deviceClass = header.DeviceClass;
            dataColorSpace = header.DataColorSpace;
            majorVersion = header.MajorVersion;
            return true;
        }

        // The signature → component-count table (ICC.1:2004-10 Table 19) lives in
        // Core.Imaging.IccDataColourSpaces, shared with the JP2 header reader.
    }
}
