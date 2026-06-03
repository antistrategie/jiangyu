using System;
using System.IO;
using System.Linq;
using Jiangyu.Sdk;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Jiangyu.Sdk.Analyzers.Tests;

// Builds a compilation over the runtime references plus Jiangyu.Sdk, shared by the
// analyzer and generator tests so the reference set lives in one place.
internal static class TestCompilation
{
    public static CSharpCompilation Create(params string[] sources)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(path => !string.IsNullOrEmpty(path))
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .Append(MetadataReference.CreateFromFile(typeof(JiangyuTypeAttribute).Assembly.Location))
            .ToList();

        return CSharpCompilation.Create(
            "TestUnderTest",
            sources.Select(source => CSharpSyntaxTree.ParseText(source)),
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }
}
