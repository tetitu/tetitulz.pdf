// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Numerics;

namespace Tetitulz.Pdf.Core.Tests.Signing;

/// <summary>
/// An Ed448 detached CMS verified by code this repository's signer does not use: a DER reader for
/// RFC 5652's SignedData, SHAKE256 (FIPS 202) and Ed448 (RFC 8032 §5.2) written here from the
/// standards, over <see cref="BigInteger"/> — none of it the cryptographic library the signer runs on.
/// </summary>
internal static class IndependentEd448Cms
{
    internal sealed record Result(bool Verified, string DigestAlgorithm, bool DigestHasParameters, string Reason);

    private const string Shake256Oid = "2.16.840.1.101.3.4.2.12";
    private const string Ed448Oid = "1.3.101.113";
    private const string MessageDigestOid = "1.2.840.113549.1.9.4";

    /// <summary>Verifies <paramref name="cms"/> (DER ContentInfo) over the detached <paramref name="content"/>.</summary>
    internal static Result Verify(byte[] cms, byte[] content)
    {
        try
        {
            Der contentInfo = Der.Read(cms, 0);
            Der signedData = contentInfo.Children[1].Children[0];
            var parts = signedData.Children;
            Der? certificates = parts.FirstOrDefault(p => p.Tag == 0xA0);
            Der signerInfos = parts.Last(p => p.Tag == 0x31);
            Der signer = signerInfos.Children[0];

            Der digestAlgorithm = signer.Children[2];
            string digestOid = digestAlgorithm.Children[0].Oid();
            bool digestParameters = digestAlgorithm.Children.Count > 1;
            Der signedAttrs = signer.Children.First(c => c.Tag == 0xA0);
            Der signatureAlgorithm = signer.Children.First(c => c.Tag == 0x30 && c != digestAlgorithm && c.Children.Count > 0 && c.Children[0].Tag == 0x06 && c.Children[0].Oid() == Ed448Oid);
            byte[] signature = signer.Children.First(c => c.Tag == 0x04).Value;
            if (signatureAlgorithm == null) return new Result(false, digestOid, digestParameters, "no Ed448 signature algorithm");

            // The message digest attribute against SHAKE256 of the content, 512 bits (RFC 8419 §2.3).
            byte[]? messageDigest = null;
            foreach (Der attribute in signedAttrs.Children)
                if (attribute.Children[0].Oid() == MessageDigestOid) messageDigest = attribute.Children[1].Children[0].Value;
            if (messageDigest == null) return new Result(false, digestOid, digestParameters, "no message digest attribute");
            if (!messageDigest.AsSpan().SequenceEqual(Shake256(content, 64))) return new Result(false, digestOid, digestParameters, "the message digest does not match the content");

            // The certificate's Ed448 key: SubjectPublicKeyInfo inside the first certificate's TBS.
            if (certificates == null) return new Result(false, digestOid, digestParameters, "no certificate");
            Der tbs = certificates.Children[0].Children[0];
            Der spki = tbs.Children.First(c => c.Tag == 0x30 && c.Children.Count == 2 && c.Children[0].Tag == 0x30 &&
                                                c.Children[0].Children[0].Tag == 0x06 && c.Children[0].Children[0].Oid() == Ed448Oid);
            byte[] key = spki.Children[1].Value.Skip(1).ToArray();   // the BIT STRING's unused-bits byte first

            // The signature is over the attributes' DER with the SET tag (RFC 5652 §5.4).
            byte[] message = (byte[])signedAttrs.Encoding.Clone();
            message[0] = 0x31;
            bool ok = Ed448.Verify(key, message, signature);
            return new Result(ok, digestOid, digestParameters, ok ? "verified" : "the Ed448 signature does not verify");
        }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentException or InvalidOperationException)
        {
            return new Result(false, "", false, "the CMS could not be read: " + e.Message);
        }
    }

    // ------------------------------------------------------------------ DER

    private sealed class Der
    {
        public int Tag;
        public byte[] Value = Array.Empty<byte>();
        public byte[] Encoding = Array.Empty<byte>();
        public List<Der> Children = new();

        public static Der Read(byte[] d, int at) => Read(d, ref at);

        private static Der Read(byte[] d, ref int at)
        {
            int start = at;
            int tag = d[at++];
            int length = d[at++];
            if (length >= 0x80)
            {
                int n = length & 0x7F;
                if (n == 0 || n > 4) throw new ArgumentException("an indefinite or oversized DER length");
                length = 0;
                for (int i = 0; i < n; i++) length = (length << 8) | d[at++];
            }
            if (at + length > d.Length) throw new ArgumentException("a DER value runs past its container");
            var node = new Der { Tag = tag, Value = d.AsSpan(at, length).ToArray(), Encoding = d.AsSpan(start, at + length - start).ToArray() };
            if ((tag & 0x20) != 0)
            {
                int p = 0;
                while (p < node.Value.Length) node.Children.Add(Read(node.Value, ref p));
            }
            at += length;
            return node;
        }

        public string Oid()
        {
            if (Tag != 0x06) throw new InvalidOperationException("not an OBJECT IDENTIFIER");
            var parts = new List<string> { (Value[0] / 40).ToString(), (Value[0] % 40).ToString() };
            long v = 0;
            for (int i = 1; i < Value.Length; i++)
            {
                v = (v << 7) | (uint)(Value[i] & 0x7F);
                if ((Value[i] & 0x80) == 0) { parts.Add(v.ToString()); v = 0; }
            }
            return string.Join(".", parts);
        }
    }

    // ------------------------------------------------------------------ SHAKE256 (FIPS 202)

    internal static byte[] Shake256(byte[] input, int outputLength)
    {
        const int rate = 136;
        var state = new ulong[25];
        var padded = new byte[(input.Length / rate + 1) * rate];
        Buffer.BlockCopy(input, 0, padded, 0, input.Length);
        padded[input.Length] ^= 0x1F;
        padded[padded.Length - 1] ^= 0x80;
        for (int block = 0; block < padded.Length; block += rate)
        {
            for (int i = 0; i < rate / 8; i++) state[i] ^= BitConverter.ToUInt64(padded, block + 8 * i);
            KeccakF(state);
        }
        var output = new byte[outputLength];
        for (int done = 0; ; )
        {
            for (int i = 0; i < rate / 8 && done < outputLength; i++)
                for (int b = 0; b < 8 && done < outputLength; b++) output[done++] = (byte)(state[i] >> (8 * b));
            if (done >= outputLength) return output;
            KeccakF(state);
        }
    }

    private static readonly ulong[] RoundConstants =
    {
        0x0000000000000001, 0x0000000000008082, 0x800000000000808A, 0x8000000080008000, 0x000000000000808B, 0x0000000080000001,
        0x8000000080008081, 0x8000000000008009, 0x000000000000008A, 0x0000000000000088, 0x0000000080008009, 0x000000008000000A,
        0x000000008000808B, 0x800000000000008B, 0x8000000000008089, 0x8000000000008003, 0x8000000000008002, 0x8000000000000080,
        0x000000000000800A, 0x800000008000000A, 0x8000000080008081, 0x8000000000008080, 0x0000000080000001, 0x8000000080008008,
    };

    private static readonly int[] Rotations = { 0, 1, 62, 28, 27, 36, 44, 6, 55, 20, 3, 10, 43, 25, 39, 41, 45, 15, 21, 8, 18, 2, 61, 56, 14 };

    private static void KeccakF(ulong[] a)
    {
        var c = new ulong[5];
        var b = new ulong[25];
        for (int round = 0; round < 24; round++)
        {
            for (int x = 0; x < 5; x++) c[x] = a[x] ^ a[x + 5] ^ a[x + 10] ^ a[x + 15] ^ a[x + 20];
            for (int x = 0; x < 5; x++)
            {
                ulong dx = c[(x + 4) % 5] ^ BitOperations.RotateLeft(c[(x + 1) % 5], 1);
                for (int y = 0; y < 25; y += 5) a[y + x] ^= dx;
            }
            for (int x = 0; x < 5; x++)
                for (int y = 0; y < 5; y++)
                    b[y + 5 * ((2 * x + 3 * y) % 5)] = BitOperations.RotateLeft(a[x + 5 * y], Rotations[x + 5 * y]);
            for (int y = 0; y < 25; y += 5)
                for (int x = 0; x < 5; x++) a[y + x] = b[y + x] ^ (~b[y + (x + 1) % 5] & b[y + (x + 2) % 5]);
            a[0] ^= RoundConstants[round];
        }
    }

    // ------------------------------------------------------------------ Ed448 (RFC 8032 §5.2)

    internal static class Ed448
    {
        private static readonly BigInteger P = BigInteger.Pow(2, 448) - BigInteger.Pow(2, 224) - 1;
        private static readonly BigInteger D = P - 39081;
        private static readonly BigInteger L = BigInteger.Pow(2, 446) - BigInteger.Parse("13818066809895115352007386748515426880336692474882178609894547503885");
        private static readonly BigInteger Bx = BigInteger.Parse("224580040295924300187604334099896036246789641632564134246125461686950415467406032909029192869357953282578032075146446173674602635247710");
        private static readonly BigInteger By = BigInteger.Parse("298819210078481492676017930443930673437544040154080242095928241372331506189835876003536878655418784733982303233503462500531545062832660");

        private readonly record struct Point(BigInteger X, BigInteger Y, BigInteger Z);

        private static BigInteger Mod(BigInteger v) { v %= P; return v.Sign < 0 ? v + P : v; }

        private static Point Add(Point p, Point q)
        {
            BigInteger a = Mod(p.Z * q.Z), b = Mod(a * a), c = Mod(p.X * q.X), d = Mod(p.Y * q.Y);
            BigInteger e = Mod(D * c * d), f = Mod(b - e), g = Mod(b + e), h = Mod((p.X + p.Y) * (q.X + q.Y));
            return new Point(Mod(a * f * (h - c - d)), Mod(a * g * (d - c)), Mod(f * g));
        }

        private static Point Multiply(BigInteger k, Point p)
        {
            var result = new Point(0, 1, 1);
            while (k > 0)
            {
                if (!k.IsEven) result = Add(result, p);
                p = Add(p, p);
                k >>= 1;
            }
            return result;
        }

        private static bool Same(Point p, Point q) => Mod(p.X * q.Z) == Mod(q.X * p.Z) && Mod(p.Y * q.Z) == Mod(q.Y * p.Z);

        private static BigInteger LittleEndian(ReadOnlySpan<byte> bytes) => new(bytes, isUnsigned: true, isBigEndian: false);

        private static Point? Decode(byte[] encoded)
        {
            if (encoded.Length != 57) return null;
            bool sign = (encoded[56] & 0x80) != 0;
            byte[] yb = (byte[])encoded.Clone();
            yb[56] &= 0x7F;
            BigInteger y = LittleEndian(yb);
            if (y >= P) return null;
            BigInteger u = Mod(y * y - 1), v = Mod(D * y * y - 1);
            // x = u^3 v (u^5 v^3)^((p-3)/4)
            BigInteger x = Mod(BigInteger.ModPow(u, 3, P) * v * BigInteger.ModPow(Mod(BigInteger.ModPow(u, 5, P) * BigInteger.ModPow(v, 3, P)), (P - 3) / 4, P));
            if (Mod(v * x * x) != u) return null;
            if (x.IsZero && sign) return null;
            if (!x.IsEven != sign) x = P - x;
            return new Point(x, y, 1);
        }

        internal static bool Verify(byte[] publicKey, byte[] message, byte[] signature)
        {
            if (signature.Length != 114) return false;
            Point? a = Decode(publicKey);
            Point? r = Decode(signature.Take(57).ToArray());
            if (a == null || r == null) return false;
            BigInteger s = LittleEndian(signature.AsSpan(57, 57));
            if (s >= L) return false;
            byte[] dom4 = System.Text.Encoding.ASCII.GetBytes("SigEd448").Concat(new byte[] { 0, 0 }).ToArray();
            BigInteger k = LittleEndian(Shake256(dom4.Concat(signature.Take(57)).Concat(publicKey).Concat(message).ToArray(), 114)) % L;
            var basePoint = new Point(Bx, By, 1);
            Point left = Multiply(4 * s, basePoint);
            Point right = Add(Multiply(4, r.Value), Multiply(4 * k, a.Value));
            return Same(left, right);
        }
    }
}
