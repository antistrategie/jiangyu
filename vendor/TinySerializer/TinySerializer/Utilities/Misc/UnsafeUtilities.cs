using System;
using System.Runtime.InteropServices;

namespace TinySerializer.Utilities.Misc {
    public static class UnsafeUtilities {
        public static unsafe int StringToBytes(byte[] buffer, string value, bool needs16BitSupport) {
            int byteCount = needs16BitSupport ? value.Length * 2 : value.Length;
            
            if (buffer.Length < byteCount) {
                throw new ArgumentException("Buffer is not large enough to contain the given string; a size of at least " + byteCount + " is required.");
            }
            
            GCHandle toHandle = default;
            
            try {
                toHandle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                
                if (needs16BitSupport) {
                    if (BitConverter.IsLittleEndian) {
                        fixed (char* charPtr1 = value) {
                            ushort* fromPtr1 = (ushort*)charPtr1;
                            ushort* toPtr1 = (ushort*)toHandle.AddrOfPinnedObject().ToPointer();
                            
                            for (int i = 0; i < byteCount; i += sizeof(ushort)) {
                                *toPtr1++ = *fromPtr1++;
                            }
                        }
                    } else {
                        fixed (char* charPtr2 = value) {
                            byte* fromPtr2 = (byte*)charPtr2;
                            byte* toPtr2 = (byte*)toHandle.AddrOfPinnedObject().ToPointer();
                            
                            for (int i = 0; i < byteCount; i += sizeof(ushort)) {
                                *toPtr2 = *(fromPtr2 + 1);
                                *(toPtr2 + 1) = *fromPtr2;
                                
                                fromPtr2 += 2;
                                toPtr2 += 2;
                            }
                        }
                    }
                } else {
                    if (BitConverter.IsLittleEndian) {
                        fixed (char* charPtr3 = value) {
                            byte* fromPtr3 = (byte*)charPtr3;
                            byte* toPtr3 = (byte*)toHandle.AddrOfPinnedObject().ToPointer();
                            
                            for (int i = 0; i < byteCount; i += sizeof(byte)) {
                                fromPtr3++;
                                *toPtr3++ = *fromPtr3++;
                            }
                        }
                    } else {
                        fixed (char* charPtr4 = value) {
                            byte* fromPtr4 = (byte*)charPtr4;
                            byte* toPtr4 = (byte*)toHandle.AddrOfPinnedObject().ToPointer();
                            
                            for (int i = 0; i < byteCount; i += sizeof(byte)) {
                                *toPtr4++ = *fromPtr4++;
                                fromPtr4++;
                            }
                        }
                    }
                }
            } finally {
                if (toHandle.IsAllocated) {
                    toHandle.Free();
                }
            }
            
            return byteCount;
        }
        
        private struct Struct256Bit {
            public decimal d1;
            public decimal d2;
        }
        
        public static unsafe void MemoryCopy(void* from, void* to, int bytes) {
            byte* end = (byte*)to + bytes;
            
            Struct256Bit* fromBigPtr = (Struct256Bit*)from;
            Struct256Bit* toBigPtr = (Struct256Bit*)to;
            
            while ((toBigPtr + 1) <= end) {
                *toBigPtr++ = *fromBigPtr++;
            }
            
            byte* fromSmallPtr = (byte*)fromBigPtr;
            byte* toSmallPtr = (byte*)toBigPtr;
            
            while (toSmallPtr < end) {
                *toSmallPtr++ = *fromSmallPtr++;
            }
        }
        
        public static unsafe void MemoryCopy(object from, object to, int byteCount, int fromByteOffset, int toByteOffset) {
            GCHandle fromHandle = default;
            GCHandle toHandle = default;
            
            if (fromByteOffset % sizeof(ulong) != 0 || toByteOffset % sizeof(ulong) != 0) {
                throw new ArgumentException("Byte offset must be divisible by " + sizeof(ulong) + " (IE, sizeof(ulong))");
            }
            
            try {
                int restBytes = byteCount % sizeof(ulong);
                int ulongCount = (byteCount - restBytes) / sizeof(ulong);
                int fromOffsetCount = fromByteOffset / sizeof(ulong);
                int toOffsetCount = toByteOffset / sizeof(ulong);
                
                fromHandle = GCHandle.Alloc(from, GCHandleType.Pinned);
                toHandle = GCHandle.Alloc(to, GCHandleType.Pinned);
                
                ulong* fromUlongPtr = (ulong*)fromHandle.AddrOfPinnedObject().ToPointer();
                ulong* toUlongPtr = (ulong*)toHandle.AddrOfPinnedObject().ToPointer();
                
                if (fromOffsetCount > 0) {
                    fromUlongPtr += fromOffsetCount;
                }
                
                if (toOffsetCount > 0) {
                    toUlongPtr += toOffsetCount;
                }
                
                for (int i = 0; i < ulongCount; i++) {
                    *toUlongPtr++ = *fromUlongPtr++;
                }
                
                if (restBytes > 0) {
                    byte* fromBytePtr = (byte*)fromUlongPtr;
                    byte* toBytePtr = (byte*)toUlongPtr;
                    
                    for (int i = 0; i < restBytes; i++) {
                        *toBytePtr++ = *fromBytePtr++;
                    }
                }
            } finally {
                if (fromHandle.IsAllocated) {
                    fromHandle.Free();
                }
                
                if (toHandle.IsAllocated) {
                    toHandle.Free();
                }
            }
        }
    }
}