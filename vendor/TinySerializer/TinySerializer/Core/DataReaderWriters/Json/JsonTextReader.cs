using System;
using System.Collections.Generic;
using System.IO;
using TinySerializer.Core.Misc;

namespace TinySerializer.Core.DataReaderWriters.Json {
    public class JsonTextReader : IDisposable {
        private static readonly Dictionary<char, EntryType?> EntryDelineators = new Dictionary<char, EntryType?> {
            { '{', EntryType.StartOfNode },
            { '}', EntryType.EndOfNode },
            { ',', null },
            { '[', EntryType.PrimitiveArray },
            { ']', EntryType.EndOfArray },
        };
        
        private static readonly Dictionary<char, char> UnescapeDictionary = new Dictionary<char, char>() {
            { 'a', '\a' },
            { 'b', '\b' },
            { 'f', '\f' },
            { 'n', '\n' },
            { 'r', '\r' },
            { 't', '\t' },
            { '0', '\0' }
        };
        
        private StreamReader reader;
        private int bufferIndex = 0;
        private char[] buffer = new char[256];
        private char? lastReadChar;
        private char? peekedChar;
        private Queue<char> emergencyPlayback;
        
        public DeserializationContext Context { get; private set; }
        
        public JsonTextReader(Stream stream, DeserializationContext context) {
            if (stream == null) {
                throw new ArgumentNullException("stream");
            }
            
            if (context == null) {
                throw new ArgumentNullException("context");
            }
            
            if (stream.CanRead == false) {
                throw new ArgumentException("Cannot read from stream");
            }
            
            reader = new StreamReader(stream);
            Context = context;
        }
        
        public void Reset() {
            peekedChar = null;
            
            if (emergencyPlayback != null) {
                emergencyPlayback.Clear();
            }
        }
        
        public void Dispose() { }
        
        public void ReadToNextEntry(out string name, out string valueContent, out EntryType entry) {
            int valueSeparatorIndex = -1;
            bool insideString = false;
            EntryType? foundEntryType;
            
            bufferIndex = -1;
            
            while (reader.EndOfStream == false) {
                char c = PeekChar();
                
                if (insideString && lastReadChar == '\\') {
                    if (c == '\\') {
                        lastReadChar = null;
                        SkipChar();
                        continue;
                    } else {
                        switch (c) {
                            case 'a':
                            case 'b':
                            case 'f':
                            case 'n':
                            case 'r':
                            case 't':
                            case '0':
                                c = UnescapeDictionary[c];
                                
                                lastReadChar = c;
                                buffer[bufferIndex] = c;
                                SkipChar();
                                continue;
                            
                            case 'u':
                                SkipChar();
                                
                                char c1 = ConsumeChar();
                                char c2 = ConsumeChar();
                                char c3 = ConsumeChar();
                                char c4 = ConsumeChar();
                                
                                if (IsHex(c1) && IsHex(c2) && IsHex(c3) && IsHex(c4)) {
                                    c = ParseHexChar(c1, c2, c3, c4);
                                    
                                    lastReadChar = c;
                                    buffer[bufferIndex] = c;
                                    continue;
                                } else {
                                    Context.Config.DebugContext.LogError("A wild non-hex value appears at position " + reader.BaseStream.Position + "! \\-u-" + c1 + "-" + c2 + "-"
                                                                         + c3 + "-" + c4 + "; current buffer: '" + new string(buffer, 0, bufferIndex + 1)
                                                                         + "'. If the error handling policy is resilient, an attempt will be made to recover from this emergency without a fatal parse error...");
                                    
                                    lastReadChar = null;
                                    
                                    if (emergencyPlayback == null) {
                                        emergencyPlayback = new Queue<char>(5);
                                    }
                                    
                                    emergencyPlayback.Enqueue('u');
                                    emergencyPlayback.Enqueue(c1);
                                    emergencyPlayback.Enqueue(c2);
                                    emergencyPlayback.Enqueue(c3);
                                    emergencyPlayback.Enqueue(c4);
                                    continue;
                                }
                        }
                    }
                }
                
                if (insideString == false && c == ':' && valueSeparatorIndex == -1) {
                    valueSeparatorIndex = bufferIndex + 1;
                }
                
                if (c == '"') {
                    if (insideString && lastReadChar == '\\') {
                        lastReadChar = '"';
                        buffer[bufferIndex] = '"';
                        SkipChar();
                        continue;
                    } else {
                        ReadCharIntoBuffer();
                        insideString = !insideString;
                        continue;
                    }
                }
                
                if (insideString) {
                    ReadCharIntoBuffer();
                } else {
                    if (char.IsWhiteSpace(c)) {
                        SkipChar();
                        continue;
                    }
                    
                    if (EntryDelineators.TryGetValue(c, out foundEntryType)) {
                        if (foundEntryType == null) {
                            SkipChar();
                            
                            if (bufferIndex == -1) {
                                continue;
                            } else {
                                ParseEntryFromBuffer(out name, out valueContent, out entry, valueSeparatorIndex, null);
                                return;
                            }
                        } else {
                            entry = foundEntryType.Value;
                            
                            switch (entry) {
                                case EntryType.StartOfNode: {
                                    EntryType dummy;
                                    ConsumeChar();
                                    ParseEntryFromBuffer(out name, out valueContent, out dummy, valueSeparatorIndex, EntryType.StartOfNode);
                                    return;
                                }
                                
                                case EntryType.PrimitiveArray: {
                                    EntryType dummy;
                                    ConsumeChar();
                                    ParseEntryFromBuffer(out name, out valueContent, out dummy, valueSeparatorIndex, EntryType.PrimitiveArray);
                                    return;
                                }
                                
                                case EntryType.EndOfNode:
                                    if (bufferIndex == -1) {
                                        ConsumeChar();
                                        name = null;
                                        valueContent = null;
                                        return;
                                    } else {
                                        ParseEntryFromBuffer(out name, out valueContent, out entry, valueSeparatorIndex, null);
                                        return;
                                    }
                                
                                case EntryType.EndOfArray: {
                                    if (bufferIndex == -1) {
                                        ConsumeChar();
                                        name = null;
                                        valueContent = null;
                                        return;
                                    } else {
                                        ParseEntryFromBuffer(out name, out valueContent, out entry, valueSeparatorIndex, null);
                                        return;
                                    }
                                }
                                
                                default:
                                    throw new NotImplementedException();
                            }
                        }
                    } else {
                        ReadCharIntoBuffer();
                    }
                }
            }
            
            if (bufferIndex == -1) {
                name = null;
                valueContent = null;
                entry = EntryType.EndOfStream;
            } else {
                ParseEntryFromBuffer(out name, out valueContent, out entry, valueSeparatorIndex, EntryType.EndOfStream);
            }
        }
        
        private void ParseEntryFromBuffer(out string name, out string valueContent, out EntryType entry, int valueSeparatorIndex, EntryType? hintEntry) {
            if (bufferIndex >= 0) {
                if (valueSeparatorIndex == -1) {
                    if (hintEntry != null) {
                        name = null;
                        valueContent = new string(buffer, 0, bufferIndex + 1);
                        entry = hintEntry.Value;
                        return;
                    } else {
                        name = null;
                        valueContent = new string(buffer, 0, bufferIndex + 1);
                        
                        EntryType? guessedPrimitiveType = GuessPrimitiveType(valueContent);
                        
                        if (guessedPrimitiveType != null) {
                            entry = guessedPrimitiveType.Value;
                        } else {
                            entry = EntryType.Invalid;
                        }
                        
                        return;
                    }
                } else {
                    if (buffer[0] == '"') {
                        name = new string(buffer, 1, valueSeparatorIndex - 2);
                    } else {
                        name = new string(buffer, 0, valueSeparatorIndex);
                    }
                    
                    if (StringComparer.Ordinal.Equals(name, JsonConfig.REGULAR_ARRAY_CONTENT_SIG) && hintEntry == EntryType.StartOfArray) {
                        valueContent = null;
                        entry = EntryType.StartOfArray;
                        return;
                    }
                    
                    if (StringComparer.Ordinal.Equals(name, JsonConfig.PRIMITIVE_ARRAY_CONTENT_SIG) && hintEntry == EntryType.StartOfArray) {
                        valueContent = null;
                        entry = EntryType.PrimitiveArray;
                        return;
                    }
                    
                    if (StringComparer.Ordinal.Equals(name, JsonConfig.INTERNAL_REF_SIG)) {
                        name = null;
                        valueContent = new string(buffer, 0, bufferIndex + 1);
                        entry = EntryType.InternalReference;
                        return;
                    }
                    
                    if (StringComparer.Ordinal.Equals(name, JsonConfig.EXTERNAL_INDEX_REF_SIG)) {
                        name = null;
                        valueContent = new string(buffer, 0, bufferIndex + 1);
                        entry = EntryType.ExternalReferenceByIndex;
                        return;
                    }
                    
                    if (StringComparer.Ordinal.Equals(name, JsonConfig.EXTERNAL_GUID_REF_SIG)) {
                        name = null;
                        valueContent = new string(buffer, 0, bufferIndex + 1);
                        entry = EntryType.ExternalReferenceByGuid;
                        return;
                    }
                    
                    if (StringComparer.Ordinal.Equals(name, JsonConfig.EXTERNAL_STRING_REF_SIG_OLD)) {
                        name = null;
                        valueContent = new string(buffer, 0, bufferIndex + 1);
                        entry = EntryType.ExternalReferenceByString;
                        return;
                    }
                    
                    if (StringComparer.Ordinal.Equals(name, JsonConfig.EXTERNAL_STRING_REF_SIG_FIXED)) {
                        name = null;
                        valueContent = new string(buffer, 0, bufferIndex + 1);
                        entry = EntryType.ExternalReferenceByString;
                        return;
                    }
                    
                    if (bufferIndex >= valueSeparatorIndex) {
                        valueContent = new string(buffer, valueSeparatorIndex + 1, bufferIndex - valueSeparatorIndex);
                    } else {
                        valueContent = null;
                    }
                    
                    if (valueContent != null) {
                        if (StringComparer.Ordinal.Equals(name, JsonConfig.REGULAR_ARRAY_LENGTH_SIG)) {
                            entry = EntryType.StartOfArray;
                            return;
                        }
                        
                        if (StringComparer.Ordinal.Equals(name, JsonConfig.PRIMITIVE_ARRAY_LENGTH_SIG)) {
                            entry = EntryType.PrimitiveArray;
                            return;
                        }
                        
                        if (valueContent.Length == 0 && hintEntry.HasValue) {
                            entry = hintEntry.Value;
                            return;
                        }
                        
                        if (StringComparer.OrdinalIgnoreCase.Equals(valueContent, "null")) {
                            entry = EntryType.Null;
                            return;
                        } else if (StringComparer.Ordinal.Equals(valueContent, "{")) {
                            entry = EntryType.StartOfNode;
                            return;
                        } else if (StringComparer.Ordinal.Equals(valueContent, "}")) {
                            entry = EntryType.EndOfNode;
                            return;
                        } else if (StringComparer.Ordinal.Equals(valueContent, "[")) {
                            entry = EntryType.StartOfArray;
                            return;
                        } else if (StringComparer.Ordinal.Equals(valueContent, "]")) {
                            entry = EntryType.EndOfArray;
                            return;
                        } else if (valueContent.StartsWith(JsonConfig.INTERNAL_REF_SIG, StringComparison.Ordinal)) {
                            entry = EntryType.InternalReference;
                            return;
                        } else if (valueContent.StartsWith(JsonConfig.EXTERNAL_INDEX_REF_SIG, StringComparison.Ordinal)) {
                            entry = EntryType.ExternalReferenceByIndex;
                            return;
                        } else if (valueContent.StartsWith(JsonConfig.EXTERNAL_GUID_REF_SIG, StringComparison.Ordinal)) {
                            entry = EntryType.ExternalReferenceByGuid;
                            return;
                        } else if (valueContent.StartsWith(JsonConfig.EXTERNAL_STRING_REF_SIG_OLD, StringComparison.Ordinal)) {
                            entry = EntryType.ExternalReferenceByString;
                            return;
                        } else if (valueContent.StartsWith(JsonConfig.EXTERNAL_STRING_REF_SIG_FIXED, StringComparison.Ordinal)) {
                            entry = EntryType.ExternalReferenceByString;
                            return;
                        } else {
                            EntryType? guessedPrimitiveType = GuessPrimitiveType(valueContent);
                            
                            if (guessedPrimitiveType != null) {
                                entry = guessedPrimitiveType.Value;
                                return;
                            }
                        }
                    }
                }
            }
            
            if (hintEntry != null) {
                name = null;
                valueContent = null;
                entry = hintEntry.Value;
                return;
            }
            
            if (bufferIndex == -1) {
                Context.Config.DebugContext.LogError("Failed to parse empty entry in the stream.");
            } else {
                Context.Config.DebugContext.LogError("Tried and failed to parse entry with content '" + new string(buffer, 0, bufferIndex + 1) + "'.");
            }
            
            if (hintEntry == EntryType.EndOfStream) {
                name = null;
                valueContent = null;
                entry = EntryType.EndOfStream;
            } else {
                name = null;
                valueContent = null;
                entry = EntryType.Invalid;
            }
        }
        
        private bool IsHex(char c) {
            return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
        }
        
        private uint ParseSingleChar(char c, uint multiplier) {
            uint p = 0;
            
            if (c >= '0' && c <= '9') {
                p = (uint)(c - '0') * multiplier;
            } else if (c >= 'A' && c <= 'F') {
                p = (uint)((c - 'A') + 10) * multiplier;
            } else if (c >= 'a' && c <= 'f') {
                p = (uint)((c - 'a') + 10) * multiplier;
            }
            
            return p;
        }
        
        private char ParseHexChar(char c1, char c2, char c3, char c4) {
            uint p1 = ParseSingleChar(c1, 0x1000);
            uint p2 = ParseSingleChar(c2, 0x100);
            uint p3 = ParseSingleChar(c3, 0x10);
            uint p4 = ParseSingleChar(c4, 0x1);
            
            try {
                return (char)(p1 + p2 + p3 + p4);
            } catch (Exception) {
                Context.Config.DebugContext.LogError("Could not parse invalid hex values: " + c1 + c2 + c3 + c4);
                return ' ';
            }
        }
        
        private char ReadCharIntoBuffer() {
            bufferIndex++;
            
            if (bufferIndex >= buffer.Length - 1) {
                char[] newBuffer = new char[buffer.Length * 2];
                Buffer.BlockCopy(buffer, 0, newBuffer, 0, buffer.Length * sizeof(char));
                buffer = newBuffer;
            }
            
            char c = ConsumeChar();
            
            buffer[bufferIndex] = c;
            lastReadChar = c;
            
            return c;
        }
        
        private EntryType? GuessPrimitiveType(string content) {
            if (StringComparer.OrdinalIgnoreCase.Equals(content, "null")) {
                return EntryType.Null;
            } else if (content.Length >= 2 && content[0] == '"' && content[content.Length - 1] == '"') {
                return EntryType.String;
            } else if (content.Length == 36 && content.LastIndexOf('-') > 0) {
                return EntryType.Guid;
            } else if (content.Contains(".") || content.Contains(",")) {
                return EntryType.FloatingPoint;
            } else if (StringComparer.OrdinalIgnoreCase.Equals(content, "true") || StringComparer.OrdinalIgnoreCase.Equals(content, "false")) {
                return EntryType.Boolean;
            } else if (content.Length >= 1) {
                return EntryType.Integer;
            }
            
            return null;
        }
        
        private char PeekChar() {
            if (peekedChar == null) {
                if (emergencyPlayback != null && emergencyPlayback.Count > 0) {
                    peekedChar = emergencyPlayback.Dequeue();
                } else {
                    peekedChar = (char)reader.Read();
                }
            }
            
            return peekedChar.Value;
        }
        
        private void SkipChar() {
            if (peekedChar == null) {
                if (emergencyPlayback != null && emergencyPlayback.Count > 0) {
                    emergencyPlayback.Dequeue();
                } else {
                    reader.Read();
                }
            } else {
                peekedChar = null;
            }
        }
        
        private char ConsumeChar() {
            if (peekedChar == null) {
                if (emergencyPlayback != null && emergencyPlayback.Count > 0) {
                    return emergencyPlayback.Dequeue();
                } else {
                    return (char)reader.Read();
                }
            } else {
                char? c = peekedChar;
                peekedChar = null;
                return c.Value;
            }
        }
    }
}