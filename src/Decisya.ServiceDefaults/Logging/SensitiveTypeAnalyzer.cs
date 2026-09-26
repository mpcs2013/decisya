using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using Decisya.SharedKernel;
using Decisya.SharedKernel.Observability;

namespace Decisya.ServiceDefaults.Logging;

/// <summary>
/// Type-shape analysis behind <see cref="SensitiveDataMaskingProcessor"/>: which types are
/// scalars, which carry <see cref="SensitiveAttribute"/>, whose graph the processor has to
/// render itself, and whose getters it is allowed to call at all.
/// </summary>
/// <remarks>
/// Every answer is cached per <see cref="Type"/>, so the reflection cost is paid once per
/// process per type. Every analysis failure answers "render it" or "mask it", never "pass
/// it through": the analyzer fails closed.
/// </remarks>
internal static class SensitiveTypeAnalyzer
{
    /// <summary>
    /// How deep the graph walk looks for a <see cref="SensitiveAttribute"/> before it gives
    /// up and declares the shape undetermined.
    /// </summary>
    internal const int MaxGraphDepth = 3;

    private static readonly ConcurrentDictionary<Type, bool> ScalarCache = new();
    private static readonly ConcurrentDictionary<Type, bool> SensitiveTypeCache = new();
    private static readonly ConcurrentDictionary<Type, bool> NeedsRenderingCache = new();
    private static readonly ConcurrentDictionary<Type, bool> RenderableCache = new();
    private static readonly ConcurrentDictionary<Type, bool> ScalarElementsCache = new();
    private static readonly ConcurrentDictionary<Type, bool> ShapeMaskingElementsCache = new();
    private static readonly ConcurrentDictionary<PropertyInfo, bool> SensitiveMemberCache = new();
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> RenderablePropertyCache = new();

    /// <summary>Unwraps <c>Nullable&lt;T&gt;</c>; returns <paramref name="type"/> otherwise.</summary>
    internal static Type Unwrap(Type type) => Nullable.GetUnderlyingType(type) ?? type;

    /// <summary>
    /// M-1 (G4-15-16, 17; G6 review): <see langword="true"/> for <see cref="string"/> and
    /// <see cref="Uri"/> (after unwrapping <c>Nullable&lt;T&gt;</c>). Both are rule-2 scalars
    /// for top-level values and collection-element <em>shape</em> purposes — they cannot
    /// carry a nested member — but their <em>contents</em> can still be a JWT, a bearer
    /// token, or userinfo/query that <see cref="SensitiveDataMaskingProcessor.MaskString"/>
    /// or <see cref="SensitiveDataMaskingProcessor.RenderUri"/> must process. Treating them
    /// as inert scalars inside a member or a collection element (as <see cref="IsScalar"/>
    /// does) let such content reach a passed-through record's compiler-generated
    /// <c>ToString()</c>, or a framework type's, unmasked.
    /// </summary>
    internal static bool IsShapeMaskableScalar(Type type)
    {
        var unwrapped = Unwrap(type);
        return unwrapped == typeof(string) || unwrapped == typeof(Uri);
    }

    /// <summary>
    /// Rule 2: values the processor lets through untouched, because they cannot carry a
    /// nested member and their <c>ToString()</c> reveals nothing the caller did not
    /// already put in the log call.
    /// </summary>
    internal static bool IsScalar(Type type) => ScalarCache.GetOrAdd(type, static t =>
        t == typeof(string)
        || t.IsPrimitive
        || t.IsEnum
        || t == typeof(decimal)
        || t == typeof(Guid)
        || t == typeof(DateTime)
        || t == typeof(DateTimeOffset)
        || t == typeof(DateOnly)
        || t == typeof(TimeOnly)
        || t == typeof(TimeSpan)
        || t == typeof(Uri)
        || t == typeof(Money)
        || t == typeof(Currency)
        || (t.IsValueType && t.Namespace?.StartsWith("NodaTime", StringComparison.Ordinal) == true));

    /// <summary>Rule 3: the type itself (or a base type) carries <see cref="SensitiveAttribute"/>.</summary>
    internal static bool IsSensitiveType(Type type) => SensitiveTypeCache.GetOrAdd(type, static t =>
    {
        try
        {
            return Attribute.IsDefined(t, typeof(SensitiveAttribute), inherit: true);
        }
        catch (Exception)
        {
            ObservabilityDiagnostics.RecordMaskingError("attribute");
            return true;
        }
    });

    /// <summary>Anything enumerable other than a string, for rule 4b.</summary>
    internal static bool IsEnumerable(Type type) =>
        type != typeof(string) && typeof(IEnumerable).IsAssignableFrom(type);

    /// <summary>
    /// Rule 4b: <see langword="true"/> only when every <c>IEnumerable&lt;T&gt;</c> this type
    /// implements has a rule 2 scalar element type (or a <c>KeyValuePair</c> of two of
    /// them). A non-generic <see cref="IEnumerable"/>, an <c>object</c> element, an
    /// interface element or a sequence of composite values is undetermined and is masked
    /// whole: a collection's own public properties (<c>Count</c>, <c>Capacity</c>) never
    /// expose its elements, so a graph walk would miss a <see cref="SensitiveAttribute"/>
    /// inside them and the sink's <c>ToString()</c> would print it.
    /// </summary>
    internal static bool HasScalarElements(Type type) => ScalarElementsCache.GetOrAdd(type, static t =>
    {
        try
        {
            var elementTypes = GetGenericEnumerableElementTypes(t);

            if (elementTypes.Length == 0)
            {
                return false;
            }

            foreach (var elementType in elementTypes)
            {
                if (!IsScalarElement(elementType))
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception)
        {
            ObservabilityDiagnostics.RecordMaskingError("collection");
            return false;
        }
    });

    /// <summary>
    /// M-1 (G4-15-16, 17; G6 review): <see langword="true"/> when this collection's element
    /// type (from the <c>IEnumerable&lt;T&gt;</c> interfaces it implements) is
    /// <see cref="IsShapeMaskableScalar"/>. <see cref="HasScalarElements"/> already lets such
    /// a collection's <em>shape</em> pass through unmasked-whole, but a <c>List&lt;string&gt;</c>
    /// or <c>List&lt;Uri&gt;</c> can still hold a JWT, a bearer token, or Uri userinfo/query
    /// per element, which needs the same <c>MaskString</c>/<c>RenderUri</c> treatment a
    /// top-level or member value gets. A <c>KeyValuePair&lt;TKey,TValue&gt;</c> collection
    /// (for example <c>Dictionary&lt;string,string&gt;</c>) is intentionally out of scope
    /// here — mask its entries only if a later call site actually logs one.
    /// </summary>
    internal static bool ElementsNeedShapeMasking(Type type) => ShapeMaskingElementsCache.GetOrAdd(type, static t =>
    {
        try
        {
            return GetGenericEnumerableElementTypes(t).Any(elementType => IsShapeMaskableScalar(Unwrap(elementType)));
        }
        catch (Exception)
        {
            ObservabilityDiagnostics.RecordMaskingError("collection");
            return false;
        }
    });

    private static Type[] GetGenericEnumerableElementTypes(Type type) =>
        type.GetInterfaces()
            .Concat(type.IsInterface ? [type] : Array.Empty<Type>())
            .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            .Select(i => i.GenericTypeArguments[0])
            .ToArray();

    private static bool IsScalarElement(Type elementType)
    {
        var element = Unwrap(elementType);

        if (IsSensitiveType(element))
        {
            return false;
        }

        if (IsScalar(element))
        {
            return true;
        }

        if (element.IsGenericType && element.GetGenericTypeDefinition() == typeof(KeyValuePair<,>))
        {
            return IsScalarElement(element.GenericTypeArguments[0])
                && IsScalarElement(element.GenericTypeArguments[1]);
        }

        return false;
    }

    /// <summary>
    /// Rule 4: the type's graph contains a <see cref="SensitiveAttribute"/> at any depth up
    /// to <see cref="MaxGraphDepth"/>, or cannot be fully determined (an <c>object</c>,
    /// interface, abstract or opaque-collection member). Such values are never handed to
    /// the sink's <c>ToString()</c>.
    /// </summary>
    internal static bool NeedsProcessorRendering(Type type) =>
        NeedsRenderingCache.GetOrAdd(type, static t => AnalyzeGraph(t, 0, []));

    /// <summary>
    /// Change 2 of the G3 threat model: the processor calls getters only on types it can
    /// reason about. Calling an arbitrary framework getter is a side effect
    /// (<c>HttpRequest.Form</c> reads the body synchronously, <c>HttpContext.Session</c>
    /// throws, an EF lazy-loading navigation issues a query) and can print a raw token
    /// held in a plain <c>string</c> property. Every other type that rule 4 selects is
    /// masked whole instead.
    /// </summary>
    internal static bool IsProcessorRenderable(Type type) => RenderableCache.GetOrAdd(type, static t =>
    {
        if (IsAnonymous(t) || IsTupleLike(t))
        {
            return true;
        }

        return t.Assembly.GetName().Name?.StartsWith("Decisya.", StringComparison.Ordinal) == true;
    });

    /// <summary>The public, readable, non-indexed instance properties rule 4 renders.</summary>
    internal static PropertyInfo[] GetRenderableProperties(Type type) =>
        RenderablePropertyCache.GetOrAdd(type, static t =>
        {
            try
            {
                return t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(p => p.CanRead && p.GetMethod is { IsPublic: true } && p.GetIndexParameters().Length == 0)
                    .ToArray();
            }
            catch (Exception)
            {
                ObservabilityDiagnostics.RecordMaskingError("members");
                return [];
            }
        });

    /// <summary>
    /// <see langword="true"/> when the member, a member it overrides, or the interface
    /// member it implements carries <see cref="SensitiveAttribute"/>, or when its runtime
    /// type does.
    /// </summary>
    /// <remarks>
    /// <see cref="Attribute.IsDefined(MemberInfo, Type, bool)"/> is used rather than
    /// <see cref="MemberInfo.IsDefined(Type, bool)"/>: the latter ignores its
    /// <c>inherit</c> argument for properties, so a derived record that overrides a
    /// <see cref="SensitiveAttribute"/> property without restating the attribute would lose
    /// the marking.
    /// </remarks>
    internal static bool IsSensitiveMember(PropertyInfo property) =>
        SensitiveMemberCache.GetOrAdd(property, static p =>
        {
            try
            {
                if (Attribute.IsDefined(p, typeof(SensitiveAttribute), inherit: true))
                {
                    return true;
                }

                var propertyType = Unwrap(p.PropertyType);
                return IsSensitiveType(propertyType) || IsSensitiveOnInterface(p);
            }
            catch (Exception)
            {
                ObservabilityDiagnostics.RecordMaskingError("attribute");
                return true;
            }
        });

    private static bool IsSensitiveOnInterface(PropertyInfo property)
    {
        var declaringType = property.DeclaringType;
        if (declaringType is null || declaringType.IsInterface || property.GetMethod is null)
        {
            return false;
        }

        foreach (var contract in declaringType.GetInterfaces())
        {
            InterfaceMapping map;
            try
            {
                map = declaringType.GetInterfaceMap(contract);
            }
            catch (Exception)
            {
                // Generic type definitions and a few reflection-only shapes refuse the map.
                // Fail closed for this interface only.
                ObservabilityDiagnostics.RecordMaskingError("interface_map");
                return true;
            }

            for (var i = 0; i < map.TargetMethods.Length; i++)
            {
                if (map.TargetMethods[i] != property.GetMethod)
                {
                    continue;
                }

                foreach (var candidate in contract.GetProperties())
                {
                    if (candidate.GetMethod == map.InterfaceMethods[i]
                        && Attribute.IsDefined(candidate, typeof(SensitiveAttribute), inherit: true))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static bool AnalyzeGraph(Type type, int depth, HashSet<Type> visiting)
    {
        if (depth > MaxGraphDepth)
        {
            return true;
        }

        if (!visiting.Add(type))
        {
            // A cycle: the type is already being analysed higher up the stack, and that
            // frame decides the answer.
            return false;
        }

        try
        {
            foreach (var property in GetRenderableProperties(type))
            {
                if (IsSensitiveMember(property) || IsDeniedMemberName(property.Name))
                {
                    return true;
                }

                if (MemberNeedsRendering(Unwrap(property.PropertyType), depth, visiting))
                {
                    return true;
                }
            }

            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (Attribute.IsDefined(field, typeof(SensitiveAttribute), inherit: true)
                    || IsSensitiveType(Unwrap(field.FieldType))
                    || IsDeniedMemberName(field.Name)
                    || MemberNeedsRendering(Unwrap(field.FieldType), depth, visiting))
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception)
        {
            ObservabilityDiagnostics.RecordMaskingError("graph");
            return true;
        }
        finally
        {
            visiting.Remove(type);
        }
    }

    private static bool MemberNeedsRendering(Type memberType, int depth, HashSet<Type> visiting)
    {
        // M-1 (G4-15-16, 17; G6 review): checked before the general IsScalar shortcut. A
        // string or Uri member can carry a JWT, a bearer token, or userinfo/query that
        // MaskString/RenderUri must process. Without this, a Decisya record whose members
        // are otherwise all rule-2 scalars (for example record R(string Note, Uri Callback))
        // got NeedsProcessorRendering == false and passed through rule 6 untouched, so its
        // compiler-generated ToString() printed Note and Callback raw. A framework type
        // (not Decisya-declared) with such a member is instead masked whole once this makes
        // NeedsProcessorRendering true for it (IsProcessorRenderable still rejects it) —
        // closing the AuthenticationHeaderValue ("Bearer <token>") case the same way.
        if (IsShapeMaskableScalar(memberType))
        {
            return true;
        }

        if (IsScalar(memberType))
        {
            return false;
        }

        if (memberType == typeof(object) || memberType.IsInterface || memberType.IsAbstract)
        {
            return true;
        }

        if (IsEnumerable(memberType))
        {
            return !HasScalarElements(memberType);
        }

        return IsSensitiveType(memberType) || AnalyzeGraph(memberType, depth + 1, visiting);
    }

    private static bool IsDeniedMemberName(string name) => SensitiveDataMaskingProcessor.IsDeniedKey(name);

    private static bool IsAnonymous(Type type) =>
        type.IsGenericType
        && Attribute.IsDefined(type, typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), inherit: false)
        && type.Name.Contains("AnonymousType", StringComparison.Ordinal);

    private static bool IsTupleLike(Type type)
    {
        if (!type.IsGenericType)
        {
            return false;
        }

        var definition = type.GetGenericTypeDefinition();
        return definition == typeof(KeyValuePair<,>)
            || definition.FullName?.StartsWith("System.ValueTuple`", StringComparison.Ordinal) == true
            || definition.FullName?.StartsWith("System.Tuple`", StringComparison.Ordinal) == true;
    }
}
