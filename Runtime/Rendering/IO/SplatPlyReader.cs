// SPDX-License-Identifier: MIT
// Adapted from aras-p/UnityGaussianSplatting (MIT), Editor/Utils/PLYFileReader.cs.
// Changes vs upstream: runtime assembly + renamed type; properties of elements other than "vertex" are
// no longer counted into the vertex stride; all standard PLY scalar types are sized correctly (upstream
// gave unknown types size 0, silently corrupting the stride); truncated files and short reads are
// detected; the vertex buffer is disposed if reading fails.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Unity.Collections;

namespace SplatPresso.Rendering.IO
{
    /// <summary>
    /// Minimal reader for binary little-endian PLY files (the format 3DGS tools and TripoSplat emit).
    /// Only the "vertex" element is read; it must be the first element with data.
    /// </summary>
    public static class SplatPlyReader
    {
        /// <summary>PLY scalar property types. Only <see cref="Float"/> properties are consumed by the splat reader.</summary>
        public enum ElementType
        {
            None,
            Float,
            Double,
            UChar,
            Char,
            Short,
            UShort,
            Int,
            UInt,
        }

        // C# arrays and NativeArrays make it hard to have a "byte" array larger than 2GB.
        const long kMaxFileSize = 2L * 1024 * 1024 * 1024;
        const int kMaxHeaderLines = 9000;

        /// <summary>Reads only the header. Returns zero counts and an empty list if the file does not exist.</summary>
        public static void ReadFileHeader(string filePath, out int vertexCount, out int vertexStride, out List<(string, ElementType)> attrs)
        {
            vertexCount = 0;
            vertexStride = 0;
            attrs = new List<(string, ElementType)>();
            if (!File.Exists(filePath))
                return;
            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read))
                ReadHeaderImpl(filePath, out vertexCount, out vertexStride, out attrs, fs);
        }

        /// <summary>
        /// Reads the header and the raw vertex data. <paramref name="vertices"/> is allocated with
        /// <see cref="Allocator.Persistent"/>; the caller owns it and must dispose it.
        /// </summary>
        public static void ReadFile(string filePath, out int vertexCount, out int vertexStride, out List<(string, ElementType)> attrs, out NativeArray<byte> vertices)
        {
            vertices = default;
            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read))
            {
                ReadHeaderImpl(filePath, out vertexCount, out vertexStride, out attrs, fs);

                long dataBytes = (long)vertexCount * vertexStride;
                if (dataBytes > fs.Length - fs.Position)
                    throw new IOException($"PLY {filePath} read error: file is truncated (header declares {vertexCount} vertices of {vertexStride} bytes, only {fs.Length - fs.Position} data bytes present)");

                vertices = new NativeArray<byte>((int)dataBytes, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                try
                {
                    // A single Stream.Read may legally return fewer bytes than requested: loop until full.
                    Span<byte> span = vertices.AsSpan();
                    int total = 0;
                    while (total < span.Length)
                    {
                        int read = fs.Read(span.Slice(total));
                        if (read <= 0)
                            break;
                        total += read;
                    }
                    if (total != span.Length)
                        throw new IOException($"PLY {filePath} read error, expected {span.Length} data bytes got {total}");
                }
                catch
                {
                    vertices.Dispose();
                    vertices = default;
                    throw;
                }
            }
        }

        /// <summary>Size in bytes of one value of the given type (0 for <see cref="ElementType.None"/>).</summary>
        public static int TypeToSize(ElementType t)
        {
            switch (t)
            {
                case ElementType.None: return 0;
                case ElementType.Float: return 4;
                case ElementType.Double: return 8;
                case ElementType.UChar: return 1;
                case ElementType.Char: return 1;
                case ElementType.Short: return 2;
                case ElementType.UShort: return 2;
                case ElementType.Int: return 4;
                case ElementType.UInt: return 4;
                default: throw new ArgumentOutOfRangeException(nameof(t), t, null);
            }
        }

        static ElementType ParseType(string token)
        {
            switch (token)
            {
                case "float": case "float32": return ElementType.Float;
                case "double": case "float64": return ElementType.Double;
                case "uchar": case "uint8": return ElementType.UChar;
                case "char": case "int8": return ElementType.Char;
                case "short": case "int16": return ElementType.Short;
                case "ushort": case "uint16": return ElementType.UShort;
                case "int": case "int32": return ElementType.Int;
                case "uint": case "uint32": return ElementType.UInt;
                default: return ElementType.None;
            }
        }

        static void ReadHeaderImpl(string filePath, out int vertexCount, out int vertexStride, out List<(string, ElementType)> attrs, FileStream fs)
        {
            if (fs.Length >= kMaxFileSize)
                throw new IOException($"PLY {filePath} read error: currently files larger than 2GB are not supported");

            vertexCount = 0;
            vertexStride = 0;
            attrs = new List<(string, ElementType)>();

            string magic = ReadLine(fs);
            if (magic != "ply")
                throw new IOException($"PLY {filePath} read error: not a PLY file (missing 'ply' magic line)");

            bool gotBinaryLE = false;
            bool gotEndHeader = false;
            bool inVertexElement = false;
            bool sawVertexElement = false;
            for (int lineIdx = 0; lineIdx < kMaxHeaderLines; ++lineIdx)
            {
                string line = ReadLine(fs);
                if (line == null)
                    break; // EOF
                if (line == "end_header")
                {
                    gotEndHeader = true;
                    break;
                }
                string[] tokens = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length == 0)
                    continue;

                switch (tokens[0])
                {
                    case "format":
                        if (tokens.Length >= 2 && tokens[1] == "binary_little_endian")
                            gotBinaryLE = true;
                        break;
                    case "element":
                        if (tokens.Length < 3)
                            throw new IOException($"PLY {filePath} read error: malformed element line '{line}'");
                        inVertexElement = tokens[1] == "vertex";
                        if (inVertexElement)
                        {
                            if (!int.TryParse(tokens[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out vertexCount) || vertexCount < 0)
                                throw new IOException($"PLY {filePath} read error: bad vertex count '{tokens[2]}'");
                            sawVertexElement = true;
                        }
                        else if (!sawVertexElement && tokens[2] != "0")
                        {
                            // Data of a preceding element would sit in front of the vertex data.
                            throw new IOException($"PLY {filePath} not supported: element '{tokens[1]}' precedes the vertex element");
                        }
                        break;
                    case "property":
                        if (!inVertexElement)
                            break; // properties of other elements do not contribute to the vertex stride
                        if (tokens.Length >= 2 && tokens[1] == "list")
                            throw new IOException($"PLY {filePath} not supported: list property in the vertex element ('{line}')");
                        if (tokens.Length < 3)
                            throw new IOException($"PLY {filePath} read error: malformed property line '{line}'");
                        ElementType type = ParseType(tokens[1]);
                        if (type == ElementType.None)
                            throw new IOException($"PLY {filePath} not supported: unknown property type '{tokens[1]}'");
                        vertexStride += TypeToSize(type);
                        attrs.Add((tokens[2], type));
                        break;
                }
            }

            if (!gotBinaryLE)
                throw new IOException($"PLY {filePath} not supported: needs to be binary, little endian PLY format");
            if (!gotEndHeader)
                throw new IOException($"PLY {filePath} read error: header has no end_header line");
        }

        // Returns null at end of file. Strips a trailing CR (CRLF headers).
        static string ReadLine(FileStream fs)
        {
            var byteBuffer = new List<byte>(64);
            bool any = false;
            while (true)
            {
                int b = fs.ReadByte();
                if (b == -1)
                    break;
                any = true;
                if (b == '\n')
                    break;
                byteBuffer.Add((byte)b);
            }
            if (!any)
                return null;
            if (byteBuffer.Count > 0 && byteBuffer[byteBuffer.Count - 1] == '\r')
                byteBuffer.RemoveAt(byteBuffer.Count - 1);
            return Encoding.UTF8.GetString(byteBuffer.ToArray()).Trim();
        }
    }
}
