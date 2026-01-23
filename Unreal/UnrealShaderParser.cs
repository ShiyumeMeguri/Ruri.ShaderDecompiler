using System;
using System.Collections.Generic;
using System.IO;
using Ruri.ShaderDecompiler.Intermediate;

namespace Ruri.ShaderDecompiler.Unreal
{
    public class UnrealShaderParser
    {
        public static ShaderBundle Parse(byte[] data)
        {
            using var reader = new BinaryReader(new MemoryStream(data));
            
            // Try to parse FShaderResourceTable
            // SRT structure:
            // uint32 ResourceTableBits
            // TArray<uint32> ShaderResourceViewMap
            // TArray<uint32> SamplerMap
            // TArray<uint32> UnorderedAccessViewMap
            // TArray<uint32> ResourceTableLayoutHashes
            // TArray<uint32> TextureMap (Added in later versions, verify presence)
            
            // Heuristic: If it starts with a reasonable bitmask (usually 0 or small) and then valid array lengths
            // It might be a UE shader.
            
            // However, FModel might export raw DXBC if it skips SRT? 
            // If data starts with "DXBC", it's raw.
            if (IsDxbc(data))
            {
                return new ShaderBundle { NativeCode = data, Architecture = ShaderArchitecture.Dxbc };
            }
            if (IsDxil(data))
            {
                return new ShaderBundle { NativeCode = data, Architecture = ShaderArchitecture.Dxil };
            }

            // Assume UE format with SRT
            var srt = new FShaderResourceTable();
            try {
                srt.ResourceTableBits = reader.ReadUInt32();
                srt.ShaderResourceViewMap = ReadUInt32Array(reader);
                srt.SamplerMap = ReadUInt32Array(reader);
                srt.UnorderedAccessViewMap = ReadUInt32Array(reader);
                srt.ResourceTableLayoutHashes = ReadUInt32Array(reader);
                // TextureMap presence depends on version. Try to read.
                // If EOF, might be old version.
                if (reader.BaseStream.Position < reader.BaseStream.Length)
                {
                    srt.TextureMap = ReadUInt32Array(reader);
                }
            } catch {
                // Not a valid SRT, fall through to fallback
                Console.WriteLine("[Debug] SRT Parse failed, falling back to scan.");
            }


            // After SRT, there might be Optional Data (FShaderCodePackedResourceCounts etc)
            // UE uses FShaderCodeReader which checks for optional data map.
            // But usually the Native Code follows immediately or after some padding.
            
            // Find DXBC or DXIL magic
            long codeStart = -1;
            ShaderArchitecture arch = ShaderArchitecture.Unknown;
            
            long currentPos = reader.BaseStream.Position;
            byte[] remaining = reader.ReadBytes((int)(reader.BaseStream.Length - currentPos));
            
            int dxbcOffset = FindSequence(remaining, new byte[] { 0x44, 0x58, 0x42, 0x43 }); // DXBC
            if (dxbcOffset >= 0)
            {
                codeStart = currentPos + dxbcOffset;
                arch = ShaderArchitecture.Dxbc;
            }
            else
            {
                int dxilOffset = FindSequence(remaining, new byte[] { 0x44, 0x58, 0x49, 0x4C }); // DXIL
                if (dxilOffset >= 0)
                {
                    codeStart = currentPos + dxilOffset;
                    arch = ShaderArchitecture.Dxil;
                }
                else
                {
                     // Try finding "SHEX" or "ILDB" if header is stripped
                     int shexOffset = FindSequence(remaining, new byte[] { 0x53, 0x48, 0x45, 0x58 }); // SHEX
                     if (shexOffset >= 0)
                     {
                         // Found stripped DXBC
                         codeStart = currentPos; // We might need to reconstruct header, but for now take from current
                         // Wait, if SHEX found, the data start is likely 'currentPos' (after SRT).
                         // We'll pass the whole remaining block to Repair
                         arch = ShaderArchitecture.Dxbc;
                     }
                }
            }

            if (arch != ShaderArchitecture.Unknown && codeStart >= 0)
            {
                // Parse optional data between SRT and Code
                var metadata = new UnrealMetadata { SRT = srt, UniformBufferNames = new List<string>() };

                long optionalDataStart = reader.BaseStream.Position; // End of SRT
                long optionalDataLength = codeStart - optionalDataStart;
                
                if (optionalDataLength > 0)
                {
                    byte[] optData = new byte[optionalDataLength];
                    Array.Copy(data, optionalDataStart, optData, 0, optionalDataLength);
                    ParseOptionalData(optData, metadata);
                }

                // Extract code
                int len = (int)(data.Length - codeStart);
                uint containerSize = BitConverter.ToUInt32(data, (int)codeStart + 24);
                
                // If containerSize is valid and smaller than len, there's data after
                int nativeCodeSize = (int)containerSize;
                if (nativeCodeSize <= 0 || nativeCodeSize > len) nativeCodeSize = len;

                byte[] code = new byte[nativeCodeSize];
                Array.Copy(data, codeStart, code, 0, nativeCodeSize);
                
                // Check for optional data after native code
                if (nativeCodeSize < len)
                {
                    int optAfterStart = (int)codeStart + nativeCodeSize;
                    int optAfterSize = len - nativeCodeSize;
                    byte[] optDataAfter = new byte[optAfterSize];
                    Array.Copy(data, optAfterStart, optDataAfter, 0, optAfterSize);
                    ParseOptionalData(optDataAfter, metadata);
                }

                var bundle = new ShaderBundle 
                { 
                    NativeCode = code, 
                    Architecture = arch,
                    EngineMetadata = metadata 
                };
                
                // Map Uniform Buffers
                if (metadata.UniformBufferNames != null && metadata.UniformBufferNames.Count > 0)
                {
                    for (int i = 0; i < metadata.UniformBufferNames.Count; i++)
                    {
                        bundle.Symbols.Resources.Add(new ResourceSymbol
                        {
                            Name = metadata.UniformBufferNames[i],
                            Set = 0,
                            Binding = i,
                            Type = ResourceType.UniformBuffer,
                            Slot = i
                        });
                    }
                }
                
                return bundle;
            }
            
            // Fallback: Scan for magic in the whole buffer
            long fallbackOffset = -1;
            var fallbackArch = ShaderArchitecture.Unknown;
            
            // Re-use logic to find DXBC/DXIL/SHEX in 'data'
            Console.WriteLine($"[Debug] Fallback Scan on {data.Length} bytes...");
            int fDxbc = FindSequence(data, new byte[] { 0x44, 0x58, 0x42, 0x43 });
            if (fDxbc >= 0) { Console.WriteLine($"[Debug] Found DXBC at {fDxbc}"); fallbackOffset = fDxbc; fallbackArch = ShaderArchitecture.Dxbc; }
            else 
            {
                int fDxil = FindSequence(data, new byte[] { 0x44, 0x58, 0x49, 0x4C });
                if (fDxil >= 0) { Console.WriteLine($"[Debug] Found DXIL at {fDxil}"); fallbackOffset = fDxil; fallbackArch = ShaderArchitecture.Dxil; }
                else
                {
                    int fShex = FindSequence(data, new byte[] { 0x53, 0x48, 0x45, 0x58 });
                    if (fShex >= 0) { Console.WriteLine($"[Debug] Found SHEX at {fShex}"); fallbackOffset = fShex; fallbackArch = ShaderArchitecture.Dxbc; }
                    else Console.WriteLine("[Debug] No magic found.");
                }
            }

            if (fallbackArch != ShaderArchitecture.Unknown && fallbackOffset >= 0)
            {
                int len = (int)(data.Length - fallbackOffset);
                byte[] code = new byte[len];
                Array.Copy(data, fallbackOffset, code, 0, len);
                return new ShaderBundle { NativeCode = code, Architecture = fallbackArch };
            }

            return new ShaderBundle { NativeCode = data, Architecture = ShaderArchitecture.Unknown };
        }

        public class UnrealMetadata
        {
            public FShaderResourceTable SRT;
            public List<string> UniformBufferNames;
            public string ShaderName;
        }

        private static void ParseOptionalData(byte[] data, UnrealMetadata metadata)
        {
            try 
            {
                using var stream = new MemoryStream(data);
                using var reader = new BinaryReader(stream);
                
                while (stream.Position < stream.Length)
                {
                    byte key = reader.ReadByte();
                    int size = reader.ReadInt32();
                    
                    if (stream.Position + size > stream.Length) break;
                    long nextPos = stream.Position + size;
                    
                    // 'u' = UniformBuffers (Array<FString>)
                    if (key == (byte)'u') 
                    {
                        if (metadata.UniformBufferNames == null) metadata.UniformBufferNames = new List<string>();
                        
                        // int count
                        int count = reader.ReadInt32();
                        for(int i=0; i<count; i++)
                        {
                            int strLen = reader.ReadInt32(); // FString length
                            string s = "";
                            if (strLen == 0) { }
                            else if (strLen > 0)
                            {
                                // Detection: Read strLen bytes. If we find many nulls or it looks like UTF16, read strLen*2.
                                byte[] peek = reader.ReadBytes(Math.Min(strLen * 2, (int)(stream.Length - stream.Position)));
                                stream.Position -= peek.Length;

                                if (peek.Length >= 2 && peek[1] == 0) // Heuristic: second byte 0 is common for ASCII in UTF16
                                {
                                    byte[] strBytes = reader.ReadBytes(strLen * 2);
                                    if (strLen > 1) s = System.Text.Encoding.Unicode.GetString(strBytes, 0, (strLen - 1) * 2);
                                }
                                else
                                {
                                    byte[] strBytes = reader.ReadBytes(strLen);
                                    if (strLen > 1) s = System.Text.Encoding.ASCII.GetString(strBytes, 0, strLen - 1);
                                }
                            }
                            else
                            {
                                int len = -strLen;
                                byte[] strBytes = reader.ReadBytes(len);
                                if (len > 1) s = System.Text.Encoding.ASCII.GetString(strBytes, 0, len - 1);
                            }
                            metadata.UniformBufferNames.Add(s);
                            // Console.WriteLine($"[Debug] Found UB Name: {s}");
                        }
                    }
                    // 'n' = ShaderName (Straight ANSI string)
                    else if (key == (byte)'n')
                    {
                        if (size > 1)
                        {
                            byte[] nameBytes = reader.ReadBytes(size - 1); // Exclude null terminator
                            reader.ReadByte(); // Consume null terminator
                            metadata.ShaderName = System.Text.Encoding.ASCII.GetString(nameBytes);
                            Console.WriteLine($"[Debug] Found Shader Name: {metadata.ShaderName}");
                        }
                        else
                        {
                             reader.ReadBytes(size);
                        }
                    }
                    else
                    {
                        // Skip
                        // Console.WriteLine($"[Debug] Skipped Optional Data Key: {(char)key} Size: {size}");
                        stream.Seek(size, SeekOrigin.Current); // Use original size logic (reader might have advanced)
                        // Wait, if I read bytes above, current position changed.
                        // It's safer to always use nextPos
                    }
                    stream.Position = nextPos;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Debug] Error parsing optional data: {ex.Message}");
            }
        }


        private static List<uint> ReadUInt32Array(BinaryReader reader)
        {
            int count = reader.ReadInt32();
            if (count < 0 || count > 10000) throw new Exception("Invalid array count");
            var list = new List<uint>(count);
            for(int i=0; i<count; i++) list.Add(reader.ReadUInt32());
            return list;
        }

        private static bool IsDxbc(byte[] data)
        {
             if (data.Length < 4) return false;
             return data[0] == 0x44 && data[1] == 0x58 && data[2] == 0x42 && data[3] == 0x43;
        }
        
        private static bool IsDxil(byte[] data)
        {
             if (data.Length < 4) return false;
             return data[0] == 0x44 && data[1] == 0x58 && data[2] == 0x49 && data[3] == 0x4C;
        }

        private static ShaderArchitecture DetectArch(byte[] data)
        {
            if (IsDxbc(data)) return ShaderArchitecture.Dxbc;
            if (IsDxil(data)) return ShaderArchitecture.Dxil;
            return ShaderArchitecture.Unknown;
        }

        private static int FindSequence(byte[] haystack, byte[] needle)
        {
            for (int i = 0; i <= haystack.Length - needle.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j])
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

    public struct FShaderResourceTable
    {
        public uint ResourceTableBits;
        public List<uint> ShaderResourceViewMap;
        public List<uint> SamplerMap;
        public List<uint> UnorderedAccessViewMap;
        public List<uint> ResourceTableLayoutHashes;
        public List<uint> TextureMap;
    }
}
