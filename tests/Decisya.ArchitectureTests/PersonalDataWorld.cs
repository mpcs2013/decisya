using System.Collections.Immutable;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Taint = System.Collections.Immutable.ImmutableHashSet<Decisya.ArchitectureTests.Elem>;

namespace Decisya.ArchitectureTests;

/// <summary>Where a personal value came from: a catalogue category (or the [Sensitive] marker) and a label.</summary>
internal sealed record TaintSource(string CategoryId, string Label);

/// <summary>
/// One element of a taint set. <see cref="Src"/> is a concrete source; <see cref="Param"/> is a symbolic parameter of
/// the method under analysis (its summary), with <see cref="From"/> the member of the argument that is meant ("" for
/// the whole argument). <see cref="Held"/> is the field of the carrying object the value is stored in ("" for the
/// value itself): this is the field-level taint of G3 G4-230-01.
/// </summary>
internal readonly record struct Elem(TaintSource? Src, int Param, string From, string Held)
{
    public static Elem Of(TaintSource source, string held = "") => new(source, -1, string.Empty, held);

    public static Elem OfParam(int index) => new(null, index, string.Empty, string.Empty);
}

/// <summary>A parameter (and member of it) that reaches a sink inside a method; the sink argument's static type.</summary>
internal sealed record SinkUse(Elem E, TypeReference? ArgType, string Sink);

/// <summary>What a call does, in terms of its arguments: its return value, what it stores into its arguments, the sinks it reaches.</summary>
internal sealed class Summary
{
    public static readonly Summary Empty = new();

    public Taint Return { get; set; } = Taint.Empty;

    public Dictionary<int, Taint> ArgOut { get; } = [];

    public HashSet<SinkUse> Uses { get; } = [];
}

internal sealed record RawFinding(string Site, string File, int Line, TaintSource Source, string Sink);

/// <summary>
/// The universe one run of <see cref="PersonalDataLogRule"/> works in: the assemblies read with Cecil (never loaded), a
/// resolver over the test host's trusted platform assemblies, the catalogue index, type and member facts, the summaries
/// and the findings.
/// </summary>
internal sealed class PersonalDataWorld : IDisposable
{
    internal const string SensitiveAttributeName = "Decisya.SharedKernel.Observability.SensitiveAttribute";
    internal const string ReviewedAttributeName = "Decisya.SharedKernel.Observability.PersonalDataLogReviewedAttribute";
    internal const string LoggerMessageAttributeName = "Microsoft.Extensions.Logging.LoggerMessageAttribute";
    internal const string GeneratedCodeAttributeName = "System.CodeDom.Compiler.GeneratedCodeAttribute";
    internal const string MarkedSensitiveId = "marked-sensitive";
    internal const string UserIdentifierId = "user-identifier";
    internal const int MinimumJustificationLength = 20;

    private readonly PersonalDataCatalogue _catalogue;
    private readonly Resolver _resolver = new();
    private readonly List<ModuleDefinition> _scope = [];
    private readonly HashSet<ModuleDefinition> _scopeSet = [];
    private readonly List<MethodDefinition> _methods = [];

    private readonly Dictionary<string, string> _memberCategory = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _memberNameCategory = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _typeCategory = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _claimCategory = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ClaimReaderEntry> _readers = new(StringComparer.Ordinal);
    private readonly HashSet<string> _maskers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _categoryName = new(StringComparer.Ordinal);

    private readonly Dictionary<string, Taint> _globals = new(StringComparer.Ordinal);
    private readonly Dictionary<MethodDefinition, Summary> _summaries = [];
    private readonly HashSet<MethodDefinition> _inProgress = [];
    private readonly Dictionary<string, RawFinding> _findings = new(StringComparer.Ordinal);
    private bool _globalsChanged;

    private readonly Dictionary<MethodReference, MethodDefinition?> _methodCache = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, TypeDefinition?> _typeCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<string>> _chainCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<TaintSource>> _unsanitizedCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _pathSensitiveCache = new(StringComparer.Ordinal);
    private readonly Dictionary<MethodDefinition, string> _getterPathCache = [];

    public PersonalDataWorld(IEnumerable<string> assemblyPaths, PersonalDataCatalogue catalogue)
    {
        _catalogue = catalogue;
        foreach (var path in assemblyPaths)
        {
            var assembly = AssemblyDefinition.ReadAssembly(
                path,
                new ReaderParameters
                {
                    ReadSymbols = true,
                    SymbolReaderProvider = new PortablePdbReaderProvider(),
                    AssemblyResolver = _resolver,
                    InMemory = true,
                });
            _resolver.Register(assembly);
            _scope.Add(assembly.MainModule);
            _scopeSet.Add(assembly.MainModule);
        }

        foreach (var method in _scope.SelectMany(module => module.GetTypes()).SelectMany(type => type.Methods))
        {
            _methods.Add(method);
        }

        BuildIndex();
        Stats = new PersonalDataStats { AssembliesRead = _scope.Count };
    }

    public PersonalDataStats Stats { get; private set; }

    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>Fails loudly, naming the method, instead of hanging the test host if the analysis ever fails to converge.</summary>
    internal void CheckBudget(string site)
    {
        if (_clock.Elapsed > TimeSpan.FromSeconds(90))
        {
            throw new InvalidOperationException($"PersonalDataLogRule exceeded its 90 s budget while analysing {site}.");
        }
    }

    public void Dispose() => _resolver.Dispose();

    // ---------------------------------------------------------------- the run

    public PersonalDataEvaluation Run()
    {
        for (var pass = 0; pass < 6; pass++)
        {
            _summaries.Clear();
            _findings.Clear();
            Stats = new PersonalDataStats { AssembliesRead = _scope.Count };
            _globalsChanged = false;

            foreach (var method in _methods.Where(m => m.HasBody))
            {
                GetSummary(method);
            }

            if (!_globalsChanged)
            {
                break;
            }
        }

        ScanLoggerMessageCallSites();

        var uses = CollectAttributeUses(out var problems);
        var valid = uses.Where(use => use.Valid).ToList();
        problems.AddRange(PinProblems(valid));

        var suppressions = valid
            .Select(use => new PersonalDataSuppression(use.Site, use.Category, use.Justification))
            .Distinct()
            .ToList();

        var findings = new List<PersonalDataFinding>();
        foreach (var raw in _findings.Values.OrderBy(f => f.File, StringComparer.Ordinal).ThenBy(f => f.Line))
        {
            if (valid.Any(use => string.Equals(use.Site, raw.Site, StringComparison.Ordinal)
                && string.Equals(use.Category, raw.Source.CategoryId, StringComparison.Ordinal)))
            {
                continue;
            }

            findings.Add(new PersonalDataFinding(
                raw.File, raw.Line, raw.Site, raw.Source.CategoryId, CategoryName(raw.Source.CategoryId), raw.Source.Label, raw.Sink));
        }

        return new PersonalDataEvaluation(findings, suppressions, problems, Stats);
    }

    internal Summary GetSummary(MethodDefinition method)
    {
        if (_summaries.TryGetValue(method, out var done))
        {
            return done;
        }

        if (!_inProgress.Add(method))
        {
            return Summary.Empty;
        }

        try
        {
            Summary summary;
            if (method.HasBody && !IsGeneratedLoggerBody(method))
            {
                Stats.MethodsAnalyzed++;
                summary = new MethodRun(this, method).Run();
            }
            else
            {
                summary = Summary.Empty;
            }

            _summaries[method] = summary;
            return summary;
        }
        finally
        {
            _inProgress.Remove(method);
        }
    }

    internal bool InScope(MethodDefinition method) => _scopeSet.Contains(method.Module);

    internal static bool IsGeneratedLoggerBody(MethodDefinition method) =>
        HasAttribute(method, LoggerMessageAttributeName) && HasAttribute(method, GeneratedCodeAttributeName);

    internal void AddFinding(RawFinding finding)
    {
        var key = $"{finding.Site}|{finding.File}|{finding.Line}|{finding.Sink}|{finding.Source.CategoryId}|{finding.Source.Label}";
        _findings.TryAdd(key, finding);
    }

    // ---------------------------------------------------------------- global field store (flow-insensitive)

    internal Taint GlobalGet(string key) => _globals.TryGetValue(key, out var taint) ? taint : Taint.Empty;

    internal void GlobalAdd(string key, Taint add)
    {
        var sources = add.Where(e => e.Src is not null).ToImmutableHashSet();
        if (sources.IsEmpty)
        {
            return;
        }

        var current = GlobalGet(key);
        var next = current.Union(sources);
        if (next.Count != current.Count)
        {
            _globals[key] = next;
            _globalsChanged = true;
        }
    }

    // ---------------------------------------------------------------- catalogue index

    private void BuildIndex()
    {
        foreach (var category in _catalogue.Categories)
        {
            _categoryName[category.Id] = category.Name;
            foreach (var member in category.Members)
            {
                _memberCategory.TryAdd(member, category.Id);
            }

            foreach (var name in category.MemberNames)
            {
                _memberNameCategory.TryAdd(name, category.Id);
            }

            foreach (var type in category.Types)
            {
                _typeCategory.TryAdd(type, category.Id);
            }

            foreach (var claim in category.ClaimTypes)
            {
                _claimCategory.TryAdd(claim, category.Id);
            }
        }

        foreach (var reader in _catalogue.ClaimReaders)
        {
            _readers.TryAdd(reader.Member, reader);
        }

        foreach (var masker in _catalogue.Maskers)
        {
            _maskers.Add(masker);
        }
    }

    internal string CategoryName(string id) =>
        _categoryName.TryGetValue(id, out var name) ? name : id == MarkedSensitiveId ? "Marked [Sensitive]" : id;

    internal string? CategoryOfClaimType(string claimType) => _claimCategory.GetValueOrDefault(claimType);

    internal bool IsMasker(MethodReference method) =>
        _maskers.Contains(Reflect(method.DeclaringType) + "::" + method.Name);

    internal bool TryGetReader(MethodReference method, out ClaimReaderEntry reader)
    {
        var declaring = Reflect(method.DeclaringType);
        var name = method.Name.StartsWith("get_", StringComparison.Ordinal) ? method.Name[4..] : method.Name;
        return _readers.TryGetValue(declaring + "::" + name, out reader!) || _readers.TryGetValue(declaring + "::" + method.Name, out reader!);
    }

    // ---------------------------------------------------------------- type and member facts

    internal static string Reflect(TypeReference type) => IlScan.ElementOf(type).FullName.Replace('/', '+');

    internal static string Norm(string fieldName) =>
        fieldName.StartsWith('<') && fieldName.EndsWith(">k__BackingField", StringComparison.Ordinal)
            ? fieldName[1..fieldName.IndexOf('>', StringComparison.Ordinal)]
            : fieldName;

    internal static bool HasAttribute(ICustomAttributeProvider provider, string fullName) =>
        provider.HasCustomAttributes && provider.CustomAttributes.Any(a => string.Equals(a.AttributeType.FullName, fullName, StringComparison.Ordinal));

    internal TypeDefinition? ResolveType(TypeReference? type)
    {
        if (type is null)
        {
            return null;
        }

        if (type is TypeDefinition definition)
        {
            return definition;
        }

        var key = type.FullName + "|" + type.Scope?.Name;
        if (_typeCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        TypeDefinition? resolved;
        try
        {
            resolved = type.Resolve();
        }
        catch (AssemblyResolutionException)
        {
            resolved = null;
        }
        catch (NotSupportedException)
        {
            resolved = null;
        }

        _typeCache[key] = resolved;
        return resolved;
    }

    internal MethodDefinition? ResolveMethod(MethodReference method)
    {
        if (method is MethodDefinition definition)
        {
            return definition;
        }

        if (_methodCache.TryGetValue(method, out var cached))
        {
            return cached;
        }

        MethodDefinition? resolved;
        try
        {
            resolved = method.Resolve();
        }
        catch (AssemblyResolutionException)
        {
            resolved = null;
        }
        catch (NotSupportedException)
        {
            resolved = null;
        }

        _methodCache[method] = resolved;
        return resolved;
    }

    /// <summary>The type without by-ref, modifiers and <c>Nullable&lt;T&gt;</c>.</summary>
    internal static TypeReference? Unwrap(TypeReference? type)
    {
        while (true)
        {
            switch (type)
            {
                case null:
                    return null;
                case ByReferenceType reference:
                    type = reference.ElementType;
                    continue;
                case PinnedType pinned:
                    type = pinned.ElementType;
                    continue;
                case IModifierType:
                    type = ((TypeSpecification)type).ElementType;
                    continue;
                case GenericInstanceType generic when string.Equals(generic.ElementType.FullName, "System.Nullable`1", StringComparison.Ordinal):
                    type = generic.GenericArguments[0];
                    continue;
                default:
                    return type;
            }
        }
    }

    /// <summary>The definition of a Decisya.* type (not compiler-generated, not a generated logger struct), else <see langword="null"/>.</summary>
    internal TypeDefinition? DecisyaDefinition(TypeReference? type)
    {
        var unwrapped = Unwrap(type);
        if (unwrapped is null or ArrayType or GenericParameter or FunctionPointerType)
        {
            return null;
        }

        var element = IlScan.ElementOf(unwrapped);
        if (!element.FullName.StartsWith("Decisya.", StringComparison.Ordinal))
        {
            return null;
        }

        var definition = ResolveType(element);
        return definition is null || IsGenerated(definition) ? null : definition;
    }

    internal static bool IsGenerated(TypeDefinition type) =>
        type.Name.StartsWith('<') || type.Name.StartsWith("__", StringComparison.Ordinal);

    internal static bool IsDecisyaName(TypeReference type) =>
        IlScan.ElementOf(type).FullName.StartsWith("Decisya.", StringComparison.Ordinal);

    /// <summary>A type that is, or derives from, an exception: its constructor arguments are a sink (S4).</summary>
    internal bool IsExceptionType(TypeReference type)
    {
        for (var current = ResolveType(type); current is not null; current = ResolveType(current.BaseType))
        {
            if (string.Equals(current.FullName, "System.Exception", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return IlScan.ElementOf(type).FullName.EndsWith("Exception", StringComparison.Ordinal);
    }

    internal static bool IsStaticField(FieldReference field)
    {
        try
        {
            return field.Resolve()?.IsStatic ?? false;
        }
        catch (AssemblyResolutionException)
        {
            return false;
        }
    }

    /// <summary><c>bool</c>, an enum, or a nullable of either: values that carry no personal text.</summary>
    internal bool IsNonPersonalKind(TypeReference? type)
    {
        var unwrapped = Unwrap(type);
        if (unwrapped is null or GenericParameter)
        {
            return false;
        }

        if (unwrapped.FullName is "System.Boolean" or "System.Void")
        {
            return true;
        }

        return unwrapped is not ArrayType && ResolveType(unwrapped) is { IsEnum: true };
    }

    internal bool TypeIsSensitive(TypeDefinition type)
    {
        for (var current = type; current is not null; current = ResolveType(current.BaseType))
        {
            if (HasAttribute(current, SensitiveAttributeName))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The field-level name a getter reads: the field it loads, else the property name.</summary>
    internal string MemberPathOfGetter(MethodReference getter, MethodDefinition? definition)
    {
        if (definition is null)
        {
            return getter.Name.Length > 4 ? getter.Name[4..] : getter.Name;
        }

        if (_getterPathCache.TryGetValue(definition, out var cached))
        {
            return cached;
        }

        var path = definition.Name.StartsWith("get_", StringComparison.Ordinal) ? definition.Name[4..] : definition.Name;
        if (definition.HasBody)
        {
            foreach (var instruction in definition.Body.Instructions)
            {
                if (instruction.OpCode == OpCodes.Ldfld
                    && instruction.Operand is FieldReference field
                    && string.Equals(field.DeclaringType.FullName, definition.DeclaringType.FullName, StringComparison.Ordinal))
                {
                    path = Norm(field.Name);
                    break;
                }
            }
        }

        _getterPathCache[definition] = path;
        return path;
    }

    private string PropertyPath(PropertyDefinition property) =>
        property.GetMethod is { } getter ? MemberPathOfGetter(getter, getter) : property.Name;

    /// <summary>Whether the member stored under <paramref name="path"/> in <paramref name="type"/> carries [Sensitive].</summary>
    internal bool PathIsSensitive(TypeDefinition type, string path)
    {
        var key = type.FullName + "|" + path;
        if (_pathSensitiveCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var result = false;
        foreach (var owner in TypesToCheck(type))
        {
            if (owner.Fields.Any(f => string.Equals(Norm(f.Name), path, StringComparison.Ordinal) && HasAttribute(f, SensitiveAttributeName))
                || owner.Properties.Any(p => HasAttribute(p, SensitiveAttributeName)
                    && (string.Equals(p.Name, path, StringComparison.Ordinal) || string.Equals(PropertyPath(p), path, StringComparison.Ordinal))))
            {
                result = true;
                break;
            }
        }

        _pathSensitiveCache[key] = result;
        return result;
    }

    private IEnumerable<TypeDefinition> TypesToCheck(TypeDefinition type)
    {
        for (var current = type; current is not null; current = ResolveType(current.BaseType))
        {
            yield return current;
        }

        foreach (var reference in AllInterfaces(type))
        {
            if (ResolveType(reference) is { } definition)
            {
                yield return definition;
            }
        }
    }

    internal IEnumerable<TypeReference> AllInterfaces(TypeDefinition type)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<TypeDefinition>();
        for (var current = type; current is not null; current = ResolveType(current.BaseType))
        {
            pending.Push(current);
        }

        while (pending.Count > 0)
        {
            var next = pending.Pop();
            foreach (var implementation in next.Interfaces)
            {
                if (seen.Add(implementation.InterfaceType.FullName))
                {
                    yield return implementation.InterfaceType;
                    if (ResolveType(IlScan.ElementOf(implementation.InterfaceType)) is { } inner)
                    {
                        pending.Push(inner);
                    }
                }
            }
        }
    }

    /// <summary>The type's own reflection name, its base types and its interfaces: an entry on any of them covers a member of the type.</summary>
    private IReadOnlyList<string> ChainNames(TypeReference type)
    {
        var element = IlScan.ElementOf(type);
        if (_chainCache.TryGetValue(element.FullName, out var cached))
        {
            return cached;
        }

        var names = new List<string> { Reflect(element) };
        if (ResolveType(element) is { } definition)
        {
            for (var current = ResolveType(definition.BaseType); current is not null; current = ResolveType(current.BaseType))
            {
                names.Add(Reflect(current));
            }

            names.AddRange(AllInterfaces(definition).Select(Reflect));
        }

        _chainCache[element.FullName] = names;
        return names;
    }

    // ---------------------------------------------------------------- sources

    internal TaintSource? SourceOfCall(MethodReference method)
    {
        if (method.Name.StartsWith("get_", StringComparison.Ordinal) && method.Name.Length > 4)
        {
            return SourceOfAccessor(method);
        }

        foreach (var name in ChainNames(method.DeclaringType))
        {
            if (_memberCategory.TryGetValue(name + "::" + method.Name, out var category))
            {
                return new TaintSource(category, name + "::" + method.Name);
            }
        }

        return null;
    }

    private TaintSource? SourceOfAccessor(MethodReference method)
    {
        var property = method.Name[4..];
        foreach (var name in ChainNames(method.DeclaringType))
        {
            if (_memberCategory.TryGetValue(name + "::" + property, out var category))
            {
                return new TaintSource(category, name + "::" + property);
            }
        }

        var label = Reflect(method.DeclaringType) + "::" + property;
        if (IsDecisyaName(method.DeclaringType)
            && _memberNameCategory.TryGetValue(property, out var byName)
            && !IsNonPersonalKind(method.ReturnType))
        {
            return new TaintSource(byName, label);
        }

        var definition = ResolveMethod(method);
        return definition is not null && IsSensitiveAccessor(definition) ? new TaintSource(MarkedSensitiveId, label) : null;
    }

    internal TaintSource? SourceOfField(FieldReference field)
    {
        var member = Norm(field.Name);
        foreach (var name in ChainNames(field.DeclaringType))
        {
            if (_memberCategory.TryGetValue(name + "::" + member, out var category))
            {
                return new TaintSource(category, name + "::" + member);
            }
        }

        var label = Reflect(field.DeclaringType) + "::" + member;
        if (IsDecisyaName(field.DeclaringType)
            && _memberNameCategory.TryGetValue(member, out var byName)
            && !IsNonPersonalKind(field.FieldType))
        {
            return new TaintSource(byName, label);
        }

        FieldDefinition? definition;
        try
        {
            definition = field.Resolve();
        }
        catch (AssemblyResolutionException)
        {
            definition = null;
        }

        if (definition is null)
        {
            return null;
        }

        var sensitive = HasAttribute(definition, SensitiveAttributeName)
            || definition.DeclaringType.Properties.Any(p => HasAttribute(p, SensitiveAttributeName) && string.Equals(p.Name, member, StringComparison.Ordinal));
        return sensitive ? new TaintSource(MarkedSensitiveId, label) : null;
    }

    private bool IsSensitiveAccessor(MethodDefinition getter)
    {
        var property = getter.DeclaringType.Properties.FirstOrDefault(p => p.GetMethod == getter);
        if (property is null)
        {
            return false;
        }

        if (HasAttribute(property, SensitiveAttributeName))
        {
            return true;
        }

        var backing = getter.DeclaringType.Fields.FirstOrDefault(f => string.Equals(f.Name, $"<{property.Name}>k__BackingField", StringComparison.Ordinal));
        if (backing is not null && HasAttribute(backing, SensitiveAttributeName))
        {
            return true;
        }

        return AllInterfaces(getter.DeclaringType)
            .Select(ResolveType)
            .OfType<TypeDefinition>()
            .Any(i => i.Properties.Any(p => string.Equals(p.Name, property.Name, StringComparison.Ordinal) && HasAttribute(p, SensitiveAttributeName)));
    }

    internal TaintSource? SourceOfType(TypeReference? type)
    {
        var unwrapped = Unwrap(type);
        if (unwrapped is null or GenericParameter or ArrayType)
        {
            return null;
        }

        var name = Reflect(unwrapped);
        return _typeCategory.TryGetValue(name, out var category) ? new TaintSource(category, name) : null;
    }

    /// <summary>
    /// The personal-data members of a Decisya type (to depth 3) that do not carry [Sensitive]: the type-based half of the
    /// object rule. A type carrying [Sensitive] has none.
    /// </summary>
    internal IReadOnlyList<TaintSource> UnsanitizedMembers(TypeDefinition type, int depth = 0, HashSet<string>? visited = null)
    {
        if (depth == 0 && _unsanitizedCache.TryGetValue(type.FullName, out var cached))
        {
            return cached;
        }

        visited ??= new HashSet<string>(StringComparer.Ordinal);
        var result = new List<TaintSource>();
        if (depth < 3 && visited.Add(type.FullName) && !TypeIsSensitive(type))
        {
            for (var current = type; current is not null && IsDecisyaName(current); current = ResolveType(current.BaseType))
            {
                foreach (var property in current.Properties)
                {
                    var sensitive = HasAttribute(property, SensitiveAttributeName)
                        || current.Fields.Any(f => string.Equals(f.Name, $"<{property.Name}>k__BackingField", StringComparison.Ordinal) && HasAttribute(f, SensitiveAttributeName));
                    if (sensitive)
                    {
                        continue;
                    }

                    var key = Reflect(current) + "::" + property.Name;
                    var personal = _memberCategory.TryGetValue(key, out var category)
                        || (_memberNameCategory.TryGetValue(property.Name, out category) && !IsNonPersonalKind(property.PropertyType));
                    if (personal)
                    {
                        result.Add(new TaintSource(category!, key));
                    }
                    else if (DecisyaDefinition(property.PropertyType) is { } nested)
                    {
                        result.AddRange(UnsanitizedMembers(nested, depth + 1, visited));
                    }
                }
            }
        }

        if (depth == 0)
        {
            _unsanitizedCache[type.FullName] = result;
        }

        return result;
    }

    // ---------------------------------------------------------------- repository-relative paths (G3 S-02)

    internal static string RelativePath(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return "unknown";
        }

        var path = url.Replace('\\', '/');
        if (path.StartsWith("/_/", StringComparison.Ordinal))
        {
            return path[3..];
        }

        // Both '/' and '\' are separators on every OS: a PDB built on the other OS must not leak its user name.
        var driveLetter = path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '/';
        var rooted = driveLetter || path.StartsWith('/');
        if (!rooted)
        {
            return path;
        }

        if (!driveLetter)
        {
            path = Path.GetFullPath(path).Replace('\\', '/');
        }
        else if (Path.IsPathRooted(url))
        {
            path = Path.GetFullPath(url).Replace('\\', '/');
        }

        var root = Path.GetFullPath(RepoPaths.Find(string.Empty)).Replace('\\', '/').TrimEnd('/') + "/";
        var rootIsDrive = root.Length >= 3 && char.IsAsciiLetter(root[0]) && root[1] == ':';
        var comparison = driveLetter && rootIsDrive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (path.StartsWith(root, comparison))
        {
            return path[root.Length..];
        }

        return path[(path.LastIndexOf('/') + 1)..];
    }

    // ---------------------------------------------------------------- [PersonalDataLogReviewed]

    internal sealed record AttributeUse(string Site, string Category, string Justification, bool Valid);

    private List<AttributeUse> CollectAttributeUses(out List<string> problems)
    {
        problems = [];
        var uses = new List<AttributeUse>();
        foreach (var method in _methods)
        {
            if (!method.HasCustomAttributes)
            {
                continue;
            }

            var site = Reflect(method.DeclaringType) + "::" + method.Name;
            foreach (var attribute in method.CustomAttributes.Where(a => string.Equals(a.AttributeType.FullName, ReviewedAttributeName, StringComparison.Ordinal)))
            {
                var category = attribute.ConstructorArguments.Count > 0 ? attribute.ConstructorArguments[0].Value as string ?? string.Empty : string.Empty;
                var justification = attribute.ConstructorArguments.Count > 1 ? attribute.ConstructorArguments[1].Value as string ?? string.Empty : string.Empty;
                var valid = true;

                if (string.IsNullOrWhiteSpace(justification))
                {
                    problems.Add($"{site}: a [PersonalDataLogReviewed] use has a blank justification.");
                    valid = false;
                }
                else if (justification.Trim().Length < MinimumJustificationLength)
                {
                    problems.Add($"{site}: a [PersonalDataLogReviewed] use has a justification under {MinimumJustificationLength} characters.");
                    valid = false;
                }

                var known = _catalogue.Categories.FirstOrDefault(c => string.Equals(c.Id, category, StringComparison.Ordinal));
                if (known is null)
                {
                    problems.Add($"{site}: a [PersonalDataLogReviewed] use names an unknown category id.");
                    valid = false;
                }
                else if (!known.Suppressible)
                {
                    problems.Add($"{site}: a [PersonalDataLogReviewed] use names category '{category}', which can never be suppressed.");
                    valid = false;
                }

                uses.Add(new AttributeUse(site, category, justification, valid));
            }
        }

        return uses;
    }

    private IEnumerable<string> PinProblems(List<AttributeUse> valid)
    {
        foreach (var use in valid.Where(u => !_catalogue.Suppressions.Any(s => Matches(s, u))))
        {
            yield return $"{use.Site}: [PersonalDataLogReviewed(\"{use.Category}\", ...)] has no matching entry in the catalogue's suppressions (site, category and justification must all match).";
        }

        foreach (var entry in _catalogue.Suppressions)
        {
            var category = _catalogue.Categories.FirstOrDefault(c => string.Equals(c.Id, entry.Category, StringComparison.Ordinal));
            if (category is null)
            {
                yield return $"Catalogue suppression for {entry.Site} names an unknown category id.";
            }
            else if (!category.Suppressible)
            {
                yield return $"Catalogue suppression for {entry.Site} names category '{entry.Category}', which can never be suppressed.";
            }

            if (!valid.Any(u => Matches(entry, u)))
            {
                yield return $"Catalogue suppression for {entry.Site} ({entry.Category}) has no matching [PersonalDataLogReviewed] use (stale).";
            }
        }
    }

    private static bool Matches(SuppressionEntry entry, AttributeUse use) =>
        string.Equals(entry.Site, use.Site, StringComparison.Ordinal)
        && string.Equals(entry.Category, use.Category, StringComparison.Ordinal)
        && string.Equals(entry.Justification, use.Justification, StringComparison.Ordinal);

    // ---------------------------------------------------------------- non-vacuity: a plain scan for [LoggerMessage] call sites

    private void ScanLoggerMessageCallSites()
    {
        foreach (var method in _methods.Where(m => m.HasBody && !IsGeneratedLoggerBody(m)))
        {
            foreach (var instruction in method.Body.Instructions)
            {
                if (instruction.OpCode != OpCodes.Call && instruction.OpCode != OpCodes.Callvirt)
                {
                    continue;
                }

                if (IlScan.MethodOperand(instruction.Operand) is { } reference
                    && ResolveMethod(reference) is { } target
                    && HasAttribute(target, LoggerMessageAttributeName))
                {
                    Stats.LoggerMessageCallSitesInIl.Add(MethodRun.SiteOf(method) + " -> " + Reflect(reference.DeclaringType) + "::" + reference.Name);
                }
            }
        }
    }

    // ---------------------------------------------------------------- R5: the catalogue's entries exist

    public IReadOnlyList<string> CheckCatalogue(out IReadOnlyList<string> notReferenced)
    {
        var problems = new List<string>();
        var notFound = new List<string>();
        var closure = BuildClosure();
        var namespaces = new Dictionary<ModuleDefinition, HashSet<string>>();

        var ids = _catalogue.Categories.Select(c => c.Id).ToList();
        foreach (var duplicate in ids.GroupBy(id => id, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            problems.Add($"Category id '{duplicate.Key}' is not unique.");
        }

        foreach (var category in _catalogue.Categories)
        {
            foreach (var member in category.Members)
            {
                CheckMemberEntry($"{category.Id}.members", member, closure, namespaces, problems, notFound);
            }

            foreach (var type in category.Types)
            {
                CheckTypeEntry($"{category.Id}.types", type, closure, namespaces, problems, notFound);
            }
        }

        foreach (var masker in _catalogue.Maskers)
        {
            CheckMemberEntry("maskers", masker, closure, namespaces, problems, notFound);
        }

        foreach (var type in _catalogue.RedactingTypes)
        {
            CheckTypeEntry("redactingTypes", type, closure, namespaces, problems, notFound);
            if (FindType(type, closure) is { } definition
                && !TypeIsSensitive(definition)
                && !definition.Methods.Any(m => string.Equals(m.Name, "ToString", StringComparison.Ordinal) && m.Parameters.Count == 0))
            {
                problems.Add($"redactingTypes: '{type}' neither carries [Sensitive] nor declares its own ToString.");
            }
        }

        foreach (var reader in _catalogue.ClaimReaders)
        {
            CheckMemberEntry("claimReaders", reader.Member, closure, namespaces, problems, notFound);
            if (!ids.Contains(reader.FallbackCategory, StringComparer.Ordinal))
            {
                problems.Add($"claimReaders: '{reader.Member}' names an unknown fallbackCategory.");
            }

            if (reader.NameParameter is not null && TrySplit(reader.Member, out var typeName, out var memberName)
                && FindType(typeName, closure) is { } owner)
            {
                var methods = owner.Methods.Where(m => string.Equals(m.Name, memberName, StringComparison.Ordinal)).ToList();
                if (methods.Count > 0 && !methods.Any(m => m.Parameters.Any(p => string.Equals(p.Name, reader.NameParameter, StringComparison.Ordinal))))
                {
                    problems.Add($"claimReaders: no overload of '{reader.Member}' has a parameter named '{reader.NameParameter}'.");
                }
            }
        }

        foreach (var suppression in _catalogue.Suppressions)
        {
            if (!ids.Contains(suppression.Category, StringComparer.Ordinal))
            {
                problems.Add($"suppressions: '{suppression.Site}' names an unknown category id.");
            }
        }

        notReferenced = notFound;
        return problems;
    }

    private static bool TrySplit(string entry, out string typeName, out string memberName)
    {
        var index = entry.IndexOf("::", StringComparison.Ordinal);
        if (index <= 0 || index + 2 >= entry.Length || entry.Contains('*', StringComparison.Ordinal))
        {
            typeName = memberName = string.Empty;
            return false;
        }

        typeName = entry[..index];
        memberName = entry[(index + 2)..];
        return true;
    }

    private static void CheckMemberEntry(
        string list,
        string entry,
        List<ModuleDefinition> closure,
        Dictionary<ModuleDefinition, HashSet<string>> namespaces,
        List<string> problems,
        List<string> notFound)
    {
        if (!TrySplit(entry, out var typeName, out var memberName))
        {
            problems.Add($"{list}: '{entry}' is not Namespace.Type::Member (a namespace or a wildcard is not accepted).");
            return;
        }

        if (FindType(typeName, closure) is { } definition)
        {
            if (!HasMember(definition, memberName))
            {
                problems.Add($"{list}: '{entry}' names a member that does not exist.");
            }

            return;
        }

        ReportMissingType(list, entry, typeName, closure, namespaces, problems, notFound);
    }

    private static void CheckTypeEntry(
        string list,
        string entry,
        List<ModuleDefinition> closure,
        Dictionary<ModuleDefinition, HashSet<string>> namespaces,
        List<string> problems,
        List<string> notFound)
    {
        if (entry.Contains('*', StringComparison.Ordinal) || entry.Contains("::", StringComparison.Ordinal))
        {
            problems.Add($"{list}: '{entry}' is not a full type name.");
            return;
        }

        if (FindType(entry, closure) is null)
        {
            ReportMissingType(list, entry, entry, closure, namespaces, problems, notFound);
        }
    }

    private static void ReportMissingType(
        string list,
        string entry,
        string typeName,
        List<ModuleDefinition> closure,
        Dictionary<ModuleDefinition, HashSet<string>> namespaces,
        List<string> problems,
        List<string> notFound)
    {
        if (typeName.StartsWith("Decisya.", StringComparison.Ordinal))
        {
            problems.Add($"{list}: '{entry}' names a type that does not exist in LogScope.");
            return;
        }

        var lastDot = typeName.LastIndexOf('.');
        var ns = lastDot > 0 ? typeName[..lastDot] : string.Empty;
        var declared = closure.Any(module =>
        {
            if (!namespaces.TryGetValue(module, out var set))
            {
                set = [.. module.Types.Select(t => t.Namespace), .. module.ExportedTypes.Select(t => t.Namespace)];
                namespaces[module] = set;
            }

            return set.Contains(ns);
        });

        if (declared)
        {
            problems.Add($"{list}: '{entry}' names a type that does not exist in a referenced assembly.");
        }
        else
        {
            notFound.Add($"{list}: {entry}");
        }
    }

    private static bool HasMember(TypeDefinition type, string member) =>
        type.Methods.Any(m => string.Equals(m.Name, member, StringComparison.Ordinal))
        || type.Properties.Any(p => string.Equals(p.Name, member, StringComparison.Ordinal))
        || type.Fields.Any(f => string.Equals(f.Name, member, StringComparison.Ordinal));

    private static TypeDefinition? FindType(string reflectionName, List<ModuleDefinition> closure)
    {
        var cecilName = reflectionName.Replace('+', '/');
        foreach (var module in closure)
        {
            var found = module.GetType(cecilName);
            if (found is not null)
            {
                return found;
            }
        }

        foreach (var module in closure)
        {
            var exported = module.ExportedTypes.FirstOrDefault(t => string.Equals(t.FullName, cecilName, StringComparison.Ordinal));
            if (exported is not null)
            {
                try
                {
                    var resolved = exported.Resolve();
                    if (resolved is not null)
                    {
                        return resolved;
                    }
                }
                catch (AssemblyResolutionException)
                {
                    // Not resolvable here: keep looking.
                }
            }
        }

        return null;
    }

    /// <summary>The scope and every assembly it references, transitively, resolved with the same resolver.</summary>
    private List<ModuleDefinition> BuildClosure()
    {
        var closure = new List<ModuleDefinition>(_scope);
        var seen = new HashSet<string>(_scope.Select(m => m.Assembly.Name.Name), StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<ModuleDefinition>(_scope);
        while (queue.Count > 0)
        {
            var module = queue.Dequeue();
            foreach (var reference in module.AssemblyReferences)
            {
                if (!seen.Add(reference.Name))
                {
                    continue;
                }

                try
                {
                    var assembly = _resolver.Resolve(reference);
                    closure.Add(assembly.MainModule);
                    queue.Enqueue(assembly.MainModule);
                }
                catch (AssemblyResolutionException)
                {
                    // An assembly the host cannot resolve is simply not in the closure.
                }
            }
        }

        return closure;
    }

    // ---------------------------------------------------------------- resolver

    private sealed class Resolver : IAssemblyResolver
    {
        private readonly Dictionary<string, AssemblyDefinition> _byName = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _trusted = new(StringComparer.OrdinalIgnoreCase);

        public Resolver()
        {
            if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string list)
            {
                foreach (var path in list.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                {
                    _trusted.TryAdd(Path.GetFileNameWithoutExtension(path), path);
                }
            }
        }

        public void Register(AssemblyDefinition assembly) => _byName[assembly.Name.Name] = assembly;

        public AssemblyDefinition Resolve(AssemblyNameReference name) => Resolve(name, new ReaderParameters());

        public AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters parameters)
        {
            if (_byName.TryGetValue(name.Name, out var found))
            {
                return found;
            }

            if (_trusted.TryGetValue(name.Name, out var path))
            {
                var assembly = AssemblyDefinition.ReadAssembly(path, new ReaderParameters { AssemblyResolver = this });
                _byName[name.Name] = assembly;
                return assembly;
            }

            throw new AssemblyResolutionException(name);
        }

        public void Dispose()
        {
            foreach (var assembly in _byName.Values)
            {
                assembly.Dispose();
            }

            _byName.Clear();
        }
    }
}
