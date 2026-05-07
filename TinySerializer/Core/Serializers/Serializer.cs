using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using TinySerializer.Core.DataReaderWriters;
using TinySerializer.Core.Misc;
using TinySerializer.Utilities;
using TinySerializer.Utilities.Extensions;
using TinySerializer.Utilities.Misc;

namespace TinySerializer.Core.Serializers {
#pragma warning disable 618
    
    public abstract class Serializer {
        private static readonly Dictionary<Type, Type> PrimitiveReaderWriterTypes = new Dictionary<Type, Type>() {
            { typeof(char), typeof(CharSerializer) },
            { typeof(string), typeof(StringSerializer) },
            { typeof(sbyte), typeof(SByteSerializer) },
            { typeof(short), typeof(Int16Serializer) },
            { typeof(int), typeof(Int32Serializer) },
            { typeof(long), typeof(Int64Serializer) },
            { typeof(byte), typeof(ByteSerializer) },
            { typeof(ushort), typeof(UInt16Serializer) },
            { typeof(uint), typeof(UInt32Serializer) },
            { typeof(ulong), typeof(UInt64Serializer) },
            { typeof(decimal), typeof(DecimalSerializer) },
            { typeof(bool), typeof(BooleanSerializer) },
            { typeof(float), typeof(SingleSerializer) },
            { typeof(double), typeof(DoubleSerializer) },
            { typeof(IntPtr), typeof(IntPtrSerializer) },
            { typeof(UIntPtr), typeof(UIntPtrSerializer) },
            { typeof(Guid), typeof(GuidSerializer) }
        };
        
        private static readonly object LOCK = new object();
        
        private static readonly Dictionary<Type, Serializer> Weak_ReaderWriterCache = new Dictionary<Type, Serializer>(FastTypeComparer.Instance);
        private static readonly Dictionary<Type, Serializer> Strong_ReaderWriterCache = new Dictionary<Type, Serializer>(FastTypeComparer.Instance);
        
        [Conditional("UNITY_EDITOR")]
        protected static void FireOnSerializedType(Type type) { }
        
        public static Serializer GetForValue(object value) {
            if (ReferenceEquals(value, null)) {
                return Get(typeof(object));
            } else {
                return Get(value.GetType());
            }
        }
        
        public static Serializer<T> Get<T>() {
            return (Serializer<T>)Get(typeof(T), false);
        }
        
        public static Serializer Get(Type type) {
            return Get(type, true);
        }
        
        private static Serializer Get(Type type, bool allowWeakFallback) {
            if (type == null) {
                throw new ArgumentNullException();
            }
            
            Serializer result;
            
            Dictionary<Type, Serializer> cache = allowWeakFallback ? Weak_ReaderWriterCache : Strong_ReaderWriterCache;
            
            lock (LOCK) {
                if (cache.TryGetValue(type, out result) == false) {
                    result = Create(type, allowWeakFallback);
                    cache.Add(type, result);
                }
            }
            
            return result;
        }
        
        public abstract object ReadValueWeak(IDataReader reader);
        
        public void WriteValueWeak(object value, IDataWriter writer) {
            WriteValueWeak(null, value, writer);
        }
        
        public abstract void WriteValueWeak(string name, object value, IDataWriter writer);
        
        private static Serializer Create(Type type, bool allowWeakfallback) {
            ExecutionEngineException aotEx = null;
            
            try {
                Type resultType = null;
                
                if (type.IsEnum) {
                    if (allowWeakfallback) {
                        return new AnySerializer(type);
                    }
                    
                    resultType = typeof(EnumSerializer<>).MakeGenericType(type);
                } else if (FormatterUtilities.IsPrimitiveType(type)) {
                    try {
                        resultType = PrimitiveReaderWriterTypes[type];
                    } catch (KeyNotFoundException) {
                        Console.WriteLine("Failed to find primitive serializer for " + type.Name);
                    }
                } else {
                    if (allowWeakfallback) {
                        return new AnySerializer(type);
                    }
                    
                    resultType = typeof(ComplexTypeSerializer<>).MakeGenericType(type);
                }
                
                return (Serializer)Activator.CreateInstance(resultType);
            } catch (TargetInvocationException ex) {
                if (ex.GetBaseException() is ExecutionEngineException) {
                    aotEx = ex.GetBaseException() as ExecutionEngineException;
                } else {
                    throw ex;
                }
            } catch (TypeInitializationException ex) {
                if (ex.GetBaseException() is ExecutionEngineException) {
                    aotEx = ex.GetBaseException() as ExecutionEngineException;
                } else {
                    throw ex;
                }
            } catch (ExecutionEngineException ex) {
                aotEx = ex;
            }
            
            if (allowWeakfallback) {
                return new AnySerializer(type);
            }
            
            LogAOTError(type, aotEx);
            throw aotEx;
        }
        
        private static void LogAOTError(Type type, ExecutionEngineException ex) {
            Console.WriteLine("No AOT serializer was pre-generated for the type '" + type.GetNiceFullName() + "'. "
                              + "Please use Odin's AOT generation feature to generate an AOT dll before building, and ensure that '" + type.GetNiceFullName()
                              + "' is in the list of supported types after a scan. If it is not, please " + "report an issue and add it to the list manually.");
            
            throw new SerializationAbortException("AOT serializer was missing for type '" + type.GetNiceFullName() + "'.");
        }
    }
    
    public abstract class Serializer<T> : Serializer {
        public override object ReadValueWeak(IDataReader reader) {
            return ReadValue(reader);
        }
        
        public override void WriteValueWeak(string name, object value, IDataWriter writer) {
            WriteValue(name, (T)value, writer);
        }
        
        public abstract T ReadValue(IDataReader reader);
        
        public void WriteValue(T value, IDataWriter writer) {
            WriteValue(null, value, writer);
        }
        
        public abstract void WriteValue(string name, T value, IDataWriter writer);
        
        [Conditional("UNITY_EDITOR")]
        protected static void FireOnSerializedType() {
            FireOnSerializedType(typeof(T));
        }
    }
}