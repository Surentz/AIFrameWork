using System.Reflection;
using System.Runtime.CompilerServices;

// CacheDuration and CacheScope (Caching/CacheDuration.cs, Messaging/CacheScope.cs) are internal
// on purpose - neither is part of this layer's public surface - but Infrastructure.Tests tests
// their arithmetic and key composition directly rather than only through AddCaching's public
// entry point, so the test assembly needs to see them. This is the ordinary same-layer
// production/test friend-assembly grant, not the cross-layer coupling Domain's ClearDomainEvents
// comment warns against - Infrastructure.Tests is this layer's own test project, not another
// architectural layer.
[assembly: InternalsVisibleTo("AiFramework.Infrastructure.Tests")]

namespace AiFramework.Infrastructure;

/// <summary>Stable handle on this assembly for tests and assembly scanning.</summary>
public static class AssemblyMarker
{
    public static Assembly Assembly => typeof(AssemblyMarker).Assembly;
}
