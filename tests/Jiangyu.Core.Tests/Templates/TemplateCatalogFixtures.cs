// Fixture types used by TemplateTypeCatalog / TemplateMemberQuery tests.
// Compiled into the test assembly so tests can open the assembly via
// MetadataLoadContext and reflect on these controlled shapes without relying
// on MENACE's Il2Cpp wrappers.

namespace Sirenix.Serialization
{
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
    public sealed class OdinSerializeAttribute : Attribute
    {
    }
}

namespace UnityEngine
{
    public class ScriptableObject
    {
    }

    // Stand-ins for Unity asset classes the validator dispatches on by
    // simple Type.Name. They don't need to inherit from UnityEngine.Object
    // for the validator's purpose; AssetCategory only consults the name.
    public class Sprite { }
    public class Texture2D { }
    public class AudioClip { }
    public class Material { }
    public class Mesh { }
    public class GameObject { }
}

namespace Menace.Tools
{
    public class DataTemplate
    {
    }
}

namespace Menace.Tools
{
    // Minimal stand-in for the game's [NamedArray(typeof(T))] attribute used
    // on enum-indexed primitive arrays. The catalog detects this by short
    // name, so the containing namespace only needs to be distinct enough not
    // to collide with Sirenix / Unity attributes.
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
    public sealed class NamedArrayAttribute(Type enumType) : Attribute
    {
        public Type EnumType { get; } = enumType;
    }
}

namespace Jiangyu.Core.Tests.Templates.Fixtures.Gameplay
{
    public enum FixtureDamageType
    {
        Blunt,
        Ballistic,
        Plasma,
    }

    public enum FixtureAttribute
    {
        Agility = 0,
        WeaponSkill = 1,
        Vitality = 2,
    }

    public class FixtureNamedArrayHolder : Menace.Tools.DataTemplate
    {
        [Menace.Tools.NamedArray(typeof(FixtureAttribute))]
        public byte[] Attributes { get; set; } = new byte[3];
    }

    // For the "ref-required-when-polymorphic" validator path: an abstract
    // DataTemplate base (modder must specify ref=) and a holder template
    // that exposes both a polymorphic and a concrete reference field.
    public abstract class FixtureBaseDataTemplate : Menace.Tools.DataTemplate
    {
    }

    // Concrete subtype of FixtureBaseDataTemplate. The catalog's polymorphism
    // detector sees this as a strict reference-target descendant, so any
    // field typed FixtureBaseDataTemplate is reported as polymorphic — even
    // though Il2CppInterop wrappers strip the abstract bit on generation.
    public class FixtureConcreteDerived : FixtureBaseDataTemplate
    {
        public int DerivedField { get; set; }
    }

    public class FixtureRefHolder : Menace.Tools.DataTemplate
    {
        public FixtureBaseDataTemplate? PolymorphicRef { get; set; }
        public FixtureSkillTemplate? ConcreteRef { get; set; }
    }

    public class FixtureProperties
    {
        public int Accuracy { get; set; }
        public float Armor { get; set; }
        public string? DisplayName { get; set; }
        public bool IsCritical { get; set; }
        public FixtureDamageType DamageType { get; set; }
        public int ReadOnlyField { get; } = 0;
    }

    public class FixtureSkillTemplate : Menace.Tools.DataTemplate
    {
        public int Uses { get; set; }
        public float Cooldown { get; set; }
    }

    // Template carrying Unity-asset-typed fields, used by the asset-reference
    // validator tests. Fields cover the supported categories plus the two
    // deferred ones (Mesh, GameObject) so the validator's deferral message
    // can be exercised against a real declared type, and a non-asset field
    // for the rejection path.
    public class FixtureAssetHolder : Menace.Tools.DataTemplate
    {
        public UnityEngine.Sprite? Icon { get; set; }
        public UnityEngine.Texture2D? Album { get; set; }
        public UnityEngine.AudioClip? Bark { get; set; }
        public UnityEngine.Material? Skin { get; set; }
        public UnityEngine.Mesh? Geometry { get; set; }
        public UnityEngine.GameObject? Prefab { get; set; }
        public string? NotAnAsset { get; set; }
    }

    public interface IFixtureAoEShape
    {
        int Radius { get; }
    }

    // Concrete implementation of the polymorphic-scalar interface IFixtureAoEShape.
    // Used by HandlerConstruction validator tests for Odin-routed scalar
    // construction on a non-collection field. Radius is settable because the
    // Phase 2b construction path writes inner ops via reflection.
    public class FixtureAoEShapeImpl : IFixtureAoEShape
    {
        public int Radius { get; set; }
    }

    public abstract class FixtureCondition
    {
        public string? Label { get; set; }
    }

    public abstract class FixtureProjectileData
    {
        public float Speed { get; set; }
    }

    public abstract class FixtureScriptableAbstract : UnityEngine.ScriptableObject
    {
        public string? AssetName { get; set; }
    }

    public class FixtureBaseEntity
    {
        public string? m_ID { get; set; }
        public string? m_Name { get; set; }
    }

    // Concrete subtype of FixtureBaseDataTemplate used by the polymorphic-
    // descent validator tests. Carries a unique field so descent into the
    // list element via type=hint can resolve it.
    public class FixtureConcreteDerivedB : FixtureBaseDataTemplate
    {
        public string? DerivedFieldB { get; set; }
    }

    public class FixtureEntity : FixtureBaseEntity
    {
        public FixtureProperties Properties { get; set; } = new();
        public List<FixtureSkillTemplate> Skills { get; set; } = new();
        public FixtureSkillTemplate InitialSkill { get; set; } = new();
        public int[] BoneIndices { get; set; } = [];

        // Polymorphic reference array: declared element type is the abstract
        // FixtureBaseDataTemplate, but live data holds concrete subtypes
        // (FixtureConcreteDerived, FixtureConcreteDerivedB). Mirrors the
        // shape of SkillTemplate.EventHandlers in production data.
        public List<FixtureBaseDataTemplate> Handlers { get; set; } = new();
        public bool IsEnabled { get; set; }
        public float HudYOffsetScale { get; set; }

        // Wider integer family — exercise scalar-kind mapping and applier
        // range-checked widening for sibling integer widths. Real game data
        // uses these (e.g. UInt16 for SkillTemplate.Repetitions).
        public ushort Repetitions { get; set; }
        public short SignedShort { get; set; }
        public uint UInt32Field { get; set; }
        public long Int64Field { get; set; }
        public ulong UInt64Field { get; set; }
        public sbyte SByteField { get; set; }
        public double DoubleField { get; set; }
        [Sirenix.Serialization.OdinSerialize]
        public FixtureCondition? CustomCondition { get; set; }
        public int ReadOnlyCount { get; } = 0;

        // Type-based Odin detection targets (no [OdinSerialize] attribute):
        public IFixtureAoEShape? AoEShape { get; set; }
        public FixtureProjectileData? Projectile { get; set; }
        public HashSet<FixtureSkillTemplate>? SkillsRemoved { get; set; }
        public IFixtureAoEShape[]? AoEShapes { get; set; }

        // Should NOT be flagged — abstract but descends from ScriptableObject:
        public FixtureScriptableAbstract? ScriptableRef { get; set; }
    }
}

namespace Jiangyu.Core.Tests.Templates.Fixtures.Other
{
    // Same short name as Gameplay.FixtureSkillTemplate to exercise
    // ambiguous-short-name resolution. The FQN form stays unambiguous.
    public class FixtureSkillTemplate
    {
        public int Placeholder { get; set; }
    }

    // Non-subtype short-name twin of Gameplay.FixtureConcreteDerived. The
    // polymorphic-descent resolver must restrict to subtypes of the
    // descent's element type and not return this class when descending
    // through a List<FixtureBaseDataTemplate>: this type doesn't extend
    // that base. Mirrors the production Effects.Attack vs
    // AI.Behaviors.Attack collision: the latter is short-name-identical
    // but not in the SkillEventHandlerTemplate hierarchy.
    public class FixtureConcreteDerived
    {
        public string? UnrelatedField { get; set; }
    }
}

namespace Il2CppJiangyu.Core.Tests.Templates.Fixtures.Wrapped
{
    // Il2Cpp-prefixed namespace twin used to exercise the resolver's prefix-
    // stripping path. Its short-name twin lives in a deliberately different
    // tail namespace (Fixtures.Plain, not Fixtures.Wrapped) so that a hint
    // matching only one of them via prefix-strip selects unambiguously.
    public class FixtureWrappedTwin
    {
        public int Placeholder { get; set; }
    }
}

namespace Jiangyu.Core.Tests.Templates.Fixtures.Plain
{
    // Plain-namespace short-name twin. The unwrapped form of the
    // Il2Cpp-wrapped twin's namespace differs from this one, so each twin is
    // hint-selectable on its own.
    public class FixtureWrappedTwin
    {
        public string? Other { get; set; }
    }
}
