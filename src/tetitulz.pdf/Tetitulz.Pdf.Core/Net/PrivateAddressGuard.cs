// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Net;
using System.Net.Sockets;

namespace Tetitulz.Pdf.Core.Net
{
    /// <summary>
    /// SSRF guard: classifies whether a URL/host points at a loopback, private,
    /// link-local, or otherwise reserved address. Shared by every code path that
    /// fetches a URL sourced (directly or indirectly) from an untrusted document or
    /// certificate — OCSP/CRL (DssBuilder), TSA, HTML subresources, blockchain RPC.
    ///
    /// Resolves DNS names so a public hostname that resolves to a private IP (the
    /// DNS-rebinding case) is still blocked. Callers fetching from a live browser
    /// should re-check on every request and redirect, since this only inspects a URL.
    /// </summary>
    public static class PrivateAddressGuard
    {
        /// <summary>True when the address is loopback, private, link-local, ULA,
        /// cloud-metadata (169.254.169.254), multicast, or otherwise not a routable
        /// public address.</summary>
        public static bool IsPrivateOrReserved(IPAddress ip)
        {
            if (ip == null) return true;

            // Normalise the standard IPv4-mapped form (::ffff:a.b.c.d) to IPv4.
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();

            if (ip.AddressFamily == AddressFamily.InterNetwork)
                return IsPrivateOrReservedV4(ip.GetAddressBytes());

            if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            {
                byte[] b = ip.GetAddressBytes(); // 16 bytes, scope-INDEPENDENT

                // Loopback ::1 by bytes — NOT IPAddress.IsLoopback, which compares the ScopeId
                // and returns false for a scoped loopback like ::1%1, letting it read as public.
                if (IsAllZeroExceptLastOne(b)) return true;
                // Unspecified :: (semantically 0.0.0.0 "this host").
                if (IsAllZero(b)) return true;

                if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast) return true;
                if ((b[0] & 0xFE) == 0xFC) return true;                      // fc00::/7 unique-local

                // Prefixes that are never a public host's own address. 64:ff9b:1::/48 is NAT64 for
                // local use (RFC 8215): the network that deploys it translates it to whatever IPv4
                // it chooses, its own private ranges included, at a prefix length this guard cannot
                // know — so the whole prefix is refused rather than one embedding guessed at.
                // 2001::/32 is Teredo, which carries an IPv4 address obscured in the low bits, and
                // 2001:db8::/32 is documentation (RFC 3849).
                if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B && b[4] == 0x00 && b[5] == 0x01) return true;
                if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x00 && b[3] == 0x00) return true;
                if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0D && b[3] == 0xB8) return true;

                // IPv6 forms that EMBED an IPv4 address the framework does not auto-map. Without
                // this, ::127.0.0.1 (IPv4-compatible), 64:ff9b::7f00:1 (NAT64), 2002:7f00:1::
                // (6to4), and ::ffff:0:7f00:1 (translated) all read as public and reach the
                // embedded private/loopback/metadata target. Re-classify the embedded IPv4.
                if (TryExtractEmbeddedIPv4(b, out byte[] v4))
                    return IsPrivateOrReservedV4(v4);

                return false;
            }

            return true; // unknown family → block
        }

        private static bool IsPrivateOrReservedV4(byte[] b)
        {
            if (b == null || b.Length != 4) return true;
            if (b[0] == 0)   return true;                                // 0.0.0.0/8 (this host)
            if (b[0] == 10)  return true;                                // 10.0.0.0/8
            if (b[0] == 127) return true;                                // 127.0.0.0/8 loopback
            if (b[0] == 169 && b[1] == 254) return true;                 // 169.254.0.0/16 link-local + metadata
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;    // 172.16.0.0/12
            if (b[0] == 192 && b[1] == 168) return true;                 // 192.168.0.0/16
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return true;   // 100.64.0.0/10 CGNAT
            if (b[0] == 192 && b[1] == 0 && b[2] == 0) return true;      // 192.0.0.0/24
            if (b[0] == 198 && (b[1] == 18 || b[1] == 19)) return true;  // 198.18.0.0/15 benchmarking (RFC 2544), used inside networks
            if (b[0] == 192 && b[1] == 0 && b[2] == 2) return true;      // 192.0.2.0/24 documentation (RFC 5737)
            if (b[0] == 198 && b[1] == 51 && b[2] == 100) return true;   // 198.51.100.0/24 documentation
            if (b[0] == 203 && b[1] == 0 && b[2] == 113) return true;    // 203.0.113.0/24 documentation
            if (b[0] >= 224) return true;                                // 224.0.0.0/4 multicast + 240/4 reserved
            return false;
        }

        private static bool IsAllZero(byte[] b)
        {
            for (int i = 0; i < b.Length; i++) if (b[i] != 0) return false;
            return true;
        }

        private static bool IsAllZeroExceptLastOne(byte[] b)
        {
            for (int i = 0; i < b.Length - 1; i++) if (b[i] != 0) return false;
            return b[b.Length - 1] == 1;
        }

        // Extracts an embedded IPv4 from the IPv6 transition/compat prefixes the runtime does
        // not normalise via IsIPv4MappedToIPv6. Returns false for native IPv6.
        private static bool TryExtractEmbeddedIPv4(byte[] b, out byte[] v4)
        {
            v4 = null;
            bool first8Zero = b[0] == 0 && b[1] == 0 && b[2] == 0 && b[3] == 0
                           && b[4] == 0 && b[5] == 0 && b[6] == 0 && b[7] == 0;

            // 64:ff9b::/96 well-known NAT64 prefix → embedded IPv4 in the low 32 bits.
            if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B
                && b[4] == 0 && b[5] == 0 && b[6] == 0 && b[7] == 0
                && b[8] == 0 && b[9] == 0 && b[10] == 0 && b[11] == 0)
            { v4 = new[] { b[12], b[13], b[14], b[15] }; return true; }

            // 2002::/16 6to4 → embedded IPv4 is bytes[2..6].
            if (b[0] == 0x20 && b[1] == 0x02)
            { v4 = new[] { b[2], b[3], b[4], b[5] }; return true; }

            // ::ffff:0:a.b.c.d translated (ffff at bytes[8..10]) → low 32 bits.
            if (first8Zero && b[8] == 0xFF && b[9] == 0xFF)
            { v4 = new[] { b[12], b[13], b[14], b[15] }; return true; }

            // ::a.b.c.d IPv4-compatible ::/96 (first 12 bytes zero; :: and ::1 already handled).
            if (first8Zero && b[8] == 0 && b[9] == 0 && b[10] == 0 && b[11] == 0)
            { v4 = new[] { b[12], b[13], b[14], b[15] }; return true; }

            return false;
        }

        /// <summary>
        /// Validates that <paramref name="url"/> is an absolute http/https URL whose
        /// host does not resolve to any private/reserved address. Returns false (and a
        /// null <paramref name="uri"/>) when the scheme is disallowed, the host cannot
        /// be resolved, or any resolved address is blocked.
        /// </summary>
        public static bool IsSafePublicHttpUrl(string url, out Uri uri)
        {
            uri = null;
            if (string.IsNullOrWhiteSpace(url)) return false;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)) return false;
            if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) return false;
            if (!IsSafeHost(parsed.DnsSafeHost)) return false;
            uri = parsed;
            return true;
        }

        /// <summary>True when the host is a literal public IP, or a name that resolves
        /// only to public addresses. Any resolution failure or any private/reserved
        /// address makes it unsafe (fail closed).</summary>
        /// <remarks>
        /// <b>This answers a question and throws the evidence away, which is what makes it a
        /// pre-check rather than a guarantee.</b> A caller that fetches by hostname afterwards
        /// resolves again, and the second answer is the one the connection uses — so a name with a
        /// short TTL can be public here and private there. Use
        /// <see cref="TryResolveSafeAddresses"/> and connect to what it returns where the transport
        /// allows it; <c>GuardedHttpConnect</c> does exactly that.
        /// </remarks>
        public static bool IsSafeHost(string host) => TryResolveSafeAddresses(host, out _);

        /// <summary>
        /// Resolves <paramref name="host"/> and hands back the addresses, but only when <b>every</b>
        /// one of them is public.
        /// </summary>
        /// <remarks>
        /// <para><b>Returning the addresses is the point.</b> <see cref="IsSafeHost"/> discards
        /// them, so the caller has to name the host again to connect and the name is resolved a
        /// second time — the check and the connection can therefore disagree, which is the
        /// DNS-rebinding case this exists to close. Connect to what comes back here and the address
        /// checked is the address dialled.</para>
        /// <para><b>All-or-nothing, deliberately.</b> A name resolving to one public and one private
        /// address is refused outright rather than filtered down to the public ones: which address
        /// a connection picks is the resolver's and the stack's business, and a name answering that
        /// way is far more likely to be an attack than a deployment.</para>
        /// <para>A literal IP is its own resolution. Anything unresolvable fails closed.</para>
        /// </remarks>
        /// <param name="host">A hostname or an IP literal.</param>
        /// <param name="addresses">
        /// The vetted addresses, or an empty array when the result is false. Never null.
        /// </param>
        public static bool TryResolveSafeAddresses(string host, out IPAddress[] addresses)
        {
            addresses = EmptyAddresses;
            if (string.IsNullOrWhiteSpace(host)) return false;

            // Literal IP (covers decimal/hex IPv4 forms that IPAddress normalises).
            if (IPAddress.TryParse(host, out var literal))
            {
                if (IsPrivateOrReserved(literal)) return false;
                addresses = new[] { literal };
                return true;
            }

            IPAddress[] resolved;
            try
            {
                resolved = Dns.GetHostAddresses(host);
            }
            catch
            {
                return false; // unresolvable → fail closed
            }

            if (resolved == null || resolved.Length == 0) return false;
            foreach (var a in resolved)
                if (IsPrivateOrReserved(a)) return false;

            addresses = resolved;
            return true;
        }

        private static readonly IPAddress[] EmptyAddresses = new IPAddress[0];
    }
}
