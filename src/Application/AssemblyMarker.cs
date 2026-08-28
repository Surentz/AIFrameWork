using System.Reflection;

namespace AiFramework.Application;

/// <summary>Stable handle on this assembly for tests and assembly scanning.</summary>
public static class AssemblyMarker
{
    public static Assembly Assembly => typeof(AssemblyMarker).Assembly;
}
