using System;

/// <summary>
/// 纯托管 AES-256-GCM（认证加密）。
/// ★ 严禁改用 System.Security.Cryptography.AesGcm：手机版 Godot Mono AOT 下该类型
///   依赖原生库且会被裁剪（见 NetCrypto.cs 同款约定）。
/// 实现范围：AES-256（Nr=14）块加密 + CTR + GHASH + GCM AEAD。
/// 只实现加密方向（CTR 与 GCM 都只需要正向块加密），因此不含逆 S-box / 解密轮。
/// 正确性由 NIST GCM 标准测试向量保证（relay/PvzheCryptoTests）。
///
/// 线程模型：一个实例持有一份轮密钥与复用缓冲，**不保证线程安全**。
/// 协议层应每个方向（或每条连接）各持一个实例。
/// </summary>
public sealed class NetAesGcm
{
    // ---------- AES S-box（FIPS 197） ----------
    static readonly byte[] Sbox = new byte[256]
    {
        0x63,0x7c,0x77,0x7b,0xf2,0x6b,0x6f,0xc5,0x30,0x01,0x67,0x2b,0xfe,0xd7,0xab,0x76,
        0xca,0x82,0xc9,0x7d,0xfa,0x59,0x47,0xf0,0xad,0xd4,0xa2,0xaf,0x9c,0xa4,0x72,0xc0,
        0xb7,0xfd,0x93,0x26,0x36,0x3f,0xf7,0xcc,0x34,0xa5,0xe5,0xf1,0x71,0xd8,0x31,0x15,
        0x04,0xc7,0x23,0xc3,0x18,0x96,0x05,0x9a,0x07,0x12,0x80,0xe2,0xeb,0x27,0xb2,0x75,
        0x09,0x83,0x2c,0x1a,0x1b,0x6e,0x5a,0xa0,0x52,0x3b,0xd6,0xb3,0x29,0xe3,0x2f,0x84,
        0x53,0xd1,0x00,0xed,0x20,0xfc,0xb1,0x5b,0x6a,0xcb,0xbe,0x39,0x4a,0x4c,0x58,0xcf,
        0xd0,0xef,0xaa,0xfb,0x43,0x4d,0x33,0x85,0x45,0xf9,0x02,0x7f,0x50,0x3c,0x9f,0xa8,
        0x51,0xa3,0x40,0x8f,0x92,0x9d,0x38,0xf5,0xbc,0xb6,0xda,0x21,0x10,0xff,0xf3,0xd2,
        0xcd,0x0c,0x13,0xec,0x5f,0x97,0x44,0x17,0xc4,0xa7,0x7e,0x3d,0x64,0x5d,0x19,0x73,
        0x60,0x81,0x4f,0xdc,0x22,0x2a,0x90,0x88,0x46,0xee,0xb8,0x14,0xde,0x5e,0x0b,0xdb,
        0xe0,0x32,0x3a,0x0a,0x49,0x06,0x24,0x5c,0xc2,0xd3,0xac,0x62,0x91,0x95,0xe4,0x79,
        0xe7,0xc8,0x37,0x6d,0x8d,0xd5,0x4e,0xa9,0x6c,0x56,0xf4,0xea,0x65,0x7a,0xae,0x08,
        0xba,0x78,0x25,0x2e,0x1c,0xa6,0xb4,0xc6,0xe8,0xdd,0x74,0x1f,0x4b,0xbd,0x8b,0x8a,
        0x70,0x3e,0xb5,0x66,0x48,0x03,0xf6,0x0e,0x61,0x35,0x57,0xb9,0x86,0xc1,0x1d,0x9e,
        0xe1,0xf8,0x98,0x11,0x69,0xd9,0x8e,0x94,0x9b,0x1e,0x87,0xe9,0xce,0x55,0x28,0xdf,
        0x8c,0xa1,0x89,0x0d,0xbf,0xe6,0x42,0x68,0x41,0x99,0x2d,0x0f,0xb0,0x54,0xbb,0x16,
    };

    /// <summary>轮常量（AES-256 需要 rcon[1..7]）。</summary>
    static readonly byte[] Rcon = { 0x00, 0x01, 0x02, 0x04, 0x08, 0x10, 0x20, 0x40, 0x80, 0x1b, 0x36 };

    const int Nr = 14;         // AES-256 轮数
    const int Nk = 8;          // AES-256 密钥字数
    const int RkWords = 4 * (Nr + 1);   // 60

    readonly uint[] _rk = new uint[RkWords];
    readonly byte[] _s = new byte[16];   // 轮状态（列主序 + 行位移暂存别处）
    readonly byte[] _t = new byte[16];
    readonly byte[] _h = new byte[16];   // GHASH 子密钥 H = E(K, 0^16)

    public NetAesGcm(byte[] key32)
    {
        if (key32 == null || key32.Length != 32)
            throw new ArgumentException("AES-256 需要 32 字节密钥", nameof(key32));
        KeyExpansion(key32);
        BlocksEncryptRaw(_h, 0, _h, 0);
    }

    // ================= AES 核心 =================

    static byte Xtime(byte x) => (byte)((x << 1) ^ ((x & 0x80) != 0 ? 0x1b : 0));

    static uint SubWord(uint w) => (uint)(
        (Sbox[(w >> 24) & 0xff] << 24) | (Sbox[(w >> 16) & 0xff] << 16) |
        (Sbox[(w >> 8) & 0xff] << 8) | Sbox[w & 0xff]);

    void KeyExpansion(byte[] key)
    {
        for (int i = 0; i < Nk; i++)
            _rk[i] = (uint)((key[i * 4] << 24) | (key[i * 4 + 1] << 16) | (key[i * 4 + 2] << 8) | key[i * 4 + 3]);

        for (int i = Nk; i < RkWords; i++)
        {
            uint temp = _rk[i - 1];
            if (i % Nk == 0)
            {
                temp = (temp << 8) | (temp >> 24);      // RotWord
                temp = SubWord(temp);
                temp ^= (uint)(Rcon[i / Nk] << 24);
            }
            else if (i % Nk == 4)
            {
                temp = SubWord(temp);                    // AES-256 额外 SubWord
            }
            _rk[i] = _rk[i - Nk] ^ temp;
        }
    }

    /// <summary>加密一个 16 字节块。src/dst 可同缓冲。</summary>
    void BlocksEncryptRaw(byte[] src, int si, byte[] dst, int di)
    {
        // 初始 AddRoundKey（列主序：state[c*4+r] 对应 input[r + 4c]）
        for (int c = 0; c < 4; c++)
        {
            uint k = _rk[c];
            _s[c * 4] = (byte)(src[si + c * 4] ^ (byte)(k >> 24));
            _s[c * 4 + 1] = (byte)(src[si + c * 4 + 1] ^ (byte)(k >> 16));
            _s[c * 4 + 2] = (byte)(src[si + c * 4 + 2] ^ (byte)(k >> 8));
            _s[c * 4 + 3] = (byte)(src[si + c * 4 + 3] ^ (byte)k);
        }

        for (int round = 1; round <= Nr; round++)
        {
            // SubBytes + ShiftRows：out[r][c] = Sbox(in[r][(c+r) mod 4])
            for (int c = 0; c < 4; c++)
            {
                for (int r = 0; r < 4; r++)
                    _t[c * 4 + r] = Sbox[_s[(((c + r) & 3) * 4) + r]];
            }

            if (round != Nr)
            {
                // MixColumns
                for (int c = 0; c < 4; c++)
                {
                    byte a0 = _t[c * 4], a1 = _t[c * 4 + 1], a2 = _t[c * 4 + 2], a3 = _t[c * 4 + 3];
                    _s[c * 4] = (byte)(Xtime(a0) ^ Xtime(a1) ^ a1 ^ a2 ^ a3);
                    _s[c * 4 + 1] = (byte)(a0 ^ Xtime(a1) ^ Xtime(a2) ^ a2 ^ a3);
                    _s[c * 4 + 2] = (byte)(a0 ^ a1 ^ Xtime(a2) ^ Xtime(a3) ^ a3);
                    _s[c * 4 + 3] = (byte)(Xtime(a0) ^ a0 ^ a1 ^ a2 ^ Xtime(a3));
                }
            }
            else
            {
                for (int i = 0; i < 16; i++) _s[i] = _t[i];
            }

            // AddRoundKey
            for (int c = 0; c < 4; c++)
            {
                uint k = _rk[round * 4 + c];
                _s[c * 4] ^= (byte)(k >> 24);
                _s[c * 4 + 1] ^= (byte)(k >> 16);
                _s[c * 4 + 2] ^= (byte)(k >> 8);
                _s[c * 4 + 3] ^= (byte)k;
            }
        }

        for (int i = 0; i < 16; i++) dst[di + i] = _s[i];
    }

    // ================= CTR =================

    /// <summary>
    /// CTR 模式异或（加密与解密同一操作）。iv 为 12 字节前缀，counter 为 4 字节大端计数器起始值。
    /// data 原地修改。
    /// </summary>
    public void CtrXor(byte[] iv12, uint counter, byte[] data, int off, int len)
    {
        if (data == null || len <= 0) return;
        var ctr = new byte[16];
        var ks = new byte[16];
        for (int i = 0; i < 12; i++) ctr[i] = iv12[i];

        int pos = off, end = off + len;
        uint c = counter;
        while (pos < end)
        {
            ctr[12] = (byte)(c >> 24);
            ctr[13] = (byte)(c >> 16);
            ctr[14] = (byte)(c >> 8);
            ctr[15] = (byte)c;
            BlocksEncryptRaw(ctr, 0, ks, 0);

            int n = (end - pos) < 16 ? (end - pos) : 16;
            for (int i = 0; i < n; i++) data[pos + i] ^= ks[i];
            pos += n;
            c++;
        }
    }

    // ================= GHASH =================

    void Xor16(byte[] a, byte[] b)
    {
        for (int i = 0; i < 16; i++) a[i] ^= b[i];
    }

    /// <summary>
    /// GHASH 单步：y = (y ^ x) * H。
    /// ★ 必须「先异或再乘」，不是「y ^= x*H」—— 两者不等价，写成后者会让 tag 全错
    ///   （NIST 向量 TC14/TC15/TC16 会立即暴露，而空输入的 TC13 依然通过，很有迷惑性）。
    /// </summary>
    void GfMulStep(byte[] y, byte[] x)
    {
        var t = new byte[16];
        for (int i = 0; i < 16; i++) t[i] = (byte)(y[i] ^ x[i]);

        var z = new byte[16];
        var v = new byte[16];
        for (int i = 0; i < 16; i++) v[i] = _h[i];

        for (int i = 0; i < 128; i++)
        {
            if ((t[i >> 3] & (0x80 >> (i & 7))) != 0) Xor16(z, v);
            bool lsb = (v[15] & 1) != 0;
            for (int j = 15; j > 0; j--) v[j] = (byte)((v[j] >> 1) | ((v[j - 1] & 1) << 7));
            v[0] = (byte)(v[0] >> 1);
            if (lsb) v[0] ^= 0xe1;
        }
        for (int i = 0; i < 16; i++) y[i] = z[i];
    }

    void GHashBlocks(byte[] y, byte[] data, int off, int len)
    {
        var blk = new byte[16];
        int pos = off, end = off + len;
        while (pos < end)
        {
            for (int i = 0; i < 16; i++) blk[i] = 0;
            int n = (end - pos) < 16 ? (end - pos) : 16;
            for (int i = 0; i < n; i++) blk[i] = data[pos + i];
            GfMulStep(y, blk);
            pos += n;
        }
    }

    static void WriteU64Be(byte[] b, int off, ulong v)
    {
        for (int i = 0; i < 8; i++) b[off + i] = (byte)(v >> (56 - 8 * i));
    }

    /// <summary>S = GHASH(H, A, C)，含尾部长度块。</summary>
    void GHash(byte[] y, byte[] aad, int aOff, int aLen, byte[] cipher, int cOff, int cLen)
    {
        for (int i = 0; i < 16; i++) y[i] = 0;
        if (aLen > 0) GHashBlocks(y, aad, aOff, aLen);
        if (cLen > 0) GHashBlocks(y, cipher, cOff, cLen);

        var lb = new byte[16];
        WriteU64Be(lb, 0, (ulong)aLen * 8UL);
        WriteU64Be(lb, 8, (ulong)cLen * 8UL);
        GfMulStep(y, lb);
    }

    static void Inc32(byte[] b)
    {
        uint c = (uint)((b[12] << 24) | (b[13] << 16) | (b[14] << 8) | b[15]);
        c++;
        b[12] = (byte)(c >> 24); b[13] = (byte)(c >> 16); b[14] = (byte)(c >> 8); b[15] = (byte)c;
    }

    /// <summary>
    /// 计算 J0。96 位 IV 直接用 IV||0x00000001；其他长度按规范用 GHASH 派生。
    /// </summary>
    void DeriveJ0(byte[] iv, byte[] j0)
    {
        if (iv != null && iv.Length == 12)
        {
            for (int i = 0; i < 12; i++) j0[i] = iv[i];
            j0[12] = 0; j0[13] = 0; j0[14] = 0; j0[15] = 1;
        }
        else
        {
            // J0 = GHASH(H, {}, IV)
            for (int i = 0; i < 16; i++) j0[i] = 0;
            int len = iv != null ? iv.Length : 0;
            if (len > 0) GHashBlocks(j0, iv, 0, len);
            var lb = new byte[16];
            WriteU64Be(lb, 8, (ulong)len * 8UL);
            GfMulStep(j0, lb);
        }
    }

    // ================= GCM AEAD =================

    /// <summary>
    /// 认证加密。返回 密文 || tag16（长度 = plain.Length + 16）。
    /// nonce 推荐 12 字节；aad 可为 null。
    /// </summary>
    public byte[] Seal(byte[] nonce, byte[] aad, byte[] plain)
    {
        int pLen = plain != null ? plain.Length : 0;
        int aLen = aad != null ? aad.Length : 0;

        var j0 = new byte[16];
        DeriveJ0(nonce, j0);

        var outBuf = new byte[pLen + 16];

        // C = GCTR(K, inc32(J0), P)
        var ctr = new byte[16];
        for (int i = 0; i < 16; i++) ctr[i] = j0[i];
        Inc32(ctr);

        var ks = new byte[16];
        int pos = 0;
        uint c = (uint)((ctr[12] << 24) | (ctr[13] << 16) | (ctr[14] << 8) | ctr[15]);
        var ctrBlock = new byte[16];
        for (int i = 0; i < 12; i++) ctrBlock[i] = ctr[i];
        while (pos < pLen)
        {
            ctrBlock[12] = (byte)(c >> 24); ctrBlock[13] = (byte)(c >> 16);
            ctrBlock[14] = (byte)(c >> 8); ctrBlock[15] = (byte)c;
            BlocksEncryptRaw(ctrBlock, 0, ks, 0);
            int n = (pLen - pos) < 16 ? (pLen - pos) : 16;
            for (int i = 0; i < n; i++) outBuf[pos + i] = (byte)(plain[pos + i] ^ ks[i]);
            pos += n; c++;
        }

        // T = E(K, J0) ^ GHASH(H, A, C)
        var s = new byte[16];
        GHash(s, aad, 0, aLen, outBuf, 0, pLen);
        var ej0 = new byte[16];
        BlocksEncryptRaw(j0, 0, ej0, 0);
        for (int i = 0; i < 16; i++) outBuf[pLen + i] = (byte)(s[i] ^ ej0[i]);

        return outBuf;
    }

    /// <summary>
    /// 认证解密。sealedData 为 密文||tag16。认证失败返回 null（绝不返回未认证明文）。
    /// </summary>
    public byte[] Open(byte[] nonce, byte[] aad, byte[] sealedData)
    {
        if (sealedData == null || sealedData.Length < 16) return null;
        int pLen = sealedData.Length - 16;
        int aLen = aad != null ? aad.Length : 0;

        var j0 = new byte[16];
        DeriveJ0(nonce, j0);

        // 先校验 tag（先认证后解密）
        var s = new byte[16];
        GHash(s, aad, 0, aLen, sealedData, 0, pLen);
        var ej0 = new byte[16];
        BlocksEncryptRaw(j0, 0, ej0, 0);
        var want = new byte[16];
        for (int i = 0; i < 16; i++) want[i] = (byte)(s[i] ^ ej0[i]);

        int diff = 0;
        for (int i = 0; i < 16; i++) diff |= want[i] ^ sealedData[pLen + i];
        if (diff != 0) return null;

        var plain = new byte[pLen];
        var ctr = new byte[16];
        for (int i = 0; i < 16; i++) ctr[i] = j0[i];
        Inc32(ctr);

        var ctrBlock = new byte[16];
        for (int i = 0; i < 12; i++) ctrBlock[i] = ctr[i];
        uint c = (uint)((ctr[12] << 24) | (ctr[13] << 16) | (ctr[14] << 8) | ctr[15]);

        var ks = new byte[16];
        int pos = 0;
        while (pos < pLen)
        {
            ctrBlock[12] = (byte)(c >> 24); ctrBlock[13] = (byte)(c >> 16);
            ctrBlock[14] = (byte)(c >> 8); ctrBlock[15] = (byte)c;
            BlocksEncryptRaw(ctrBlock, 0, ks, 0);
            int n = (pLen - pos) < 16 ? (pLen - pos) : 16;
            for (int i = 0; i < n; i++) plain[pos + i] = (byte)(sealedData[pos + i] ^ ks[i]);
            pos += n; c++;
        }
        return plain;
    }

    // ================= 低层便捷封装（协议层用） =================

    /// <summary>
    /// 用「4 字节方向前缀 + 8 字节序号」组成 12 字节 nonce 加密（序号单调递增，天然防重放）。
    /// aad 可为 null。
    /// </summary>
    public static byte[] SealWithSeq(byte[] key32, uint prefix, ulong seq, byte[] aad, byte[] plain)
    {
        var nonce = MakeNonce(prefix, seq);
        var aes = new NetAesGcm(key32);
        return aes.Seal(nonce, aad, plain);
    }

    /// <summary>配合 SealWithSeq 的解密；失败返回 null。</summary>
    public static byte[] OpenWithSeq(byte[] key32, uint prefix, ulong seq, byte[] aad, byte[] sealedData)
    {
        var nonce = MakeNonce(prefix, seq);
        var aes = new NetAesGcm(key32);
        return aes.Open(nonce, aad, sealedData);
    }

    static byte[] MakeNonce(uint prefix, ulong seq)
    {
        var n = new byte[12];
        n[0] = (byte)(prefix >> 24); n[1] = (byte)(prefix >> 16);
        n[2] = (byte)(prefix >> 8); n[3] = (byte)prefix;
        for (int i = 0; i < 8; i++) n[4 + i] = (byte)(seq >> (56 - 8 * i));
        return n;
    }
}
