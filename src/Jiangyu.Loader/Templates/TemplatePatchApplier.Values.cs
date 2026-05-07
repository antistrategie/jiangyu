using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Jiangyu.Shared.Templates;
using MelonLoader;

namespace Jiangyu.Loader.Templates;

// Value-conversion + composite/handler/asset/reference construction
// for the patch applier. CompiledTemplateValue is the wire format the
// emitter produces; this partial turns each kind into a runtime object
// the IL2CPP-side setters expect (boxed primitives, enum constants,
// Il2Cpp-wrapped instances, Unity Object references).
internal sealed partial class TemplatePatchApplier
{
    private static bool TryConvertScalar(
        CompiledTemplateValue value, Type targetType, ModAssetResolver assetResolver, MelonLogger.Instance log,
        out object converted, out string error)
    {
        if (value == null)
        {
            converted = null;
            error = "value payload is null.";
            return false;
        }

        // Numeric normalisation shared with compile/editor validation:
        // allow canonical narrowing/widening between Byte/Int32/Single before
        // strict target-type checks below. Integer-family destinations (short,
        // ushort, uint, long, ulong, sbyte) all normalise to the Int32 patch
        // kind here; the kind=Int32 branch below range-checks and widens to
        // the actual member type. Double normalises through Single.
        if (targetType == typeof(byte))
        {
            if (!TemplateValueCoercion.TryCoerceNumericKind(value, CompiledTemplateValueKind.Byte, out error))
            {
                converted = null;
                return false;
            }
        }
        else if (TemplateValueCoercion.IsIntegerFamilyTarget(targetType))
        {
            if (!TemplateValueCoercion.TryCoerceNumericKind(value, CompiledTemplateValueKind.Int32, out error))
            {
                converted = null;
                return false;
            }
        }
        else if (targetType == typeof(float) || targetType == typeof(double))
        {
            if (!TemplateValueCoercion.TryCoerceNumericKind(value, CompiledTemplateValueKind.Single, out error))
            {
                converted = null;
                return false;
            }
        }

        converted = null;
        error = null;

        switch (value.Kind)
        {
            case CompiledTemplateValueKind.Boolean:
                if (targetType != typeof(bool))
                {
                    error = $"value kind Boolean but member type is {targetType.FullName}.";
                    return false;
                }
                converted = value.Boolean.Value;
                return true;

            case CompiledTemplateValueKind.Byte:
                if (targetType != typeof(byte))
                {
                    error = $"value kind Byte but member type is {targetType.FullName}.";
                    return false;
                }
                converted = value.Byte.Value;
                return true;

            case CompiledTemplateValueKind.Int32:
                return TemplateValueCoercion.TryWidenInt32(value.Int32.Value, targetType, out converted, out error);

            case CompiledTemplateValueKind.Single:
                if (targetType == typeof(float))
                {
                    converted = value.Single.Value;
                    return true;
                }
                if (targetType == typeof(double))
                {
                    converted = (double)value.Single.Value;
                    return true;
                }
                error = $"value kind Single but member type is {targetType.FullName}.";
                return false;

            case CompiledTemplateValueKind.String:
                if (targetType != typeof(string))
                {
                    error = $"value kind String but member type is {targetType.FullName}.";
                    return false;
                }
                converted = value.String;
                return true;

            case CompiledTemplateValueKind.Enum:
                if (!targetType.IsEnum)
                {
                    error = $"value kind Enum but member type {targetType.FullName} is not an enum.";
                    return false;
                }
                if (!string.IsNullOrWhiteSpace(value.EnumType) &&
                    !string.Equals(value.EnumType, targetType.Name, StringComparison.Ordinal))
                {
                    error = $"value EnumType '{value.EnumType}' does not match member enum type {targetType.Name}.";
                    return false;
                }
                try
                {
                    var parsed = Enum.Parse(targetType, value.EnumValue, ignoreCase: false);
                    // Strict membership, mirroring the compile-time validator.
                    // Enum.Parse accepts any numeric form in the underlying
                    // type's range (e.g. "99" for an undefined ItemSlot
                    // value); reject those so a hand-edited compiled manifest
                    // can't sneak an undefined value past the loader.
                    if (!Enum.IsDefined(targetType, parsed))
                    {
                        error = $"enum value '{value.EnumValue}' is not a defined member of {targetType.Name}.";
                        return false;
                    }
                    converted = parsed;
                    return true;
                }
                catch (Exception ex)
                {
                    error = $"failed to parse enum value '{value.EnumValue}' for {targetType.Name}: {ex.Message}";
                    return false;
                }

            case CompiledTemplateValueKind.TemplateReference:
                return TryResolveTemplateReference(value.Reference, targetType, out converted, out error);

            case CompiledTemplateValueKind.AssetReference:
                return TryResolveAssetReference(value.Asset, targetType, assetResolver, out converted, out error);

            case CompiledTemplateValueKind.Composite:
                return TryConstructComposite(value.Composite, targetType, assetResolver, log, out converted, out error);

            case CompiledTemplateValueKind.HandlerConstruction:
                return TryConstructHandler(value.HandlerConstruction, targetType, assetResolver, log, out converted, out error);

            default:
                error = $"unknown value kind {value.Kind}.";
                return false;
        }
    }

    /// <summary>
    /// Constructs a fresh ScriptableObject of the named subtype (for the
    /// modder's <c>handler="X"</c> authoring shape on append/insert/set
    /// against a polymorphic-reference array). Routes through the existing
    /// composite construction path then sets
    /// <c>HideFlags.DontUnloadUnusedAsset</c> so scene-change GC doesn't
    /// sweep the freshly-allocated handler. When the payload's type name is
    /// empty, the array's element type is used (monomorphic case).
    /// </summary>
    private static bool TryConstructHandler(
        CompiledTemplateComposite handler, Type targetType, ModAssetResolver assetResolver, MelonLogger.Instance log,
        out object converted, out string error)
    {
        converted = null;

        if (handler == null)
        {
            error = "value kind HandlerConstruction but payload is null.";
            return false;
        }

        // If the modder omitted handler="X" because the destination is
        // monomorphic (validator already confirmed), the empty TypeName
        // resolves to the array's element type. Synthesise the payload so
        // TryConstructComposite has something to look up.
        var effectivePayload = handler;
        if (string.IsNullOrWhiteSpace(handler.TypeName))
        {
            effectivePayload = new CompiledTemplateComposite
            {
                TypeName = targetType.Name,
                Operations = handler.Operations ?? new List<CompiledTemplateSetOperation>(),
            };
        }

        if (!TryConstructComposite(effectivePayload, targetType, assetResolver, log, out converted, out error))
            return false;

        if (converted is UnityEngine.Object asUnity)
        {
            // hideFlags keeps scene-change GC from sweeping the freshly-
            // allocated handler. name matches the vanilla convention (each
            // game-shipped handler is named after its concrete subtype:
            // "AddSkill", "ChangeProperty", etc.) so inspector dumps show
            // "SkillEventHandlerTemplate:AddSkill" instead of an unnamed
            // entry. ScriptableObject.CreateInstance leaves name empty
            // by default.
            try { asUnity.hideFlags = UnityEngine.HideFlags.DontUnloadUnusedAsset; }
            catch { }
            try { asUnity.name = effectivePayload.TypeName; }
            catch { }
        }
        return true;
    }

    // Constructs a fresh instance of the composite's typeName and recursively
    // writes each authored field via the same TryConvertScalar conversion.
    // Dispatches construction by base class:
    //  - ScriptableObject subtypes: ScriptableObject.CreateInstance (runs
    //    Unity's OnEnable etc.).
    //  - Il2CppObjectBase subtypes (e.g. LocalizedLine, plain Il2CppSystem.*
    //    support types): allocate via il2cpp_object_new on the IL2CPP class
    //    pointer, then wrap with the generated (IntPtr) ctor. Skips running
    //    any IL2CPP-side ctor; fields are written individually below.
    //  - Pure managed types: Activator.CreateInstance (parameterless ctor).
    private static bool TryConstructComposite(
        CompiledTemplateComposite composite, Type targetType, ModAssetResolver assetResolver, MelonLogger.Instance log,
        out object converted, out string error)
    {
        converted = null;

        if (composite == null || string.IsNullOrWhiteSpace(composite.TypeName))
        {
            error = "value kind Composite but payload is missing typeName.";
            return false;
        }

        var resolvedType = TemplateRuntimeAccess.ResolveTemplateType(composite.TypeName, out var resolveError);
        if (resolvedType == null)
        {
            error = $"Composite: cannot resolve typeName '{composite.TypeName}': {resolveError}";
            return false;
        }

        if (!IsAssignableFromIl2Cpp(targetType, resolvedType))
        {
            error = $"Composite typeName '{composite.TypeName}' ({resolvedType.FullName}) is not assignable to member type {targetType.FullName}.";
            return false;
        }

        object instance;
        try
        {
            if (typeof(UnityEngine.ScriptableObject).IsAssignableFrom(resolvedType))
            {
                instance = UnityEngine.ScriptableObject.CreateInstance(Il2CppType.From(resolvedType));
            }
            else if (typeof(Il2CppObjectBase).IsAssignableFrom(resolvedType))
            {
                if (!TryAllocateIl2CppInstance(resolvedType, out instance, out var il2cppError))
                {
                    error = $"Composite: construction of {resolvedType.FullName} failed: {il2cppError}";
                    return false;
                }
            }
            else
            {
                instance = Activator.CreateInstance(resolvedType)!;
            }
        }
        catch (Exception ex)
        {
            error = $"Composite: construction of {resolvedType.FullName} threw: {ex.Message}";
            return false;
        }

        // Il2CppInterop polymorphic factories (e.g. ScriptableObject.CreateInstance(Il2CppType))
        // return a base-typed wrapper. Reflection on that wrapper sees only
        // base-class members, so subtype fields like AddSkill.Event would
        // be missed. Cast the wrapper to resolvedType so GetType() reports
        // the concrete type and TryGetWritableMember can find subclass
        // fields. Same Cast<T>-via-MakeGenericMethod pattern used by the
        // path-walk applier and the clone deep-copy.
        if (instance is Il2CppObjectBase il2cppInstance
            && instance.GetType() != resolvedType)
        {
            if (!TryIl2CppCast(il2cppInstance, resolvedType, out var cast, out var castError))
            {
                error = $"Composite: {castError}";
                return false;
            }
            if (cast != null) instance = cast;
        }

        // Apply each authored operation against the freshly-constructed
        // instance using the same path-walk-and-apply machinery the outer
        // applier uses on live templates. Set ops on top-level fields are
        // the common case; collection ops (Append/Insert/Remove/Clear) on
        // the constructed instance's collection members work too: e.g.
        // appending a fresh PropertyChange to a ChangeProperty handler's
        // Properties list during construction.
        if (composite.Operations != null)
        {
            foreach (var innerOp in composite.Operations)
            {
                var loadedOp = new LoadedPatchOperation(
                    innerOp.Op,
                    innerOp.FieldPath,
                    innerOp.Index,
                    (IReadOnlyList<int>?)innerOp.IndexPath ?? Array.Empty<int>(),
                    (IReadOnlyList<TemplateDescentStep>?)innerOp.Descent ?? Array.Empty<TemplateDescentStep>(),
                    innerOp.Value,
                    $"composite:{composite.TypeName}");

                var outcome = TryApplyOperation(
                    instance,
                    composite.TypeName,
                    "<construction>",
                    loadedOp,
                    assetResolver,
                    log);
                if (outcome != ApplyOutcome.Applied)
                {
                    error = $"Composite {resolvedType.Name}: inner op {innerOp.Op} '{innerOp.FieldPath}' failed (outcome={outcome}).";
                    return false;
                }
            }
        }

        converted = instance;
        error = null;
        return true;
    }

    // Resolves a modder-authored asset reference (a single name string) to a
    // live Unity Object. The lookup category is the destination field's
    // declared Unity type; the resolver walks the mod-bundle catalog first
    // and falls back to the live game-asset registry. See
    // ModAssetResolver for the JIANGYU-CONTRACT detail on resolution order.
    private static bool TryResolveAssetReference(
        CompiledAssetReference reference, Type targetType,
        ModAssetResolver assetResolver, out object converted, out string error)
    {
        converted = null;

        if (reference == null || string.IsNullOrEmpty(reference.Name))
        {
            error = "value kind AssetReference but reference name is missing.";
            return false;
        }

        if (assetResolver == null)
        {
            error = $"AssetReference '{reference.Name}': no asset resolver wired into the applier.";
            return false;
        }

        if (!Jiangyu.Shared.Replacements.AssetCategory.IsSupported(targetType.Name))
        {
            error = $"AssetReference '{reference.Name}' targets {targetType.FullName}, "
                + "which is not a supported asset category. "
                + "Sprite, Texture2D, AudioClip, and Material are supported today; "
                + "Mesh and GameObject wait on the prefab-construction layer "
                + "(see PREFAB_CLONING_TODO.md).";
            return false;
        }

        var resolved = assetResolver.TryFind(targetType, reference.Name);
        if (resolved == null)
        {
            error = $"AssetReference '{reference.Name}': no asset of type {targetType.Name} "
                + "found in the mod bundle catalog or the live game-asset registry.";
            return false;
        }

        converted = resolved;
        error = null;
        return true;
    }

    // Resolves a modder-authored (templateType, templateId) pair to the live
    // Il2Cpp wrapper. TryGetTemplateById dispatches by base class:
    // DataTemplate subtypes resolve via DataTemplateLoader.TryGet<T>(m_ID);
    // other ScriptableObject subtypes (e.g. PerkTreeTemplate) resolve via
    // Resources.FindObjectsOfTypeAll by Object.name.
    private static bool TryResolveTemplateReference(
        CompiledTemplateReference reference, Type targetType, out object converted, out string error)
    {
        converted = null;

        if (reference == null)
        {
            error = "value kind TemplateReference but reference is null.";
            return false;
        }

        // TemplateType is optional in the canonical schema: when omitted the
        // destination field's declared type IS the lookup type. Fall back to
        // targetType.Name so the rest of the resolution path stays unchanged.
        var lookupTypeName = string.IsNullOrWhiteSpace(reference.TemplateType)
            ? targetType.Name
            : reference.TemplateType;

        if (!TemplateRuntimeAccess.TryGetTemplateById(
                lookupTypeName, reference.TemplateId,
                out var resolvedTemplate, out var resolvedType, out var resolveError))
        {
            error = resolvedType == null
                ? $"TemplateReference '{lookupTypeName}:{reference.TemplateId}': {resolveError}"
                : $"TemplateReference: no live {lookupTypeName} with id '{reference.TemplateId}' found.";
            return false;
        }

        if (!targetType.IsAssignableFrom(resolvedType))
        {
            error = $"TemplateReference targets {resolvedType.FullName} but member expects {targetType.FullName}.";
            return false;
        }

        converted = resolvedTemplate;
        error = null;
        return true;
    }
}
