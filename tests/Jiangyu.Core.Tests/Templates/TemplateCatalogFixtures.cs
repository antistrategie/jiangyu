// Fixture types used by TemplateTypeCatalog / TemplateMemberQuery tests.
// Compiled into the test assembly so tests can open the assembly via
// MetadataLoadContext and reflect on these controlled shapes without relying
// on MENACE's Il2Cpp wrappers.

namespace Jiangyu.Core.Tests.Templates.Fixtures.Gameplay
{
    public enum FixtureDamageType
    {
        Blunt,
        Ballistic,
        Plasma,
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

    public class FixtureSkill
    {
        public int Uses { get; set; }
        public float Cooldown { get; set; }
    }

    public class FixtureBaseEntity
    {
        public string? m_ID { get; set; }
        public string? m_Name { get; set; }
    }

    public class FixtureEntity : FixtureBaseEntity
    {
        public FixtureProperties Properties { get; set; } = new();
        public List<FixtureSkill> Skills { get; set; } = new();
        public int[] BoneIndices { get; set; } = [];
        public bool IsEnabled { get; set; }
        public float HudYOffsetScale { get; set; }
        public int ReadOnlyCount { get; } = 0;
    }
}

namespace Jiangyu.Core.Tests.Templates.Fixtures.Other
{
    // Same short name as Gameplay.FixtureSkill to exercise ambiguous-short-name
    // resolution. The FQN form stays unambiguous.
    public class FixtureSkill
    {
        public int Placeholder { get; set; }
    }
}
