using System;
using System.Collections.Generic;
using TinySerializer.Core.DataReaderWriters;
using TinySerializer.Core.Misc;
using TinySerializer.Utilities.Extensions;
using TinySerializer.Utilities.Misc;

namespace TinySerializer.Core.Formatters {
    public sealed class PrimitiveArrayFormatter<T> : MinimalBaseFormatter<T[]> where T : struct {
        protected override T[] GetUninitializedObject() {
            return null;
        }
        
        protected override void Read(ref T[] value, IDataReader reader) {
            string name;
            
            if (reader.PeekEntry(out name) == EntryType.PrimitiveArray) {
                reader.ReadPrimitiveArray(out value);
                RegisterReferenceID(value, reader);
            } else {
                reader.SkipEntry();
            }
        }
        
        protected override void Write(ref T[] value, IDataWriter writer) {
            writer.WritePrimitiveArray(value);
        }
    }
    
    public sealed class WeakPrimitiveArrayFormatter : WeakMinimalBaseFormatter {
        private static readonly Dictionary<Type, PrimitiveArrayType> PrimitiveTypes = new Dictionary<Type, PrimitiveArrayType>(FastTypeComparer.Instance) {
            { typeof(char), PrimitiveArrayType.PrimitiveArray_char },
            { typeof(sbyte), PrimitiveArrayType.PrimitiveArray_sbyte },
            { typeof(short), PrimitiveArrayType.PrimitiveArray_short },
            { typeof(int), PrimitiveArrayType.PrimitiveArray_int },
            { typeof(long), PrimitiveArrayType.PrimitiveArray_long },
            { typeof(byte), PrimitiveArrayType.PrimitiveArray_byte },
            { typeof(ushort), PrimitiveArrayType.PrimitiveArray_ushort },
            { typeof(uint), PrimitiveArrayType.PrimitiveArray_uint },
            { typeof(ulong), PrimitiveArrayType.PrimitiveArray_ulong },
            { typeof(decimal), PrimitiveArrayType.PrimitiveArray_decimal },
            { typeof(bool), PrimitiveArrayType.PrimitiveArray_bool },
            { typeof(float), PrimitiveArrayType.PrimitiveArray_float },
            { typeof(double), PrimitiveArrayType.PrimitiveArray_double },
            { typeof(Guid), PrimitiveArrayType.PrimitiveArray_Guid },
        };
        
        public enum PrimitiveArrayType {
            PrimitiveArray_char,
            PrimitiveArray_sbyte,
            PrimitiveArray_short,
            PrimitiveArray_int,
            PrimitiveArray_long,
            PrimitiveArray_byte,
            PrimitiveArray_ushort,
            PrimitiveArray_uint,
            PrimitiveArray_ulong,
            PrimitiveArray_decimal,
            PrimitiveArray_bool,
            PrimitiveArray_float,
            PrimitiveArray_double,
            PrimitiveArray_Guid,
        }
        
        private readonly Type ElementType;
        private readonly PrimitiveArrayType PrimitiveType;
        
        public WeakPrimitiveArrayFormatter(Type arrayType, Type elementType) : base(arrayType) {
            ElementType = elementType;
            
            if (!PrimitiveTypes.TryGetValue(elementType, out PrimitiveType)) {
                throw new SerializationAbortException("The type '" + elementType.GetNiceFullName()
                                                      + "' is not a type that can be written as a primitive array, yet the primitive array formatter is being used for it.");
            }
        }
        
        protected override object GetUninitializedObject() {
            return null;
        }
        
        protected override void Read(ref object value, IDataReader reader) {
            string name;
            
            if (reader.PeekEntry(out name) == EntryType.PrimitiveArray) {
                switch (PrimitiveType) {
                    case PrimitiveArrayType.PrimitiveArray_char: {
                        char[] readValue;
                        reader.ReadPrimitiveArray<char>(out readValue);
                        value = readValue;
                    }
                        
                        break;
                    
                    case PrimitiveArrayType.PrimitiveArray_sbyte: {
                        sbyte[] readValue;
                        reader.ReadPrimitiveArray<sbyte>(out readValue);
                        value = readValue;
                    }
                        
                        break;
                    
                    case PrimitiveArrayType.PrimitiveArray_short: {
                        short[] readValue;
                        reader.ReadPrimitiveArray<short>(out readValue);
                        value = readValue;
                    }
                        
                        break;
                    
                    case PrimitiveArrayType.PrimitiveArray_int: {
                        int[] readValue;
                        reader.ReadPrimitiveArray<int>(out readValue);
                        value = readValue;
                    }
                        
                        break;
                    
                    case PrimitiveArrayType.PrimitiveArray_long: {
                        long[] readValue;
                        reader.ReadPrimitiveArray<long>(out readValue);
                        value = readValue;
                    }
                        
                        break;
                    
                    case PrimitiveArrayType.PrimitiveArray_byte: {
                        byte[] readValue;
                        reader.ReadPrimitiveArray<byte>(out readValue);
                        value = readValue;
                    }
                        
                        break;
                    
                    case PrimitiveArrayType.PrimitiveArray_ushort: {
                        ushort[] readValue;
                        reader.ReadPrimitiveArray<ushort>(out readValue);
                        value = readValue;
                    }
                        
                        break;
                    
                    case PrimitiveArrayType.PrimitiveArray_uint: {
                        uint[] readValue;
                        reader.ReadPrimitiveArray<uint>(out readValue);
                        value = readValue;
                    }
                        
                        break;
                    
                    case PrimitiveArrayType.PrimitiveArray_ulong: {
                        ulong[] readValue;
                        reader.ReadPrimitiveArray<ulong>(out readValue);
                        value = readValue;
                    }
                        
                        break;
                    
                    case PrimitiveArrayType.PrimitiveArray_decimal: {
                        decimal[] readValue;
                        reader.ReadPrimitiveArray<decimal>(out readValue);
                        value = readValue;
                    }
                        
                        break;
                    
                    case PrimitiveArrayType.PrimitiveArray_bool: {
                        bool[] readValue;
                        reader.ReadPrimitiveArray<bool>(out readValue);
                        value = readValue;
                    }
                        
                        break;
                    
                    case PrimitiveArrayType.PrimitiveArray_float: {
                        float[] readValue;
                        reader.ReadPrimitiveArray<float>(out readValue);
                        value = readValue;
                    }
                        
                        break;
                    
                    case PrimitiveArrayType.PrimitiveArray_double: {
                        double[] readValue;
                        reader.ReadPrimitiveArray<double>(out readValue);
                        value = readValue;
                    }
                        
                        break;
                    
                    case PrimitiveArrayType.PrimitiveArray_Guid: {
                        Guid[] readValue;
                        reader.ReadPrimitiveArray<Guid>(out readValue);
                        value = readValue;
                    }
                        
                        break;
                    
                    default:
                        throw new NotImplementedException();
                }
                
                RegisterReferenceID(value, reader);
            } else {
                reader.SkipEntry();
            }
        }
        
        protected override void Write(ref object value, IDataWriter writer) {
            switch (PrimitiveType) {
                case PrimitiveArrayType.PrimitiveArray_char: writer.WritePrimitiveArray<char>((char[])value); break;
                case PrimitiveArrayType.PrimitiveArray_sbyte: writer.WritePrimitiveArray<sbyte>((sbyte[])value); break;
                case PrimitiveArrayType.PrimitiveArray_short: writer.WritePrimitiveArray<short>((short[])value); break;
                case PrimitiveArrayType.PrimitiveArray_int: writer.WritePrimitiveArray<int>((int[])value); break;
                case PrimitiveArrayType.PrimitiveArray_long: writer.WritePrimitiveArray<long>((long[])value); break;
                case PrimitiveArrayType.PrimitiveArray_byte: writer.WritePrimitiveArray<byte>((byte[])value); break;
                case PrimitiveArrayType.PrimitiveArray_ushort: writer.WritePrimitiveArray<ushort>((ushort[])value); break;
                case PrimitiveArrayType.PrimitiveArray_uint: writer.WritePrimitiveArray<uint>((uint[])value); break;
                case PrimitiveArrayType.PrimitiveArray_ulong: writer.WritePrimitiveArray<ulong>((ulong[])value); break;
                case PrimitiveArrayType.PrimitiveArray_decimal: writer.WritePrimitiveArray<decimal>((decimal[])value); break;
                case PrimitiveArrayType.PrimitiveArray_bool: writer.WritePrimitiveArray<bool>((bool[])value); break;
                case PrimitiveArrayType.PrimitiveArray_float: writer.WritePrimitiveArray<float>((float[])value); break;
                case PrimitiveArrayType.PrimitiveArray_double: writer.WritePrimitiveArray<double>((double[])value); break;
                case PrimitiveArrayType.PrimitiveArray_Guid: writer.WritePrimitiveArray<Guid>((Guid[])value); break;
            }
        }
    }
}