using System;
using System.Text;

/// <summary>
/// 纯托管 SHA-256 / HMAC-SHA256 / HKDF-SHA256。
/// ★ 严禁改成 System.Security.Cryptography：手机版是 Godot Mono AOT，
///   BCL 密码学类型会被裁剪/依赖原生库而不可用（见 NetCrypto.cs 同款约定）。
/// 正确性由 RFC 4231 / RFC 5869 测试向量保证（relay/PvzheCryptoTests）。
/// </summary>
public static class NetSha256
{
    static readonly uint[] K = new uint[64]
    {
        0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
        0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
        0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
        0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
        0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
        0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
        0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
        0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2,
    };

    static uint Rotr(uint x, int n) => (x >> n) | (x << (32 - n));

    /// <summary>SHA-256，输出 32 字节。</summary>
    public static byte[] Hash(byte[] data)
    {
        uint h0 = 0x6a09e667, h1 = 0xbb67ae85, h2 = 0x3c6ef372, h3 = 0xa54ff53a;
        uint h4 = 0x510e527f, h5 = 0x9b05688c, h6 = 0x1f83d9ab, h7 = 0x5be0cd19;

        int len = data != null ? data.Length : 0;
        long bitLen = (long)len * 8L;

        // 填充：1 个 0x80 + 若干 0 + 8 字节大端长度，使总长为 64 的倍数
        int rem = len & 63;
        int padLen = rem < 56 ? (56 - rem) : (120 - rem);
        var buf = new byte[len + padLen + 8];
        if (len > 0) Array.Copy(data, 0, buf, 0, len);
        buf[len] = 0x80;
        for (int i = 0; i < 8; i++) buf[buf.Length - 1 - i] = (byte)(bitLen >> (8 * i));

        var w = new uint[64];
        for (int off = 0; off < buf.Length; off += 64)
        {
            for (int i = 0; i < 16; i++)
            {
                int p = off + i * 4;
                w[i] = (uint)((buf[p] << 24) | (buf[p + 1] << 16) | (buf[p + 2] << 8) | buf[p + 3]);
            }
            for (int i = 16; i < 64; i++)
            {
                uint x = w[i - 15], y = w[i - 2];
                uint s0 = Rotr(x, 7) ^ Rotr(x, 18) ^ (x >> 3);
                uint s1 = Rotr(y, 17) ^ Rotr(y, 19) ^ (y >> 10);
                w[i] = unchecked(w[i - 16] + s0 + w[i - 7] + s1);
            }

            uint a = h0, b = h1, c = h2, d = h3, e = h4, f = h5, g = h6, h = h7;
            for (int i = 0; i < 64; i++)
            {
                uint s1 = Rotr(e, 6) ^ Rotr(e, 11) ^ Rotr(e, 25);
                uint ch = (e & f) ^ (~e & g);
                uint t1 = unchecked(h + s1 + ch + K[i] + w[i]);
                uint s0 = Rotr(a, 2) ^ Rotr(a, 13) ^ Rotr(a, 22);
                uint maj = (a & b) ^ (a & c) ^ (b & c);
                uint t2 = unchecked(s0 + maj);
                h = g; g = f; f = e; e = unchecked(d + t1); d = c; c = b; b = a; a = unchecked(t1 + t2);
            }
            h0 = unchecked(h0 + a); h1 = unchecked(h1 + b); h2 = unchecked(h2 + c); h3 = unchecked(h3 + d);
            h4 = unchecked(h4 + e); h5 = unchecked(h5 + f); h6 = unchecked(h6 + g); h7 = unchecked(h7 + h);
        }

        var outb = new byte[32];
        uint[] hs = { h0, h1, h2, h3, h4, h5, h6, h7 };
        for (int i = 0; i < 8; i++)
        {
            outb[i * 4] = (byte)(hs[i] >> 24);
            outb[i * 4 + 1] = (byte)(hs[i] >> 16);
            outb[i * 4 + 2] = (byte)(hs[i] >> 8);
            outb[i * 4 + 3] = (byte)hs[i];
        }
        return outb;
    }

    /// <summary>HMAC-SHA256，输出 32 字节。</summary>
    public static byte[] Hmac(byte[] key, byte[] data)
    {
        byte[] k = key;
        if (k == null) k = new byte[0];
        if (k.Length > 64) k = Hash(k);

        var ipad = new byte[64];
        var opad = new byte[64];
        for (int i = 0; i < 64; i++)
        {
            byte kb = i < k.Length ? k[i] : (byte)0;
            ipad[i] = (byte)(kb ^ 0x36);
            opad[i] = (byte)(kb ^ 0x5c);
        }

        int dl = data != null ? data.Length : 0;
        var inner = new byte[64 + dl];
        Array.Copy(ipad, 0, inner, 0, 64);
        if (dl > 0) Array.Copy(data, 0, inner, 64, dl);
        var ih = Hash(inner);

        var outer = new byte[64 + 32];
        Array.Copy(opad, 0, outer, 0, 64);
        Array.Copy(ih, 0, outer, 64, 32);
        return Hash(outer);
    }

    /// <summary>
    /// HMAC-SHA256（字符串 key 版）。
    /// ★ 绝对不要与 byte[] 版做成重载：注入器按「类型名.方法名」映射复制方法体，
    ///   同名重载会配错指令流 → 坏 IL（call 操作数变 null/指向别的指令）。
    /// </summary>
    public static byte[] HmacStr(string key, byte[] data)
        => Hmac(Encoding.UTF8.GetBytes(key ?? ""), data);

    /// <summary>HKDF-SHA256（info 为字符串版）。★ 同样禁止与 byte[] 版构成重载，见 HmacStr 注释。</summary>
    public static byte[] HkdfStr(byte[] ikm, byte[] salt, string info, int outLen)
        => Hkdf(ikm, salt, Encoding.UTF8.GetBytes(info ?? ""), outLen);

    /// <summary>
    /// HKDF-SHA256（RFC 5869 extract-then-expand）。
    /// salt 为空时按 RFC 用 32 字节全 0；info 用于用途隔离（domain separation）。
    /// </summary>
    public static byte[] Hkdf(byte[] ikm, byte[] salt, byte[] infoB, int outLen)
    {
        if (outLen <= 0 || outLen > 255 * 32) throw new ArgumentOutOfRangeException(nameof(outLen));
        if (salt == null || salt.Length == 0) salt = new byte[32];

        var prk = Hmac(salt, ikm ?? new byte[0]);
        if (infoB == null) infoB = new byte[0];

        var outb = new byte[outLen];
        var t = new byte[0];
        int pos = 0;
        byte counter = 1;
        while (pos < outLen)
        {
            var buf = new byte[t.Length + infoB.Length + 1];
            Array.Copy(t, 0, buf, 0, t.Length);
            Array.Copy(infoB, 0, buf, t.Length, infoB.Length);
            buf[buf.Length - 1] = counter;
            t = Hmac(prk, buf);
            int n = t.Length < (outLen - pos) ? t.Length : (outLen - pos);
            Array.Copy(t, 0, outb, pos, n);
            pos += n;
            counter++;
        }
        return outb;
    }

    /// <summary>常量时间比较（防时序侧信道）。</summary>
    public static bool FixedEquals(byte[] a, byte[] b)
    {
        if (a == null || b == null || a.Length != b.Length) return false;
        int diff = 0;
        for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
        return diff == 0;
    }

    /// <summary>字节数组转小写 hex（日志/指纹显示用）。</summary>
    public static string Hex(byte[] b)
    {
        if (b == null) return "";
        var sb = new StringBuilder(b.Length * 2);
        for (int i = 0; i < b.Length; i++) sb.Append(b[i].ToString("x2"));
        return sb.ToString();
    }

    /// <summary>hex 字符串转字节（测试向量用；长度非法返回 null）。</summary>
    public static byte[] FromHex(string s)
    {
        if (s == null) return null;
        var t = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == ' ' || c == '\n' || c == '\r' || c == '\t' || c == '-') continue;
            t.Append(c);
        }
        if ((t.Length & 1) != 0) return null;
        var r = new byte[t.Length / 2];
        for (int i = 0; i < r.Length; i++)
        {
            int hi = HexVal(t[i * 2]);
            int lo = HexVal(t[i * 2 + 1]);
            if (hi < 0 || lo < 0) return null;
            r[i] = (byte)((hi << 4) | lo);
        }
        return r;
    }

    static int HexVal(char c)
    {
        if (c >= '0' && c <= '9') return c - '0';
        if (c >= 'a' && c <= 'f') return c - 'a' + 10;
        if (c >= 'A' && c <= 'F') return c - 'A' + 10;
        return -1;
    }
}
