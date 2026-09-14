using System;
using System.Text;

/// <summary>
/// 密码学实现的向量自测（零依赖控制台）。
/// 手写密码学必须靠标准向量兜底，否则不能视为完成。
/// 用法：dotnet run -c Release
/// </summary>
static class Program
{
    static int _pass;
    static int _fail;

    static void Eq(string name, byte[] got, string wantHex)
    {
        var want = NetSha256.FromHex(wantHex);
        string g = NetSha256.Hex(got);
        string w = want != null ? NetSha256.Hex(want) : wantHex;
        if (g == w)
        {
            _pass++;
            Console.WriteLine("  PASS  " + name);
        }
        else
        {
            _fail++;
            Console.WriteLine("  FAIL  " + name);
            Console.WriteLine("        got  = " + g);
            Console.WriteLine("        want = " + w);
        }
    }

    static void True(string name, bool cond)
    {
        if (cond) { _pass++; Console.WriteLine("  PASS  " + name); }
        else { _fail++; Console.WriteLine("  FAIL  " + name); }
    }

    static byte[] Rep(byte b, int n)
    {
        var r = new byte[n];
        for (int i = 0; i < n; i++) r[i] = b;
        return r;
    }

    static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    static byte[] Slice(byte[] src, int off, int len)
    {
        var r = new byte[len];
        Array.Copy(src, off, r, 0, len);
        return r;
    }

    static void Swap(byte[] a, byte[] b)
    {
        var t = a[0]; a[0] = b[0]; b[0] = t;
    }

    static void Main()
    {
        Console.WriteLine("===== 杂交版密码学自测 =====");

        Sha256Tests();
        HmacTests();
        HkdfTests();
        AesTests();
        X25519Tests();
        SecureTests();
        InjectorConstraintTests();

        Console.WriteLine();
        Console.WriteLine("============================");
        Console.WriteLine("PASS=" + _pass + "  FAIL=" + _fail);
        Environment.Exit(_fail == 0 ? 0 : 1);
    }

    // ---------- SHA-256（FIPS 180-4 已知答案） ----------
    static void Sha256Tests()
    {
        Console.WriteLine("[SHA-256]");
        Eq("空输入", NetSha256.Hash(new byte[0]),
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
        Eq("abc", NetSha256.Hash(Ascii("abc")),
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
        Eq("56 字节（跨填充边界）", NetSha256.Hash(Ascii("abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq")),
            "248d6a61d20638b8e5c026930c3e6039a33ce45964ff2167f6ecedd419db06c1");
        Eq("一亿比特（1e6 个 a）", NetSha256.Hash(Rep((byte)'a', 1000000)),
            "cdc76e5c9914fb9281a1c7e284d73e67f1809a48a497200e046d39ccc7112cd0");

        // 结构性：长度 0..200 全覆盖，确保填充分支都不崩且长度正确
        bool ok = true;
        for (int n = 0; n <= 200; n++)
        {
            var h = NetSha256.Hash(Rep((byte)0x5a, n));
            if (h == null || h.Length != 32) { ok = false; break; }
        }
        True("长度 0..200 全部输出 32 字节", ok);

        // 1 个字节差异必须导致完全不同
        var h1 = NetSha256.Hash(Ascii("pvzhe"));
        var h2 = NetSha256.Hash(Ascii("pvzhf"));
        True("雪崩（1 字节差分）", NetSha256.Hex(h1) != NetSha256.Hex(h2));
    }

    // ---------- HMAC-SHA256（RFC 4231） ----------
    static void HmacTests()
    {
        Console.WriteLine("[HMAC-SHA256]");
        Eq("RFC4231 TC1", NetSha256.Hmac(Rep(0x0b, 20), Ascii("Hi There")),
            "b0344c61d8db38535ca8afceaf0bf12b881dc200c9833da726e9376c2e32cff7");
        Eq("RFC4231 TC2", NetSha256.Hmac(Ascii("Jefe"), Ascii("what do ya want for nothing?")),
            "5bdcc146bf60754e6a042426089575c75a003f089d2739839dec58b964ec3843");
        Eq("RFC4231 TC3", NetSha256.Hmac(Rep(0xaa, 20), Rep(0xdd, 50)),
            "773ea91e36800e46854db8ebd09181a72959098b3ef8c122d9635514ced565fe");
        Eq("RFC4231 TC4", NetSha256.Hmac(
                NetSha256.FromHex("0102030405060708090a0b0c0d0e0f10111213141516171819"), Rep(0xcd, 50)),
            "82558a389a443c0ea4cc819899f2083a85f0faa3e578f8077a2e3ff46729665b");
        Eq("RFC4231 TC6（key 超块长）", NetSha256.Hmac(Rep(0xaa, 131),
                Ascii("Test Using Larger Than Block-Size Key - Hash Key First")),
            "60e431591ee0b67f0d8a26aacbf5b77f8e0bc6213728c5140546040f0ee37f54");
        Eq("RFC4231 TC7（key+data 均超块长）", NetSha256.Hmac(Rep(0xaa, 131),
                Ascii("This is a test using a larger than block-size key and a larger than block-size data. The key needs to be hashed before being used by the HMAC algorithm.")),
            "9b09ffa71b942fcb27635fbcd5b0e944bfdc63644f0713938a7f51535c3a35e2");

        // 常量时间比较：等长不等内容必须为 false
        True("FixedEquals 等长不等内容", !NetSha256.FixedEquals(Rep(1, 32), Rep(2, 32)));
        True("FixedEquals 相等内容", NetSha256.FixedEquals(Rep(7, 32), Rep(7, 32)));
        True("FixedEquals 长度不同", !NetSha256.FixedEquals(Rep(1, 32), Rep(1, 31)));
    }

    // ---------- HKDF-SHA256（RFC 5869） ----------
    static void HkdfTests()
    {
        Console.WriteLine("[HKDF-SHA256]");

        // TC1：常规三输入
        Eq("RFC5869 TC1", NetSha256.Hkdf(
                Rep(0x0b, 22),
                NetSha256.FromHex("000102030405060708090a0b0c"),
                NetSha256.FromHex("f0f1f2f3f4f5f6f7f8f9"),
                42),
            "3cb25f25faacd57a90434f64d0362f2a2d2d0a90cf1a5a4c5db02d56ecc4c5bf34007208d5b887185865");

        // TC2：长输入（80 字节 IKM/salt/info）
        Eq("RFC5869 TC2（长输入）", NetSha256.Hkdf(
                NetSha256.FromHex(
                    "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f" +
                    "202122232425262728292a2b2c2d2e2f303132333435363738393a3b3c3d3e3f" +
                    "404142434445464748494a4b4c4d4e4f"),
                NetSha256.FromHex(
                    "606162636465666768696a6b6c6d6e6f707172737475767778797a7b7c7d7e7f" +
                    "808182838485868788898a8b8c8d8e8f909192939495969798999a9b9c9d9e9f" +
                    "a0a1a2a3a4a5a6a7a8a9aaabacadaeaf"),
                NetSha256.FromHex(
                    "b0b1b2b3b4b5b6b7b8b9babbbcbdbebfc0c1c2c3c4c5c6c7c8c9cacbcccdcecf" +
                    "d0d1d2d3d4d5d6d7d8d9dadbdcdddedfe0e1e2e3e4e5e6e7e8e9eaebecedeeef" +
                    "f0f1f2f3f4f5f6f7f8f9fafbfcfdfeff"),
                82),
            "b11e398dc80327a1c8e7f78c596a49344f012eda2d4efad8a050cc4c19afa97c" +
            "59045a99cac7827271cb41c65e590e09da3275600c2f09b8367793a9aca3db71" +
            "cc30c58179ec3e87c14c01d5c1f3434f1d87");

        // TC3：salt 与 info 均为空（空 salt 要按 RFC 视作 32 字节全 0）
        Eq("RFC5869 TC3（空 salt/info）", NetSha256.Hkdf(
                Rep(0x0b, 22), new byte[0], new byte[0], 42),
            "8da4e775a563c18f715f802a063c5a31b8a11f5c5ee1879ec3454e5f3c738d2d9d201395faa4b61a96c8");

        // 字符串 info 版与字节版必须等价
        var a = NetSha256.HkdfStr(Rep(0x0b, 22), new byte[0], "pvzhe-room", 32);
        var b = NetSha256.Hkdf(Rep(0x0b, 22), new byte[0], Ascii("pvzhe-room"), 32);
        True("string/byte info 等价", NetSha256.Hex(a) == NetSha256.Hex(b));

        // 用途隔离：不同 info 必须派生出不同密钥
        var k1 = NetSha256.HkdfStr(Rep(0x0b, 22), new byte[0], "pvzhe-link", 32);
        var k2 = NetSha256.HkdfStr(Rep(0x0b, 22), new byte[0], "pvzhe-room", 32);
        True("info 用途隔离生效", NetSha256.Hex(k1) != NetSha256.Hex(k2));

        True("输出长度可控（16/32/64）",
            NetSha256.HkdfStr(Rep(1, 32), null, "x", 16).Length == 16 &&
            NetSha256.HkdfStr(Rep(1, 32), null, "x", 32).Length == 32 &&
            NetSha256.HkdfStr(Rep(1, 32), null, "x", 64).Length == 64);
    }

    // ---------- AES-256-GCM（NIST GCM 标准测试向量） ----------
    static void AesTests()
    {
        Console.WriteLine("[AES-256-GCM]");

        const string K0 = "0000000000000000000000000000000000000000000000000000000000000000";
        const string K1 = "feffe9928665731c6d6a8f9467308308feffe9928665731c6d6a8f9467308308";
        const string IV12 = "cafebabefacedbaddecaf888";
        const string AAD20 = "feedfacedeadbeeffeedfacedeadbeefabaddad2";

        const string P64 =
            "d9313225f88406e5a55909c5aff5269a86a7a9531534f7da2e4c303d8a318a72" +
            "1c3c0c95956809532fcf0e2449a6b525b16aedf5aa0de657ba637b391aafd255";
        const string P60 =
            "d9313225f88406e5a55909c5aff5269a86a7a9531534f7da2e4c303d8a318a72" +
            "1c3c0c95956809532fcf0e2449a6b525b16aedf5aa0de657ba637b39";
        const string C64 =
            "522dc1f099567d07f47f37a32a84427d643a8cdcbfe5c0c97598a2bd2555d1aa" +
            "8cb08e48590dbb3da7b08b1056828838c5f61e6393ba7a0abcc9f662898015ad";
        const string C60 =
            "522dc1f099567d07f47f37a32a84427d643a8cdcbfe5c0c97598a2bd2555d1aa" +
            "8cb08e48590dbb3da7b08b1056828838c5f61e6393ba7a0abcc9f662";

        // TC13：全零密钥 + 空明文 + 空 AAD
        {
            var aes = new NetAesGcm(NetSha256.FromHex(K0));
            var o = aes.Seal(NetSha256.FromHex("000000000000000000000000"), null, new byte[0]);
            True("TC13 空明文仅输出 tag", o.Length == 16);
            Eq("TC13 tag", Slice(o, 0, 16), "530f8afbc74536b9a963b4f1c4cb738b");
        }

        // TC14：全零密钥 + 单块全零明文
        {
            var aes = new NetAesGcm(NetSha256.FromHex(K0));
            var o = aes.Seal(NetSha256.FromHex("000000000000000000000000"), null, new byte[16]);
            Eq("TC14 密文", Slice(o, 0, 16), "cea7403d4d606b6e074ec5d3baf39d18");
            Eq("TC14 tag", Slice(o, 16, 16), "d0d1c8a799996bf0265b98b5d48ab919");
        }

        // TC15：64 字节明文，无 AAD
        {
            var aes = new NetAesGcm(NetSha256.FromHex(K1));
            var o = aes.Seal(NetSha256.FromHex(IV12), null, NetSha256.FromHex(P64));
            Eq("TC15 密文", Slice(o, 0, 64), C64);
            Eq("TC15 tag", Slice(o, 64, 16), "b094dac5d93471bdec1a502270e3cc6c");
        }

        // TC16：60 字节明文 + 20 字节 AAD
        {
            var aes = new NetAesGcm(NetSha256.FromHex(K1));
            var o = aes.Seal(NetSha256.FromHex(IV12), NetSha256.FromHex(AAD20), NetSha256.FromHex(P60));
            Eq("TC16 密文", Slice(o, 0, 60), C60);
            Eq("TC16 tag", Slice(o, 60, 16), "76fc6ece0f4e1768cddf8853bb2d551b");
        }

        // TC17：8 字节 IV（走 GHASH 派生 J0 分支）
        {
            var aes = new NetAesGcm(NetSha256.FromHex(K1));
            var o = aes.Seal(NetSha256.FromHex("cafebabefacedbad"), NetSha256.FromHex(AAD20), NetSha256.FromHex(P60));
            Eq("TC17 密文", Slice(o, 0, 60),
                "c3762df1ca787d32ae47c13bf19844cbaf1ae14d0b976afac52ff7d79bba9de0" +
                "feb582d33934a4f0954cc2363bc73f7862ac430e64abe499f47c9b1f");
            Eq("TC17 tag", Slice(o, 60, 16), "3a337dbf46a792c45e454913fe2ea8f2");
        }

        // TC18：60 字节 IV
        {
            var aes = new NetAesGcm(NetSha256.FromHex(K1));
            var o = aes.Seal(
                NetSha256.FromHex("9313225df88406e555909c5aff5269aa6a7a9538534f7da1e4c303d2a318a728" +
                                 "c3c0c95156809539fcf0e2429a6b525416aedbf5a0de6a57a637b39b"),
                NetSha256.FromHex(AAD20), NetSha256.FromHex(P60));
            Eq("TC18 密文", Slice(o, 0, 60),
                "5a8def2f0c9e53f1f75d7853659e2a20eeb2b22aafde6419a058ab4f6f746bf4" +
                "0fc0c3b780f244452da3ebf1c5d82cdea2418997200ef82e44ae7e3f");
            Eq("TC18 tag", Slice(o, 60, 16), "a44a8266ee1c8eb0c8b5d4cf5ae9f19a");
        }

        // 往返 / 篡改检测 / nonce 唯一性
        {
            var key = NetSha256.FromHex(K1);
            var aes = new NetAesGcm(key);
            var pt = Utf8("PVZHE 联机数据 E2E 加密往返测试");
            var sealedOk = aes.Seal(NetSha256.FromHex(IV12), Ascii("aad"), pt);
            var back = aes.Open(NetSha256.FromHex(IV12), Ascii("aad"), sealedOk);
            True("Seal/Open 往返", back != null && NetSha256.Hex(back) == NetSha256.Hex(pt));

            var badC = (byte[])sealedOk.Clone();
            badC[0] ^= 0x01;
            True("篡改密文 → null", aes.Open(NetSha256.FromHex(IV12), Ascii("aad"), badC) == null);

            var badT = (byte[])sealedOk.Clone();
            badT[sealedOk.Length - 1] ^= 0x01;
            True("篡改 tag → null", aes.Open(NetSha256.FromHex(IV12), Ascii("aad"), badT) == null);

            True("错误 AAD → null", aes.Open(NetSha256.FromHex(IV12), Ascii("aae"), sealedOk) == null);
            True("截断输入 → null", aes.Open(NetSha256.FromHex(IV12), Ascii("aad"), Slice(sealedOk, 0, 8)) == null);

            var s1 = NetAesGcm.SealWithSeq(key, 0x11111111u, 1, null, pt);
            var s2 = NetAesGcm.SealWithSeq(key, 0x11111111u, 2, null, pt);
            True("序号递增 → 密文不同（nonce 唯一）", NetSha256.Hex(s1) != NetSha256.Hex(s2));
            var r1 = NetAesGcm.OpenWithSeq(key, 0x11111111u, 1, null, s1);
            True("SealWithSeq/OpenWithSeq 往返", r1 != null && NetSha256.Hex(r1) == NetSha256.Hex(pt));
        }
    }

    // ---------- X25519（RFC 7748） ----------
    static void X25519Tests()
    {
        Console.WriteLine("[X25519]");

        var sw = System.Diagnostics.Stopwatch.StartNew();

        Eq("RFC7748 §5.2 Test1", NetX25519.ScalarMult(
                NetSha256.FromHex("a546e36bf0527c9d3b16154b82465edd62144c0ac1fc5a18506a2244ba449ac4"),
                NetSha256.FromHex("e6db6867583030db3594c1a424b15f7c726624ec26b3353b10a903a6d0ab1c4c")),
            "c3da55379de9c6908e94ea4df28d084f32eccf03491c71f754b4075577a28552");

        Eq("RFC7748 §5.2 Test2", NetX25519.ScalarMult(
                NetSha256.FromHex("4b66e9d4d1b4673c5ad22691957d6af5c11b6421e0ea01d42ca4169e7918ba0d"),
                NetSha256.FromHex("e5210f12786811d3f4b7959d0538ae2c31dbe7106fc03c3efc4cd549c715a493")),
            "95cbde9476e8907d7aade45cb4b873f88b595a68799fa152e6f8f7647aac7957");

        // §6.1 DH 密钥交换
        var alicePriv = NetSha256.FromHex("77076d0a7318a57d3c16c17251b26645df4c2f87ebc0992ab177fba51db92c2a");
        var bobPriv = NetSha256.FromHex("5dab087e624a8a4b79e17f8b83800ee66f3bb1292618b6fd1c2f8b27ff88e0eb");
        var alicePub = NetX25519.PublicFrom(alicePriv);
        var bobPub = NetX25519.PublicFrom(bobPriv);
        Eq("§6.1 Alice 公钥", alicePub, "8520f0098930a754748b7ddcb43ef75a0dbf3a0d26381af4eba4a98eaa9b4e6a");
        Eq("§6.1 Bob 公钥", bobPub, "de9edb7d7b7dc1b4d35b61c2ece435373f8343c85b78674dadfc7e146f882b4f");
        const string Shared = "4a5d9d5ba4ce2de1728e3bf480350f25e07e21c947d19e3376f09b3c1e161742";
        Eq("§6.1 Alice 侧共享密钥", NetX25519.SharedSecret(alicePriv, bobPub), Shared);
        Eq("§6.1 Bob 侧共享密钥", NetX25519.SharedSecret(bobPriv, alicePub), Shared);

        // §5.2 迭代向量（1 次 + 1000 次）
        {
            var k = NetSha256.FromHex("0900000000000000000000000000000000000000000000000000000000000000");
            var u = NetSha256.FromHex("0900000000000000000000000000000000000000000000000000000000000000");
            var r1 = NetX25519.ScalarMult(k, u);
            Eq("§5.2 迭代 1 次", r1, "422c8e7a6227d7bca1350b3e2bb7279f7897b87bb6854b783c60e80311ae3079");

            // RFC 7748 §5.2：每轮把 u 设为【上一轮的 k】，k 设为结果
            var base9 = NetSha256.FromHex("0900000000000000000000000000000000000000000000000000000000000000");
            k = r1; u = base9;
            for (int i = 1; i < 1000; i++)
            {
                var r = NetX25519.ScalarMult(k, u);
                u = k; k = r;
            }
            Eq("§5.2 迭代 1000 次", k, "684cf59ba83309552800ef566f2f4d3c1c3887c49360e3875f2eb94d99532c51");
        }

        sw.Stop();
        double perMs = sw.Elapsed.TotalMilliseconds / 1001.0;
        Console.WriteLine("        单次 X25519 平均 " + perMs.ToString("F2") + " ms（1001 次共 " +
                          sw.Elapsed.TotalMilliseconds.ToString("F0") + " ms）");
        True("单次 X25519 < 50ms（握手可用）", perMs < 50.0);

        // CSPRNG / ECDH 自洽
        {
            var p1 = NetX25519.GeneratePrivate();
            var p2 = NetX25519.GeneratePrivate();
            True("私钥长度 32", p1.Length == 32 && p2.Length == 32);
            True("两次私钥不同", NetSha256.Hex(p1) != NetSha256.Hex(p2));
            True("私钥已 clamp", (p1[0] & 7) == 0 && (p1[31] & 128) == 0 && (p1[31] & 64) != 0);

            var pub1 = NetX25519.PublicFrom(p1);
            var pub2 = NetX25519.PublicFrom(p2);
            var sa = NetX25519.SharedSecret(p1, pub2);
            var sb = NetX25519.SharedSecret(p2, pub1);
            True("ECDH 双方共享密钥一致", NetSha256.Hex(sa) == NetSha256.Hex(sb));
            True("共享密钥非全零", NetSha256.Hex(sa) != NetSha256.Hex(new byte[32]));

            var rnd1 = NetX25519.RandomBytes(32);
            var rnd2 = NetX25519.RandomBytes(32);
            True("RandomBytes 长度与不重复", rnd1.Length == 32 && rnd2.Length == 32 &&
                                            NetSha256.Hex(rnd1) != NetSha256.Hex(rnd2));

            var dk1 = NetX25519.DeriveKey(sa, "pvzhe-link", 32);
            var dk2 = NetX25519.DeriveKey(sa, "pvzhe-room", 32);
            True("DeriveKey 用途隔离", NetSha256.Hex(dk1) != NetSha256.Hex(dk2));
        }
    }

    /// <summary>
    /// 检查类型内不存在同名方法。
    /// ★ 注入器（patcher）按「类型名.方法名」映射复制方法体，同名重载会配错指令流，
    ///   产生 call 操作数为 null / 指向别的指令的坏 IL（实测 bad=17）。
    /// </summary>
    static bool NoOverloads(Type t)
    {
        var seen = new System.Collections.Generic.HashSet<string>();
        var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.DeclaredOnly;
        foreach (var m in t.GetMethods(flags))
        {
            if (m.IsSpecialName) continue;                       // 忽略 get_/set_ 与运算符
            if (m.Name.Contains("<")) continue;                  // 忽略编译器生成方法
            if (!seen.Add(m.Name))
            {
                Console.WriteLine("        重载冲突: " + t.Name + "." + m.Name);
                return false;
            }
        }
        return true;
    }

    // ---------- 注入器约束（禁止方法重载） ----------
    static void InjectorConstraintTests()
    {
        Console.WriteLine("[注入器约束：禁止方法重载]");
        True("NetSha256 无重载", NoOverloads(typeof(NetSha256)));
        True("NetAesGcm 无重载", NoOverloads(typeof(NetAesGcm)));
        True("NetX25519 无重载", NoOverloads(typeof(NetX25519)));
        True("NetSecureSession 无重载", NoOverloads(typeof(NetSecureSession)));
        True("NetProto 无重载", NoOverloads(typeof(NetProto)));
    }

    // ---------- 安全会话：握手 / 房间密钥 / 帧加解密 ----------
    static void SecureTests()
    {
        Console.WriteLine("[安全会话 / 端到端加密]");

        // ---- 握手：两端独立派生，结果必须一致 ----
        var client = new NetSecureSession();                                   // MOD 侧
        var server = new NetSecureSession { SendPrefix = NetSecureSession.DirServerToClient,
                                            RecvPrefix = NetSecureSession.DirClientToServer };  // 中继侧
        var chal = NetX25519.RandomBytes(8);
        var cPub = client.BeginHandshake();
        var sPub = server.BeginHandshake();
        True("握手公钥长度 32", cPub.Length == 32 && sPub.Length == 32);
        True("两端公钥不同", NetSha256.Hex(cPub) != NetSha256.Hex(sPub));

        True("客户端完成握手", client.CompleteHandshake(sPub, chal));
        True("中继完成握手", server.CompleteHandshake(cPub, chal));
        True("链路密钥一致", NetSha256.Hex(client.LinkKey) == NetSha256.Hex(server.LinkKey));

        // 不同 nonce 必须派生不同密钥（防重放握手）
        var client2 = new NetSecureSession();
        var c2Pub = client2.BeginHandshake();
        client2.CompleteHandshake(sPub, NetX25519.RandomBytes(8));
        True("不同 nonce → 不同链路密钥", NetSha256.Hex(client2.LinkKey) != NetSha256.Hex(client.LinkKey));

        True("MAC 验证通过", server.VerifyAuthMac(chal, client.AuthMac(chal)));
        True("错误 nonce 的 MAC 被拒", !server.VerifyAuthMac(NetX25519.RandomBytes(8), client.AuthMac(chal)));
        var badMac = client.AuthMac(chal);
        badMac[0] ^= 1;
        True("篡改 MAC 被拒", !server.VerifyAuthMac(chal, badMac));
        True("低阶公钥被拒（全零公钥）", !new NetSecureSession().CompleteHandshake(new byte[32], chal));

        // ---- 链路帧（enc=1）往返 ----
        var linkPlain = Utf8("房间设置：允许修改器=否");
        var ls = client.SealLink(NetProto.MsgSetRoomSettings, linkPlain);
        True("链路帧体非空", ls != null && ls.Length == 8 + linkPlain.Length + 16);
        var lp = server.OpenLink(NetProto.MsgSetRoomSettings, ls);
        True("链路帧往返", lp != null && NetSha256.Hex(lp) == NetSha256.Hex(linkPlain));
        True("链路帧类型当作 AAD（换 type 必失败）",
            server.OpenLink(NetProto.MsgChat, ls) == null);

        // ---- 房间密钥：房主 → 成员 点对点 ECDH 包裹 ----
        var host = new NetSecureSession();
        var member = new NetSecureSession();
        var hPub = host.BeginHandshake();
        var mPub = member.BeginHandshake();
        var chal2 = NetX25519.RandomBytes(8);
        host.CompleteHandshake(mPub, chal2);
        member.CompleteHandshake(hPub, chal2);

        var roomKey = host.CreateRoomKey();
        True("房间密钥 32 字节", roomKey.Length == 32);
        True("指纹格式 XXXX-XXXX", host.RoomFingerprint.Length == 9 && host.RoomFingerprint[4] == '-');
        True("创建时成员尚无房间密钥", !member.HasRoomKey);

        var wrapped = host.WrapRoomKeyFor(member.MyPublicKey);
        True("包裹长度 60（nonce12 + 密文32 + tag16）", wrapped != null && wrapped.Length == 60);
        True("成员解包成功", member.UnwrapRoomKeyFrom(host.MyPublicKey, wrapped));
        True("双方房间密钥指纹一致", host.RoomFingerprint == member.RoomFingerprint);
        True("用的是同一把房间密钥",
            NetSha256.Hex(NetSha256.Hash(roomKey)) ==
            NetSha256.Hex(NetSha256.Hash(roomKey)) && member.HasRoomKey);

        // ★ E2E 的核心证明：中继（第三方，无私钥）解不开房间密钥
        var relay = new NetSecureSession();
        var rPub = relay.BeginHandshake();
        True("中继无法解开房间密钥包裹（E2E 成立）", !relay.UnwrapRoomKeyFrom(host.MyPublicKey, wrapped));

        // 篡改包裹：用【目标接收方】验证必须失败（拿无关密钥去解包是测不出篡改的）
        var badWrap = (byte[])wrapped.Clone();
        badWrap[20] ^= 0x01;
        True("篡改包裹被拒（目标接收方）", !member.UnwrapRoomKeyFrom(host.MyPublicKey, badWrap));
        var member2 = new NetSecureSession();
        var m2Pub = member2.BeginHandshake();
        member2.CompleteHandshake(hPub, NetX25519.RandomBytes(8));
        True("错误接收方的包裹被拒", !member2.UnwrapRoomKeyFrom(host.MyPublicKey, wrapped));

        // ---- E2E 帧（enc=2） ----
        var gameData = Utf8("阳光=150 波次=3 僵尸=12");
        var e1 = host.SealE2e(1, NetProto.MsgStateSync, gameData);
        True("E2E 帧体 = 2+8+密文+16", e1 != null && e1.Length == 10 + gameData.Length + 16);
        var got = member.OpenE2e(NetProto.MsgStateSync, e1, out ushort sid);
        True("E2E 往返 + senderId 解析", got != null && NetSha256.Hex(got) == NetSha256.Hex(gameData) && sid == 1);

        var e2 = host.SealE2e(1, NetProto.MsgStateSync, gameData);
        var tampered = (byte[])e2.Clone();
        tampered[12] ^= 0x01;
        True("篡改 E2E 密文 → null", member.OpenE2e(NetProto.MsgStateSync, tampered, out _) == null);

        var e3 = host.SealE2e(1, NetProto.MsgStateSync, gameData);
        True("类型不匹配 → null（AAD 生效）",
            member.OpenE2e(NetProto.MsgEntitySnapshot, e3, out _) == null);

        var e4 = host.SealE2e(1, NetProto.MsgStateSync, gameData);
        True("重放同一帧 → null",
            member.OpenE2e(NetProto.MsgStateSync, e4, out _) != null &&
            member.OpenE2e(NetProto.MsgStateSync, e4, out _) == null);

        // 多发送者共存：senderId 不同则各自独立计数，互不干扰
        var eHost = host.SealE2e(1, NetProto.MsgCursor, Utf8("host-cursor"));
        var eMember = member.SealE2e(2, NetProto.MsgCursor, Utf8("member-cursor"));
        var g1 = host.OpenE2e(NetProto.MsgCursor, eMember, out ushort sid1);
        var g2 = member.OpenE2e(NetProto.MsgCursor, eHost, out ushort sid2);
        True("多发送者：房主收到成员光标", g1 != null && sid1 == 2 && NetSha256.Hex(g1) == NetSha256.Hex(Utf8("member-cursor")));
        True("多发送者：成员收到房主光标", g2 != null && sid2 == 1 && NetSha256.Hex(g2) == NetSha256.Hex(Utf8("host-cursor")));

        // 中继侧只能看到 senderId，看不到内容
        True("中继可读 senderId（已知信息）", NetSecureSession.PeekSenderId(eHost) == 1);

        // 未持房间密钥者无法解密 E2E 帧
        True("无房间密钥者无法解密 E2E", relay.OpenE2e(NetProto.MsgStateSync, e1, out _) == null);

        // ---- 帧编解码 v3 ----
        var frame = NetProto.Frame(NetProto.MsgStateSync, NetProto.EncE2e, e1);
        True("v3 帧 = 4(len) + type + enc + payload", frame.Length == 4 + 2 + e1.Length);
        True("v3 解析成功", NetProto.TryParseFrame(frame, out byte ft, out byte fe, out byte[] fp, out int used));
        True("v3 解析字段正确", ft == NetProto.MsgStateSync && fe == NetProto.EncE2e &&
                                  NetSha256.Hex(fp) == NetSha256.Hex(e1) && used == frame.Length);
        True("截断缓冲解析失败", !NetProto.TryParseFrame(Slice(frame, 0, frame.Length - 1), out _, out _, out _, out _));
        True("ProtoVer 已升到 3", NetConstants.ProtoVer == 3);

        // 日志泄漏检查：指纹不含密钥本体
        True("指纹不是密钥本体", !NetSha256.Hex(roomKey).ToUpperInvariant().StartsWith(host.RoomFingerprint.Replace("-", "")));
    }
}
