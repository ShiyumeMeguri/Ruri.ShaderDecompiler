
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Ruri.ShaderDecompiler.Utils
{
    public static class DxbcRepair
    {
        private static readonly byte[] DxbcMagic = { 0x44, 0x58, 0x42, 0x43 }; // "DXBC"
        private static readonly byte[] ShexMagic = { 0x53, 0x48, 0x45, 0x58 }; // "SHEX"
        private static readonly byte[] IsgnMagic = { 0x49, 0x53, 0x47, 0x4E }; // "ISGN"
        private static readonly byte[] RdefMagic = { 0x52, 0x44, 0x45, 0x46 }; // "RDEF"

        public static byte[]? ExtractAndRepairDxbc(byte[] rawData)
        {
            // 1. Find DXBC Start
            int dxbcStart = FindMagic(rawData, DxbcMagic);
            if (dxbcStart < 0)
            {
                Console.WriteLine("[DxbcRepair] DXBC magic not found.");
                return null;
            }
            Console.WriteLine($"[DxbcRepair] DXBC found at offset {dxbcStart}");

            // 2. Check if this is a valid DXBC container with proper header
            // DXBC Header: Magic(4) + Checksum(16) + Version(4) + Size(4) + ChunkCount(4) = 32 bytes
            if (dxbcStart + 32 <= rawData.Length)
            {
                uint containerSize = BitConverter.ToUInt32(rawData, dxbcStart + 24);
                uint chunkCount = BitConverter.ToUInt32(rawData, dxbcStart + 28);
                
                // Sanity check: size should be reasonable and fit in buffer
                if (containerSize > 0 && containerSize <= (rawData.Length - dxbcStart) && 
                    chunkCount > 0 && chunkCount < 20) // Usually < 10 chunks
                {
                    Console.WriteLine($"[DxbcRepair] Valid DXBC container detected. Size={containerSize}, Chunks={chunkCount}");
                    
                    // Extract the complete container directly
                    byte[] validDxbc = new byte[containerSize];
                    Array.Copy(rawData, dxbcStart, validDxbc, 0, (int)containerSize);
                    return validDxbc;
                }
            }

            // 3. Invalid header - need to reconstruct from SHEX chunk
            Console.WriteLine("[DxbcRepair] Container header invalid, attempting reconstruction...");
            
            int shexOffset = FindMagic(rawData, ShexMagic, dxbcStart);
            if (shexOffset < 0)
            {
                Console.WriteLine("[DxbcRepair] SHEX chunk not found.");
                return null;
            }

            // 4. Extract SHEX
            if (shexOffset + 8 > rawData.Length) return null;
            uint shexPayloadSize = BitConverter.ToUInt32(rawData, shexOffset + 4);
            int shexTotalSize = 8 + (int)shexPayloadSize;

            if (shexOffset + shexTotalSize > rawData.Length)
            {
                Console.WriteLine($"[DxbcRepair] SHEX size mismatch. Payload: {shexPayloadSize}, Avail: {rawData.Length - shexOffset}");
                return null;
            }

            byte[] shexData = new byte[shexTotalSize];
            Array.Copy(rawData, shexOffset, shexData, 0, shexTotalSize);
            Console.WriteLine($"[DxbcRepair] Extracted SHEX ({shexTotalSize} bytes).");

            // 4. Create Dummy ISGN
            // ISGN: "ISGN" + Size(8) + NumElements(0) + Unknown(0)
            // Total 16 bytes.
            byte[] dummyIsgn = new byte[16];
            Array.Copy(IsgnMagic, 0, dummyIsgn, 0, 4);
            BitConverter.GetBytes((uint)8).CopyTo(dummyIsgn, 4); // Size = 8
            BitConverter.GetBytes((uint)0).CopyTo(dummyIsgn, 8); // Num Elements = 0
            BitConverter.GetBytes((uint)0).CopyTo(dummyIsgn, 12); // Unknown = 0

            // 5. Rebuild Container
            // Header: 32 bytes
            // Offsets: 2 * 4 = 8 bytes
            // Chunks: 16 (ISGN) + shexTotalSize
            int headerSize = 32;
            int offsetTableSize = 8;
            int totalSize = headerSize + offsetTableSize + dummyIsgn.Length + shexData.Length;

            byte[] newDxbc = new byte[totalSize];

            // Header
            Array.Copy(DxbcMagic, 0, newDxbc, 0, 4);
            // Checksum (Computed later)
            // Version 1.0 (0x00010000 -> 1, 0)
            // Little Endian: 01 00 00 00 ? No. 1.0 is 1.
            // D3D11 is usually 1.
            newDxbc[20] = 0x01; 
            
            BitConverter.GetBytes((uint)totalSize).CopyTo(newDxbc, 24); // Size
            BitConverter.GetBytes((uint)2).CopyTo(newDxbc, 28); // Chunk Count

            // Offsets
            int currentOffset = headerSize + offsetTableSize;
            
            // Offset 0: ISGN
            BitConverter.GetBytes((uint)currentOffset).CopyTo(newDxbc, 32);
            Array.Copy(dummyIsgn, 0, newDxbc, currentOffset, dummyIsgn.Length);
            currentOffset += dummyIsgn.Length;

            // Offset 1: SHEX
            BitConverter.GetBytes((uint)currentOffset).CopyTo(newDxbc, 36);
            Array.Copy(shexData, 0, newDxbc, currentOffset, shexData.Length);
            
            // Compute Checksum
            // Algorithm: MD5 of bytes 20..End. Placed at 4.
            using (var md5 = MD5.Create())
            {
                // Hash from offset 20 to end
                byte[] hash = md5.ComputeHash(newDxbc, 20, newDxbc.Length - 20);
                Array.Copy(hash, 0, newDxbc, 4, 16);
            }

            Console.WriteLine($"[DxbcRepair] Rebuilt DXBC Container ({totalSize} bytes).");
            return newDxbc;
        }

        private static int FindMagic(byte[] data, byte[] magic, int start = 0)
        {
            for (int i = start; i <= data.Length - magic.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < magic.Length; j++)
                {
                    if (data[i + j] != magic[j])
                    {
                        match = false;
                        break;
                    }
                }
                if (match) return i;
            }
            return -1;
        }
    }
}
