using System.Collections.Generic;
using System.Text;

/// <summary>极简二进制写入器（小端）。纯 System，不引用 Godot——便于被中继工程与单测工程共享。</summary>
public class BitWriter
{
    readonly List<byte> _buf = new List<byte>(256);
    public int Length => _buf.Count;

    public BitWriter WriteByte(byte v) { _buf.Add(v); return this; }
    public BitWriter WriteU16(ushort v) { _buf.Add((byte)v); _buf.Add((byte)(v >> 8)); return this; }
    public BitWriter WriteU32(uint v)
    {
        _buf.Add((byte)v); _buf.Add((byte)(v >> 8)); _buf.Add((byte)(v >> 16)); _buf.Add((byte)(v >> 24));
        return this;
    }
    public BitWriter WriteI16(short v) => WriteU16(unchecked((ushort)v));
    public BitWriter WriteBytes(byte[] data)
    {
        if (data != null) _buf.AddRange(data);
        return this;
    }
    public BitWriter WriteStr(string s)
    {
        byte[] u = Encoding.UTF8.GetBytes(s ?? "");
        if (u.Length > 0xFFFF) u = new byte[0];
        WriteU16((ushort)u.Length);
        _buf.AddRange(u);
        return this;
    }
    public byte[] ToArray() => _buf.ToArray();
}

/// <summary>极简二进制读取器（小端），越界安全（返回 0 / 空）。</summary>
public class BitReader
{
    readonly byte[] _buf;
    public int Position { get; private set; }
    public BitReader(byte[] buf) { _buf = buf ?? new byte[0]; }
    public bool HasMore => Position < _buf.Length;

    public byte ReadByte()
    {
        if (Position + 1 > _buf.Length) { Position = _buf.Length; return 0; }
        return _buf[Position++];
    }
    public ushort ReadU16()
    {
        if (Position + 2 > _buf.Length) { Position = _buf.Length; return 0; }
        ushort v = (ushort)(_buf[Position] | (_buf[Position + 1] << 8));
        Position += 2;
        return v;
    }
    public uint ReadU32()
    {
        if (Position + 4 > _buf.Length) { Position = _buf.Length; return 0; }
        uint v = (uint)(_buf[Position] | (_buf[Position + 1] << 8) | (_buf[Position + 2] << 16) | (_buf[Position + 3] << 24));
        Position += 4;
        return v;
    }
    public short ReadI16() => unchecked((short)ReadU16());
    public byte[] ReadBytes(int n)
    {
        if (n <= 0) return new byte[0];
        if (Position + n > _buf.Length) n = _buf.Length - Position;
        var r = new byte[n];
        System.Array.Copy(_buf, Position, r, 0, n);
        Position += n;
        return r;
    }
    public string ReadStr()
    {
        int n = ReadU16();
        return Encoding.UTF8.GetString(ReadBytes(n));
    }
}
