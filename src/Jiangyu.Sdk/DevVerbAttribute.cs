using System;

namespace Jiangyu.Sdk;

/// <summary>
/// Marks a static class in a mod as a container of dev verbs: developer-only commands the dev
/// loader's bridge can invoke by name (<c>"ClassName.MethodName"</c>), the same way it invokes the
/// SDK's own <c>Jiangyu.Game.*</c> verbs. Each public static method on the class is a verb, marshalled
/// and invoked exactly like an SDK verb (mark one that changes game state with <see cref="MutatingVerbAttribute"/>
/// so it runs only when the request passes <c>mutate: true</c>).
///
/// <para>Dev verbs are unreachable in a shipped mod: the bridge and verb runner live only in the dev
/// loader, never in the player loader. Keep the code out of releases too by putting it in a
/// <c>*.Dev.cs</c> source file, which the mod build excludes from a release compile.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class DevVerbAttribute : Attribute
{
}
