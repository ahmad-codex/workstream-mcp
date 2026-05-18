using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Dapper;

namespace Workstream.Data.Mapping;

/// <summary>
/// Maps Postgres snake_case column names to PascalCase C# property names so Dapper can
/// hydrate domain records without explicit AS aliases. Registered once at startup via
/// <see cref="Register"/>.
/// </summary>
public sealed class SnakeCaseTypeMap<T> : SqlMapper.ITypeMap
{
    private readonly DefaultTypeMap _inner = new(typeof(T));

    public ConstructorInfo? FindConstructor(string[] names, Type[] types)
    {
        // Prefer constructors whose params (PascalCase) line up with the given column names
        // (snake_case → PascalCase). The default ITypeMap impl handles this when we transform
        // the names below.
        var transformed = names.Select(SnakeToPascal).ToArray();
        return _inner.FindConstructor(transformed, types);
    }

    public ConstructorInfo? FindExplicitConstructor() => _inner.FindExplicitConstructor();

    public SqlMapper.IMemberMap? GetConstructorParameter(ConstructorInfo constructor, string columnName)
        => _inner.GetConstructorParameter(constructor, SnakeToPascal(columnName));

    public SqlMapper.IMemberMap? GetMember(string columnName)
        => _inner.GetMember(SnakeToPascal(columnName));

    public static string SnakeToPascal(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var sb = new StringBuilder(s.Length);
        bool upper = true;
        foreach (var c in s)
        {
            if (c == '_') { upper = true; continue; }
            sb.Append(upper ? char.ToUpperInvariant(c) : c);
            upper = false;
        }
        return sb.ToString();
    }

    private static readonly HashSet<Type> _registered = new();
    private static readonly object _lock = new();

    public static void Register()
    {
        lock (_lock)
        {
            foreach (var t in DomainRecordTypes())
            {
                if (_registered.Contains(t)) continue;
                var mapType = typeof(SnakeCaseTypeMap<>).MakeGenericType(t);
                var map = (SqlMapper.ITypeMap)Activator.CreateInstance(mapType)!;
                SqlMapper.SetTypeMap(t, map);
                _registered.Add(t);
            }
        }
    }

    private static IEnumerable<Type> DomainRecordTypes()
    {
        // Every record under Workstream.Core.Domain that the data layer hydrates.
        var asm = typeof(Core.Domain.WorkTask).Assembly;
        return asm.GetTypes()
                  .Where(t => t is { IsClass: true, IsAbstract: false }
                              && t.Namespace is "Workstream.Core.Domain"
                              && t.GetCustomAttribute<CompilerServicesAttr>() == null);
    }

    // Filter helper: avoids picking up compiler-generated types.
    private sealed class CompilerServicesAttr : Attribute { }
}
