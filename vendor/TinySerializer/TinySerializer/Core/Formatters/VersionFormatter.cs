using System;
using TinySerializer.Core.DataReaderWriters;
using TinySerializer.Core.Formatters;
using TinySerializer.Core.Misc;

[assembly: RegisterFormatter(typeof(VersionFormatter))]

namespace TinySerializer.Core.Formatters {
    public sealed class VersionFormatter : MinimalBaseFormatter<Version> {
        protected override Version GetUninitializedObject() {
            return null;
        }
        
        protected override void Read(ref Version value, IDataReader reader) {
            int major = 0, minor = 0, build = 0, revision = 0;
            
            reader.ReadInt32(out major);
            reader.ReadInt32(out minor);
            reader.ReadInt32(out build);
            reader.ReadInt32(out revision);
            
            if (major < 0 || minor < 0) {
                value = new Version();
            } else if (build < 0) {
                value = new Version(major, minor);
            } else if (revision < 0) {
                value = new Version(major, minor, build);
            } else {
                value = new Version(major, minor, build, revision);
            }
        }
        
        protected override void Write(ref Version value, IDataWriter writer) {
            writer.WriteInt32(null, value.Major);
            writer.WriteInt32(null, value.Minor);
            writer.WriteInt32(null, value.Build);
            writer.WriteInt32(null, value.Revision);
        }
    }
}