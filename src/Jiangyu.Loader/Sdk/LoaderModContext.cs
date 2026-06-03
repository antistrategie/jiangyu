using System;
using System.Collections.Generic;
using System.IO;
using Jiangyu.Loader.Bundles;
using Jiangyu.Loader.Logging;
using Jiangyu.Sdk;
using Jiangyu.Shared.Bundles;

namespace Jiangyu.Loader.Sdk;

/// <summary>The concrete <see cref="ModContext"/> the loader binds to each mod.</summary>
internal sealed class LoaderModContext : ModContext
{
    private const string UnknownVersion = "0.0.0";

    public LoaderModContext(string modId, string modFolder, string version, IModHostLog hostLog, InProcessHookBus hooks, IModAssets assets, IModCoroutines coroutines, IModPatches patches)
    {
        ModId = modId;
        ModFolder = modFolder;
        Version = version;
        Log = new TaggedLog(modId, hostLog);
        Hooks = hooks;
        Assets = assets ?? NullModAssets.Instance;
        Coroutines = coroutines ?? NullModCoroutines.Instance;
        Patches = patches ?? NullModPatches.Instance;
    }

    public override string ModId { get; }

    public override string ModFolder { get; }

    public override string Version { get; }

    public override IModLog Log { get; }

    public override IModState State { get; } = new PersistentModState();

    public override IHookBus Hooks { get; }

    public override IModAssets Assets { get; }

    public override IModCoroutines Coroutines { get; }

    public override IModPatches Patches { get; }

    /// <summary>A factory that shares one hook bus and host log across every mod,
    /// resolving each mod's folder as <c>&lt;modsDir&gt;/&lt;modId&gt;</c>, reading its
    /// version from the deployed <c>jiangyu.json</c>, resolving its bundled assets
    /// through <paramref name="assetsProvider"/>, driving its coroutines through
    /// <paramref name="coroutineStart"/> and <paramref name="coroutineStop"/>, and
    /// exposing method patching when <paramref name="patchingEnabled"/>.</summary>
    public static Func<string, ModContext> Factory(
        IModHostLog hostLog,
        InProcessHookBus hooks,
        string modsDir,
        Func<string, IModAssets> assetsProvider = null,
        Func<System.Collections.IEnumerator, object> coroutineStart = null,
        Action<object> coroutineStop = null,
        bool patchingEnabled = false)
        => modId =>
        {
            var modFolder = Path.Combine(modsDir, modId);
            var assets = assetsProvider?.Invoke(modId);
            IModCoroutines coroutines = coroutineStart != null
                ? new ModCoroutineRunner(modId, coroutineStart, coroutineStop, hostLog)
                : NullModCoroutines.Instance;
            IModPatches patches = patchingEnabled
                ? new ModPatchService(modId, hostLog)
                : NullModPatches.Instance;
            return new LoaderModContext(modId, modFolder, ReadVersion(modFolder), hostLog, hooks, assets, coroutines, patches);
        };

    private static string ReadVersion(string modFolder)
    {
        if (LoaderManifest.TryRead(modFolder, out var manifest) && !string.IsNullOrWhiteSpace(manifest.Version))
            return manifest.Version!;
        return UnknownVersion;
    }

    private sealed class TaggedLog : IModLog
    {
        private readonly string _modId;
        private readonly IModHostLog _host;

        public TaggedLog(string modId, IModHostLog host)
        {
            _modId = modId;
            _host = host;
        }

        public void Debug(string message)
        {
            if (Jiangyu.Sdk.Log.MinLevel <= LogLevel.Debug)
                _host.Debug($"[{_modId}] {message}");
        }

        public void Info(string message) => _host.Info($"[{_modId}] {message}");

        public void Warn(string message) => _host.Warn($"[{_modId}] {message}");

        public void Error(string message) => _host.Error($"[{_modId}] {message}");
    }
}
