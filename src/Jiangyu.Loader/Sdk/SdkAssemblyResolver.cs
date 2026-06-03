using System;
using System.Reflection;

namespace Jiangyu.Loader.Sdk;

/// <summary>
/// Redirects a mod's reference to the Jiangyu.Sdk assembly to the copy merged
/// into the loader. Mods compile against a standalone Jiangyu.Sdk.dll. At runtime
/// those types are merged into Jiangyu.Loader, so a mod's Jiangyu.Sdk reference is
/// resolved to the loader assembly here.
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
        return requested == "Jiangyu.Sdk" ? typeof(Jiangyu.Sdk.JiangyuMod).Assembly : null;
    }
}
