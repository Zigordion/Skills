#:package Microsoft.CodeAnalysis.CSharp@5.0.0
#:property Nullable=enable
#:property PublishAot=false

// Compares the C# types in the changed files of the current branch with the same files at the merge base.
// The tool parses syntax only. It does not compile the solution, so it is fast on large code bases.
// Run: dotnet run BranchDiff.cs -- --base origin/main --out branch-diff.json

using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

ToolOptions options;

try
{
    options = ToolOptions.Parse(args);
}
catch (ArgumentException exception)
{
    Console.Error.WriteLine(exception.Message);
    Console.Error.WriteLine(ToolOptions.Usage);
    return 2;
}

try
{
    BranchDiffTool tool = new(options);
    tool.Run();
    return 0;
}
catch (GitException exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}
catch (Win32Exception)
{
    Console.Error.WriteLine("Git was not found. Make sure git.exe is on PATH.");
    return 1;
}

/// <summary>
/// Contains the command line options of the tool.
/// </summary>
internal sealed class ToolOptions
{
    /// <summary>
    /// Gets the usage text that the tool shows when an argument is not valid.
    /// </summary>
    public const string Usage =
        "Usage: dotnet run BranchDiff.cs -- [--base <branch-or-commit>] [--out <file>] [--repo <folder>] [--include-external] [--committed-only]";

    /// <summary>
    /// Gets the branch or commit to compare with. If the value is <see langword="null"/>, the tool finds the default branch.
    /// </summary>
    public string? BaseRef { get; private set; }

    /// <summary>
    /// Gets the path of the JSON output file. If the value is <see langword="null"/>, the tool writes to the standard output.
    /// </summary>
    public string? OutputPath { get; private set; }

    /// <summary>
    /// Gets a folder in the Git repository.
    /// </summary>
    public string RepositoryPath { get; private set; } = Directory.GetCurrentDirectory();

    /// <summary>
    /// Gets a value that tells if the tool keeps dependencies to types that are not declared in the repository.
    /// </summary>
    public bool IncludeExternal { get; private set; }

    /// <summary>
    /// Gets a value that tells if the tool ignores changes that are not committed.
    /// </summary>
    public bool CommittedOnly { get; private set; }

    /// <summary>
    /// Reads the command line arguments.
    /// </summary>
    /// <param name="args">The command line arguments.</param>
    /// <returns>The options that the arguments specify.</returns>
    /// <exception cref="ArgumentException">An argument is not known or does not have a value.</exception>
    public static ToolOptions Parse(string[] args)
    {
        ToolOptions options = new();

        for (int index = 0; index < args.Length; index++)
        {
            string argument = args[index];

            switch (argument)
            {
                case "--base":
                    options.BaseRef = ReadValue(args, ref index, argument);
                    break;
                case "--out":
                    options.OutputPath = ReadValue(args, ref index, argument);
                    break;
                case "--repo":
                    options.RepositoryPath = ReadValue(args, ref index, argument);
                    break;
                case "--include-external":
                    options.IncludeExternal = true;
                    break;
                case "--committed-only":
                    options.CommittedOnly = true;
                    break;
                default:
                    throw new ArgumentException($"The argument '{argument}' is not known.");
            }
        }

        return options;
    }

    private static string ReadValue(string[] args, ref int index, string name)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"The argument '{name}' must have a value.");
        }

        index++;
        return args[index];
    }
}

/// <summary>
/// Shows that a Git command failed.
/// </summary>
/// <param name="message">The error message.</param>
internal sealed class GitException(string message) : Exception(message);

/// <summary>
/// Runs Git commands in one folder.
/// </summary>
/// <param name="workingDirectory">The folder where Git runs.</param>
internal sealed class GitClient(string workingDirectory)
{
    private readonly string _workingDirectory = workingDirectory;

    /// <summary>
    /// Runs a Git command and returns the standard output.
    /// </summary>
    /// <param name="arguments">The arguments for Git.</param>
    /// <returns>The standard output of the command.</returns>
    /// <exception cref="GitException">The command returned an exit code that is not 0.</exception>
    public string Run(params string[] arguments)
    {
        if (!TryRun(out string output, out string error, arguments))
        {
            throw new GitException($"git {string.Join(' ', arguments)} failed: {error.Trim()}");
        }

        return output;
    }

    /// <summary>
    /// Runs a Git command. The method does not throw an exception if the command fails.
    /// </summary>
    /// <param name="output">The standard output of the command.</param>
    /// <param name="arguments">The arguments for Git.</param>
    /// <returns><see langword="true"/> if the exit code is 0. Otherwise, <see langword="false"/>.</returns>
    public bool TryRun(out string output, params string[] arguments) => TryRun(out output, out string _, arguments);

    private bool TryRun(out string output, out string error, string[] arguments)
    {
        ProcessStartInfo startInfo = new("git")
        {
            WorkingDirectory = _workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo) ?? throw new GitException("Git did not start.");

        // Read the error stream at the same time as the output stream. This prevents a deadlock when a buffer is full.
        Task<string> errorTask = process.StandardError.ReadToEndAsync();
        output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        error = errorTask.GetAwaiter().GetResult();

        return process.ExitCode == 0;
    }
}

/// <summary>
/// Contains one file that changed between the merge base and the branch.
/// </summary>
/// <param name="Status">The change: A (added), M (modified), D (deleted) or R (renamed).</param>
/// <param name="Path">The path of the file relative to the repository root.</param>
/// <param name="OldPath">The path at the merge base, if the file was renamed.</param>
internal sealed record ChangedFile(string Status, string Path, string? OldPath);

/// <summary>
/// Contains one dependency from a type to another type.
/// </summary>
/// <param name="Source">The key of the type that has the dependency.</param>
/// <param name="Target">The key of the type that the source depends on.</param>
/// <param name="Kind">The kind of dependency: inherits, implements, has, injects, uses, creates or registers.</param>
internal readonly record struct EdgeModel(string Source, string Target, string Kind);

/// <summary>
/// Contains one member of a type.
/// </summary>
/// <param name="signature">The signature that identifies the member in the type.</param>
/// <param name="kind">The kind of member, for example method or property.</param>
/// <param name="visibility">The visibility of the member.</param>
/// <param name="location">The file and line of the member.</param>
internal sealed class MemberModel(string signature, string kind, string visibility, string location)
{
    /// <summary>
    /// Gets the signature that identifies the member in the type.
    /// </summary>
    public string Signature { get; } = signature;

    /// <summary>
    /// Gets the kind of member.
    /// </summary>
    public string Kind { get; } = kind;

    /// <summary>
    /// Gets the visibility of the member.
    /// </summary>
    public string Visibility { get; } = visibility;

    /// <summary>
    /// Gets the file and line of the member.
    /// </summary>
    public string Location { get; } = location;

    /// <summary>
    /// Gets the modifiers that are important for a class diagram, for example static or abstract.
    /// </summary>
    public SortedSet<string> Modifiers { get; } = [];

    /// <summary>
    /// Gets or sets the tokens of the member without comments and white space.
    /// </summary>
    public string Tokens { get; set; } = string.Empty;
}

/// <summary>
/// Contains one type declaration. Partial declarations with the same key are merged.
/// </summary>
/// <param name="key">The name of the type and the generic arity, for example <c>Repository&lt;&gt;</c>.</param>
/// <param name="display">The name of the type that a diagram shows.</param>
/// <param name="kind">The kind of type, for example class or interface.</param>
internal sealed class TypeModel(string key, string display, string kind)
{
    /// <summary>
    /// Gets the key of the type.
    /// </summary>
    public string Key { get; } = key;

    /// <summary>
    /// Gets the name of the type that a diagram shows.
    /// </summary>
    public string Display { get; } = display;

    /// <summary>
    /// Gets the kind of type.
    /// </summary>
    public string Kind { get; } = kind;

    /// <summary>
    /// Gets or sets the visibility of the type.
    /// </summary>
    public string Visibility { get; set; } = "internal";

    /// <summary>
    /// Gets the namespaces where the type is declared.
    /// </summary>
    public SortedSet<string> Namespaces { get; } = [];

    /// <summary>
    /// Gets the modifiers of the type, for example abstract or static.
    /// </summary>
    public SortedSet<string> Modifiers { get; } = [];

    /// <summary>
    /// Gets the files where the type is declared.
    /// </summary>
    public SortedSet<string> Files { get; } = [];

    /// <summary>
    /// Gets the members of the type. The key is <see cref="MemberModel.Signature"/>.
    /// </summary>
    public Dictionary<string, MemberModel> Members { get; } = [];

    /// <summary>
    /// Gets the dependencies of the type.
    /// </summary>
    public HashSet<EdgeModel> Edges { get; } = [];
}

/// <summary>
/// Finds the names of all types that are declared in the repository.
/// </summary>
internal static partial class TypeNameIndex
{
    /// <summary>
    /// Finds type declarations in source text. The pattern is fast and approximate. A false match only adds a name to the index.
    /// </summary>
    [GeneratedRegex(@"\b(class|interface|struct|enum|record)\s+(?:class\s+|struct\s+)?@?([A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex DeclarationPattern();

    /// <summary>
    /// Adds the type names that are declared in a source text to the index.
    /// </summary>
    /// <param name="text">The source text.</param>
    /// <param name="typeNames">The names of all declared types.</param>
    /// <param name="interfaceNames">The names of the declared interfaces.</param>
    public static void AddDeclarations(string text, HashSet<string> typeNames, HashSet<string> interfaceNames)
    {
        foreach (Match match in DeclarationPattern().Matches(text))
        {
            string name = match.Groups[2].Value;
            typeNames.Add(name);

            if (match.Groups[1].Value == "interface")
            {
                interfaceNames.Add(name);
            }
        }
    }
}

/// <summary>
/// Finds the implementation classes in dependency injection registrations.
/// </summary>
/// <remarks>
/// The parser reads syntax only. It finds the common registration methods of Microsoft.Extensions.DependencyInjection, Simple Injector and Autofac.
/// It does not find registrations by assembly scanning, by convention or through variables.
/// </remarks>
internal static partial class RegistrationParser
{
    /// <summary>
    /// Finds registration methods, for example AddScoped, TryAddSingleton, AddKeyedTransient, AddHostedService, RegisterSingleton,
    /// RegisterConditional or RegisterType.
    /// </summary>
    [GeneratedRegex(
        @"^(?:(?:Try)?Add(?:Keyed)?(?:Singleton|Scoped|Transient)|TryAddEnumerable|AddHostedService|AddHttpClient|AddDbContext(?:Pool|Factory)?|Register(?:Singleton|Scoped|Transient|Conditional|Decorator|Instance|Type)?)$")]
    private static partial Regex RegistrationMethodPattern();

    /// <summary>
    /// Finds the methods of the Simple Injector <c>Container.Collection</c> property that add one class to a collection.
    /// </summary>
    [GeneratedRegex(@"^(?:Append|Register)$")]
    private static partial Regex CollectionMethodPattern();

    /// <summary>
    /// Finds the factory methods of <c>ServiceDescriptor</c>, for example <c>ServiceDescriptor.Scoped</c>.
    /// </summary>
    [GeneratedRegex(@"^(?:Keyed)?(?:Singleton|Scoped|Transient)$")]
    private static partial Regex DescriptorMethodPattern();

    private static readonly HashSet<string> SingleTypeMethods = ["AddHostedService", "AddHttpClient", "AddDbContext", "AddDbContextPool", "AddDbContextFactory", "RegisterType"];

    /// <summary>
    /// Returns the implementation types that an invocation registers. The result is empty if the invocation is not a known registration.
    /// </summary>
    /// <param name="invocation">The invocation to examine.</param>
    /// <param name="isInterfaceName">Returns <see langword="true"/> if a type name is the name of an interface.</param>
    /// <returns>The implementation types. The service type, for example an interface, is not included.</returns>
    public static IEnumerable<TypeSyntax> FindImplementationTypes(InvocationExpressionSyntax invocation, Func<string, bool> isInterfaceName)
    {
        SimpleNameSyntax? name = invocation.Expression switch
        {
            MemberAccessExpressionSyntax memberAccess => memberAccess.Name,
            SimpleNameSyntax simpleName => simpleName,
            _ => null,
        };

        if (name is null || !IsRegistration(invocation, name))
        {
            return [];
        }

        SeparatedSyntaxList<ArgumentSyntax> arguments = invocation.ArgumentList.Arguments;

        if (name is GenericNameSyntax generic)
        {
            SeparatedSyntaxList<TypeSyntax> typeArguments = generic.TypeArgumentList.Arguments;

            // AddScoped<IService, Implementation>() and AddHttpClient<IClient, Client>() register the second type.
            if (typeArguments.Count >= 2)
            {
                return [typeArguments[1]];
            }

            // Container.Collection.Register<IPlugin>(typeof(PluginA), typeof(PluginB)) registers every typeof type.
            List<TypeSyntax> typeOfTypes = TypeOfTypes(arguments);

            if (typeOfTypes.Count > 0)
            {
                return typeOfTypes;
            }

            // AddScoped<IService>(provider => new Implementation(...)) registers the class that the factory creates.
            List<TypeSyntax> created = CreatedTypes(arguments);

            if (created.Count > 0)
            {
                return created;
            }

            // AddScoped<Implementation>() registers the class itself. AddSingleton<IService>(instance) has no class in the syntax.
            if (arguments.Count == 0 || SingleTypeMethods.Contains(generic.Identifier.ValueText))
            {
                return [typeArguments[0]];
            }

            return [];
        }

        // Register(typeof(IService), typeof(Implementation)) registers the last typeof type.
        List<TypeSyntax> types = TypeOfTypes(arguments);

        if (types.Count >= 2)
        {
            return [types[^1]];
        }

        // AddSingleton(new Implementation()) and RegisterInstance(new Implementation()) register the created class.
        // A plain Register call without typeof is skipped, because CancellationToken.Register(() => ...) has the same syntax.
        if (name.Identifier.ValueText != "Register")
        {
            List<TypeSyntax> createdTypes = CreatedTypes(arguments);

            if (createdTypes.Count > 0)
            {
                return createdTypes;
            }
        }

        // AddScoped(typeof(Implementation)) registers the class itself.
        // Register(typeof(IHandler<>), assemblies) is a batch registration. The implementation classes are not in the syntax.
        if (types.Count == 1 && !isInterfaceName(NameOf(types[0])))
        {
            return [types[0]];
        }

        return [];
    }

    private static List<TypeSyntax> TypeOfTypes(SeparatedSyntaxList<ArgumentSyntax> arguments) =>
        [.. arguments.Select(argument => argument.Expression).OfType<TypeOfExpressionSyntax>().Select(typeOf => typeOf.Type)];

    private static string NameOf(TypeSyntax type) => type switch
    {
        QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
        AliasQualifiedNameSyntax aliasQualified => aliasQualified.Name.Identifier.ValueText,
        SimpleNameSyntax simple => simple.Identifier.ValueText,
        _ => type.ToString(),
    };

    private static bool IsRegistration(InvocationExpressionSyntax invocation, SimpleNameSyntax name)
    {
        string method = name.Identifier.ValueText;

        if (RegistrationMethodPattern().IsMatch(method))
        {
            return true;
        }

        // Container.Collection.Append<IPlugin, PluginA>(). Append on other objects, for example a StringBuilder, is not a registration.
        if (invocation.Expression is MemberAccessExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Collection" } }
            && CollectionMethodPattern().IsMatch(method))
        {
            return true;
        }

        return invocation.Expression is MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "ServiceDescriptor" } }
            && DescriptorMethodPattern().IsMatch(method);
    }

    private static List<TypeSyntax> CreatedTypes(SeparatedSyntaxList<ArgumentSyntax> arguments)
    {
        List<TypeSyntax> types = [];

        foreach (ArgumentSyntax argument in arguments)
        {
            switch (argument.Expression)
            {
                case ObjectCreationExpressionSyntax creation:
                    types.Add(creation.Type);
                    break;
                case LambdaExpressionSyntax lambda:
                    // Only a new expression that is the result of the factory counts. Other new expressions are arguments of the constructor.
                    if (lambda.ExpressionBody is ObjectCreationExpressionSyntax lambdaCreation)
                    {
                        types.Add(lambdaCreation.Type);
                    }
                    else if (lambda.Block is not null)
                    {
                        types.AddRange(lambda.Block.DescendantNodes()
                            .OfType<ReturnStatementSyntax>()
                            .Select(statement => statement.Expression)
                            .OfType<ObjectCreationExpressionSyntax>()
                            .Select(creation => creation.Type));
                    }

                    break;
            }
        }

        return types;
    }
}

/// <summary>
/// Parses C# source text and adds the types, members and dependencies to a model.
/// </summary>
/// <param name="knownTypeNames">The names of all types that are declared in the repository.</param>
/// <param name="interfaceNames">The names of all interfaces that are declared in the repository.</param>
/// <param name="includeExternal">If <see langword="true"/>, the builder keeps dependencies to types that are not in <paramref name="knownTypeNames"/>.</param>
internal sealed class SourceModelBuilder(IReadOnlySet<string> knownTypeNames, IReadOnlySet<string> interfaceNames, bool includeExternal)
{
    private static readonly CSharpParseOptions ParseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);
    private static readonly HashSet<string> IgnoredNames = ["var", "dynamic", "nint", "nuint", "unmanaged", "notnull"];

    private readonly IReadOnlySet<string> _knownTypeNames = knownTypeNames;
    private readonly IReadOnlySet<string> _interfaceNames = interfaceNames;
    private readonly bool _includeExternal = includeExternal;

    /// <summary>
    /// Parses a source text and adds all types in it to <paramref name="types"/>.
    /// </summary>
    /// <param name="text">The source text.</param>
    /// <param name="location">The text that identifies the file in the output, for example <c>base:src/Order.cs</c>.</param>
    /// <param name="types">The model that receives the types. The key is <see cref="TypeModel.Key"/>.</param>
    public void AddSource(string text, string location, Dictionary<string, TypeModel> types)
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText(text.TrimStart('\uFEFF'), ParseOptions);

        foreach (BaseTypeDeclarationSyntax declaration in tree.GetRoot().DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
        {
            // C# 14 extension blocks have no name. The builder adds their members to the static class that contains them.
            if (declaration is ExtensionBlockDeclarationSyntax)
            {
                continue;
            }

            AddType(declaration, location, types);
        }

        AddTopLevelStatements(tree.GetRoot(), location, types);
    }

    private void AddTopLevelStatements(SyntaxNode root, string location, Dictionary<string, TypeModel> types)
    {
        List<GlobalStatementSyntax> statements = [.. root.ChildNodes().OfType<GlobalStatementSyntax>()];

        if (statements.Count == 0)
        {
            return;
        }

        // The compiler puts top-level statements in a generated Program class. A partial Program class in the same project merges with it.
        if (!types.TryGetValue("Program", out TypeModel? model))
        {
            model = new("Program", "Program", "class");
            types.Add("Program", model);
        }

        model.Files.Add(location);
        model.Namespaces.Add("(global)");
        HashSet<string> typeParameters = [];

        foreach (GlobalStatementSyntax statement in statements)
        {
            AddBodyEdges(model, statement, typeParameters);
        }

        MemberModel member = new("<top-level statements>", "method", "private", LocationOf(statements[0], location))
        {
            Tokens = string.Join(' ', statements.SelectMany(statement => statement.DescendantTokens()).Select(token => token.Text)),
        };

        if (!model.Members.TryAdd(member.Signature, member))
        {
            model.Members[member.Signature].Tokens += " | " + member.Tokens;
        }
    }

    private void AddType(BaseTypeDeclarationSyntax declaration, string location, Dictionary<string, TypeModel> types)
    {
        TypeDeclarationSyntax? typeDeclaration = declaration as TypeDeclarationSyntax;
        string name = declaration.Identifier.ValueText;
        string key = CreateKey(name, typeDeclaration?.Arity ?? 0);

        if (!types.TryGetValue(key, out TypeModel? model))
        {
            string display = ContainingTypePrefix(declaration) + name + (typeDeclaration?.TypeParameterList?.ToString() ?? string.Empty);
            model = new(key, display, KindOf(declaration));
            types.Add(key, model);
        }

        model.Namespaces.Add(NamespaceOf(declaration));
        model.Files.Add(location);
        model.Visibility = VisibilityOf(declaration.Modifiers, declaration.Parent is BaseTypeDeclarationSyntax ? "private" : "internal");

        foreach (SyntaxToken modifier in declaration.Modifiers)
        {
            if (modifier.ValueText is "abstract" or "static" or "sealed" or "readonly" or "ref")
            {
                model.Modifiers.Add(modifier.ValueText);
            }
        }

        HashSet<string> typeParameters = TypeParametersInScope(declaration);
        AddBaseEdges(model, declaration, typeParameters);

        if (declaration is EnumDeclarationSyntax enumDeclaration)
        {
            foreach (EnumMemberDeclarationSyntax enumMember in enumDeclaration.Members)
            {
                AddMember(model, new(enumMember.Identifier.ValueText, "enum member", "public", LocationOf(enumMember, location)), enumMember);
            }

            return;
        }

        if (typeDeclaration is null)
        {
            return;
        }

        if (typeDeclaration.ParameterList is not null)
        {
            AddPrimaryConstructor(model, typeDeclaration, typeParameters, location);
        }

        foreach (MemberDeclarationSyntax member in typeDeclaration.Members)
        {
            if (member is ExtensionBlockDeclarationSyntax extensionBlock)
            {
                string receiver = extensionBlock.ParameterList is null ? string.Empty : ParameterText(extensionBlock.ParameterList.Parameters);
                HashSet<string> extensionTypeParameters = [.. typeParameters, .. TypeParameterNames(extensionBlock.TypeParameterList)];

                if (extensionBlock.ParameterList is not null)
                {
                    AddParameterEdges(model, extensionBlock.ParameterList.Parameters, "uses", extensionTypeParameters);
                }

                foreach (MemberDeclarationSyntax extensionMember in extensionBlock.Members)
                {
                    AddMemberDeclaration(model, extensionMember, extensionTypeParameters, location, $"extension({receiver}) ");
                }

                continue;
            }

            // Nested types are separate types. The loop in AddSource adds them.
            if (member is BaseTypeDeclarationSyntax)
            {
                continue;
            }

            AddMemberDeclaration(model, member, typeParameters, location, string.Empty);
        }
    }

    private void AddPrimaryConstructor(TypeModel model, TypeDeclarationSyntax declaration, HashSet<string> typeParameters, string location)
    {
        bool isRecord = declaration is RecordDeclarationSyntax;
        SeparatedSyntaxList<ParameterSyntax> parameters = declaration.ParameterList!.Parameters;

        foreach (ParameterSyntax parameter in parameters)
        {
            AddEdges(model, parameter.Type, isRecord ? "has" : "injects", isRecord ? "has" : "injects", typeParameters);

            // The positional parameters of a record are also public properties.
            if (isRecord && parameter.Type is not null)
            {
                string signature = $"{parameter.Identifier.ValueText} : {CompactText(parameter.Type)}";
                AddMember(model, new(signature, "property", "public", LocationOf(parameter, location)), parameter);
            }
        }

        MemberModel constructor = new($"{declaration.Identifier.ValueText}({ParameterText(parameters)})", "constructor", "public", LocationOf(declaration.ParameterList, location));
        AddMember(model, constructor, declaration.ParameterList);
    }

    private void AddMemberDeclaration(TypeModel model, MemberDeclarationSyntax member, HashSet<string> typeParameters, string location, string prefix)
    {
        string defaultVisibility = model.Kind == "interface" ? "public" : "private";
        string visibility = VisibilityOf(member.Modifiers, defaultVisibility);
        string place = LocationOf(member, location);

        switch (member)
        {
            case MethodDeclarationSyntax method:
            {
                HashSet<string> scope = [.. typeParameters, .. TypeParameterNames(method.TypeParameterList)];
                string signature = $"{prefix}{method.Identifier.ValueText}{method.TypeParameterList}({ParameterText(method.ParameterList.Parameters)}) : {CompactText(method.ReturnType)}";
                AddEdges(model, method.ReturnType, "uses", "uses", scope);
                AddParameterEdges(model, method.ParameterList.Parameters, "uses", scope);
                AddBodyEdges(model, method, scope);
                AddMember(model, WithModifiers(new(signature, "method", visibility, place), member.Modifiers), member);
                break;
            }
            case ConstructorDeclarationSyntax constructor:
            {
                bool isStatic = constructor.Modifiers.Any(SyntaxKind.StaticKeyword);
                string signature = $"{constructor.Identifier.ValueText}({ParameterText(constructor.ParameterList.Parameters)})";
                AddParameterEdges(model, constructor.ParameterList.Parameters, isStatic ? "uses" : "injects", typeParameters);
                AddBodyEdges(model, constructor, typeParameters);
                AddMember(model, WithModifiers(new(signature, "constructor", isStatic ? "private" : visibility, place), member.Modifiers), member);
                break;
            }
            case DestructorDeclarationSyntax destructor:
                AddBodyEdges(model, destructor, typeParameters);
                AddMember(model, new($"~{destructor.Identifier.ValueText}()", "destructor", "protected", place), member);
                break;
            case PropertyDeclarationSyntax property:
            {
                string signature = $"{prefix}{property.Identifier.ValueText} : {CompactText(property.Type)}";
                AddEdges(model, property.Type, "has", "has", typeParameters);
                AddBodyEdges(model, property, typeParameters);
                AddMember(model, WithModifiers(new(signature, "property", visibility, place), member.Modifiers), member);
                break;
            }
            case IndexerDeclarationSyntax indexer:
            {
                string signature = $"{prefix}this[{ParameterText(indexer.ParameterList.Parameters)}] : {CompactText(indexer.Type)}";
                AddEdges(model, indexer.Type, "uses", "uses", typeParameters);
                AddParameterEdges(model, indexer.ParameterList.Parameters, "uses", typeParameters);
                AddBodyEdges(model, indexer, typeParameters);
                AddMember(model, WithModifiers(new(signature, "indexer", visibility, place), member.Modifiers), member);
                break;
            }
            case BaseFieldDeclarationSyntax field:
            {
                string kind = field is EventFieldDeclarationSyntax ? "event" : "field";
                AddEdges(model, field.Declaration.Type, "has", "has", typeParameters);
                AddBodyEdges(model, field, typeParameters);

                foreach (VariableDeclaratorSyntax variable in field.Declaration.Variables)
                {
                    string signature = $"{(kind == "event" ? "event " : string.Empty)}{variable.Identifier.ValueText} : {CompactText(field.Declaration.Type)}";
                    AddMember(model, WithModifiers(new(signature, kind, visibility, LocationOf(variable, location)), member.Modifiers), variable);
                }

                break;
            }
            case EventDeclarationSyntax eventDeclaration:
            {
                string signature = $"event {eventDeclaration.Identifier.ValueText} : {CompactText(eventDeclaration.Type)}";
                AddEdges(model, eventDeclaration.Type, "has", "has", typeParameters);
                AddBodyEdges(model, eventDeclaration, typeParameters);
                AddMember(model, WithModifiers(new(signature, "event", visibility, place), member.Modifiers), member);
                break;
            }
            case OperatorDeclarationSyntax operatorDeclaration:
            {
                string parameters = ParameterText(operatorDeclaration.ParameterList.Parameters);
                string signature = $"{prefix}operator {operatorDeclaration.OperatorToken.Text}({parameters}) : {CompactText(operatorDeclaration.ReturnType)}";
                AddEdges(model, operatorDeclaration.ReturnType, "uses", "uses", typeParameters);
                AddParameterEdges(model, operatorDeclaration.ParameterList.Parameters, "uses", typeParameters);
                AddBodyEdges(model, operatorDeclaration, typeParameters);
                AddMember(model, WithModifiers(new(signature, "operator", visibility, place), member.Modifiers), member);
                break;
            }
            case ConversionOperatorDeclarationSyntax conversion:
            {
                string parameters = ParameterText(conversion.ParameterList.Parameters);
                string signature = $"{prefix}{conversion.ImplicitOrExplicitKeyword.Text} operator {CompactText(conversion.Type)}({parameters})";
                AddEdges(model, conversion.Type, "uses", "uses", typeParameters);
                AddParameterEdges(model, conversion.ParameterList.Parameters, "uses", typeParameters);
                AddBodyEdges(model, conversion, typeParameters);
                AddMember(model, WithModifiers(new(signature, "operator", visibility, place), member.Modifiers), member);
                break;
            }
            case DelegateDeclarationSyntax delegateDeclaration:
            {
                string parameters = ParameterText(delegateDeclaration.ParameterList.Parameters);
                string signature = $"delegate {delegateDeclaration.Identifier.ValueText}({parameters}) : {CompactText(delegateDeclaration.ReturnType)}";
                AddMember(model, new(signature, "delegate", visibility, place), member);
                break;
            }
        }
    }

    private static MemberModel WithModifiers(MemberModel member, SyntaxTokenList modifiers)
    {
        foreach (SyntaxToken modifier in modifiers)
        {
            if (modifier.ValueText is "static" or "abstract" or "virtual" or "override" or "sealed" or "async" or "const" or "readonly" or "extern" or "required")
            {
                member.Modifiers.Add(modifier.ValueText);
            }
        }

        return member;
    }

    private static void AddMember(TypeModel model, MemberModel member, SyntaxNode node)
    {
        string tokens = string.Join(' ', node.DescendantTokens().Select(token => token.Text));

        // Partial methods have two declarations with the same signature. The builder keeps the tokens of both parts.
        if (model.Members.TryGetValue(member.Signature, out MemberModel? existing))
        {
            existing.Tokens += " | " + tokens;
            return;
        }

        member.Tokens = tokens;
        model.Members.Add(member.Signature, member);
    }

    private void AddBaseEdges(TypeModel model, BaseTypeDeclarationSyntax declaration, HashSet<string> typeParameters)
    {
        if (declaration.BaseList is null || declaration is EnumDeclarationSyntax)
        {
            return;
        }

        bool isStruct = declaration is StructDeclarationSyntax
            || (declaration is RecordDeclarationSyntax record && record.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword));
        int position = 0;

        foreach (BaseTypeSyntax baseType in declaration.BaseList.Types)
        {
            string kind;

            if (declaration is InterfaceDeclarationSyntax)
            {
                kind = "inherits";
            }
            else if (isStruct || position > 0 || IsInterfaceName(RightmostName(baseType.Type)))
            {
                kind = "implements";
            }
            else
            {
                kind = "inherits";
            }

            AddEdges(model, baseType.Type, kind, "uses", typeParameters);
            position++;
        }
    }

    private void AddParameterEdges(TypeModel model, SeparatedSyntaxList<ParameterSyntax> parameters, string kind, HashSet<string> typeParameters)
    {
        foreach (ParameterSyntax parameter in parameters)
        {
            AddEdges(model, parameter.Type, kind, kind, typeParameters);
        }
    }

    private void AddBodyEdges(TypeModel model, SyntaxNode member, HashSet<string> typeParameters)
    {
        AddCreationEdges(model, member, typeParameters);
        AddRegistrationEdges(model, member, typeParameters);
    }

    private void AddRegistrationEdges(TypeModel model, SyntaxNode member, HashSet<string> typeParameters)
    {
        foreach (InvocationExpressionSyntax invocation in member.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            foreach (TypeSyntax implementation in RegistrationParser.FindImplementationTypes(invocation, IsInterfaceName))
            {
                List<(string Name, int Arity, bool IsArgument)> references = [];
                CollectReferences(implementation, false, references);

                // Only the registered class gets the arrow. Its type arguments, for example Order in Repository<Order>, do not.
                foreach ((string name, int arity, bool isArgument) in references)
                {
                    if (isArgument || typeParameters.Contains(name) || IgnoredNames.Contains(name))
                    {
                        continue;
                    }

                    if (!_includeExternal && !_knownTypeNames.Contains(name))
                    {
                        continue;
                    }

                    string target = CreateKey(name, arity);

                    if (target != model.Key)
                    {
                        model.Edges.Add(new(model.Key, target, "registers"));
                    }
                }
            }
        }
    }

    private void AddCreationEdges(TypeModel model, SyntaxNode member, HashSet<string> typeParameters)
    {
        // Target-typed new() has no type in the syntax. The field, property or parameter type already gives that dependency.
        foreach (ObjectCreationExpressionSyntax creation in member.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
        {
            AddEdges(model, creation.Type, "creates", "uses", typeParameters);
        }
    }

    private void AddEdges(TypeModel model, TypeSyntax? type, string kind, string argumentKind, HashSet<string> typeParameters)
    {
        if (type is null)
        {
            return;
        }

        List<(string Name, int Arity, bool IsArgument)> references = [];
        CollectReferences(type, false, references);

        foreach ((string name, int arity, bool isArgument) in references)
        {
            if (typeParameters.Contains(name) || IgnoredNames.Contains(name))
            {
                continue;
            }

            if (!_includeExternal && !_knownTypeNames.Contains(name))
            {
                continue;
            }

            string target = CreateKey(name, arity);

            if (target != model.Key)
            {
                model.Edges.Add(new(model.Key, target, isArgument ? argumentKind : kind));
            }
        }
    }

    private static void CollectReferences(TypeSyntax type, bool isArgument, List<(string Name, int Arity, bool IsArgument)> references)
    {
        switch (type)
        {
            case IdentifierNameSyntax identifier:
                references.Add((identifier.Identifier.ValueText, 0, isArgument));
                break;
            case GenericNameSyntax generic:
                references.Add((generic.Identifier.ValueText, generic.Arity, isArgument));

                foreach (TypeSyntax argument in generic.TypeArgumentList.Arguments)
                {
                    CollectReferences(argument, true, references);
                }

                break;
            case QualifiedNameSyntax qualified:
                // Only the right part is a type. The left part is a namespace or a containing type.
                CollectReferences(qualified.Right, isArgument, references);
                break;
            case AliasQualifiedNameSyntax aliasQualified:
                CollectReferences(aliasQualified.Name, isArgument, references);
                break;
            case NullableTypeSyntax nullable:
                CollectReferences(nullable.ElementType, isArgument, references);
                break;
            case ArrayTypeSyntax array:
                CollectReferences(array.ElementType, true, references);
                break;
            case PointerTypeSyntax pointer:
                CollectReferences(pointer.ElementType, isArgument, references);
                break;
            case RefTypeSyntax reference:
                CollectReferences(reference.Type, isArgument, references);
                break;
            case ScopedTypeSyntax scoped:
                CollectReferences(scoped.Type, isArgument, references);
                break;
            case TupleTypeSyntax tuple:
                foreach (TupleElementSyntax element in tuple.Elements)
                {
                    CollectReferences(element.Type, true, references);
                }

                break;
            case FunctionPointerTypeSyntax functionPointer:
                foreach (FunctionPointerParameterSyntax parameter in functionPointer.ParameterList.Parameters)
                {
                    CollectReferences(parameter.Type, true, references);
                }

                break;
        }
    }

    private bool IsInterfaceName(string name)
    {
        if (_interfaceNames.Contains(name))
        {
            return true;
        }

        // The name is not declared in the repository. Use the .NET naming rule for interfaces.
        return !_knownTypeNames.Contains(name) && name.Length > 1 && name[0] == 'I' && char.IsUpper(name[1]);
    }

    private static string RightmostName(TypeSyntax type) => type switch
    {
        QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
        AliasQualifiedNameSyntax aliasQualified => aliasQualified.Name.Identifier.ValueText,
        SimpleNameSyntax simple => simple.Identifier.ValueText,
        _ => type.ToString(),
    };

    /// <summary>
    /// Creates the key of a type from the name and the generic arity, for example <c>Map&lt;,&gt;</c> for a type with two type parameters.
    /// </summary>
    /// <param name="name">The name of the type.</param>
    /// <param name="arity">The number of type parameters.</param>
    /// <returns>The key of the type.</returns>
    public static string CreateKey(string name, int arity) => arity == 0 ? name : $"{name}<{new string(',', arity - 1)}>";

    private static string KindOf(BaseTypeDeclarationSyntax declaration) => declaration switch
    {
        InterfaceDeclarationSyntax => "interface",
        EnumDeclarationSyntax => "enum",
        StructDeclarationSyntax => "struct",
        RecordDeclarationSyntax record when record.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword) => "record struct",
        RecordDeclarationSyntax => "record",
        _ => "class",
    };

    private static string VisibilityOf(SyntaxTokenList modifiers, string defaultVisibility)
    {
        bool isPublic = modifiers.Any(SyntaxKind.PublicKeyword);
        bool isProtected = modifiers.Any(SyntaxKind.ProtectedKeyword);
        bool isInternal = modifiers.Any(SyntaxKind.InternalKeyword);
        bool isPrivate = modifiers.Any(SyntaxKind.PrivateKeyword);
        bool isFile = modifiers.Any(SyntaxKind.FileKeyword);

        if (isPublic)
        {
            return "public";
        }

        if (isProtected && isInternal)
        {
            return "protected internal";
        }

        if (isPrivate && isProtected)
        {
            return "private protected";
        }

        if (isProtected)
        {
            return "protected";
        }

        if (isInternal)
        {
            return "internal";
        }

        if (isFile)
        {
            return "file";
        }

        return isPrivate ? "private" : defaultVisibility;
    }

    private static string NamespaceOf(SyntaxNode node)
    {
        List<string> parts = [];

        foreach (BaseNamespaceDeclarationSyntax namespaceDeclaration in node.Ancestors().OfType<BaseNamespaceDeclarationSyntax>())
        {
            parts.Insert(0, namespaceDeclaration.Name.ToString());
        }

        return parts.Count == 0 ? "(global)" : string.Join('.', parts);
    }

    private static string ContainingTypePrefix(SyntaxNode node)
    {
        StringBuilder prefix = new();

        foreach (BaseTypeDeclarationSyntax containingType in node.Ancestors().OfType<BaseTypeDeclarationSyntax>())
        {
            prefix.Insert(0, containingType.Identifier.ValueText + ".");
        }

        return prefix.ToString();
    }

    private static HashSet<string> TypeParametersInScope(SyntaxNode node)
    {
        HashSet<string> names = [];

        foreach (TypeDeclarationSyntax declaration in node.AncestorsAndSelf().OfType<TypeDeclarationSyntax>())
        {
            names.UnionWith(TypeParameterNames(declaration.TypeParameterList));
        }

        return names;
    }

    private static IEnumerable<string> TypeParameterNames(TypeParameterListSyntax? list) =>
        list?.Parameters.Select(parameter => parameter.Identifier.ValueText) ?? [];

    private static string ParameterText(SeparatedSyntaxList<ParameterSyntax> parameters) =>
        string.Join(", ", parameters.Select(ParameterTypeText));

    private static string ParameterTypeText(ParameterSyntax parameter)
    {
        string modifiers = string.Join(' ', parameter.Modifiers.Select(modifier => modifier.ValueText));
        string type = parameter.Type is null ? parameter.Identifier.ValueText : CompactText(parameter.Type);
        return modifiers.Length == 0 ? type : $"{modifiers} {type}";
    }

    private static string LocationOf(SyntaxNode node, string location) =>
        $"{location}:{node.GetLocation().GetLineSpan().StartLinePosition.Line + 1}";

    /// <summary>
    /// Writes a syntax node as one line of text without comments and with normal spacing.
    /// </summary>
    /// <param name="node">The syntax node, for example a type.</param>
    /// <returns>The text of the node, for example <c>Dictionary&lt;string, List&lt;int&gt;&gt;</c>.</returns>
    public static string CompactText(SyntaxNode node)
    {
        StringBuilder builder = new();
        SyntaxToken previous = default;

        foreach (SyntaxToken token in node.DescendantTokens())
        {
            if (builder.Length > 0 && (IsWord(previous) && IsWord(token) || previous.IsKind(SyntaxKind.CommaToken)))
            {
                builder.Append(' ');
            }

            builder.Append(token.Text);
            previous = token;
        }

        return builder.ToString();
    }

    private static bool IsWord(SyntaxToken token) => token.IsKind(SyntaxKind.IdentifierToken) || SyntaxFacts.IsKeywordKind(token.Kind());
}

/// <summary>
/// Compares the branch with the merge base and writes the result as JSON.
/// </summary>
/// <param name="options">The command line options.</param>
internal sealed class BranchDiffTool(ToolOptions options)
{
    private readonly ToolOptions _options = options;

    /// <summary>
    /// Runs the comparison and writes the JSON result to <see cref="ToolOptions.OutputPath"/> or to the standard output.
    /// </summary>
    public void Run()
    {
        string root = new GitClient(_options.RepositoryPath).Run("rev-parse", "--show-toplevel").Trim();
        GitClient git = new(root);
        string baseRef = _options.BaseRef ?? ResolveDefaultBase(git);
        string mergeBase = git.Run("merge-base", "HEAD", baseRef).Trim();
        string head = git.Run("rev-parse", "--abbrev-ref", "HEAD").Trim();

        List<ChangedFile> changedFiles = GetChangedFiles(git, mergeBase);
        Dictionary<string, string> oldTexts = [];
        Dictionary<string, string> newTexts = [];

        foreach (ChangedFile file in changedFiles)
        {
            if (file.Status is "M" or "D" or "R")
            {
                oldTexts[file.OldPath ?? file.Path] = git.Run("show", $"{mergeBase}:{file.OldPath ?? file.Path}");
            }

            if (file.Status is not "D")
            {
                string? text = ReadBranchText(git, root, file.Path);

                if (text is not null)
                {
                    newTexts[file.Path] = text;
                }
            }
        }

        HashSet<string> knownTypeNames = [];
        HashSet<string> interfaceNames = [];
        IndexRepositoryTypes(git, root, knownTypeNames, interfaceNames);

        foreach (string text in oldTexts.Values)
        {
            TypeNameIndex.AddDeclarations(text, knownTypeNames, interfaceNames);
        }

        SourceModelBuilder builder = new(knownTypeNames, interfaceNames, _options.IncludeExternal);
        Dictionary<string, TypeModel> oldTypes = [];
        Dictionary<string, TypeModel> newTypes = [];

        foreach (KeyValuePair<string, string> pair in oldTexts)
        {
            builder.AddSource(pair.Value, $"base:{pair.Key}", oldTypes);
        }

        foreach (KeyValuePair<string, string> pair in newTexts)
        {
            builder.AddSource(pair.Value, pair.Key, newTypes);
        }

        byte[] json = DiffWriter.Write(baseRef, mergeBase, head, !_options.CommittedOnly, changedFiles, oldTypes, newTypes);

        if (_options.OutputPath is null)
        {
            using Stream output = Console.OpenStandardOutput();
            output.Write(json);
        }
        else
        {
            string fullPath = Path.GetFullPath(_options.OutputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllBytes(fullPath, json);
            Console.Error.WriteLine($"Wrote {fullPath} ({changedFiles.Count} changed C# files, merge base {mergeBase[..Math.Min(10, mergeBase.Length)]}).");
        }
    }

    private static string ResolveDefaultBase(GitClient git)
    {
        if (git.TryRun(out string originHead, "symbolic-ref", "--quiet", "--short", "refs/remotes/origin/HEAD") && originHead.Trim().Length > 0)
        {
            return originHead.Trim();
        }

        string[] candidates = ["origin/main", "origin/master", "origin/develop", "main", "master", "develop"];

        foreach (string candidate in candidates)
        {
            if (git.TryRun(out string _, "rev-parse", "--verify", "--quiet", candidate + "^{commit}"))
            {
                return candidate;
            }
        }

        throw new GitException("The tool did not find a default branch. Use --base <branch>.");
    }

    private List<ChangedFile> GetChangedFiles(GitClient git, string mergeBase)
    {
        List<ChangedFile> files = [];
        string[] diffArguments = _options.CommittedOnly
            ? ["diff", "--name-status", "-M", "-z", mergeBase, "HEAD", "--", "*.cs"]
            : ["diff", "--name-status", "-M", "-z", mergeBase, "--", "*.cs"];
        string[] parts = git.Run(diffArguments).Split('\0', StringSplitOptions.RemoveEmptyEntries);

        for (int index = 0; index < parts.Length;)
        {
            char status = parts[index][0];

            if (status is 'R' && index + 2 < parts.Length)
            {
                files.Add(new("R", parts[index + 2], parts[index + 1]));
                index += 3;
            }
            else if (status is 'C' && index + 2 < parts.Length)
            {
                // A copy does not change the original file. The tool shows the copy as a new file.
                files.Add(new("A", parts[index + 2], null));
                index += 3;
            }
            else if (index + 1 < parts.Length)
            {
                string normalized = status switch
                {
                    'A' => "A",
                    'D' => "D",
                    _ => "M",
                };

                files.Add(new(normalized, parts[index + 1], null));
                index += 2;
            }
            else
            {
                break;
            }
        }

        if (!_options.CommittedOnly)
        {
            foreach (string path in git.Run("ls-files", "--others", "--exclude-standard", "-z", "--", "*.cs").Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                files.Add(new("A", path, null));
            }
        }

        return files;
    }

    private string? ReadBranchText(GitClient git, string root, string path)
    {
        if (_options.CommittedOnly)
        {
            return git.TryRun(out string text, "show", $"HEAD:{path}") ? text : null;
        }

        string fullPath = Path.Combine(root, path);
        return File.Exists(fullPath) ? File.ReadAllText(fullPath) : null;
    }

    private static void IndexRepositoryTypes(GitClient git, string root, HashSet<string> knownTypeNames, HashSet<string> interfaceNames)
    {
        IEnumerable<string> tracked = git.Run("ls-files", "-z", "--", "*.cs").Split('\0', StringSplitOptions.RemoveEmptyEntries);
        IEnumerable<string> untracked = git.Run("ls-files", "--others", "--exclude-standard", "-z", "--", "*.cs").Split('\0', StringSplitOptions.RemoveEmptyEntries);

        foreach (string path in tracked.Concat(untracked))
        {
            string fullPath = Path.Combine(root, path);

            if (File.Exists(fullPath))
            {
                TypeNameIndex.AddDeclarations(File.ReadAllText(fullPath), knownTypeNames, interfaceNames);
            }
        }
    }
}

/// <summary>
/// Compares two models and writes the differences as JSON.
/// </summary>
internal static class DiffWriter
{
    /// <summary>
    /// Compares the types at the merge base with the types on the branch and returns the result as UTF-8 JSON.
    /// </summary>
    /// <param name="baseRef">The branch or commit that the user compared with.</param>
    /// <param name="mergeBase">The merge base commit.</param>
    /// <param name="head">The name of the current branch.</param>
    /// <param name="includesWorkingTree">If <see langword="true"/>, the result contains changes that are not committed.</param>
    /// <param name="changedFiles">The changed C# files.</param>
    /// <param name="oldTypes">The types at the merge base.</param>
    /// <param name="newTypes">The types on the branch.</param>
    /// <returns>The JSON document as UTF-8 bytes.</returns>
    public static byte[] Write(
        string baseRef,
        string mergeBase,
        string head,
        bool includesWorkingTree,
        List<ChangedFile> changedFiles,
        Dictionary<string, TypeModel> oldTypes,
        Dictionary<string, TypeModel> newTypes)
    {
        using MemoryStream stream = new();
        JsonWriterOptions writerOptions = new() { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        using Utf8JsonWriter writer = new(stream, writerOptions);

        SortedSet<string> keys = [.. oldTypes.Keys, .. newTypes.Keys];
        List<string> unchangedTypes = [];
        HashSet<string> structurallyChanged = [];
        HashSet<EdgeModel> oldEdges = [.. oldTypes.Values.SelectMany(type => type.Edges)];
        HashSet<EdgeModel> newEdges = [.. newTypes.Values.SelectMany(type => type.Edges)];

        writer.WriteStartObject();
        writer.WriteString("base", baseRef);
        writer.WriteString("mergeBase", mergeBase);
        writer.WriteString("head", head);
        writer.WriteBoolean("includesWorkingTree", includesWorkingTree);

        writer.WriteStartArray("changedFiles");

        foreach (ChangedFile file in changedFiles)
        {
            writer.WriteStartObject();
            writer.WriteString("status", file.Status);
            writer.WriteString("path", file.Path);

            if (file.OldPath is not null)
            {
                writer.WriteString("oldPath", file.OldPath);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteStartArray("types");

        foreach (string key in keys)
        {
            oldTypes.TryGetValue(key, out TypeModel? oldType);
            newTypes.TryGetValue(key, out TypeModel? newType);
            string status = TypeStatus(oldType, newType);

            if (status == "unchanged")
            {
                unchangedTypes.Add(key);
                continue;
            }

            if (status is "added" or "removed" or "structural")
            {
                structurallyChanged.Add(key);
            }

            WriteType(writer, key, status, oldType, newType);
        }

        writer.WriteEndArray();

        writer.WriteStartArray("unchangedTypes");

        foreach (string key in unchangedTypes)
        {
            writer.WriteStringValue(key);
        }

        writer.WriteEndArray();

        // Changed dependencies are the main input for the class diagram.
        writer.WriteStartArray("changedEdges");

        foreach (EdgeModel edge in newEdges.Except(oldEdges).OrderBy(edge => edge.Source).ThenBy(edge => edge.Target))
        {
            WriteEdge(writer, edge, "added");
        }

        foreach (EdgeModel edge in oldEdges.Except(newEdges).OrderBy(edge => edge.Source).ThenBy(edge => edge.Target))
        {
            WriteEdge(writer, edge, "removed");
        }

        writer.WriteEndArray();

        // Unchanged dependencies of changed types give context. The diagram shows only the ones that help the reader.
        writer.WriteStartArray("contextEdges");

        foreach (EdgeModel edge in newEdges.Intersect(oldEdges).Where(edge => structurallyChanged.Contains(edge.Source)).OrderBy(edge => edge.Source).ThenBy(edge => edge.Target))
        {
            WriteEdge(writer, edge, "unchanged");
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();

        return stream.ToArray();
    }

    private static string TypeStatus(TypeModel? oldType, TypeModel? newType)
    {
        if (oldType is null)
        {
            return "added";
        }

        if (newType is null)
        {
            return "removed";
        }

        bool sameShape = oldType.Kind == newType.Kind
            && oldType.Visibility == newType.Visibility
            && oldType.Modifiers.SetEquals(newType.Modifiers)
            && oldType.Edges.SetEquals(newType.Edges)
            && oldType.Members.Count == newType.Members.Count
            && oldType.Members.Values.All(member => newType.Members.TryGetValue(member.Signature, out MemberModel? other) && SameShape(member, other));

        if (!sameShape)
        {
            return "structural";
        }

        bool sameBodies = oldType.Members.Values.All(member => member.Tokens == newType.Members[member.Signature].Tokens);
        return sameBodies ? "unchanged" : "behavioral";
    }

    private static bool SameShape(MemberModel first, MemberModel second) =>
        first.Visibility == second.Visibility && first.Modifiers.SetEquals(second.Modifiers);

    private static void WriteType(Utf8JsonWriter writer, string key, string status, TypeModel? oldType, TypeModel? newType)
    {
        TypeModel current = newType ?? oldType!;

        writer.WriteStartObject();
        writer.WriteString("key", key);
        writer.WriteString("display", current.Display);
        writer.WriteString("kind", current.Kind);
        writer.WriteString("status", status);
        writer.WriteString("visibility", current.Visibility);
        WriteStrings(writer, "namespaces", current.Namespaces);
        WriteStrings(writer, "modifiers", current.Modifiers);

        if (oldType is not null && newType is not null)
        {
            if (oldType.Kind != newType.Kind)
            {
                writer.WriteString("kindBefore", oldType.Kind);
            }

            if (oldType.Visibility != newType.Visibility)
            {
                writer.WriteString("visibilityBefore", oldType.Visibility);
            }

            if (!oldType.Modifiers.SetEquals(newType.Modifiers))
            {
                WriteStrings(writer, "modifiersBefore", oldType.Modifiers);
            }
        }

        WriteStrings(writer, "files", [.. oldType?.Files ?? [], .. newType?.Files ?? []]);

        int unchangedCount = 0;
        writer.WriteStartArray("members");

        SortedSet<string> signatures = [.. oldType?.Members.Keys ?? Enumerable.Empty<string>(), .. newType?.Members.Keys ?? Enumerable.Empty<string>()];

        foreach (string signature in signatures)
        {
            MemberModel? oldMember = null;
            MemberModel? newMember = null;
            oldType?.Members.TryGetValue(signature, out oldMember);
            newType?.Members.TryGetValue(signature, out newMember);

            string memberStatus = (oldMember, newMember) switch
            {
                (null, _) => "added",
                (_, null) => "removed",
                _ when !SameShape(oldMember!, newMember!) || oldMember!.Tokens != newMember!.Tokens => "modified",
                _ => "unchanged",
            };

            if (memberStatus == "unchanged")
            {
                unchangedCount++;
                continue;
            }

            MemberModel member = newMember ?? oldMember!;
            writer.WriteStartObject();
            writer.WriteString("signature", signature);
            writer.WriteString("kind", member.Kind);
            writer.WriteString("visibility", member.Visibility);
            WriteStrings(writer, "modifiers", member.Modifiers);
            writer.WriteString("status", memberStatus);

            if (oldMember is not null && newMember is not null && !SameShape(oldMember, newMember))
            {
                writer.WriteString("visibilityBefore", oldMember.Visibility);
                WriteStrings(writer, "modifiersBefore", oldMember.Modifiers);
            }

            writer.WriteString("location", member.Location);

            // The location at the merge base lets the reader compare the old body with the new body.
            if (oldMember is not null && newMember is not null)
            {
                writer.WriteString("locationBefore", oldMember.Location);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteNumber("unchangedMemberCount", unchangedCount);
        writer.WriteEndObject();
    }

    private static void WriteEdge(Utf8JsonWriter writer, EdgeModel edge, string status)
    {
        writer.WriteStartObject();
        writer.WriteString("source", edge.Source);
        writer.WriteString("target", edge.Target);
        writer.WriteString("kind", edge.Kind);
        writer.WriteString("status", status);
        writer.WriteEndObject();
    }

    private static void WriteStrings(Utf8JsonWriter writer, string name, IEnumerable<string> values)
    {
        writer.WriteStartArray(name);

        foreach (string value in values.Distinct())
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }
}
