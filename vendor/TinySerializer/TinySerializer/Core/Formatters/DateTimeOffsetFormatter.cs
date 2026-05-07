using System;
using System.Globalization;
using TinySerializer.Core.DataReaderWriters;
using TinySerializer.Core.Formatters;
using TinySerializer.Core.Misc;

[assembly: RegisterFormatter(typeof(DateTimeOffsetFormatter))]

namespace TinySerializer.Core.Formatters {
    public sealed class DateTimeOffsetFormatter : MinimalBaseFormatter<DateTimeOffset> {
        protected override void Read(ref DateTimeOffset value, IDataReader reader) {
            string name;
            
            if (reader.PeekEntry(out name) == EntryType.String) {
                string str;
                reader.ReadString(out str);
                DateTimeOffset.TryParse(str, out value);
            }
        }
        
        protected override void Write(ref DateTimeOffset value, IDataWriter writer) {
            writer.WriteString(null, value.ToString("O", CultureInfo.InvariantCulture));
        }
    }
}