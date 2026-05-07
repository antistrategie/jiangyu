using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using TinySerializer.Core.Misc;

namespace TinySerializer.Core.DataReaderWriters.Json {
    public class JsonDataWriter : BaseDataWriter {
        private static readonly uint[] ByteToHexCharLookup = CreateByteToHexLookup();
        private static readonly string NEW_LINE = Environment.NewLine;
        
        private bool justStarted;
        private bool forceNoSeparatorNextLine;
        
        private Dictionary<Type, Delegate> primitiveTypeWriters;
        private Dictionary<Type, int> seenTypes = new Dictionary<Type, int>(16);
        
        private byte[] buffer = new byte[1024 * 100];
        private int bufferIndex = 0;
        
        public JsonDataWriter() : this(null, null, true) { }
        
        public JsonDataWriter(Stream stream, SerializationContext context, bool formatAsReadable = true) : base(stream, context) {
            FormatAsReadable = formatAsReadable;
            justStarted = true;
            EnableTypeOptimization = true;
            
            primitiveTypeWriters = new Dictionary<Type, Delegate>() {
                { typeof(char), (Action<string, char>)WriteChar },
                { typeof(sbyte), (Action<string, sbyte>)WriteSByte },
                { typeof(short), (Action<string, short>)WriteInt16 },
                { typeof(int), (Action<string, int>)WriteInt32 },
                { typeof(long), (Action<string, long>)WriteInt64 },
                { typeof(byte), (Action<string, byte>)WriteByte },
                { typeof(ushort), (Action<string, ushort>)WriteUInt16 },
                { typeof(uint), (Action<string, uint>)WriteUInt32 },
                { typeof(ulong), (Action<string, ulong>)WriteUInt64 },
                { typeof(decimal), (Action<string, decimal>)WriteDecimal },
                { typeof(bool), (Action<string, bool>)WriteBoolean },
                { typeof(float), (Action<string, float>)WriteSingle },
                { typeof(double), (Action<string, double>)WriteDouble },
                { typeof(Guid), (Action<string, Guid>)WriteGuid }
            };
        }
        
        public bool FormatAsReadable;
        public bool EnableTypeOptimization;
        
        public void MarkJustStarted() {
            justStarted = true;
        }
        
        public override void FlushToStream() {
            if (bufferIndex > 0) {
                Stream.Write(buffer, 0, bufferIndex);
                bufferIndex = 0;
            }
            
            base.FlushToStream();
        }
        
        public override void BeginReferenceNode(string name, Type type, int id) {
            WriteEntry(name, "{");
            PushNode(name, id, type);
            forceNoSeparatorNextLine = true;
            WriteInt32(JsonConfig.ID_SIG, id);
            
            if (type != null) {
                WriteTypeEntry(type);
            }
        }
        
        public override void BeginStructNode(string name, Type type) {
            WriteEntry(name, "{");
            PushNode(name, -1, type);
            forceNoSeparatorNextLine = true;
            
            if (type != null) {
                WriteTypeEntry(type);
            }
        }
        
        public override void EndNode(string name) {
            PopNode(name);
            StartNewLine(true);
            
            EnsureBufferSpace(1);
            buffer[bufferIndex++] = (byte)'}';
        }
        
        public override void BeginArrayNode(long length) {
            WriteInt64(JsonConfig.REGULAR_ARRAY_LENGTH_SIG, length);
            WriteEntry(JsonConfig.REGULAR_ARRAY_CONTENT_SIG, "[");
            forceNoSeparatorNextLine = true;
            PushArray();
        }
        
        public override void EndArrayNode() {
            PopArray();
            StartNewLine(true);
            
            EnsureBufferSpace(1);
            buffer[bufferIndex++] = (byte)']';
        }
        
        public override void WritePrimitiveArray<T>(T[] array) {
            if (FormatterUtilities.IsPrimitiveArrayType(typeof(T)) == false) {
                throw new ArgumentException("Type " + typeof(T).Name + " is not a valid primitive array type.");
            }
            
            if (array == null) {
                throw new ArgumentNullException("array");
            }
            
            Action<string, T> writer = (Action<string, T>)primitiveTypeWriters[typeof(T)];
            
            WriteInt64(JsonConfig.PRIMITIVE_ARRAY_LENGTH_SIG, array.Length);
            WriteEntry(JsonConfig.PRIMITIVE_ARRAY_CONTENT_SIG, "[");
            forceNoSeparatorNextLine = true;
            PushArray();
            
            for (int i = 0; i < array.Length; i++) {
                writer(null, array[i]);
            }
            
            PopArray();
            StartNewLine(true);
            
            EnsureBufferSpace(1);
            buffer[bufferIndex++] = (byte)']';
        }
        
        public override void WriteBoolean(string name, bool value) {
            WriteEntry(name, value ? "true" : "false");
        }
        
        public override void WriteByte(string name, byte value) {
            WriteUInt64(name, value);
        }
        
        public override void WriteChar(string name, char value) {
            WriteString(name, value.ToString(CultureInfo.InvariantCulture));
        }
        
        public override void WriteDecimal(string name, decimal value) {
            WriteEntry(name, value.ToString("G", CultureInfo.InvariantCulture));
        }
        
        public override void WriteDouble(string name, double value) {
            WriteEntry(name, value.ToString("R", CultureInfo.InvariantCulture));
        }
        
        public override void WriteInt32(string name, int value) {
            WriteInt64(name, value);
        }
        
        public override void WriteInt64(string name, long value) {
            WriteEntry(name, value.ToString("D", CultureInfo.InvariantCulture));
        }
        
        public override void WriteNull(string name) {
            WriteEntry(name, "null");
        }
        
        public override void WriteInternalReference(string name, int id) {
            WriteEntry(name, JsonConfig.INTERNAL_REF_SIG + ":" + id.ToString("D", CultureInfo.InvariantCulture));
        }
        
        public override void WriteSByte(string name, sbyte value) {
            WriteInt64(name, value);
        }
        
        public override void WriteInt16(string name, short value) {
            WriteInt64(name, value);
        }
        
        public override void WriteSingle(string name, float value) {
            WriteEntry(name, value.ToString("R", CultureInfo.InvariantCulture));
        }
        
        public override void WriteString(string name, string value) {
            StartNewLine();
            
            if (name != null) {
                EnsureBufferSpace(name.Length + value.Length + 6);
                
                buffer[bufferIndex++] = (byte)'"';
                
                for (int i = 0; i < name.Length; i++) {
                    buffer[bufferIndex++] = (byte)name[i];
                }
                
                buffer[bufferIndex++] = (byte)'"';
                buffer[bufferIndex++] = (byte)':';
                
                if (FormatAsReadable) {
                    buffer[bufferIndex++] = (byte)' ';
                }
            } else EnsureBufferSpace(value.Length + 2);
            
            buffer[bufferIndex++] = (byte)'"';
            
            Buffer_WriteString_WithEscape(value);
            
            buffer[bufferIndex++] = (byte)'"';
        }
        
        public override void WriteGuid(string name, Guid value) {
            WriteEntry(name, value.ToString("D", CultureInfo.InvariantCulture));
        }
        
        public override void WriteUInt32(string name, uint value) {
            WriteUInt64(name, value);
        }
        
        public override void WriteUInt64(string name, ulong value) {
            WriteEntry(name, value.ToString("D", CultureInfo.InvariantCulture));
        }
        
        public override void WriteExternalReference(string name, int index) {
            WriteEntry(name, JsonConfig.EXTERNAL_INDEX_REF_SIG + ":" + index.ToString("D", CultureInfo.InvariantCulture));
        }
        
        public override void WriteExternalReference(string name, Guid guid) {
            WriteEntry(name, JsonConfig.EXTERNAL_GUID_REF_SIG + ":" + guid.ToString("D", CultureInfo.InvariantCulture));
        }
        
        public override void WriteExternalReference(string name, string id) {
            if (id == null) {
                throw new ArgumentNullException("id");
            }
            
            WriteEntry(name, JsonConfig.EXTERNAL_STRING_REF_SIG_FIXED);
            EnsureBufferSpace(id.Length + 3);
            buffer[bufferIndex++] = (byte)':';
            buffer[bufferIndex++] = (byte)'"';
            Buffer_WriteString_WithEscape(id);
            buffer[bufferIndex++] = (byte)'"';
        }
        
        public override void WriteUInt16(string name, ushort value) {
            WriteUInt64(name, value);
        }
        
        public override void Dispose() { }
        
        public override void PrepareNewSerializationSession() {
            base.PrepareNewSerializationSession();
            seenTypes.Clear();
            justStarted = true;
        }
        
        public override string GetDataDump() {
            if (!Stream.CanRead) {
                return "Json data stream for writing cannot be read; cannot dump data.";
            }
            
            if (!Stream.CanSeek) {
                return "Json data stream cannot seek; cannot dump data.";
            }
            
            long oldPosition = Stream.Position;
            
            byte[] bytes = new byte[oldPosition];
            
            Stream.Position = 0;
            Stream.Read(bytes, 0, (int)oldPosition);
            
            Stream.Position = oldPosition;
            
            return "Json: " + Encoding.UTF8.GetString(bytes, 0, bytes.Length);
        }
        
        private void WriteEntry(string name, string contents) {
            StartNewLine();
            
            if (name != null) {
                EnsureBufferSpace(name.Length + contents.Length + 4);
                
                buffer[bufferIndex++] = (byte)'"';
                
                for (int i = 0; i < name.Length; i++) {
                    buffer[bufferIndex++] = (byte)name[i];
                }
                
                buffer[bufferIndex++] = (byte)'"';
                buffer[bufferIndex++] = (byte)':';
                
                if (FormatAsReadable) {
                    buffer[bufferIndex++] = (byte)' ';
                }
            } else EnsureBufferSpace(contents.Length);
            
            for (int i = 0; i < contents.Length; i++) {
                buffer[bufferIndex++] = (byte)contents[i];
            }
        }
        
        private void WriteTypeEntry(Type type) {
            int id;
            
            if (EnableTypeOptimization) {
                if (seenTypes.TryGetValue(type, out id)) {
                    WriteInt32(JsonConfig.TYPE_SIG, id);
                } else {
                    id = seenTypes.Count;
                    seenTypes.Add(type, id);
                    WriteString(JsonConfig.TYPE_SIG, id + "|" + Context.Binder.BindToName(type, Context.Config.DebugContext));
                }
            } else {
                WriteString(JsonConfig.TYPE_SIG, Context.Binder.BindToName(type, Context.Config.DebugContext));
            }
        }
        
        private void StartNewLine(bool noSeparator = false) {
            if (justStarted) {
                justStarted = false;
                return;
            }
            
            if (noSeparator == false && forceNoSeparatorNextLine == false) {
                EnsureBufferSpace(1);
                buffer[bufferIndex++] = (byte)',';
            }
            
            forceNoSeparatorNextLine = false;
            
            if (FormatAsReadable) {
                int count = NodeDepth * 4;
                
                EnsureBufferSpace(NEW_LINE.Length + count);
                
                for (int i = 0; i < NEW_LINE.Length; i++) {
                    buffer[bufferIndex++] = (byte)NEW_LINE[i];
                }
                
                for (int i = 0; i < count; i++) {
                    buffer[bufferIndex++] = (byte)' ';
                }
            }
        }
        
        
        private void EnsureBufferSpace(int space) {
            int length = buffer.Length;
            
            if (space > length) {
                throw new Exception("Insufficient buffer capacity");
            }
            
            if (bufferIndex + space > length) {
                FlushToStream();
            }
        }
        
        private void Buffer_WriteString_WithEscape(string str) {
            EnsureBufferSpace(str.Length);
            
            for (int i = 0; i < str.Length; i++) {
                char c = str[i];
                
                if (c < 0 || c > 127) {
                    EnsureBufferSpace((str.Length - i) + 6);
                    
                    buffer[bufferIndex++] = (byte)'\\';
                    buffer[bufferIndex++] = (byte)'u';
                    
                    int byte1 = c >> 8;
                    byte byte2 = (byte)c;
                    
                    uint lookup = ByteToHexCharLookup[byte1];
                    
                    buffer[bufferIndex++] = (byte)lookup;
                    buffer[bufferIndex++] = (byte)(lookup >> 16);
                    
                    lookup = ByteToHexCharLookup[byte2];
                    
                    buffer[bufferIndex++] = (byte)lookup;
                    buffer[bufferIndex++] = (byte)(lookup >> 16);
                    continue;
                }
                
                EnsureBufferSpace(2);
                
                switch (c) {
                    case '"':
                        buffer[bufferIndex++] = (byte)'\\';
                        buffer[bufferIndex++] = (byte)'"';
                        break;
                    
                    case '\\':
                        buffer[bufferIndex++] = (byte)'\\';
                        buffer[bufferIndex++] = (byte)'\\';
                        break;
                    
                    case '\a':
                        buffer[bufferIndex++] = (byte)'\\';
                        buffer[bufferIndex++] = (byte)'a';
                        break;
                    
                    case '\b':
                        buffer[bufferIndex++] = (byte)'\\';
                        buffer[bufferIndex++] = (byte)'b';
                        break;
                    
                    case '\f':
                        buffer[bufferIndex++] = (byte)'\\';
                        buffer[bufferIndex++] = (byte)'f';
                        break;
                    
                    case '\n':
                        buffer[bufferIndex++] = (byte)'\\';
                        buffer[bufferIndex++] = (byte)'n';
                        break;
                    
                    case '\r':
                        buffer[bufferIndex++] = (byte)'\\';
                        buffer[bufferIndex++] = (byte)'r';
                        break;
                    
                    case '\t':
                        buffer[bufferIndex++] = (byte)'\\';
                        buffer[bufferIndex++] = (byte)'t';
                        break;
                    
                    case '\0':
                        buffer[bufferIndex++] = (byte)'\\';
                        buffer[bufferIndex++] = (byte)'0';
                        break;
                    
                    default:
                        buffer[bufferIndex++] = (byte)c;
                        break;
                }
            }
        }
        
        private static uint[] CreateByteToHexLookup() {
            uint[] result = new uint[256];
            
            for (int i = 0; i < 256; i++) {
                string s = i.ToString("x2", CultureInfo.InvariantCulture);
                result[i] = ((uint)s[0]) + ((uint)s[1] << 16);
            }
            
            return result;
        }
    }
}