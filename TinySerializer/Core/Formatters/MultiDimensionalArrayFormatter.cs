using System;
using System.Globalization;
using System.Text;
using TinySerializer.Core.DataReaderWriters;
using TinySerializer.Core.Misc;
using TinySerializer.Core.Serializers;

namespace TinySerializer.Core.Formatters {
    public sealed class MultiDimensionalArrayFormatter<TArray, TElement> : BaseFormatter<TArray> where TArray : class {
        private const string RANKS_NAME = "ranks";
        private const char RANKS_SEPARATOR = '|';
        
        private static readonly int ArrayRank;
        private static readonly Serializer<TElement> ValueReaderWriter = Serializer.Get<TElement>();
        
        static MultiDimensionalArrayFormatter() {
            if (typeof(TArray).IsArray == false) {
                throw new ArgumentException("Type " + typeof(TArray).Name + " is not an array.");
            }
            
            if (typeof(TArray).GetElementType() != typeof(TElement)) {
                throw new ArgumentException("Array of type " + typeof(TArray).Name + " does not have the required element type of " + typeof(TElement).Name + ".");
            }
            
            ArrayRank = typeof(TArray).GetArrayRank();
            
            if (ArrayRank <= 1) {
                throw new ArgumentException("Array of type " + typeof(TArray).Name + " only has one rank.");
            }
        }
        
        protected override TArray GetUninitializedObject() {
            return null;
        }
        
        protected override void DeserializeImplementation(ref TArray value, IDataReader reader) {
            string name;
            EntryType entry = reader.PeekEntry(out name);
            
            if (entry == EntryType.StartOfArray) {
                long length;
                reader.EnterArray(out length);
                
                entry = reader.PeekEntry(out name);
                
                if (entry != EntryType.String || name != RANKS_NAME) {
                    value = default(TArray);
                    reader.SkipEntry();
                    return;
                }
                
                string lengthStr;
                reader.ReadString(out lengthStr);
                
                string[] lengthsStrs = lengthStr.Split(RANKS_SEPARATOR);
                
                if (lengthsStrs.Length != ArrayRank) {
                    value = default(TArray);
                    reader.SkipEntry();
                    return;
                }
                
                int[] lengths = new int[lengthsStrs.Length];
                
                for (int i = 0; i < lengthsStrs.Length; i++) {
                    int rankVal;
                    
                    if (int.TryParse(lengthsStrs[i], out rankVal)) {
                        lengths[i] = rankVal;
                    } else {
                        value = default(TArray);
                        reader.SkipEntry();
                        return;
                    }
                }
                
                long rankTotal = lengths[0];
                
                for (int i = 1; i < lengths.Length; i++) {
                    rankTotal *= lengths[i];
                }
                
                if (rankTotal != length) {
                    value = default(TArray);
                    reader.SkipEntry();
                    return;
                }
                
                value = (TArray)(object)Array.CreateInstance(typeof(TElement), lengths);
                
                RegisterReferenceID(value, reader);
                
                int elements = 0;
                
                try {
                    IterateArrayWrite((Array)(object)value, () =>
                    {
                        if (reader.PeekEntry(out name) == EntryType.EndOfArray) {
                            reader.Context.Config.DebugContext.LogError("Reached end of array after " + elements + " elements, when " + length + " elements were expected.");
                            throw new InvalidOperationException();
                        }
                        
                        TElement v = ValueReaderWriter.ReadValue(reader);
                        
                        if (reader.IsInArrayNode == false) {
                            reader.Context.Config.DebugContext.LogError("Reading array went wrong. Data dump: " + reader.GetDataDump());
                            throw new InvalidOperationException();
                        }
                        
                        elements++;
                        return v;
                    });
                } catch (InvalidOperationException) { } catch (Exception ex) {
                    reader.Context.Config.DebugContext.LogException(ex);
                }
                
                reader.ExitArray();
            } else {
                value = default(TArray);
                reader.SkipEntry();
            }
        }
        
        protected override void SerializeImplementation(ref TArray value, IDataWriter writer) {
            Array array = value as Array;
            
            try {
                writer.BeginArrayNode(array.LongLength);
                
                int[] lengths = new int[ArrayRank];
                
                for (int i = 0; i < ArrayRank; i++) {
                    lengths[i] = array.GetLength(i);
                }
                
                StringBuilder sb = new StringBuilder();
                
                for (int i = 0; i < ArrayRank; i++) {
                    if (i > 0) {
                        sb.Append(RANKS_SEPARATOR);
                    }
                    
                    sb.Append(lengths[i].ToString(CultureInfo.InvariantCulture));
                }
                
                string lengthStr = sb.ToString();
                
                writer.WriteString(RANKS_NAME, lengthStr);
                
                IterateArrayRead((Array)(object)value, (v) =>
                {
                    ValueReaderWriter.WriteValue(v, writer);
                });
            } finally {
                writer.EndArrayNode();
            }
        }
        
        private void IterateArrayWrite(Array a, Func<TElement> write) {
            int[] indices = new int[ArrayRank];
            IterateArrayWrite(a, 0, indices, write);
        }
        
        private void IterateArrayWrite(Array a, int rank, int[] indices, Func<TElement> write) {
            for (int i = 0; i < a.GetLength(rank); i++) {
                indices[rank] = i;
                
                if (rank + 1 < a.Rank) {
                    IterateArrayWrite(a, rank + 1, indices, write);
                } else {
                    a.SetValue(write(), indices);
                }
            }
        }
        
        private void IterateArrayRead(Array a, Action<TElement> read) {
            int[] indices = new int[ArrayRank];
            IterateArrayRead(a, 0, indices, read);
        }
        
        private void IterateArrayRead(Array a, int rank, int[] indices, Action<TElement> read) {
            for (int i = 0; i < a.GetLength(rank); i++) {
                indices[rank] = i;
                
                if (rank + 1 < a.Rank) {
                    IterateArrayRead(a, rank + 1, indices, read);
                } else {
                    read((TElement)a.GetValue(indices));
                }
            }
        }
    }
    
    public sealed class WeakMultiDimensionalArrayFormatter : WeakBaseFormatter {
        private const string RANKS_NAME = "ranks";
        private const char RANKS_SEPARATOR = '|';
        
        private readonly int ArrayRank;
        private readonly Type ElementType;
        private readonly Serializer ValueReaderWriter;
        
        public WeakMultiDimensionalArrayFormatter(Type arrayType, Type elementType) : base(arrayType) {
            ArrayRank = arrayType.GetArrayRank();
            ElementType = elementType;
            ValueReaderWriter = Serializer.Get(elementType);
        }
        
        protected override object GetUninitializedObject() {
            return null;
        }
        
        protected override void DeserializeImplementation(ref object value, IDataReader reader) {
            string name;
            EntryType entry = reader.PeekEntry(out name);
            
            if (entry == EntryType.StartOfArray) {
                long length;
                reader.EnterArray(out length);
                
                entry = reader.PeekEntry(out name);
                
                if (entry != EntryType.String || name != RANKS_NAME) {
                    value = null;
                    reader.SkipEntry();
                    return;
                }
                
                string lengthStr;
                reader.ReadString(out lengthStr);
                
                string[] lengthsStrs = lengthStr.Split(RANKS_SEPARATOR);
                
                if (lengthsStrs.Length != ArrayRank) {
                    value = null;
                    reader.SkipEntry();
                    return;
                }
                
                int[] lengths = new int[lengthsStrs.Length];
                
                for (int i = 0; i < lengthsStrs.Length; i++) {
                    int rankVal;
                    
                    if (int.TryParse(lengthsStrs[i], out rankVal)) {
                        lengths[i] = rankVal;
                    } else {
                        value = null;
                        reader.SkipEntry();
                        return;
                    }
                }
                
                long rankTotal = lengths[0];
                
                for (int i = 1; i < lengths.Length; i++) {
                    rankTotal *= lengths[i];
                }
                
                if (rankTotal != length) {
                    value = null;
                    reader.SkipEntry();
                    return;
                }
                
                value = Array.CreateInstance(ElementType, lengths);
                
                RegisterReferenceID(value, reader);
                
                int elements = 0;
                
                try {
                    IterateArrayWrite((Array)(object)value, () =>
                    {
                        if (reader.PeekEntry(out name) == EntryType.EndOfArray) {
                            reader.Context.Config.DebugContext.LogError("Reached end of array after " + elements + " elements, when " + length + " elements were expected.");
                            throw new InvalidOperationException();
                        }
                        
                        object v = ValueReaderWriter.ReadValueWeak(reader);
                        
                        if (reader.IsInArrayNode == false) {
                            reader.Context.Config.DebugContext.LogError("Reading array went wrong. Data dump: " + reader.GetDataDump());
                            throw new InvalidOperationException();
                        }
                        
                        elements++;
                        return v;
                    });
                } catch (InvalidOperationException) { } catch (Exception ex) {
                    reader.Context.Config.DebugContext.LogException(ex);
                }
                
                reader.ExitArray();
            } else {
                value = null;
                reader.SkipEntry();
            }
        }
        
        protected override void SerializeImplementation(ref object value, IDataWriter writer) {
            Array array = value as Array;
            
            try {
                writer.BeginArrayNode(array.LongLength);
                
                int[] lengths = new int[ArrayRank];
                
                for (int i = 0; i < ArrayRank; i++) {
                    lengths[i] = array.GetLength(i);
                }
                
                StringBuilder sb = new StringBuilder();
                
                for (int i = 0; i < ArrayRank; i++) {
                    if (i > 0) {
                        sb.Append(RANKS_SEPARATOR);
                    }
                    
                    sb.Append(lengths[i].ToString(CultureInfo.InvariantCulture));
                }
                
                string lengthStr = sb.ToString();
                
                writer.WriteString(RANKS_NAME, lengthStr);
                
                IterateArrayRead((Array)(object)value, (v) =>
                {
                    ValueReaderWriter.WriteValueWeak(v, writer);
                });
            } finally {
                writer.EndArrayNode();
            }
        }
        
        private void IterateArrayWrite(Array a, Func<object> write) {
            int[] indices = new int[ArrayRank];
            IterateArrayWrite(a, 0, indices, write);
        }
        
        private void IterateArrayWrite(Array a, int rank, int[] indices, Func<object> write) {
            for (int i = 0; i < a.GetLength(rank); i++) {
                indices[rank] = i;
                
                if (rank + 1 < a.Rank) {
                    IterateArrayWrite(a, rank + 1, indices, write);
                } else {
                    a.SetValue(write(), indices);
                }
            }
        }
        
        private void IterateArrayRead(Array a, Action<object> read) {
            int[] indices = new int[ArrayRank];
            IterateArrayRead(a, 0, indices, read);
        }
        
        private void IterateArrayRead(Array a, int rank, int[] indices, Action<object> read) {
            for (int i = 0; i < a.GetLength(rank); i++) {
                indices[rank] = i;
                
                if (rank + 1 < a.Rank) {
                    IterateArrayRead(a, rank + 1, indices, read);
                } else {
                    read(a.GetValue(indices));
                }
            }
        }
    }
}