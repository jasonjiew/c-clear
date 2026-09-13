using System;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;

namespace Cclear.Core.Licensing;

/// <summary>
/// Ed25519（RFC 8032）精简实现（BigInteger 版本，零第三方依赖）。
/// 用途限定：离线许可证的签名/验证（毫秒级性能足够），非通用密码库。
/// .NET 8 运行时没有内置 Ed25519 类型，故按 V2 计划“零新依赖”的要求在此实现；
/// 正确性由 RFC 8032 官方测试向量保证（见 tests）。
/// </summary>
public static class Ed25519Rfc8032
{
    // 域参数（RFC 8032 §5.1）
    private static readonly BigInteger P = (BigInteger.One << 255) - 19;
    private static readonly BigInteger L = (BigInteger.One << 252)
        + BigInteger.Parse("27742317777372353535851937790883648493");
    private static readonly BigInteger D = Neg(121665) * Inverse(121666) % P;

    // 基点 B（x0=0 恢复；T = X·Y mod p）
    private static readonly Point BasePoint = new(
        BigInteger.Parse("15112221349535400772501151409588531511454012693041857206046113283949847762202"),
        BigInteger.Parse("46316835694926478169428394003475163141307993866256225615783033603165251855960"),
        BigInteger.One,
        BigInteger.Parse("46827403850823179245072216630277197565144205554125654976674165829533817101731"));

    private static readonly BigInteger SqrtM1 = BigInteger.ModPow(2, (P - 1) / 4, P);

    /// <summary>由 32 字节种子推导 32 字节公钥。</summary>
    public static byte[] PublicKeyFromSeed(ReadOnlySpan<byte> seed)
    {
        var a = Clamp(Sha512Seed(seed));
        return EncodePoint(ScalarMult(a, BasePoint));
    }

    /// <summary>Ed25519 签名（seed 32 字节，返回 64 字节签名）。</summary>
    public static byte[] Sign(ReadOnlySpan<byte> seed, ReadOnlySpan<byte> message)
    {
        var hash = Sha512Seed(seed);
        var a = Clamp(hash);
        var prefix = hash[32..64];
        var publicKey = EncodePoint(ScalarMult(a, BasePoint));

        var rHash = Sha512(prefix, message.ToArray());
        var r = ModL(rHash);
        var rEncoded = EncodePoint(ScalarMult(r, BasePoint));

        var k = ModL(Sha512(rEncoded, publicKey, message.ToArray()));
        var s = (r + k * a) % L;

        var signature = new byte[64];
        rEncoded.CopyTo(signature, 0);
        Write32LE(s, signature.AsSpan(32));
        return signature;
    }

    /// <summary>Ed25519 验证（RFC 8032：拒绝非规范 S；校验 S·B = R + h·A）。</summary>
    public static bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> signature, ReadOnlySpan<byte> message)
    {
        if (publicKey.Length != 32 || signature.Length != 64)
        {
            return false;
        }
        var aPoint = TryDecodePoint(publicKey);
        if (aPoint is null)
        {
            return false;
        }

        var rEncoded = signature[0..32].ToArray();
        var s = FromLE(signature[32..64]);
        if (s >= L)
        {
            return false; // 非规范 S
        }

        var k = ModL(Sha512(rEncoded, publicKey.ToArray(), message.ToArray()));
        // check = S·B - k·A；编码后必须等于 R
        var check = Add(ScalarMult(s, BasePoint), ScalarMult(k, Negate(aPoint)));
        return EncodePoint(check).AsSpan().SequenceEqual(rEncoded);
    }

    // ---------- 群运算（扩展坐标 X/Y/Z/T，a = -1） ----------

    private sealed record Point(BigInteger X, BigInteger Y, BigInteger Z, BigInteger T);

    private static Point Negate(Point p) => new(Mod(-p.X), p.Y, p.Z, Mod(-p.T));

    private static Point Add(Point p, Point q)
    {
        // RFC 8032 §5.1.4 extended 统一加法
        var a = Mod((p.Y - p.X) * (q.Y - q.X));
        var b = Mod((p.Y + p.X) * (q.Y + q.X));
        var c = Mod(2 * p.T * q.T * D);
        var d = Mod(2 * p.Z * q.Z);
        var e = b - a;
        var f = d - c;
        var g = d + c;
        var h = b + a;
        return new Point(Mod(e * f), Mod(g * h), Mod(f * g), Mod(e * h));
    }

    private static Point ScalarMult(BigInteger k, Point p)
    {
        k %= L;
        if (k < 0)
        {
            k += L;
        }
        var result = new Point(BigInteger.Zero, BigInteger.One, BigInteger.One, BigInteger.Zero); // 单位元
        var addend = p;
        while (k > 0)
        {
            if (!k.IsEven)
            {
                result = Add(result, addend);
            }
            addend = Add(addend, addend);
            k >>= 1;
        }
        return result;
    }

    // ---------- 编解码 ----------

    private static byte[] EncodePoint(Point p)
    {
        var zInv = Inverse(p.Z);
        var x = Mod(p.X * zInv);
        var y = Mod(p.Y * zInv);
        var encoded = y.ToByteArray(isUnsigned: true, isBigEndian: false);
        Array.Resize(ref encoded, 32);
        if (x.IsEven)
        {
            encoded[31] &= 0x7F;
        }
        else
        {
            encoded[31] |= 0x80;
        }
        return encoded;
    }

    private static Point? TryDecodePoint(ReadOnlySpan<byte> encoded)
    {
        if (encoded.Length != 32)
        {
            return null;
        }
        var bytes = encoded.ToArray();
        var xSign = (bytes[31] & 0x80) != 0;
        bytes[31] &= 0x7F;
        var y = FromLE(bytes);
        if (y >= P)
        {
            return null; // 非规范 y
        }

        // x² = (y² - 1) / (d·y² + 1)
        var u = Mod(y * y - 1);
        var v = Mod(D * y * y + 1);
        var x = RecoverX(u, v, xSign);
        if (x is null)
        {
            return null;
        }
        return new Point(x.Value, y, BigInteger.One, Mod(x.Value * y));
    }

    private static BigInteger? RecoverX(BigInteger u, BigInteger v, bool xSign)
    {
        // RFC 8032 §5.1.3：x² = u/v，先算出目标值再开方
        var x2 = Mod(u * Inverse(v));
        var candidate = Mod(BigInteger.ModPow(x2, (P + 3) / 8, P));
        if (Mod(candidate * candidate - x2) != 0)
        {
            // 平方根失败则乘 sqrt(-1)
            candidate = Mod(candidate * SqrtM1);
        }
        if (Mod(candidate * candidate) != x2)
        {
            return null; // 不是平方数 → 点不在曲线上
        }
        if (candidate.IsZero)
        {
            if (xSign)
            {
                return null; // 非规范（0 不允许带符号位）
            }
            return candidate;
        }
        if (candidate.IsEven == xSign)
        {
            // 奇偶不符则取另一根
            candidate = Mod(-candidate);
        }
        return candidate;
    }

    // ---------- 标量处理 ----------

    private static BigInteger Clamp(byte[] hash32)
    {
        var a = FromLE(hash32);
        a &= (BigInteger.One << 254) - 8;
        a |= BigInteger.One << 254;
        return a;
    }

    private static BigInteger FromLE(ReadOnlySpan<byte> bytes)
        => new(bytes.ToArray());

    private static void Write32LE(BigInteger value, Span<byte> target)
    {
        target.Clear();
        var bytes = value.ToByteArray(isUnsigned: true, isBigEndian: false);
        var count = Math.Min(bytes.Length, target.Length);
        bytes.AsSpan(0, count).CopyTo(target);
    }

    private static BigInteger ModL(byte[] hash)
    {
        // SHA512 输出 64 字节按无符号 LE 整数解释（补 0x00 字节避免二进制补码负数）
        var value = new BigInteger(hash.Concat(new byte[] { 0 }).ToArray());
        value %= L;
        if (value < 0)
        {
            value += L;
        }
        return value;
    }

    // ---------- 域运算 ----------

    private static BigInteger Mod(BigInteger value)
    {
        value %= P;
        return value < 0 ? value + P : value;
    }

    private static BigInteger Neg(BigInteger value) => Mod(-value);

    private static BigInteger Inverse(BigInteger value)
    {
        if (value % P == 0)
        {
            throw new DivideByZeroException("Ed25519 域元素求逆遇零");
        }
        return BigInteger.ModPow(Mod(value), P - 2, P);
    }

    private static byte[] Sha512(params byte[][] parts)
    {
        using var sha = SHA512.Create();
        foreach (var part in parts)
        {
            sha.TransformBlock(part, 0, part.Length, null, 0);
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return sha.Hash!;
    }

    private static byte[] Sha512Seed(ReadOnlySpan<byte> seed) => Sha512(seed.ToArray());
}
