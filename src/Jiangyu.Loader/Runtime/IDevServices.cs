using MelonLoader;

namespace Jiangyu.Loader.Runtime;

// The seam between the core loader and its optional dev surface (the Studio bridge
// and the on-demand probes). The implementation lives in Jiangyu.Loader.Diagnostics
// and is merged into the dev loader DLL only. JiangyuMod discovers it by reflection
// (see JiangyuMod.DiscoverDevServices), so the core loader never references the
// diagnostics assembly and the user loader DLL carries none of it. When no
// implementation is present every call is a null no-op.
internal interface IDevServices
{
    // Wire the bridge and run any load-time check. Called once from OnInitializeMelon.
    void Initialise(IDevServicesContext context);

    // Start or stop the bridge to match the dev flag, and start the request-pump loop on
    // the first call. Called on every scene load.
    void OnSceneLoaded();
}

// What the dev surface reads back from the live loader. The scene members are
// properties, not constructor values, because the bridge handlers read them when a
// request arrives, long after Initialise.
internal interface IDevServicesContext
{
    MelonLogger.Instance Logger { get; }
    string CurrentScene { get; }
    int CurrentBuildIndex { get; }
}
