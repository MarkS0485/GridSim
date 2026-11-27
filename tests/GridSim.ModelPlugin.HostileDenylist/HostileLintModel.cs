using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using GridSim.Core.Dynamics.Plugins;

[assembly: GridSimModelPlugin(1)]
[assembly: IgnoresAccessChecksTo("GridSim.Core")]

namespace System.Runtime.CompilerServices
{
    /// <summary>The runtime honours this attribute when the assembly defines it itself: it lifts the access checks on the named
    /// assembly's private members. The lint refuses the definition.</summary>
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    internal sealed class IgnoresAccessChecksToAttribute(string assemblyName) : Attribute
    {
        public string AssemblyName { get; } = assemblyName;
    }
}

namespace GridSim.ModelPlugin.HostileDenylist
{
    /// <summary>HOSTILE_LINT: a governor that, on its first Initialize, tries to empty the lint's namespace list three ways:
    /// reflection on the private field and a write through the immutable array's backing store, an accessor the runtime
    /// supplies (UnsafeAccessor), and an assembly-level IgnoresAccessChecksTo. The lint refuses the whole assembly before
    /// anything is loaded.</summary>
public sealed class HostileLintFactory : IUserModelFactory
{
    public string Id => "HOSTILE_LINT";

    public UserModelRole Role => UserModelRole.Governor;

    public IReadOnlyList<PluginParameter> Parameters { get; } = Array.Empty<PluginParameter>();

    public object Create(IReadOnlyList<double> parameters) => new HostileLintGovernor();
}

    public sealed class HostileLintGovernor : IUserGovernor
    {
        private double _pm0;

        [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "Namespaces")]
        private static extern ref ImmutableArray<string> Namespaces([UnsafeAccessorType("GridSim.Core.Dynamics.Plugins.PluginDenylist, GridSim.Core")] object? owner);

        public void Initialize(double pm0)
        {
            FieldInfo? f = typeof(PluginDenylist).GetField("Namespaces", BindingFlags.NonPublic | BindingFlags.Static);
            if (f?.GetValue(null) is ImmutableArray<string> list) ImmutableCollectionsMarshal.AsArray(list)![0] = "Nothing.";
            Namespaces(null) = ImmutableArray<string>.Empty;
            _pm0 = pm0;
        }

        public double Step(double speedDev, double dt) => _pm0;
    }
}
