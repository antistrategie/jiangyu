using Jiangyu.Core.Code;
using Xunit;

namespace Jiangyu.Core.Tests.Code;

public sealed class JiangyuTypeMetadataReaderTests
{
    [Fact]
    public void Read_DoesNotHoldAHandleOnTheInspectedDll()
    {
        // Copy a real managed assembly to a scratch path, inspect it, then delete the file.
        // The reader loads from a byte copy, so it never maps the file: a path-loaded
        // MetadataLoadContext would keep the handle on Windows and this delete would throw
        // "the process cannot access the file because it is being used by another process",
        // which is what made a later compile (it wipes compiled/ and rebuilds code/bin) fail
        // when a long-lived host had inspected the DLL. On platforms without mandatory locks
        // the delete always succeeds, so this only catches a regression back to path loading.
        var source = typeof(JiangyuTypeMetadataReader).Assembly.Location;
        var scratch = Path.Combine(Path.GetTempPath(), $"jiangyu-reader-lock-{Guid.NewGuid():N}.dll");
        File.Copy(source, scratch);
        try
        {
            JiangyuTypeMetadataReader.Read([scratch], gameDir: null, sdkDir: null, visit: (_, _, _) => { });
            File.Delete(scratch);
            Assert.False(File.Exists(scratch));
        }
        finally
        {
            if (File.Exists(scratch))
                File.Delete(scratch);
        }
    }
}
