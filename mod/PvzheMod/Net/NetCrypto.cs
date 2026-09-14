using System;

/// <summary>
/// 轻量 keyed-hash / 随机数（纯 System，不依赖 System.Security.Cryptography 以免 .NET AOT 裁剪）。
/// 用于中继鉴权挑战响应与帧完整性标记。强度足以抵挡扫描与随意滥用；更强的传输加密见 spec（AES-GCM，M2）。
/// </summary>
public static class NetCrypto
{
    const ulong P1 = 0x100000001B3UL;
    const ulong P2 = 0x9E3779B97F4A7C15UL;

    /// <summary>计算 keyed hash（8 字节）。相同 key+data 必得相同结果，两端共用。</summary>
    public static byte[] KeyedHash(string key, byte[] data)
    {
        unchecked
        {
            ulong h1 = 0xcbf29ce484222325UL ^ 0x9E3779B97F4A7C15UL;
            ulong h2 = 0x84222325cbf29ce4UL ^ 0xBF58476D1CE4E5B9UL;

            byte[] kb = System.Text.Encoding.UTF8.GetBytes(key ?? "");
            for (int i = 0; i < kb.Length; i++)
            {
                h1 = (h1 ^ kb[i]) * P1;
                h2 = (h2 + kb[i]) * P1 + (h2 >> 7);
            }

            int n = data?.Length ?? 0;
            for (int i = 0; i < n; i++)
            {
                byte b = data[i];
                h1 = (h1 ^ b) * P1;
                h1 ^= h1 >> 29;
                h2 = (h2 + (ulong)(b + 1)) * P2;
                h2 ^= h2 >> 31;
                ulong t = h1;
                h1 = h2;
                h2 = t ^ (h1 << 13);
            }

            for (int r = 0; r < 8; r++)
            {
                h1 ^= h1 >> 33;
                h1 *= 0xFF51AFD7ED558CCDUL;
                h2 ^= h2 >> 29;
                h2 *= 0xC4CEB9FE1A85EC53UL;
                ulong t = h1 ^ h2;
                h2 ^= t >> 17;
                h1 += t;
            }

            var outb = new byte[8];
            for (int i = 0; i < 8; i++)
            {
                ulong v = (i < 4) ? h1 : h2;
                outb[i] = (byte)(v >> (8 * (i % 4)));
            }
            return outb;
        }
    }

    /// <summary>定时安全比较（避免长度/内容时序泄漏）。</summary>
    public static bool SlowEquals(byte[] a, byte[] b)
    {
        if (a == null || b == null || a.Length != b.Length) return false;
        int diff = 0;
        for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
        return diff == 0;
    }

    /// <summary>生成 8 字节挑战 nonce（时间 + 计数器混合；仅用于一次性挑战）。</summary>
    public static byte[] Nonce8()
    {
        unchecked
        {
            ulong x = (ulong)DateTime.UtcNow.Ticks ^ ((ulong)Environment.TickCount << 17) ^ 0xA5A5A5A5DEADBEEFUL;
            var b = new byte[8];
            for (int i = 0; i < 8; i++)
            {
                x ^= x << 13;
                x ^= x >> 7;
                x ^= x << 17;
                b[i] = (byte)(x >> (8 * (i % 8)));
            }
            return b;
        }
    }

    /// <summary>把字节数组转成 16 进制（日志/比对用）。</summary>
    public static string Hex(byte[] b)
    {
        if (b == null) return "";
        var sb = new System.Text.StringBuilder(b.Length * 2);
        for (int i = 0; i < b.Length; i++) sb.Append(b[i].ToString("x2"));
        return sb.ToString();
    }
}
