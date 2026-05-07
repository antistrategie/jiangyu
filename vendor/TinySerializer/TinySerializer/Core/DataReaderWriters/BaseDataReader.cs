using System;
using System.IO;
using TinySerializer.Core.FormatterLocators;
using TinySerializer.Core.Formatters;
using TinySerializer.Core.Misc;
using TinySerializer.Core.Serializers;

namespace TinySerializer.Core.DataReaderWriters {
    public abstract class BaseDataReader : BaseDataReaderWriter, IDataReader {
        private DeserializationContext context;
        private Stream stream;
        
        protected BaseDataReader(Stream stream, DeserializationContext context) {
            this.context = context;
            
            if (stream != null) {
                Stream = stream;
            }
        }
        
        public int CurrentNodeId => CurrentNode.Id;
        public int CurrentNodeDepth => NodeDepth;
        public string CurrentNodeName => CurrentNode.Name;
        
        public virtual Stream Stream {
            get => stream;
            set {
                if (value == null) {
                    throw new ArgumentNullException("value");
                }
                
                if (value.CanRead == false) {
                    throw new ArgumentException("Cannot read from stream");
                }
                
                stream = value;
            }
        }
        
        public DeserializationContext Context {
            get {
                if (context == null) {
                    context = new DeserializationContext();
                }
                
                return context;
            }
            set => context = value;
        }
        
        public abstract bool EnterNode(out Type type);
        
        public abstract bool ExitNode();
        
        public abstract bool EnterArray(out long length);
        
        public abstract bool ExitArray();
        
        public abstract bool ReadPrimitiveArray<T>(out T[] array) where T : struct;
        
        public abstract EntryType PeekEntry(out string name);
        
        public abstract bool ReadInternalReference(out int id);
        
        public abstract bool ReadExternalReference(out int index);
        
        public abstract bool ReadExternalReference(out Guid guid);
        
        public abstract bool ReadExternalReference(out string id);
        
        public abstract bool ReadChar(out char value);
        
        public abstract bool ReadString(out string value);
        
        public abstract bool ReadGuid(out Guid value);
        
        public abstract bool ReadSByte(out sbyte value);
        
        public abstract bool ReadInt16(out short value);
        
        public abstract bool ReadInt32(out int value);
        
        public abstract bool ReadInt64(out long value);
        
        public abstract bool ReadByte(out byte value);
        
        public abstract bool ReadUInt16(out ushort value);
        
        public abstract bool ReadUInt32(out uint value);
        
        public abstract bool ReadUInt64(out ulong value);
        
        public abstract bool ReadDecimal(out decimal value);
        
        public abstract bool ReadSingle(out float value);
        
        public abstract bool ReadDouble(out double value);
        
        public abstract bool ReadBoolean(out bool value);
        
        public abstract bool ReadNull();
        
        public virtual void SkipEntry() {
            EntryType peekedEntry = PeekEntry();
            
            if (peekedEntry == EntryType.StartOfNode) {
                Type type;
                
                bool exitNode = true;
                
                EnterNode(out type);
                
                try {
                    if (type != null) {
                        if (FormatterUtilities.IsPrimitiveType(type)) {
                            Serializer serializer = Serializer.Get(type);
                            object value = serializer.ReadValueWeak(this);
                            
                            if (CurrentNodeId >= 0) {
                                Context.RegisterInternalReference(CurrentNodeId, value);
                            }
                        } else {
                            IFormatter formatter = FormatterLocator.GetFormatter(type, Context.Config.SerializationPolicy);
                            object value = formatter.Deserialize(this);
                            
                            if (CurrentNodeId >= 0) {
                                Context.RegisterInternalReference(CurrentNodeId, value);
                            }
                        }
                    } else {
                        while (true) {
                            peekedEntry = PeekEntry();
                            
                            if (peekedEntry == EntryType.EndOfStream) {
                                break;
                            } else if (peekedEntry == EntryType.EndOfNode) {
                                break;
                            } else if (peekedEntry == EntryType.EndOfArray) {
                                ReadToNextEntry();
                            } else {
                                SkipEntry();
                            }
                        }
                    }
                } catch (SerializationAbortException ex) {
                    exitNode = false;
                    throw ex;
                } finally {
                    if (exitNode) {
                        ExitNode();
                    }
                }
            } else if (peekedEntry == EntryType.StartOfArray) {
                ReadToNextEntry();
                
                while (true) {
                    peekedEntry = PeekEntry();
                    
                    if (peekedEntry == EntryType.EndOfStream) {
                        break;
                    } else if (peekedEntry == EntryType.EndOfArray) {
                        ReadToNextEntry();
                        break;
                    } else if (peekedEntry == EntryType.EndOfNode) {
                        ReadToNextEntry();
                    } else {
                        SkipEntry();
                    }
                }
            } else if (peekedEntry != EntryType.EndOfArray && peekedEntry != EntryType.EndOfNode) {
                ReadToNextEntry();
            }
        }
        
        public abstract void Dispose();
        
        public virtual void PrepareNewSerializationSession() {
            ClearNodes();
        }
        
        public abstract string GetDataDump();
        
        protected abstract EntryType PeekEntry();
        
        protected abstract EntryType ReadToNextEntry();
    }
}