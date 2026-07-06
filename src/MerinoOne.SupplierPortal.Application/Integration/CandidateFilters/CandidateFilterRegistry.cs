using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;

namespace MerinoOne.SupplierPortal.Application.Integration.CandidateFilters;

/// <summary>
/// R9 (TSD R9 §2.5a, D-R9-15 option C) — marks a static method as a named, code-registered candidate
/// filter for one portalEntity. The method must return <c>Expression&lt;Func&lt;TEntity,bool&gt;&gt;</c>
/// (parameterless) or take a single <c>JsonElement</c> params argument (parameterized built-ins like
/// <c>StatusIn</c>). Config rows store only the NAME; the expression tree compiles to an indexed,
/// parameterized SQL WHERE — nothing admin-authored ever reaches the database.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class CandidateFilterAttribute : Attribute
{
    public CandidateFilterAttribute(string portalEntity, string name)
    {
        PortalEntity = portalEntity;
        Name = name;
    }

    public string PortalEntity { get; }
    public string Name { get; }
}

/// <summary>One discovered filter: identity + the factory that yields the EF predicate.</summary>
public sealed record CandidateFilterDescriptor(
    string PortalEntity,
    string Name,
    Type EntityType,
    bool IsParameterized,
    Func<JsonElement?, LambdaExpression> Factory);

/// <summary>
/// Startup-reflected <c>(portalEntity, name) → predicate</c> registry. Phase A wires only the SAVE-TIME
/// validation surface (<see cref="TryValidate"/> + the config dropdown); <see cref="Resolve"/> goes live
/// in Phase B (reconciliation sweep + backfill entity scan).
/// </summary>
public interface ICandidateFilterRegistry
{
    IReadOnlyList<CandidateFilterDescriptor> All { get; }
    IReadOnlyList<CandidateFilterDescriptor> ForEntity(string portalEntity);
    bool TryValidate(string portalEntity, string name, string? paramsJson, out string? error);
    LambdaExpression Resolve(string portalEntity, string name, string? paramsJson);
}

public sealed class CandidateFilterRegistry : ICandidateFilterRegistry
{
    private readonly Dictionary<(string Entity, string Name), CandidateFilterDescriptor> _filters;

    public CandidateFilterRegistry()
    {
        _filters = new Dictionary<(string, string), CandidateFilterDescriptor>();
        var methods = typeof(CandidateFilterRegistry).Assembly
            .GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Select(m => (Method: m, Attr: m.GetCustomAttribute<CandidateFilterAttribute>()))
            .Where(x => x.Attr is not null);

        foreach (var (method, attr) in methods)
        {
            var descriptor = Describe(method, attr!);
            if (!_filters.TryAdd((attr!.PortalEntity, attr.Name), descriptor))
                throw new InvalidOperationException(
                    $"Duplicate candidate filter registration ({attr.PortalEntity}, {attr.Name}) on {method.DeclaringType?.Name}.{method.Name}.");
        }
    }

    public IReadOnlyList<CandidateFilterDescriptor> All => _filters.Values.ToList();

    public IReadOnlyList<CandidateFilterDescriptor> ForEntity(string portalEntity)
        => _filters.Values.Where(f => f.PortalEntity == portalEntity).OrderBy(f => f.Name).ToList();

    public bool TryValidate(string portalEntity, string name, string? paramsJson, out string? error)
    {
        if (!_filters.TryGetValue((portalEntity, name), out var descriptor))
        {
            error = $"Unknown candidate filter '{name}' for portal entity '{portalEntity}' — filters are code-registered (D-R9-15).";
            return false;
        }

        if (descriptor.IsParameterized)
        {
            if (string.IsNullOrWhiteSpace(paramsJson))
            {
                error = $"Candidate filter '{name}' requires params JSON (e.g. {{\"statuses\":[\"Accepted\"]}}).";
                return false;
            }
            try
            {
                using var doc = JsonDocument.Parse(paramsJson);
                _ = descriptor.Factory(doc.RootElement.Clone()); // full dry-build so bad params fail at save, not at scan
            }
            catch (Exception ex)
            {
                error = $"Candidate filter '{name}' params invalid: {ex.Message}";
                return false;
            }
        }
        else if (!string.IsNullOrWhiteSpace(paramsJson))
        {
            error = $"Candidate filter '{name}' takes no params.";
            return false;
        }

        error = null;
        return true;
    }

    public LambdaExpression Resolve(string portalEntity, string name, string? paramsJson)
    {
        if (!_filters.TryGetValue((portalEntity, name), out var descriptor))
            throw new InvalidOperationException($"Unknown candidate filter ({portalEntity}, {name}).");
        if (!descriptor.IsParameterized) return descriptor.Factory(null);
        using var doc = JsonDocument.Parse(paramsJson ?? throw new InvalidOperationException($"Filter '{name}' requires params."));
        return descriptor.Factory(doc.RootElement.Clone());
    }

    private static CandidateFilterDescriptor Describe(MethodInfo method, CandidateFilterAttribute attr)
    {
        var returnType = method.ReturnType;
        if (!returnType.IsGenericType || returnType.GetGenericTypeDefinition() != typeof(Expression<>))
            throw new InvalidOperationException($"Candidate filter {method.Name} must return Expression<Func<TEntity,bool>>.");
        var funcType = returnType.GetGenericArguments()[0];
        var entityType = funcType.GetGenericArguments()[0];

        var parameters = method.GetParameters();
        return parameters.Length switch
        {
            0 => new CandidateFilterDescriptor(attr.PortalEntity, attr.Name, entityType, IsParameterized: false,
                _ => (LambdaExpression)Unwrapped(method, null)!),
            1 when parameters[0].ParameterType == typeof(JsonElement) =>
                new CandidateFilterDescriptor(attr.PortalEntity, attr.Name, entityType, IsParameterized: true,
                    p => (LambdaExpression)Unwrapped(method, new object?[] { p ?? default(JsonElement) })!),
            _ => throw new InvalidOperationException(
                $"Candidate filter {method.Name} must be parameterless or take a single JsonElement params argument."),
        };
    }

    /// <summary>MethodInfo.Invoke wraps filter exceptions in TargetInvocationException — surface the real message (it feeds save-time validation errors).</summary>
    private static object Unwrapped(MethodInfo method, object?[]? args)
    {
        try { return method.Invoke(null, args)!; }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }
}
