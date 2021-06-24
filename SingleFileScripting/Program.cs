using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Metadata;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;

// The `CSharpScript` API cannot be used when `Assembly.Location` is not supported, see:
// - https://github.com/dotnet/roslyn/issues/50719
// - https://www.samprof.com/2018/12/15/compile-csharp-and-blazor-inside-browser-en

const string sourceText = @"""Hello World!""";

var diagnostics = new List<Diagnostic>();

// https://github.com/dotnet/roslyn/blob/version-3.2.0/src/Scripting/CSharp/CSharpScriptCompiler.cs#L43
var syntaxTree = SyntaxFactory.ParseSyntaxTree(
    sourceText,
    new CSharpParseOptions(
        kind: SourceCodeKind.Script,
        languageVersion: LanguageVersion.Latest));

diagnostics.AddRange(syntaxTree.GetDiagnostics());

static MetadataReference GetReference(Type type)
{
    unsafe
    {
        return type.Assembly.TryGetRawMetadata(out var blob, out var length)
            ? AssemblyMetadata
                .Create(ModuleMetadata.CreateFromMetadata((IntPtr) blob, length))
                .GetReference()
            : throw new InvalidOperationException($"Could not get raw metadata for type {type}");
    }
}

var references = new[]
{
    GetReference(typeof(object))
};

// In this example, a return type of `string` is expected

// Note that `ScriptBuilder` would normally generate a unique assembly name
// https://github.com/dotnet/roslyn/blob/version-3.2.0/src/Scripting/Core/ScriptBuilder.cs#L64
var compilation = CSharpCompilation.CreateScriptCompilation(
    assemblyName: "Script",
    syntaxTree,
    references,
    returnType: typeof(string));

var submissionFactory = default(Func<object[], Task<string>>);

await using (var peStream = new MemoryStream())
await using (var pdbStream = new MemoryStream())
{
    // https://github.com/dotnet/roslyn/blob/version-3.2.0/src/Scripting/Core/ScriptBuilder.cs#L121
    // https://github.com/dotnet/roslyn/blob/version-3.2.0/src/Scripting/Core/Utilities/PdbHelpers.cs#L10
    var result = compilation.Emit(
        peStream, 
        pdbStream,
        xmlDocumentationStream: null,
        win32Resources: null,
        manifestResources: null,
        new EmitOptions(
            debugInformationFormat: DebugInformationFormat.PortablePdb,
            pdbChecksumAlgorithm: default(HashAlgorithmName)));
    
    diagnostics.AddRange(result.Diagnostics);

    if (result.Success)
    {
        var scriptAssembly = AppDomain.CurrentDomain.Load(peStream.ToArray(), pdbStream.ToArray());
        
        // https://github.com/dotnet/roslyn/blob/version-3.2.0/src/Scripting/Core/ScriptBuilder.cs#L188
        var entryPoint = compilation.GetEntryPoint(CancellationToken.None) ?? throw new InvalidOperationException("Entry point could be determined");

        var entryPointType = scriptAssembly
            .GetType(
                $"{entryPoint.ContainingNamespace.MetadataName}.{entryPoint.ContainingType.MetadataName}",
                throwOnError: true,
                ignoreCase: false);
        
        var entryPointMethod = entryPointType?.GetTypeInfo().GetDeclaredMethod(entryPoint.MetadataName) ?? throw new InvalidOperationException("Entry point method could be determined");

        submissionFactory = entryPointMethod.CreateDelegate<Func<object[], Task<string>>>();
    }
}

foreach (var diagnostic in diagnostics)
{
    Console.WriteLine(diagnostic.ToString());
}

if (submissionFactory == null)
{
    Console.WriteLine("Compilation failed");
    return;
}

// The first argument is the globals type, the remaining are preceding script states 
// - https://github.com/dotnet/roslyn/blob/version-3.2.0/src/Scripting/Core/ScriptExecutionState.cs#L31
// - https://github.com/dotnet/roslyn/blob/version-3.2.0/src/Scripting/Core/ScriptExecutionState.cs#L65
var message = await submissionFactory.Invoke(new object[] { null, null });

Console.WriteLine(message);