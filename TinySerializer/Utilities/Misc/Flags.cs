using System.Reflection;

namespace TinySerializer.Utilities.Misc {
    public static class Flags {
        public const BindingFlags AnyVisibility = BindingFlags.Public | BindingFlags.NonPublic;
        public const BindingFlags InstancePublic = BindingFlags.Public | BindingFlags.Instance;
        public const BindingFlags InstancePrivate = BindingFlags.NonPublic | BindingFlags.Instance;
        public const BindingFlags InstanceAnyVisibility = AnyVisibility | BindingFlags.Instance;
        public const BindingFlags StaticPublic = BindingFlags.Public | BindingFlags.Static;
        public const BindingFlags StaticPrivate = BindingFlags.NonPublic | BindingFlags.Static;
        public const BindingFlags StaticAnyVisibility = AnyVisibility | BindingFlags.Static;
        public const BindingFlags InstancePublicDeclaredOnly = InstancePublic | BindingFlags.DeclaredOnly;
        public const BindingFlags InstancePrivateDeclaredOnly = InstancePrivate | BindingFlags.DeclaredOnly;
        public const BindingFlags InstanceAnyDeclaredOnly = InstanceAnyVisibility | BindingFlags.DeclaredOnly;
        public const BindingFlags StaticPublicDeclaredOnly = StaticPublic | BindingFlags.DeclaredOnly;
        public const BindingFlags StaticPrivateDeclaredOnly = StaticPrivate | BindingFlags.DeclaredOnly;
        public const BindingFlags StaticAnyDeclaredOnly = StaticAnyVisibility | BindingFlags.DeclaredOnly;
        public const BindingFlags StaticInstanceAnyVisibility = InstanceAnyVisibility | BindingFlags.Static;
        public const BindingFlags AllMembers = StaticInstanceAnyVisibility | BindingFlags.FlattenHierarchy;
    }
}