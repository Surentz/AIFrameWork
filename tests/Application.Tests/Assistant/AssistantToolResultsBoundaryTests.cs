using System.Reflection;
using AiFramework.Application.Assistant.ToolResults;
using FluentAssertions;

namespace AiFramework.Application.Tests.Assistant;

/// <summary>
/// Everything a tool returns is sent to the model provider. This is the rule that keeps personal
/// data out of it: no property anywhere in a tool result may be named like a user identifier.
/// A field added to a monitoring view cannot leak through the assistant without failing here.
/// </summary>
public sealed class AssistantToolResultsBoundaryTests
{
    private static readonly string[] Banned = ["UserId", "OwnerId", "Username", "UserName", "IpAddress", "UserAgent"];

    private static List<(Type Owner, PropertyInfo Property)> AllProperties()
    {
        var assembly = typeof(JobHealthResult).Assembly;
        var roots = assembly.GetTypes().Where(t => string.Equals(t.Namespace, typeof(JobHealthResult).Namespace, StringComparison.Ordinal));
        var seen = new HashSet<Type>();
        var found = new List<(Type, PropertyInfo)>();
        var pending = new Queue<Type>(roots);

        while (pending.TryDequeue(out var type))
        {
            if (!seen.Add(type) || type.Assembly != assembly)
            {
                continue;
            }

            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                found.Add((type, property));
                pending.Enqueue(property.PropertyType);
                foreach (var argument in property.PropertyType.GetGenericArguments())
                {
                    pending.Enqueue(argument);
                }
            }
        }

        return found;
    }

    [Fact]
    public void Scan_FindsTheToolResultProperties()
    {
        AllProperties().Should().HaveCountGreaterThan(30);
    }

    [Fact]
    public void Tool_results_carry_no_user_identifier()
    {
        var violations = AllProperties()
            .Where(p => Banned.Any(b => p.Property.Name.Contains(b, StringComparison.OrdinalIgnoreCase)))
            .Select(p => $"{p.Owner.Name}.{p.Property.Name}");

        string.Join(", ", violations).Should().BeEmpty();
    }
}
