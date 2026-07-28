using Il2CppInterop.Runtime;
using MelonLoader;
using UnityEngine;

namespace Jiangyu.Loader.Replacements;

/// <summary>
/// Copies MENACE MonoBehaviour components from a vanilla prefab subtree
/// onto a mod-shipped prefab subtree, node for node and field for field.
///
/// <para>A modder's Unity project has no reference to the game's script
/// assemblies, so a sub-assembly they copy out of a vanilla prefab keeps
/// its GameObjects, transforms, meshes and materials but loses every
/// MonoBehaviour on the way into the AssetBundle. This restores them at
/// load time from the live vanilla prefab.</para>
///
/// <para>Field copying goes through IL2CPP field metadata rather than the
/// Il2CppInterop wrapper properties, because the component types are only
/// known at runtime. For each field declared between the component's
/// concrete class and <c>MonoBehaviour</c>: a value-type field is copied
/// as raw bytes at the field offset, and a reference-type field is copied
/// as its object pointer through the GC write barrier. That is byte for
/// byte what the generated wrapper accessors do, so a mirrored component
/// holds the same state a wrapper-typed assignment would have written.
/// </para>
///
/// <para>References that point at a node inside the mirrored subtree are
/// remapped onto the addition's equivalent node, so a component wired to
/// its own sibling Transform or Renderer stays wired to the addition's
/// copy rather than reaching back into the vanilla prefab.</para>
/// </summary>
internal static class Il2CppComponentMirror
{
    /// <summary>
    /// ECMA-335 II.23.1.5 field attributes. Static and literal fields carry
    /// no per-instance state to copy, and a field the author marked
    /// <c>[NonSerialized]</c> is runtime scratch the game repopulates.
    /// </summary>
    private const int FieldAttributeStatic = 0x0010;
    private const int FieldAttributeLiteral = 0x0040;
    private const int FieldAttributeNotSerialized = 0x0080;
    private const int SkippedFieldAttributes =
        FieldAttributeStatic | FieldAttributeLiteral | FieldAttributeNotSerialized;

    /// <summary>
    /// Upper bound on the size of an inline value-type field the copier
    /// will move. Every Unity struct a serialised field realistically holds
    /// (Vector3, Color, Bounds, Matrix4x4, a small fixed buffer) is far
    /// under this. A field above it is reported and left at its default
    /// rather than moved on a guess.
    /// </summary>
    private const int MaxValueFieldBytes = 4096;

    /// <summary>Per-subtree tally, reported as one log line by the caller.</summary>
    internal sealed class Result
    {
        public int NodesPaired;
        public int NodesUnmatched;
        public int ComponentsAdded;
        public int ComponentsAlreadyPresent;
        public int FieldsSkipped;
        public int DanglingReferences;
        public int MaterialsRebound;
    }

    /// <summary>
    /// Mirror every MonoBehaviour under <paramref name="referenceScope"/>
    /// onto the equivalent node under <paramref name="additionScope"/>.
    /// Nodes pair by name, level by level, from the two scope roots down.
    /// <paramref name="referenceRoot"/> is the whole vanilla prefab and is
    /// used only to recognise references that escape the mirrored subtree.
    /// </summary>
    public static Result Mirror(
        Transform referenceScope,
        Transform additionScope,
        Transform referenceRoot,
        MelonLogger.Instance log,
        string label)
    {
        var result = new Result();
        var pairs = new List<(Transform Reference, Transform Addition)>();
        PairSubtrees(referenceScope, additionScope, pairs, result);
        result.NodesPaired = pairs.Count;

        // Object-pointer identity is what a field read gives us, so the
        // remap is keyed on the raw IL2CPP pointer rather than the wrapper.
        var remap = new Dictionary<IntPtr, IntPtr>();
        foreach (var (reference, addition) in pairs)
        {
            remap[reference.Pointer] = addition.Pointer;
            remap[reference.gameObject.Pointer] = addition.gameObject.Pointer;
        }

        foreach (var (reference, addition) in pairs)
            SyncRendererMaterials(reference, addition, result);

        // Attach first, copy second. A component's field can reference a
        // component on another node in the same subtree, and that target
        // has to exist before the field write can be remapped onto it.
        var work = new List<(IntPtr Reference, IntPtr Addition, IntPtr Class)>();
        foreach (var (reference, addition) in pairs)
            PairAndAttachComponents(reference.gameObject, addition.gameObject, remap, work, result, log, label);

        if (work.Count == 0)
            return result;

        var referenceObjects = CollectObjectPointers(referenceRoot);
        foreach (var (reference, addition, klass) in work)
            CopyFields(reference, addition, klass, remap, referenceObjects, result, log, label);

        return result;
    }

    /// <summary>
    /// Point the addition's renderer slots at the live vanilla materials
    /// they were copied from. The materials a modder's bundle carries for a
    /// copied sub-assembly are extractions bound to stub shaders, and HDRP
    /// surface state (the transparency keyword, the render queue) does not
    /// survive the stub round trip: the CQB laser's additive quads come
    /// back opaque white. A slot whose material still carries the vanilla
    /// material's name is swapped for the vanilla object itself. A slot the
    /// modder renamed or replaced is their own work and is left alone.
    /// </summary>
    private static void SyncRendererMaterials(Transform reference, Transform addition, Result result)
    {
        var referenceRenderer = reference.GetComponent<Renderer>();
        var additionRenderer = addition.GetComponent<Renderer>();
        if (referenceRenderer == null || additionRenderer == null) return;

        var referenceSlots = referenceRenderer.sharedMaterials;
        var additionSlots = additionRenderer.sharedMaterials;
        if (referenceSlots == null || additionSlots == null) return;

        var changed = false;
        for (var i = 0; i < additionSlots.Length; i++)
        {
            var current = additionSlots[i];
            if (current == null) continue;
            for (var j = 0; j < referenceSlots.Length; j++)
            {
                var candidate = referenceSlots[j];
                if (candidate == null || candidate.name != current.name) continue;
                if (candidate.Pointer != current.Pointer)
                {
                    additionSlots[i] = candidate;
                    result.MaterialsRebound++;
                    changed = true;
                }
                break;
            }
        }
        if (changed)
            additionRenderer.sharedMaterials = additionSlots;
    }

    /// <summary>
    /// Pair the two subtrees by name, level by level. Repeated sibling
    /// names pair in declaration order, so a copied sub-assembly holding
    /// several identically named markers still pairs one to one. A
    /// reference node with no counterpart on the addition is counted and
    /// skipped: the modder is free to delete parts of what they copied.
    /// </summary>
    private static void PairSubtrees(
        Transform reference,
        Transform addition,
        List<(Transform Reference, Transform Addition)> pairs,
        Result result)
    {
        pairs.Add((reference, addition));

        var byName = new Dictionary<string, Queue<Transform>>(StringComparer.Ordinal);
        for (var i = 0; i < addition.childCount; i++)
        {
            var child = addition.GetChild(i);
            if (child == null) continue;
            if (!byName.TryGetValue(child.name, out var queue))
                byName[child.name] = queue = new Queue<Transform>();
            queue.Enqueue(child);
        }

        for (var i = 0; i < reference.childCount; i++)
        {
            var child = reference.GetChild(i);
            if (child == null) continue;
            if (!byName.TryGetValue(child.name, out var queue) || queue.Count == 0)
            {
                result.NodesUnmatched++;
                continue;
            }
            PairSubtrees(child, queue.Dequeue(), pairs, result);
        }
    }

    /// <summary>
    /// Pair the two nodes' components by concrete class and by count, so a
    /// node carrying two of the same script pairs one to one, and give the
    /// addition a component for every MonoBehaviour it is missing.
    ///
    /// <para>Every component is paired, not only the scripts: a restored
    /// script that references a sibling MeshRenderer has to land on the
    /// addition's renderer rather than the vanilla one. A component the
    /// addition already has is left exactly as the modder authored it and
    /// only recorded in the remap, never overwritten. A missing component
    /// that is not a MonoBehaviour is left alone: engine components ship in
    /// the bundle, so an absent one is the modder's deletion.</para>
    /// </summary>
    private static void PairAndAttachComponents(
        GameObject reference,
        GameObject addition,
        Dictionary<IntPtr, IntPtr> remap,
        List<(IntPtr Reference, IntPtr Addition, IntPtr Class)> work,
        Result result,
        MelonLogger.Instance log,
        string label)
    {
        // One enumeration of the addition's own components, bucketed by
        // class, so repeats on a node are consumed in order.
        var existing = new Dictionary<IntPtr, Queue<IntPtr>>();
        foreach (var component in addition.GetComponents<Component>())
        {
            if (component == null) continue;
            var klass = IL2CPP.il2cpp_object_get_class(component.Pointer);
            if (klass == IntPtr.Zero) continue;
            if (!existing.TryGetValue(klass, out var queue))
                existing[klass] = queue = new Queue<IntPtr>();
            queue.Enqueue(component.Pointer);
        }

        var monoBehaviour = Il2CppClassPointerStore<MonoBehaviour>.NativeClassPtr;
        foreach (var component in reference.GetComponents<Component>())
        {
            if (component == null) continue;
            var klass = IL2CPP.il2cpp_object_get_class(component.Pointer);
            if (klass == IntPtr.Zero) continue;

            // Only scripts are restored. Meshes, renderers and colliders
            // come through the bundle intact, so an absent one is a
            // deletion to respect rather than a loss to repair.
            var isScript = monoBehaviour != IntPtr.Zero
                && IL2CPP.il2cpp_class_is_assignable_from(monoBehaviour, klass);

            if (existing.TryGetValue(klass, out var queue) && queue.Count > 0)
            {
                remap[component.Pointer] = queue.Dequeue();
                if (isScript) result.ComponentsAlreadyPresent++;
                continue;
            }

            if (!isScript) continue;

            // A type Jiangyu or a mod injected into the runtime exists only
            // in this process, never on a vanilla prefab, and re-adding one
            // through the native type would sidestep its managed shell.
            if (RuntimeSpecificsStore.IsInjected(klass)) continue;

            Component attached;
            try
            {
                attached = addition.AddComponent(Il2CppType.TypeFromPointer(klass));
            }
            catch (Exception ex)
            {
                log.Warning(
                    $"  Script restore on '{label}': AddComponent for '{DescribeClass(klass)}' on node "
                    + $"'{addition.name}' threw: {ex.Message}. That behaviour will be missing in-game.");
                continue;
            }

            if (attached == null)
            {
                log.Warning(
                    $"  Script restore on '{label}': could not attach '{DescribeClass(klass)}' to node "
                    + $"'{addition.name}'. That behaviour will be missing in-game.");
                continue;
            }

            remap[component.Pointer] = attached.Pointer;
            work.Add((component.Pointer, attached.Pointer, klass));
            result.ComponentsAdded++;
        }
    }

    /// <summary>
    /// Copy every instance field declared between <paramref name="klass"/>
    /// and <c>MonoBehaviour</c> from the reference component to the freshly
    /// attached one, remapping in-subtree object references on the way.
    /// </summary>
    private static unsafe void CopyFields(
        IntPtr reference,
        IntPtr addition,
        IntPtr klass,
        Dictionary<IntPtr, IntPtr> remap,
        HashSet<IntPtr> referenceObjects,
        Result result,
        MelonLogger.Instance log,
        string label)
    {
        // JIANGYU-CONTRACT: a MonoBehaviour's serialised state is exactly the
        // fields declared below UnityEngine.MonoBehaviour in its class chain.
        // Everything at or above it is engine-owned (name, hideFlags, enabled,
        // the native object handle), so the walk must stop there. It is
        // collected up front rather than tested inside the walk: a chain that
        // never reaches MonoBehaviour would otherwise run into
        // UnityEngine.Object and overwrite the destination's native handle,
        // so an unreachable stop class aborts the copy instead.
        var declaring = new List<IntPtr>();
        var stop = Il2CppClassPointerStore<MonoBehaviour>.NativeClassPtr;
        var reached = false;
        for (var current = klass; current != IntPtr.Zero; current = IL2CPP.il2cpp_class_get_parent(current))
        {
            if (current == stop)
            {
                reached = true;
                break;
            }
            declaring.Add(current);
        }

        if (!reached)
        {
            log.Warning(
                $"  Script restore on '{label}': '{DescribeClass(klass)}' does not resolve as a "
                + "UnityEngine.MonoBehaviour, so its field state cannot be copied safely. The "
                + "component is attached but unconfigured.");
            return;
        }

        foreach (var current in declaring)
        {
            var iterator = IntPtr.Zero;
            IntPtr field;
            while ((field = IL2CPP.il2cpp_class_get_fields(current, ref iterator)) != IntPtr.Zero)
            {
                if ((IL2CPP.il2cpp_field_get_flags(field) & SkippedFieldAttributes) != 0)
                    continue;

                var fieldType = IL2CPP.il2cpp_field_get_type(field);
                if (fieldType == IntPtr.Zero) continue;
                var fieldClass = IL2CPP.il2cpp_class_from_il2cpp_type(fieldType);
                if (fieldClass == IntPtr.Zero) continue;

                var offset = (int)IL2CPP.il2cpp_field_get_offset(field);
                if (offset <= 0) continue;

                if (!IL2CPP.il2cpp_class_is_valuetype(fieldClass))
                {
                    var value = *(IntPtr*)((byte*)reference + offset);
                    if (value != IntPtr.Zero)
                    {
                        if (remap.TryGetValue(value, out var mapped))
                        {
                            value = mapped;
                        }
                        else if (referenceObjects.Contains(value))
                        {
                            // Points at the vanilla prefab, outside what was
                            // copied. Left pointing there, because nulling it
                            // would be a different kind of wrong, but the
                            // modder wants to know their copy is incomplete.
                            result.DanglingReferences++;
                            log.Warning(
                                $"  Script restore on '{label}': field '{IL2CPP.il2cpp_field_get_name_(field)}' on "
                                + $"'{DescribeClass(klass)}' points outside the copied sub-assembly and still "
                                + "references the vanilla prefab. Copy the node it points at as well.");
                        }
                    }
                    IL2CPP.il2cpp_gc_wbarrier_set_field(
                        addition, (IntPtr)((byte*)addition + offset), value);
                    continue;
                }

                uint alignment = 0;
                var size = IL2CPP.il2cpp_class_value_size(fieldClass, ref alignment);
                if (size <= 0 || size > MaxValueFieldBytes)
                {
                    result.FieldsSkipped++;
                    continue;
                }
                // A struct field may embed object pointers, copied here without a
                // write barrier. Safe under IL2CPP's Boehm GC: it neither moves
                // objects nor needs barriers for correctness.
                Buffer.MemoryCopy(
                    (byte*)reference + offset, (byte*)addition + offset, size, size);
            }
        }
    }

    /// <summary>
    /// Every Component and GameObject pointer under
    /// <paramref name="root"/>. Membership is how a copied field value is
    /// recognised as a reference into the vanilla prefab.
    /// </summary>
    private static HashSet<IntPtr> CollectObjectPointers(Transform root)
    {
        var pointers = new HashSet<IntPtr>();
        if (root == null) return pointers;
        foreach (var component in root.GetComponentsInChildren<Component>(true))
        {
            if (component == null) continue;
            pointers.Add(component.Pointer);
            pointers.Add(component.gameObject.Pointer);
        }
        return pointers;
    }

    private static string DescribeClass(IntPtr klass)
    {
        var name = IL2CPP.il2cpp_class_get_name_(klass);
        var space = IL2CPP.il2cpp_class_get_namespace_(klass);
        return string.IsNullOrEmpty(space) ? name : space + "." + name;
    }
}
