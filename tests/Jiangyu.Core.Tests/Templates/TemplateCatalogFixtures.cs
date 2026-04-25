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

    public interface IFixtureAoEShape
    {
        int Radius { get; }
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

    public class FixtureEntity : FixtureBaseEntity
    {
        public FixtureProperties Properties { get; set; } = new();
        public List<FixtureSkillTemplate> Skills { get; set; } = new();
        public FixtureSkillTemplate InitialSkill { get; set; } = new();
        public int[] BoneIndices { get; set; } = [];
        public bool IsEnabled { get; set; }
        public float HudYOffsetScale { get; set; }
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
}
