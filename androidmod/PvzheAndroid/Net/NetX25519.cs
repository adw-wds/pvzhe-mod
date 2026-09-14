using System;

/// <summary>
/// 纯托管 X25519（Curve25519 ECDH，RFC 7748）。
/// ★ 严禁改用 System.Security.Cryptography.ECDiffieHellman：手机版 Godot Mono AOT
///   下该类型不可用（见 NetCrypto.cs 同款约定）。
/// 实现：字段元素 = 16 个 long limb（radix 2^16，宽松表示），Montgomery ladder
/// （算法结构对齐 RFC 7748 §5 伪代码，比内联展开版本更易审计）。
/// 正确性由 RFC 7748 §5.2 / §6.1 测试向量保证（relay/PvzheCryptoTests）。
///
/// 用途：连接握手（每次连接一份临时密钥，提供前向安全）+ 房间密钥分发（房主↔各成员）。
/// </summary>
public static class NetX25519
{
    /// <summary>a24 = (486662 - 2) / 4（RFC 7748）。</summary>
    const long A24 = 121665;

    /// <summary>标准基点 u = 9。</summary>
    public static byte[] BasePoint()
    {
        var b = new byte[32];
        b[0] = 9;
        return b;
    }

    // ================= 字段元素运算（radix 2^16，16 limbs，宽松表示） =================

    /// <summary>进位规约。允许输入 limb 为负（先 +2^16 再取进位）。</summary>
    static void Car(long[] o)
    {
        for (int i = 0; i < 16; i++)
        {
            o[i] += 1L << 16;
            long c = o[i] >> 16;
            if (i < 15) o[i + 1] += c - 1;
            else o[0] += 38 * (c - 1);      // 2^256 ≡ 38 (mod p)
            o[i] -= c << 16;
        }
    }

    static void Add(long[] o, long[] a, long[] b)
    {
        for (int i = 0; i < 16; i++) o[i] = a[i] + b[i];
    }

    static void Sub(long[] o, long[] a, long[] b)
    {
        for (int i = 0; i < 16; i++) o[i] = a[i] - b[i];
    }

    /// <summary>乘法：schoolbook 后把 2^256 以上折回（2^256 ≡ 38），再两次进位。</summary>
    static void Mul(long[] o, long[] a, long[] b)
    {
        var t = new long[31];
        for (int i = 0; i < 16; i++)
        {
            long ai = a[i];
            if (ai == 0) continue;
            for (int j = 0; j < 16; j++)
                t[i + j] += ai * b[j];
        }
        for (int i = 0; i < 15; i++) t[i] += 38 * t[i + 16];
        for (int i = 0; i < 16; i++) o[i] = t[i];
        Car(o);
        Car(o);
    }

    static void Sq(long[] o, long[] a) => Mul(o, a, a);

    static void Copy(long[] o, long[] a)
    {
        for (int i = 0; i < 16; i++) o[i] = a[i];
    }

    static void SetZero(long[] o)
    {
        for (int i = 0; i < 16; i++) o[i] = 0;
    }

    /// <summary>常量时间条件交换：b=1 交换，b=0 不交换。</summary>
    static void CSwap(int b, long[] p, long[] q)
    {
        long c = ~(b - 1L);
        for (int i = 0; i < 16; i++)
        {
            long t = c & (p[i] ^ q[i]);
            p[i] ^= t;
            q[i] ^= t;
        }
    }

    /// <summary>32 字节小端 → 字段元素，并清除第 255 位。</summary>
    static void Unpack(long[] o, byte[] n)
    {
        for (int i = 0; i < 16; i++)
            o[i] = (long)n[2 * i] | ((long)n[2 * i + 1] << 8);
        o[15] &= 0x7fff;
    }

    /// <summary>字段元素 → 32 字节小端（先归约到 [0, p)）。</summary>
    static void Pack(byte[] o, long[] n)
    {
        var t = new long[16];
        var m = new long[16];
        Copy(t, n);
        Car(t); Car(t); Car(t);

        for (int j = 0; j < 2; j++)
        {
            m[0] = t[0] - 0xffed;
            for (int i = 1; i < 15; i++)
            {
                m[i] = t[i] - 0xffff - ((m[i - 1] >> 16) & 1);
                m[i - 1] &= 0xffff;
            }
            m[15] = t[15] - 0x7fff - ((m[14] >> 16) & 1);
            long b = (m[15] >> 16) & 1;
            m[14] &= 0xffff;
            CSwap((int)(1 - b), t, m);
        }

        for (int i = 0; i < 16; i++)
        {
            o[2 * i] = (byte)(t[i] & 0xff);
            o[2 * i + 1] = (byte)((t[i] >> 8) & 0xff);
        }
    }

    /// <summary>求逆 x^(p-2)（Fermat 小定理，加法链跳过 a=2 与 a=4）。</summary>
    static void Inv(long[] o, long[] i)
    {
        var c = new long[16];
        Copy(c, i);
        for (int a = 253; a >= 0; a--)
        {
            Sq(c, c);
            if (a != 2 && a != 4) Mul(c, c, i);
        }
        Copy(o, c);
    }

    // ================= X25519 =================

    /// <summary>标量乘法 X25519(scalar, point)，输出 32 字节小端 u 坐标。</summary>
    public static byte[] ScalarMult(byte[] scalar32, byte[] point32)
    {
        if (scalar32 == null || scalar32.Length != 32) throw new ArgumentException("scalar 需 32 字节", nameof(scalar32));
        if (point32 == null || point32.Length != 32) throw new ArgumentException("point 需 32 字节", nameof(point32));

        // clamp
        var k = new byte[32];
        Array.Copy(scalar32, k, 32);
        k[0] &= 248;
        k[31] &= 127;
        k[31] |= 64;

        var x1 = new long[16];
        Unpack(x1, point32);

        var x2 = new long[16]; var z2 = new long[16];
        var x3 = new long[16]; var z3 = new long[16];
        x2[0] = 1;                      // x2 = 1
        Copy(x3, x1);                   // x3 = x1
        z3[0] = 1;                      // z3 = 1

        // 循环外预分配，避免 254 轮反复分配（AOT + 少 GC 抖动）
        var A = new long[16]; var AA = new long[16];
        var B = new long[16]; var BB = new long[16];
        var E = new long[16];
        var C = new long[16]; var D = new long[16];
        var DA = new long[16]; var CB = new long[16];
        var t1 = new long[16]; var t2 = new long[16];

        int swap = 0;
        for (int t = 254; t >= 0; t--)
        {
            int kt = (k[t >> 3] >> (t & 7)) & 1;
            swap ^= kt;
            CSwap(swap, x2, x3);
            CSwap(swap, z2, z3);
            swap = kt;

            Add(A, x2, z2);
            Sq(AA, A);
            Sub(B, x2, z2);
            Sq(BB, B);
            Sub(E, AA, BB);
            Add(C, x3, z3);
            Sub(D, x3, z3);
            Mul(DA, D, A);
            Mul(CB, C, B);
            Add(t1, DA, CB);
            Sq(x3, t1);                    // x3 = (DA + CB)^2
            Sub(t2, DA, CB);
            Sq(t2, t2);
            Mul(z3, x1, t2);               // z3 = x1 * (DA - CB)^2
            Mul(x2, AA, BB);               // x2 = AA * BB
            Mul(t1, E, A24Scalar());       // a24 * E
            Add(t1, AA, t1);
            Mul(z2, E, t1);                // z2 = E * (AA + a24*E)
        }

        CSwap(swap, x2, x3);
        CSwap(swap, z2, z3);

        Inv(z2, z2);
        Mul(x2, x2, z2);

        var outb = new byte[32];
        Pack(outb, x2);
        return outb;
    }

    /// <summary>a24 = 121665 的字段元素表示（复用静态缓冲，避免每轮分配）。</summary>
    static long[] _a24;
    static long[] A24Scalar()
    {
        if (_a24 == null)
        {
            var v = new long[16];
            v[0] = A24;
            _a24 = v;
        }
        return _a24;
    }

    /// <summary>X25519(scalar, 9)：由私钥导出公钥。</summary>
    public static byte[] PublicFrom(byte[] priv32) => ScalarMult(priv32, BasePoint());

    /// <summary>ECDH：本端私钥 × 对端公钥 → 32 字节共享密钥。</summary>
    public static byte[] SharedSecret(byte[] myPriv32, byte[] peerPub32) => ScalarMult(myPriv32, peerPub32);

    // ================= 随机数（纯托管 CSPRNG） =================

    /// <summary>
    /// 生成 32 字节私钥（已 clamp，可直接用于 ScalarMult / PublicFrom）。
    /// 熵源：Guid.NewGuid()（.NET 内部即 CSPRNG）多份 + 时间/计数器/内存指标，
    /// 再用 SHA-256 混合并做 HMAC-DRBG 式扩展。
    /// </summary>
    public static byte[] GeneratePrivate()
    {
        var seed = EntropySeed();
        var outb = NetSha256.Hmac(seed, NetSha256.HkdfStr(seed, null, "pvzhe-priv", 32));
        // clamp（RFC 7748）
        outb[0] &= 248;
        outb[31] &= 127;
        outb[31] |= 64;
        return outb;
    }

    /// <summary>生成任意长度随机字节（房间密钥等）。</summary>
    public static byte[] RandomBytes(int n)
    {
        if (n <= 0) return new byte[0];
        var seed = EntropySeed();
        // 输出 = HKDF(seed, salt=seed, info) —— 与上一次调用不可能重复（seed 含 Guid）
        return NetSha256.HkdfStr(seed, seed, "pvzhe-rand", n);
    }

    /// <summary>
    /// 采集熵并 SHA-256 混合成 32 字节种子。
    /// ★★ 只用 Guid.NewGuid()（.NET 内部即 OS CSPRNG）与 DateTime.UtcNow.Ticks。
    ///   曾用过 GC.GetTotalMemory / Stopwatch.GetTimestamp / object.GetHashCode，
    ///   这些在 Godot Mono AOT 下可能被裁剪 → 抛异常 → 整个握手静默失败
    ///   （异常被 Poll 的 catch 吃掉，日志缓冲还可能再吞一次，极难排查）。
    /// </summary>
    static byte[] EntropySeed()
    {
        var buf = new byte[32 * 8];
        int p = 0;
        for (int i = 0; i < 8; i++)
        {
            var g = Guid.NewGuid().ToByteArray();
            Array.Copy(g, 0, buf, p, 16);
            p += 16;
        }
        WriteI64(buf, ref p, DateTime.UtcNow.Ticks);
        WriteI64(buf, ref p, DateTime.UtcNow.Ticks);

        var raw = new byte[p];
        Array.Copy(buf, 0, raw, 0, p);
        var h1 = NetSha256.Hash(raw);
        var h2 = NetSha256.Hash(NetSha256.Hmac(h1, raw));
        var seed = new byte[32];
        for (int i = 0; i < 32; i++) seed[i] = (byte)(h1[i] ^ h2[i]);
        return seed;
    }

    static void WriteI64(byte[] b, ref int p, long v)
    {
        for (int i = 0; i < 8; i++) b[p + i] = (byte)(v >> (8 * i));
        p += 8;
    }

    /// <summary>把共享密钥派生成对称密钥（用途隔离）。</summary>
    public static byte[] DeriveKey(byte[] sharedSecret, string info, int outLen)
        => NetSha256.HkdfStr(sharedSecret, null, info, outLen);
}
