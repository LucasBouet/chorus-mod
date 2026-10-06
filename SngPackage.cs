using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ChorusMod;

/// <summary>
/// Unpacks a .sng chart into the classic folder (song.ini + chart +
/// audio + images), like the "zip" download of enchor.us. That download
/// isn't a server file: the site's service worker fetches the same .sng
/// and unpacks it in the browser. This does the same thing, from memory.
///
/// .sng layout (little-endian, github.com/mdsitton/SngFileFormat):
///   "SNGPKG", uint32 version, 16-byte xorMask
///   uint64 metadataLen, uint64 metadataCount,
///     { int32 keyLen, key, int32 valueLen, value } -> song.ini
///   uint64 fileMetaLen, uint64 fileCount,
///     { uint8 nameLen, name, uint64 contentsLen, uint64 contentsIndex }
///   uint64 fileDataLen, then the files' masked bytes; contentsIndex is
///   an offset from the start of the .sng.
/// Each file's byte i is unmasked with xorMask[i % 16] ^ (i % 256).
/// </summary>
public static class SngPackage
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("SNGPKG");

    public static bool IsSng(byte[] data)
    {
        if (data.Length < Magic.Length)
        {
            return false;
        }

        for (var i = 0; i < Magic.Length; i++)
        {
            if (data[i] != Magic[i])
            {
                return false;
            }
        }

        return true;
    }

    /// Writes song.ini and every packed file into targetDir.
    public static void Extract(byte[] data, string targetDir)
    {
        var (xorMask, ini, files) = ReadIndex(data);

        Directory.CreateDirectory(targetDir);
        var fullTarget = Path.GetFullPath(targetDir);

        File.WriteAllText(Path.Combine(targetDir, "song.ini"), ini, new UTF8Encoding(false));

        foreach (var (name, length, offset) in files)
        {
            // Same guard as zips: a name can't escape the chart's folder.
            var destPath = Path.GetFullPath(Path.Combine(targetDir, name));
            if (!destPath.StartsWith(fullTarget, StringComparison.OrdinalIgnoreCase))
            {
                Plugin.Logger.LogWarning($"Suspicious .sng entry ignored: {name}");
                continue;
            }

            var contents = Unmask(data, xorMask, length, offset);
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            File.WriteAllBytes(destPath, contents);
        }

        Plugin.Logger.LogInfo($"Unpacked .sng: song.ini + {files.Count} file(s) into {targetDir}");
    }

    /// Clone Hero's checksum of the chart inside: the MD5 (upper-case
    /// hex) of its notes.mid / notes.chart, the same one the trophy site
    /// sends. Null if there's no chart file.
    public static string? NotesChecksum(byte[] data)
    {
        var (xorMask, _, files) = ReadIndex(data);
        foreach (var preferred in new[] { "notes.mid", "notes.chart" })
        {
            foreach (var (name, length, offset) in files)
            {
                if (string.Equals(name, preferred, StringComparison.OrdinalIgnoreCase))
                {
                    return Md5(Unmask(data, xorMask, length, offset));
                }
            }
        }

        return null;
    }

    /// Upper-case hex, like SongEntry.ChecksumString.
    internal static string Md5(byte[] bytes)
    {
        using var md5 = System.Security.Cryptography.MD5.Create();
        return BitConverter.ToString(md5.ComputeHash(bytes)).Replace("-", "");
    }

    /// Header, metadata (as song.ini text, in the .sng's own order) and
    /// file index.
    private static (byte[] XorMask, string Ini, List<(string Name, long Length, long Offset)> Files) ReadIndex(byte[] data)
    {
        using var reader = new BinaryReader(new MemoryStream(data), Encoding.UTF8);

        reader.ReadBytes(Magic.Length);
        reader.ReadUInt32(); // version
        var xorMask = reader.ReadBytes(16);

        reader.ReadUInt64(); // metadataLen
        var metadataCount = reader.ReadUInt64();
        var ini = new StringBuilder("[song]\n");
        for (ulong i = 0; i < metadataCount; i++)
        {
            var key = ReadString(reader, reader.ReadInt32());
            var value = ReadString(reader, reader.ReadInt32());
            ini.Append(key).Append(" = ").Append(value).Append('\n');
        }

        reader.ReadUInt64(); // fileMetaLen
        var fileCount = reader.ReadUInt64();
        var files = new List<(string Name, long Length, long Offset)>();
        for (ulong i = 0; i < fileCount; i++)
        {
            var name = ReadString(reader, reader.ReadByte());
            var length = checked((long)reader.ReadUInt64());
            var offset = checked((long)reader.ReadUInt64());
            if (offset < 0 || length < 0 || offset + length > data.Length)
            {
                throw new InvalidDataException($"Corrupt .sng: '{name}' points outside the file.");
            }

            files.Add((name, length, offset));
        }

        return (xorMask, ini.ToString(), files);
    }

    private static byte[] Unmask(byte[] data, byte[] xorMask, long length, long offset)
    {
        var contents = new byte[length];
        for (long i = 0; i < length; i++)
        {
            contents[i] = (byte)(data[offset + i] ^ xorMask[i % 16] ^ (byte)(i % 256));
        }

        return contents;
    }

    private static string ReadString(BinaryReader reader, int length) =>
        length <= 0 ? "" : Encoding.UTF8.GetString(reader.ReadBytes(length));
}
