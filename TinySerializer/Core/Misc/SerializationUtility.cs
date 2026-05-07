using System;
using System.IO;
using TinySerializer.Core.DataReaderWriters;
using TinySerializer.Core.DataReaderWriters.Binary;
using TinySerializer.Core.DataReaderWriters.Json;
using TinySerializer.Core.Serializers;
using TinySerializer.Utilities.Misc;

namespace TinySerializer.Core.Misc {
    public static class SerializationUtility {
        public static IDataWriter CreateWriter(Stream stream, SerializationContext context, DataFormat format) {
            switch (format) {
                case DataFormat.Binary:
                    return new BinaryDataWriter(stream, context);
                
                case DataFormat.JSON:
                    return new JsonDataWriter(stream, context);
                
                case DataFormat.Nodes:
                    Console.WriteLine("Cannot automatically create a writer for the format '" + DataFormat.Nodes + "', because it does not use a stream.");
                    return null;
                
                default:
                    throw new NotImplementedException(format.ToString());
            }
        }
        
        public static IDataReader CreateReader(Stream stream, DeserializationContext context, DataFormat format) {
            switch (format) {
                case DataFormat.Binary:
                    return new BinaryDataReader(stream, context);
                
                case DataFormat.JSON:
                    return new JsonDataReader(stream, context);
                
                case DataFormat.Nodes:
                    Console.WriteLine("Cannot automatically create a reader for the format '" + DataFormat.Nodes + "', because it does not use a stream.");
                    return null;
                
                default:
                    throw new NotImplementedException(format.ToString());
            }
        }
        
        private static IDataWriter GetCachedWriter(out IDisposable cache, DataFormat format, Stream stream, SerializationContext context) {
            IDataWriter writer;
            
            if (format == DataFormat.Binary) {
                Cache<BinaryDataWriter> binaryCache = Cache<BinaryDataWriter>.Claim();
                BinaryDataWriter binaryWriter = binaryCache.Value;
                
                binaryWriter.Stream = stream;
                binaryWriter.Context = context;
                binaryWriter.PrepareNewSerializationSession();
                
                writer = binaryWriter;
                cache = binaryCache;
            } else if (format == DataFormat.JSON) {
                Cache<JsonDataWriter> jsonCache = Cache<JsonDataWriter>.Claim();
                JsonDataWriter jsonWriter = jsonCache.Value;
                
                jsonWriter.Stream = stream;
                jsonWriter.Context = context;
                jsonWriter.PrepareNewSerializationSession();
                
                writer = jsonWriter;
                cache = jsonCache;
            } else if (format == DataFormat.Nodes) {
                throw new InvalidOperationException("Cannot automatically create a writer for the format '" + DataFormat.Nodes + "', because it does not use a stream.");
            } else {
                throw new NotImplementedException(format.ToString());
            }
            
            return writer;
        }
        
        private static IDataReader GetCachedReader(out IDisposable cache, DataFormat format, Stream stream, DeserializationContext context) {
            IDataReader reader;
            
            if (format == DataFormat.Binary) {
                Cache<BinaryDataReader> binaryCache = Cache<BinaryDataReader>.Claim();
                BinaryDataReader binaryReader = binaryCache.Value;
                
                binaryReader.Stream = stream;
                binaryReader.Context = context;
                binaryReader.PrepareNewSerializationSession();
                
                reader = binaryReader;
                cache = binaryCache;
            } else if (format == DataFormat.JSON) {
                Cache<JsonDataReader> jsonCache = Cache<JsonDataReader>.Claim();
                JsonDataReader jsonReader = jsonCache.Value;
                
                jsonReader.Stream = stream;
                jsonReader.Context = context;
                jsonReader.PrepareNewSerializationSession();
                
                reader = jsonReader;
                cache = jsonCache;
            } else if (format == DataFormat.Nodes) {
                throw new InvalidOperationException("Cannot automatically create a reader for the format '" + DataFormat.Nodes + "', because it does not use a stream.");
            } else {
                throw new NotImplementedException(format.ToString());
            }
            
            return reader;
        }
        
        public static void SerializeValueWeak(object value, IDataWriter writer) {
            Serializer.GetForValue(value).WriteValueWeak(value, writer);
            writer.FlushToStream();
        }
        
        public static void SerializeValue<T>(T value, IDataWriter writer) {
            Serializer serializer = Serializer.Get(typeof(T));
            Serializer<T> strong = serializer as Serializer<T>;
            
            if (strong != null) {
                strong.WriteValue(value, writer);
            } else {
                serializer.WriteValueWeak(value, writer);
            }
            
            writer.FlushToStream();
        }
        
        public static void SerializeValueWeak(object value, Stream stream, DataFormat format, SerializationContext context = null) {
            IDisposable cache;
            IDataWriter writer = GetCachedWriter(out cache, format, stream, context);
            
            try {
                if (context != null) {
                    SerializeValueWeak(value, writer);
                } else {
                    using (Cache<SerializationContext> con = Cache<SerializationContext>.Claim()) {
                        writer.Context = con;
                        SerializeValueWeak(value, writer);
                    }
                }
            } finally {
                cache.Dispose();
            }
        }
        
        public static void SerializeValue<T>(T value, Stream stream, DataFormat format, SerializationContext context = null) {
            IDisposable cache;
            IDataWriter writer = GetCachedWriter(out cache, format, stream, context);
            
            try {
                if (context != null) {
                    SerializeValue(value, writer);
                } else {
                    using (Cache<SerializationContext> con = Cache<SerializationContext>.Claim()) {
                        writer.Context = con;
                        SerializeValue(value, writer);
                    }
                }
            } finally {
                cache.Dispose();
            }
        }
        
        public static byte[] SerializeValueWeak(object value, DataFormat format, SerializationContext context = null) {
            using (Cache<CachedMemoryStream> stream = CachedMemoryStream.Claim()) {
                SerializeValueWeak(value, stream.Value.MemoryStream, format, context);
                return stream.Value.MemoryStream.ToArray();
            }
        }
        
        public static byte[] SerializeValue<T>(T value, DataFormat format, SerializationContext context = null) {
            using (Cache<CachedMemoryStream> stream = CachedMemoryStream.Claim()) {
                SerializeValue(value, stream.Value.MemoryStream, format, context);
                return stream.Value.MemoryStream.ToArray();
            }
        }
        
        public static object DeserializeValueWeak(IDataReader reader) {
            return Serializer.Get(typeof(object)).ReadValueWeak(reader);
        }
        
        public static T DeserializeValue<T>(IDataReader reader) {
            Serializer serializer = Serializer.Get(typeof(T));
            Serializer<T> strong = serializer as Serializer<T>;
            
            if (strong != null) {
                return strong.ReadValue(reader);
            } else {
                return (T)serializer.ReadValueWeak(reader);
            }
        }
        
        public static object DeserializeValueWeak(Stream stream, DataFormat format, DeserializationContext context = null) {
            IDisposable cache;
            IDataReader reader = GetCachedReader(out cache, format, stream, context);
            
            try {
                if (context != null) {
                    return DeserializeValueWeak(reader);
                } else {
                    using (Cache<DeserializationContext> con = Cache<DeserializationContext>.Claim()) {
                        reader.Context = con;
                        return DeserializeValueWeak(reader);
                    }
                }
            } finally {
                cache.Dispose();
            }
        }
        
        public static T DeserializeValue<T>(Stream stream, DataFormat format, DeserializationContext context = null) {
            IDisposable cache;
            IDataReader reader = GetCachedReader(out cache, format, stream, context);
            
            try {
                if (context != null) {
                    return DeserializeValue<T>(reader);
                } else {
                    using (Cache<DeserializationContext> con = Cache<DeserializationContext>.Claim()) {
                        reader.Context = con;
                        return DeserializeValue<T>(reader);
                    }
                }
            } finally {
                cache.Dispose();
            }
        }
        
        public static object DeserializeValueWeak(byte[] bytes, DataFormat format, DeserializationContext context = null) {
            using (Cache<CachedMemoryStream> stream = CachedMemoryStream.Claim(bytes)) {
                return DeserializeValueWeak(stream.Value.MemoryStream, format, context);
            }
        }
        
        public static T DeserializeValue<T>(byte[] bytes, DataFormat format, DeserializationContext context = null) {
            using (Cache<CachedMemoryStream> stream = CachedMemoryStream.Claim(bytes)) {
                return DeserializeValue<T>(stream.Value.MemoryStream, format, context);
            }
        }
    }
}