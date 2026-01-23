using System;
using System.Collections.Generic;
using System.IO;

namespace Ruri.ShaderDecompiler.Engine
{
    public class UnrealShaderLibraryReader
    {
        public struct FShaderCodeEntry 
        { 
            public ulong Offset; 
            public uint Size; 
            public uint UncompressedSize; 
            public byte Frequency; 
        }

        public class ShaderLibrary
        {
            public uint Version;
            public List<string> ShaderMapHashes = new();
            public List<string> ShaderHashes = new();
            public FShaderCodeEntry[] ShaderEntries = Array.Empty<FShaderCodeEntry>();
            public byte[] CodeBuffer = Array.Empty<byte>();

            public byte[]? GetShaderCode(int index)
            {
                if (index < 0 || index >= ShaderEntries.Length) return null;
                var entry = ShaderEntries[index];
                
                // Safety check
                if ((long)entry.Offset + entry.Size > CodeBuffer.Length)
                {
                    return null;
                }

                var code = new byte[entry.Size];
                Array.Copy(CodeBuffer, (long)entry.Offset, code, 0, entry.Size);
                return code;
            }
        }

        public static ShaderLibrary Read(string path)
        {
            using var fs = File.OpenRead(path);
            using var reader = new BinaryReader(fs);

            var lib = new ShaderLibrary();
            lib.Version = reader.ReadUInt32();

            // ShaderMapHashes
            int count = reader.ReadInt32();
            for(int i=0; i<count; i++) lib.ShaderMapHashes.Add(ReadShaHash(reader));

            // ShaderHashes
            count = reader.ReadInt32();
            for(int i=0; i<count; i++) lib.ShaderHashes.Add(ReadShaHash(reader));

            // ShaderMapEntries
            count = reader.ReadInt32();
            // struct FShaderMapEntry { uint, uint, uint, uint } = 16 bytes
            fs.Seek(count * 16, SeekOrigin.Current);

            // ShaderCodeEntries
            count = reader.ReadInt32();
            lib.ShaderEntries = new FShaderCodeEntry[count];
            for(int i=0; i<count; i++)
            {
                lib.ShaderEntries[i] = new FShaderCodeEntry {
                   Offset = reader.ReadUInt64(),
                   Size = reader.ReadUInt32(),
                   UncompressedSize = reader.ReadUInt32(),
                   Frequency = reader.ReadByte()
                };
            }

            // PreloadEntries
            count = reader.ReadInt32();
            // struct FFileCachePreloadEntry { long, long } = 16 bytes
            fs.Seek(count * 16, SeekOrigin.Current);

            // ShaderIndices
            count = reader.ReadInt32();
            // uint = 4 bytes
            fs.Seek(count * 4, SeekOrigin.Current);

            // Code Buffer
            // The rest of the stream is the code buffer
            long remaining = fs.Length - fs.Position;
            lib.CodeBuffer = reader.ReadBytes((int)remaining);

            return lib;
        }

        private static string ReadShaHash(BinaryReader reader)
        {
            var bytes = reader.ReadBytes(20);
            return BitConverter.ToString(bytes).Replace("-", "");
        }
    }
}
