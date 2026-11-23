using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace FFXIII2SaveEditor;

// PARAM.PFD validation and re-signing for files whose hash key comes from the game's secure-file ID
// (APP.DAT / KEY.DAT). Port of the relevant parts of pfdtool / Apollo (bucanero/apollo-ps3, pfd.c).
// PARAM.SFO hashes need console/account IDs and are never recomputed here: PARAM.SFO is not modified.
public static class PfdSigner
{
    private static readonly byte[] SysconManagerKey =
    {
        0xD4, 0x13, 0xB8, 0x96, 0x63, 0xE1, 0xFE, 0x9F, 0x75, 0x14, 0x3D, 0x3B, 0xB4, 0x56, 0x52, 0x74
    };

    private static readonly byte[] KeygenKey =
    {
        0xBF, 0xCB, 0xA5, 0xAE, 0x1B, 0x07, 0xC2, 0x6C, 0x5B, 0x42,
        0x1D, 0x37, 0xCF, 0xB5, 0x13, 0x5C, 0x87, 0x99, 0x50, 0x8E
    };

    private const int HeaderKeyOffset = 0x10;
    private const int SignatureOffset = 0x20;
    private const int SignatureSize = 64;
    private const int HashTableOffset = 0x60;
    private const int EntrySize = 0x110;
    private const int EntryNameOffset = 8;
    private const int EntryNameSize = 65;
    private const int EntryKeyOffset = 80;
    private const int EntryDataSize = 192;          // key(64) + 4 hashes(80) + padding(40) + size(8)
    private const int EntryHashesOffset = 144;
    private const int EntrySizeOffset = 264;
    private const int HashSize = 20;

    public sealed record ValidationReport(bool Top, bool Bottom, bool Entries, Dictionary<string, bool> FileHashes)
    {
        public bool AllValid => Top && Bottom && Entries && FileHashes.Values.AllTrue();
    }

    private sealed class Layout
    {
        public ulong Version;
        public ulong Capacity, Reserved, Used;
        public int EntryTable, SignatureTable;
        public byte[] RealHashKey = Array.Empty<byte>();
        public byte[] Signature = Array.Empty<byte>();
    }

    private static Layout Parse(byte[] pfd)
    {
        if (pfd.Length < 0x8000 || BinaryPrimitives.ReadUInt64BigEndian(pfd.AsSpan(0, 8)) != 0x50464442UL)
            throw new InvalidOperationException("Invalid PARAM.PFD");
        var l = new Layout { Version = BinaryPrimitives.ReadUInt64BigEndian(pfd.AsSpan(8, 8)) };
        if (l.Version != 3 && l.Version != 4) throw new InvalidOperationException($"Unsupported PFD version {l.Version}");
        l.Capacity = BinaryPrimitives.ReadUInt64BigEndian(pfd.AsSpan(HashTableOffset, 8));
        l.Reserved = BinaryPrimitives.ReadUInt64BigEndian(pfd.AsSpan(HashTableOffset + 8, 8));
        l.Used = BinaryPrimitives.ReadUInt64BigEndian(pfd.AsSpan(HashTableOffset + 16, 8));
        if (l.Capacity == 0 || l.Capacity > 1024 || l.Reserved > 1024 || l.Used > l.Reserved)
            throw new InvalidOperationException("Malformed PARAM.PFD hash table");
        l.EntryTable = checked(HashTableOffset + 24 + (int)l.Capacity * 8);
        l.SignatureTable = checked(l.EntryTable + (int)l.Reserved * EntrySize);
        if (l.SignatureTable + (int)l.Capacity * HashSize > pfd.Length) throw new InvalidOperationException("Truncated PARAM.PFD");

        l.Signature = AesCbc(decrypt: true, pfd.AsSpan(HeaderKeyOffset, 16).ToArray(), pfd.AsSpan(SignatureOffset, SignatureSize).ToArray());
        byte[] hashKey = l.Signature.AsSpan(40, HashSize).ToArray();
        l.RealHashKey = l.Version == 4 ? HMACSHA1.HashData(KeygenKey, hashKey) : hashKey;
        return l;
    }

    // Checks the stored top/bottom hashes, every entry-signature bucket and the file hashes of the
    // given files (by PFD entry name). Files not supplied are not checked.
    public static ValidationReport Validate(byte[] pfd, IReadOnlyDictionary<string, byte[]> files, byte[] secureFileId)
    {
        Layout l = Parse(pfd);
        bool top = Matches(l.Signature.AsSpan(20, HashSize), TopHash(pfd, l));
        bool bottom = Matches(l.Signature.AsSpan(0, HashSize), BottomHash(pfd, l));
        bool entries = true;
        foreach (var (bucket, hash) in ExpectedEntrySignatures(pfd, l))
            if (!Matches(pfd.AsSpan(l.SignatureTable + (int)bucket * HashSize, HashSize), hash)) entries = false;

        var fileHashes = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        byte[] fileKey = HashKeyForSecureFileId(secureFileId);
        for (ulong i = 0; i < l.Used; i++)
        {
            int entry = l.EntryTable + (int)i * EntrySize;
            string name = EntryName(pfd, entry);
            if (name.Equals("PARAM.SFO", StringComparison.OrdinalIgnoreCase) || !files.TryGetValue(name, out byte[]? data)) continue;
            fileHashes[name] = Matches(pfd.AsSpan(entry + EntryHashesOffset, HashSize), HMACSHA1.HashData(fileKey, data));
        }
        return new ValidationReport(top, bottom, entries, fileHashes);
    }

    // Returns a new PARAM.PFD where the file hash and size of each supplied file (other than PARAM.SFO)
    // are updated, then every entry signature, the bottom/top hashes and the encrypted signature block.
    public static byte[] Resign(byte[] pfd, IReadOnlyDictionary<string, byte[]> files, byte[] secureFileId)
    {
        byte[] output = (byte[])pfd.Clone();
        Layout l = Parse(output);
        byte[] fileKey = HashKeyForSecureFileId(secureFileId);
        int updated = 0;
        for (ulong i = 0; i < l.Used; i++)
        {
            int entry = l.EntryTable + (int)i * EntrySize;
            string name = EntryName(output, entry);
            if (name.Equals("PARAM.SFO", StringComparison.OrdinalIgnoreCase) || !files.TryGetValue(name, out byte[]? data)) continue;
            HMACSHA1.HashData(fileKey, data).CopyTo(output, entry + EntryHashesOffset);
            BinaryPrimitives.WriteUInt64BigEndian(output.AsSpan(entry + EntrySizeOffset, 8), (ulong)data.Length);
            updated++;
        }
        if (updated != files.Count) throw new InvalidOperationException("A file to re-sign is not listed in PARAM.PFD");

        foreach (var (bucket, hash) in ExpectedEntrySignatures(output, l))
            hash.CopyTo(output, l.SignatureTable + (int)bucket * HashSize);

        byte[] signature = (byte[])l.Signature.Clone();
        BottomHash(output, l).CopyTo(signature, 0);
        TopHash(output, l).CopyTo(signature, 20);
        AesCbc(decrypt: false, output.AsSpan(HeaderKeyOffset, 16).ToArray(), signature).CopyTo(output, SignatureOffset);
        return output;
    }

    public static byte[] HashKeyForSecureFileId(byte[] secureFileId)
    {
        var key = new byte[20];
        int j = 0;
        for (int i = 0; i < 20; i++)
            key[i] = i switch { 1 => 11, 2 => 15, 5 => 14, 8 => 10, _ => secureFileId[j++] };
        return key;
    }

    private static byte[] TopHash(byte[] pfd, Layout l)
        => HMACSHA1.HashData(l.RealHashKey, pfd.AsSpan(HashTableOffset, 24 + (int)l.Capacity * 8));

    private static byte[] BottomHash(byte[] pfd, Layout l)
        => HMACSHA1.HashData(l.RealHashKey, pfd.AsSpan(l.SignatureTable, (int)l.Capacity * HashSize));

    // Same order as pfd_update(): empty buckets (head >= capacity) get HMAC(key, ""), then every
    // bucket that holds a used entry gets the HMAC of its whole entry chain. Later writes win.
    private static List<(ulong Bucket, byte[] Hash)> ExpectedEntrySignatures(byte[] pfd, Layout l)
    {
        var result = new List<(ulong, byte[])>();
        byte[] empty = HMACSHA1.HashData(l.RealHashKey, Array.Empty<byte>());
        for (ulong i = 0; i < l.Capacity; i++)
            if (BucketHead(pfd, i) >= l.Capacity) result.Add((i, empty));
        for (ulong i = 0; i < l.Used; i++)
        {
            int entry = l.EntryTable + (int)i * EntrySize;
            ulong bucket = NameBucket(EntryName(pfd, entry), l.Capacity);
            result.Add((bucket, ChainHash(pfd, l, BucketHead(pfd, bucket))));
        }
        return result;
    }

    private static ulong BucketHead(byte[] pfd, ulong bucket)
        => BinaryPrimitives.ReadUInt64BigEndian(pfd.AsSpan(HashTableOffset + 24 + (int)bucket * 8, 8));

    private static ulong NameBucket(string name, ulong capacity)
    {
        ulong hash = 0;
        unchecked { foreach (byte b in Encoding.ASCII.GetBytes(name)) hash = (hash << 5) - hash + b; }
        return hash % capacity;
    }

    private static byte[] ChainHash(byte[] pfd, Layout l, ulong index)
    {
        using var hmac = new HMACSHA1(l.RealHashKey);
        int guard = 0;
        while (index < l.Reserved && guard++ < 1024)
        {
            int entry = l.EntryTable + (int)index * EntrySize;
            hmac.TransformBlock(pfd, entry + EntryNameOffset, EntryNameSize, null, 0);
            hmac.TransformBlock(pfd, entry + EntryKeyOffset, EntryDataSize, null, 0);
            index = BinaryPrimitives.ReadUInt64BigEndian(pfd.AsSpan(entry, 8));
        }
        hmac.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return hmac.Hash!;
    }

    private static string EntryName(byte[] pfd, int entry)
    {
        string name = Encoding.ASCII.GetString(pfd, entry + EntryNameOffset, EntryNameSize);
        int zero = name.IndexOf('\0');
        return zero >= 0 ? name[..zero] : name;
    }

    private static bool Matches(ReadOnlySpan<byte> stored, byte[] computed) => stored.SequenceEqual(computed);

    private static byte[] AesCbc(bool decrypt, byte[] iv, byte[] data)
    {
        using Aes aes = Aes.Create();
        aes.Key = SysconManagerKey;
        return decrypt ? aes.DecryptCbc(data, iv, PaddingMode.None) : aes.EncryptCbc(data, iv, PaddingMode.None);
    }

    private static bool AllTrue(this IEnumerable<bool> values)
    {
        foreach (bool v in values) if (!v) return false;
        return true;
    }
}
