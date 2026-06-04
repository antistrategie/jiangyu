using System;
using System.Reflection;

namespace Jiangyu.Loader.Sdk;

/// <summary>
/// Redirects a mod's reference to the Jiangyu.Sdk and Jiangyu.Sdk.Menace assemblies
/// to the copies merged into the loader. Mods compile against standalone DLLs. At
/// runtime those types are merged into Jiangyu.Loader, so a mod's references are
/// resolved to the loader assembly here. Both are merged into the same loader
/// assembly, so both names resolve to it.
/// </summary>
internal static class SdkAssemblyResolver
{
    private static bool _installed;

    public static void Install()
    {
        if (_installed)
            return;

        _installed = true;
        AppDomain.CurrentDomain.AssemblyResolve += Resolve;
    }

    private static Assembly Resolve(object sender, ResolveEventArgs args)
    {
        var requested = new AssemblyName(args.Name).Name;
        return requested is "Jiangyu.Sdk" or "Jiangyu.Sdk.Menace"
            ? typeof(Jiangyu.Sdk.JiangyuMod).Assembly
            : null;
    }
}
