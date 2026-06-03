; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/docs/Analyzer%20Configuration.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
JIA001 | Jiangyu | Error | [JiangyuType] must be a concrete class
JIA002 | Jiangyu | Error | [JiangyuType] name is invalid
JIA003 | Jiangyu | Error | [JiangyuType] name collision
JIA004 | Jiangyu | Warning | [JiangyuType] field cannot be serialised
JIA005 | Jiangyu | Warning | [JiangyuType] needs the IL2CPP injection constructors
JIA006 | Jiangyu | Error | [JiangyuType] cannot receive the IL2CPP injection constructors
