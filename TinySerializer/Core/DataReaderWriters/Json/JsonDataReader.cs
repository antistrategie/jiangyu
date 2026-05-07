using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using TinySerializer.Core.Misc;

namespace TinySerializer.Core.DataReaderWriters.Json {
    public class JsonDataReader : BaseDataReader {
        private JsonTextReader reader;
        private EntryType? peekedEntryType;
        private string peekedEntryName;
        private string peekedEntryContent;
        private Dictionary<int, Type> seenTypes = new Dictionary<int, Type>(16);
        
        private readonly Dictionary<Type, Delegate> primitiveArrayReaders;
        
        public JsonDataReader() : this(null, null) { }
        
        public JsonDataReader(Stream stream, DeserializationContext context) : base(stream, context) {
            primitiveArrayReaders = new Dictionary<Type, Delegate>() {
                {
                    typeof(char), (Func<char>)(() =>
                    {
                        char v;
                        ReadChar(out v);
                        return v;
                    })
                }, {
                    typeof(sbyte), (Func<sbyte>)(() =>
                    {
                        sbyte v;
                        ReadSByte(out v);
                        return v;
                    })
                }, {
                    typeof(short), (Func<short>)(() =>
                    {
                        short v;
                        ReadInt16(out v);
                        return v;
                    })
                }, {
                    typeof(int), (Func<int>)(() =>
                    {
                        int v;
                        ReadInt32(out v);
                        return v;
                    })
                }, {
                    typeof(long), (Func<long>)(() =>
                    {
                        long v;
                        ReadInt64(out v);
                        return v;
                    })
                }, {
                    typeof(byte), (Func<byte>)(() =>
                    {
                        byte v;
                        ReadByte(out v);
                        return v;
                    })
                }, {
                    typeof(ushort), (Func<ushort>)(() =>
                    {
                        ushort v;
                        ReadUInt16(out v);
                        return v;
                    })
                }, {
                    typeof(uint), (Func<uint>)(() =>
                    {
                        uint v;
                        ReadUInt32(out v);
                        return v;
                    })
                }, {
                    typeof(ulong), (Func<ulong>)(() =>
                    {
                        ulong v;
                        ReadUInt64(out v);
                        return v;
                    })
                }, {
                    typeof(decimal), (Func<decimal>)(() =>
                    {
                        decimal v;
                        ReadDecimal(out v);
                        return v;
                    })
                }, {
                    typeof(bool), (Func<bool>)(() =>
                    {
                        bool v;
                        ReadBoolean(out v);
                        return v;
                    })
                }, {
                    typeof(float), (Func<float>)(() =>
                    {
                        float v;
                        ReadSingle(out v);
                        return v;
                    })
                }, {
                    typeof(double), (Func<double>)(() =>
                    {
                        double v;
                        ReadDouble(out v);
                        return v;
                    })
                }, {
                    typeof(Guid), (Func<Guid>)(() =>
                    {
                        Guid v;
                        ReadGuid(out v);
                        return v;
                    })
                }
            };
        }
        
    #pragma warning disable IDE0009
        
        public override Stream Stream {
            get => base.Stream;
            
            set {
                base.Stream = value;
                reader = new JsonTextReader(base.Stream, Context);
            }
        }
        
    #pragma warning restore IDE0009
        
        public override void Dispose() {
            reader.Dispose();
        }
        
        public override EntryType PeekEntry(out string name) {
            if (peekedEntryType != null) {
                name = peekedEntryName;
                return peekedEntryType.Value;
            }
            
            EntryType entry;
            reader.ReadToNextEntry(out name, out peekedEntryContent, out entry);
            peekedEntryName = name;
            peekedEntryType = entry;
            
            return entry;
        }
        
        public override bool EnterNode(out Type type) {
            PeekEntry();
            
            if (peekedEntryType == EntryType.StartOfNode) {
                string nodeName = peekedEntryName;
                int id = -1;
                
                ReadToNextEntry();
                
                if (peekedEntryName == JsonConfig.ID_SIG) {
                    if (int.TryParse(peekedEntryContent, NumberStyles.Any, CultureInfo.InvariantCulture, out id) == false) {
                        Context.Config.DebugContext.LogError("Failed to parse id: " + peekedEntryContent);
                        id = -1;
                    }
                    
                    ReadToNextEntry();
                }
                
                if (peekedEntryName == JsonConfig.TYPE_SIG && peekedEntryContent != null && peekedEntryContent.Length > 0) {
                    if (peekedEntryType == EntryType.Integer) {
                        int typeID;
                        
                        if (ReadInt32(out typeID)) {
                            if (seenTypes.TryGetValue(typeID, out type) == false) {
                                Context.Config.DebugContext.LogError("Missing type id for node with reference id " + id + ": " + typeID);
                            }
                        } else {
                            Context.Config.DebugContext.LogError("Failed to read type id for node with reference id " + id);
                            type = null;
                        }
                    } else {
                        int typeNameStartIndex = 1;
                        int typeID = -1;
                        int idSplitIndex = peekedEntryContent.IndexOf('|');
                        
                        if (idSplitIndex >= 0) {
                            typeNameStartIndex = idSplitIndex + 1;
                            string idStr = peekedEntryContent.Substring(1, idSplitIndex - 1);
                            
                            if (int.TryParse(idStr, NumberStyles.Any, CultureInfo.InvariantCulture, out typeID) == false) {
                                typeID = -1;
                            }
                        }
                        
                        type = Context.Binder.BindToType(peekedEntryContent.Substring(typeNameStartIndex, peekedEntryContent.Length - (1 + typeNameStartIndex)),
                                                              Context.Config.DebugContext);
                        
                        if (typeID >= 0) {
                            seenTypes[typeID] = type;
                        }
                        
                        peekedEntryType = null;
                    }
                } else {
                    type = null;
                }
                
                PushNode(nodeName, id, type);
                return true;
            } else {
                SkipEntry();
                type = null;
                return false;
            }
        }
        
        public override bool ExitNode() {
            PeekEntry();
            
            while (peekedEntryType != EntryType.EndOfNode && peekedEntryType != EntryType.EndOfStream) {
                if (peekedEntryType == EntryType.EndOfArray) {
                    Context.Config.DebugContext.LogError("Data layout mismatch; skipping past array boundary when exiting node.");
                    peekedEntryType = null;
                    
                }
                
                SkipEntry();
            }
            
            if (peekedEntryType == EntryType.EndOfNode) {
                peekedEntryType = null;
                PopNode(CurrentNodeName);
                return true;
            }
            
            return false;
        }
        
        public override bool EnterArray(out long length) {
            PeekEntry();
            
            if (peekedEntryType == EntryType.StartOfArray) {
                PushArray();
                
                if (peekedEntryName != JsonConfig.REGULAR_ARRAY_LENGTH_SIG) {
                    Context.Config.DebugContext.LogError("Array entry wasn't preceded by an array length entry!");
                    length = 0;
                    return true;
                } else {
                    int intLength;
                    
                    if (int.TryParse(peekedEntryContent, NumberStyles.Any, CultureInfo.InvariantCulture, out intLength) == false) {
                        Context.Config.DebugContext.LogError("Failed to parse array length: " + peekedEntryContent);
                        length = 0;
                        return true;
                    }
                    
                    length = intLength;
                    
                    ReadToNextEntry();
                    
                    if (peekedEntryName != JsonConfig.REGULAR_ARRAY_CONTENT_SIG) {
                        Context.Config.DebugContext.LogError("Failed to find regular array content entry after array length entry!");
                        length = 0;
                        return true;
                    }
                    
                    peekedEntryType = null;
                    return true;
                }
            } else {
                SkipEntry();
                length = 0;
                return false;
            }
        }
        
        public override bool ExitArray() {
            PeekEntry();
            
            while (peekedEntryType != EntryType.EndOfArray && peekedEntryType != EntryType.EndOfStream) {
                if (peekedEntryType == EntryType.EndOfNode) {
                    Context.Config.DebugContext.LogError("Data layout mismatch; skipping past node boundary when exiting array.");
                    peekedEntryType = null;
                    
                }
                
                SkipEntry();
            }
            
            if (peekedEntryType == EntryType.EndOfArray) {
                peekedEntryType = null;
                PopArray();
                return true;
            }
            
            return false;
        }
        
        public override bool ReadPrimitiveArray<T>(out T[] array) {
            if (FormatterUtilities.IsPrimitiveArrayType(typeof(T)) == false) {
                throw new ArgumentException("Type " + typeof(T).Name + " is not a valid primitive array type.");
            }
            
            PeekEntry();
            
            if (peekedEntryType == EntryType.PrimitiveArray) {
                PushArray();
                
                if (peekedEntryName != JsonConfig.PRIMITIVE_ARRAY_LENGTH_SIG) {
                    Context.Config.DebugContext.LogError("Array entry wasn't preceded by an array length entry!");
                    array = null;
                    return false;
                } else {
                    int intLength;
                    
                    if (int.TryParse(peekedEntryContent, NumberStyles.Any, CultureInfo.InvariantCulture, out intLength) == false) {
                        Context.Config.DebugContext.LogError("Failed to parse array length: " + peekedEntryContent);
                        array = null;
                        return false;
                    }
                    
                    ReadToNextEntry();
                    
                    if (peekedEntryName != JsonConfig.PRIMITIVE_ARRAY_CONTENT_SIG) {
                        Context.Config.DebugContext.LogError("Failed to find primitive array content entry after array length entry!");
                        array = null;
                        return false;
                    }
                    
                    peekedEntryType = null;
                    
                    Func<T> reader = (Func<T>)primitiveArrayReaders[typeof(T)];
                    array = new T[intLength];
                    
                    for (int i = 0; i < intLength; i++) {
                        array[i] = reader();
                    }
                    
                    ExitArray();
                    return true;
                }
            } else {
                SkipEntry();
                array = null;
                return false;
            }
        }
        
        public override bool ReadBoolean(out bool value) {
            PeekEntry();
            
            if (peekedEntryType == EntryType.Boolean) {
                try {
                    value = peekedEntryContent == "true";
                    return true;
                } finally {
                    MarkEntryConsumed();
                }
            } else {
                SkipEntry();
                value = default(bool);
                return false;
            }
        }
        
        public override bool ReadInternalReference(out int id) {
            PeekEntry();
            
            if (peekedEntryType == EntryType.InternalReference) {
                try {
                    return ReadAnyIntReference(out id);
                } finally {
                    MarkEntryConsumed();
                }
            } else {
                SkipEntry();
                id = -1;
                return false;
            }
        }
        
        public override bool ReadExternalReference(out int index) {
            PeekEntry();
            
            if (peekedEntryType == EntryType.ExternalReferenceByIndex) {
                try {
                    return ReadAnyIntReference(out index);
                } finally {
                    MarkEntryConsumed();
                }
            } else {
                SkipEntry();
                index = -1;
                return false;
            }
        }
        
        public override bool ReadExternalReference(out Guid guid) {
            PeekEntry();
            
            if (peekedEntryType == EntryType.ExternalReferenceByGuid) {
                string guidStr = peekedEntryContent;
                
                if (guidStr.StartsWith(JsonConfig.EXTERNAL_GUID_REF_SIG)) {
                    guidStr = guidStr.Substring(JsonConfig.EXTERNAL_GUID_REF_SIG.Length + 1);
                }
                
                try {
                    guid = new Guid(guidStr);
                    return true;
                } catch (FormatException) {
                    guid = Guid.Empty;
                    return false;
                } catch (OverflowException) {
                    guid = Guid.Empty;
                    return false;
                } finally {
                    MarkEntryConsumed();
                }
            } else {
                SkipEntry();
                guid = Guid.Empty;
                return false;
            }
        }
        
        public override bool ReadExternalReference(out string id) {
            PeekEntry();
            
            if (peekedEntryType == EntryType.ExternalReferenceByString) {
                id = peekedEntryContent;
                
                if (id.StartsWith(JsonConfig.EXTERNAL_STRING_REF_SIG_OLD)) {
                    id = id.Substring(JsonConfig.EXTERNAL_STRING_REF_SIG_OLD.Length + 1);
                } else if (id.StartsWith(JsonConfig.EXTERNAL_STRING_REF_SIG_FIXED)) {
                    id = id.Substring(JsonConfig.EXTERNAL_STRING_REF_SIG_FIXED.Length + 2, id.Length - (JsonConfig.EXTERNAL_STRING_REF_SIG_FIXED.Length + 3));
                }
                
                MarkEntryConsumed();
                return true;
            } else {
                SkipEntry();
                id = null;
                return false;
            }
        }
        
        public override bool ReadChar(out char value) {
            PeekEntry();
            
            if (peekedEntryType == EntryType.String) {
                try {
                    value = peekedEntryContent[1];
                    return true;
                } finally {
                    MarkEntryConsumed();
                }
            } else {
                SkipEntry();
                value = default;
                return false;
            }
        }
        
        public override bool ReadString(out string value) {
            PeekEntry();
            
            if (peekedEntryType == EntryType.String) {
                try {
                    value = peekedEntryContent.Substring(1, peekedEntryContent.Length - 2);
                    return true;
                } finally {
                    MarkEntryConsumed();
                }
            } else {
                SkipEntry();
                value = null;
                return false;
            }
        }
        
        public override bool ReadGuid(out Guid value) {
            PeekEntry();
            
            if (peekedEntryType == EntryType.Guid) {
                try {
                    try {
                        value = new Guid(peekedEntryContent);
                        return true;
                    } catch (FormatException) {
                        value = Guid.Empty;
                        return false;
                    } catch (OverflowException) {
                        value = Guid.Empty;
                        return false;
                    }
                } finally {
                    MarkEntryConsumed();
                }
            } else {
                SkipEntry();
                value = Guid.Empty;
                return false;
            }
        }
        
        public override bool ReadSByte(out sbyte value) {
            long longValue;
            
            if (ReadInt64(out longValue)) {
                checked {
                    try {
                        value = (sbyte)longValue;
                    } catch (OverflowException) {
                        value = default(sbyte);
                    }
                }
                
                return true;
            }
            
            value = default(sbyte);
            return false;
        }
        
        public override bool ReadInt16(out short value) {
            long longValue;
            
            if (ReadInt64(out longValue)) {
                checked {
                    try {
                        value = (short)longValue;
                    } catch (OverflowException) {
                        value = default(short);
                    }
                }
                
                return true;
            }
            
            value = default(short);
            return false;
        }
        
        public override bool ReadInt32(out int value) {
            long longValue;
            
            if (ReadInt64(out longValue)) {
                checked {
                    try {
                        value = (int)longValue;
                    } catch (OverflowException) {
                        value = default(int);
                    }
                }
                
                return true;
            }
            
            value = default(int);
            return false;
        }
        
        public override bool ReadInt64(out long value) {
            PeekEntry();
            
            if (peekedEntryType == EntryType.Integer) {
                try {
                    if (long.TryParse(peekedEntryContent, NumberStyles.Any, CultureInfo.InvariantCulture, out value)) {
                        return true;
                    } else {
                        Context.Config.DebugContext.LogError("Failed to parse long from: " + peekedEntryContent);
                        return false;
                    }
                } finally {
                    MarkEntryConsumed();
                }
            } else {
                SkipEntry();
                value = default(long);
                return false;
            }
        }
        
        public override bool ReadByte(out byte value) {
            ulong ulongValue;
            
            if (ReadUInt64(out ulongValue)) {
                checked {
                    try {
                        value = (byte)ulongValue;
                    } catch (OverflowException) {
                        value = default(byte);
                    }
                }
                
                return true;
            }
            
            value = default(byte);
            return false;
        }
        
        public override bool ReadUInt16(out ushort value) {
            ulong ulongValue;
            
            if (ReadUInt64(out ulongValue)) {
                checked {
                    try {
                        value = (ushort)ulongValue;
                    } catch (OverflowException) {
                        value = default(ushort);
                    }
                }
                
                return true;
            }
            
            value = default(ushort);
            return false;
        }
        
        public override bool ReadUInt32(out uint value) {
            ulong ulongValue;
            
            if (ReadUInt64(out ulongValue)) {
                checked {
                    try {
                        value = (uint)ulongValue;
                    } catch (OverflowException) {
                        value = default(uint);
                    }
                }
                
                return true;
            }
            
            value = default(uint);
            return false;
        }
        
        public override bool ReadUInt64(out ulong value) {
            PeekEntry();
            
            if (peekedEntryType == EntryType.Integer) {
                try {
                    if (ulong.TryParse(peekedEntryContent, NumberStyles.Any, CultureInfo.InvariantCulture, out value)) {
                        return true;
                    } else {
                        Context.Config.DebugContext.LogError("Failed to parse ulong from: " + peekedEntryContent);
                        return false;
                    }
                } finally {
                    MarkEntryConsumed();
                }
            } else {
                SkipEntry();
                value = default(ulong);
                return false;
            }
        }
        
        public override bool ReadDecimal(out decimal value) {
            PeekEntry();
            
            if (peekedEntryType == EntryType.FloatingPoint || peekedEntryType == EntryType.Integer) {
                try {
                    if (decimal.TryParse(peekedEntryContent, NumberStyles.Any, CultureInfo.InvariantCulture, out value)) {
                        return true;
                    } else {
                        Context.Config.DebugContext.LogError("Failed to parse decimal from: " + peekedEntryContent);
                        return false;
                    }
                } finally {
                    MarkEntryConsumed();
                }
            } else {
                SkipEntry();
                value = default(decimal);
                return false;
            }
        }
        
        public override bool ReadSingle(out float value) {
            PeekEntry();
            
            if (peekedEntryType == EntryType.FloatingPoint || peekedEntryType == EntryType.Integer) {
                try {
                    if (float.TryParse(peekedEntryContent, NumberStyles.Any, CultureInfo.InvariantCulture, out value)) {
                        return true;
                    } else {
                        Context.Config.DebugContext.LogError("Failed to parse float from: " + peekedEntryContent);
                        return false;
                    }
                } finally {
                    MarkEntryConsumed();
                }
            } else {
                SkipEntry();
                value = default(float);
                return false;
            }
        }
        
        public override bool ReadDouble(out double value) {
            PeekEntry();
            
            if (peekedEntryType == EntryType.FloatingPoint || peekedEntryType == EntryType.Integer) {
                try {
                    if (double.TryParse(peekedEntryContent, NumberStyles.Any, CultureInfo.InvariantCulture, out value)) {
                        return true;
                    } else {
                        Context.Config.DebugContext.LogError("Failed to parse double from: " + peekedEntryContent);
                        return false;
                    }
                } finally {
                    MarkEntryConsumed();
                }
            } else {
                SkipEntry();
                value = default(double);
                return false;
            }
        }
        
        public override bool ReadNull() {
            PeekEntry();
            
            if (peekedEntryType == EntryType.Null) {
                MarkEntryConsumed();
                return true;
            } else {
                SkipEntry();
                return false;
            }
        }
        
        public override void PrepareNewSerializationSession() {
            base.PrepareNewSerializationSession();
            peekedEntryType = null;
            peekedEntryContent = null;
            peekedEntryName = null;
            seenTypes.Clear();
            reader.Reset();
        }
        
        public override string GetDataDump() {
            if (!Stream.CanSeek) {
                return "Json data stream cannot seek; cannot dump data.";
            }
            
            long oldPosition = Stream.Position;
            
            byte[] bytes = new byte[Stream.Length];
            
            Stream.Position = 0;
            Stream.Read(bytes, 0, bytes.Length);
            
            Stream.Position = oldPosition;
            
            return "Json: " + Encoding.UTF8.GetString(bytes, 0, bytes.Length);
        }
        
        protected override EntryType PeekEntry() {
            string name;
            return PeekEntry(out name);
        }
        
        protected override EntryType ReadToNextEntry() {
            peekedEntryType = null;
            string name;
            return PeekEntry(out name);
        }
        
        private void MarkEntryConsumed() {
            if (peekedEntryType != EntryType.EndOfArray && peekedEntryType != EntryType.EndOfNode) {
                peekedEntryType = null;
            }
        }
        
        private bool ReadAnyIntReference(out int value) {
            int separatorIndex = -1;
            
            for (int i = 0; i < peekedEntryContent.Length; i++) {
                if (peekedEntryContent[i] == ':') {
                    separatorIndex = i;
                    break;
                }
            }
            
            if (separatorIndex == -1 || separatorIndex == peekedEntryContent.Length - 1) {
                Context.Config.DebugContext.LogError("Failed to parse id from: " + peekedEntryContent);
            }
            
            string idStr = peekedEntryContent.Substring(separatorIndex + 1);
            
            if (int.TryParse(idStr, NumberStyles.Any, CultureInfo.InvariantCulture, out value)) {
                return true;
            } else {
                Context.Config.DebugContext.LogError("Failed to parse id: " + idStr);
            }
            
            value = -1;
            return false;
        }
    }
}