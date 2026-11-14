using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Mega.Tests.Support;

/// <summary>
/// Stand-in for a Jellyfin interface, without a mocking library: every call is recorded, methods given with
/// <see cref="On"/> run their handler, the others return an empty value (null, 0, completed task).
/// </summary>
internal sealed class Fake<T>
    where T : class
{
    private readonly Dictionary<string, Func<object?[], object?>> _handlers = new(StringComparer.Ordinal);
    private readonly List<(string Name, object?[] Args)> _calls = new();

    public Fake()
    {
        Object = DispatchProxy.Create<T, FakeProxy>();
        ((FakeProxy)(object)Object).Handler = Dispatch;
    }

    public T Object { get; }

    /// <summary>Gets or sets the value of string properties not given with <see cref="On"/> (paths of a temporary folder).</summary>
    public Func<string, string?>? Strings { get; set; }

    public Fake<T> On(string method, Func<object?[], object?> handler)
    {
        _handlers[method] = handler;
        return this;
    }

    public IReadOnlyList<object?[]> Calls(string method)
    {
        lock (_calls)
        {
            return _calls.Where(c => c.Name == method).Select(c => c.Args).ToList();
        }
    }

    private object? Dispatch(MethodInfo method, object?[] args)
    {
        lock (_calls)
        {
            _calls.Add((method.Name, args));
        }

        if (_handlers.TryGetValue(method.Name, out var handler))
        {
            return handler(args);
        }

        if (method.ReturnType == typeof(string) && method.Name.StartsWith("get_", StringComparison.Ordinal) && Strings is not null)
        {
            return Strings(method.Name[4..]);
        }

        return Empty(method.ReturnType);
    }

    private static object? Empty(Type type)
    {
        if (type == typeof(void))
        {
            return null;
        }

        if (type == typeof(Task))
        {
            return Task.CompletedTask;
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var inner = type.GetGenericArguments()[0];
            var value = inner.IsValueType ? Activator.CreateInstance(inner) : null;
            return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(inner).Invoke(null, new[] { value });
        }

        return type.IsValueType ? Activator.CreateInstance(type) : null;
    }
}

/// <summary>Proxy behind <see cref="Fake{T}"/> (public: the generated proxy type derives from it).</summary>
public class FakeProxy : DispatchProxy
{
    internal Func<MethodInfo, object?[], object?> Handler { get; set; } = (_, _) => null;

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args ?? Array.Empty<object?>());
}
