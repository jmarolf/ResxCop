using System.Text.RegularExpressions;
using System.Xml.Linq;

using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.MSBuild;

var instance = MSBuildLocator.QueryVisualStudioInstances().First();
Console.WriteLine($"Using MSBuild at '{instance.MSBuildPath}' to load projects.");

MSBuildLocator.RegisterInstance(instance);

using var workspace = MSBuildWorkspace.Create();

//workspace.WorkspaceFailed += (o, e) => Console.WriteLine(e.Diagnostic.Message);

var solutionPath = args[0];
Console.WriteLine($"Loading solution '{solutionPath}'");

var solution = await workspace.OpenSolutionAsync(solutionPath, new ConsoleProgressReporter());
Console.WriteLine($"Finished loading solution '{solutionPath}'");

var compilations = await Task.WhenAll(solution.Projects.AsParallel().Select(x => x.GetCompilationAsync()));
if (compilations is null)
{
    Console.WriteLine("Unable to get compilations");
    return;
}

var allResourceSymbols = await compilations.AsParallel().ToAsyncEnumerable().SelectMany(compilation => GetAllResourceSymbols(compilation)).ToArrayAsync();

var xmlComments = allResourceSymbols
    .SelectMany(
        symbol => symbol.GetMembers()
        .Where(member => member.IsStatic && member.Name != "s_resourceManager" && member.Name != "ResourceManager" && member.Name != "Culture" && member.Name != "GetResourceString")
        .Select(
            x =>
            {
                var xml = x.GetDocumentationCommentXml();
                var name = Regex.Match(xml, "<member name=\"(.*)\">").Groups[1].Value;
                var resourceString = Regex.Match(xml, "<summary>(.*)</summary>").Groups[1].Value;
                //Console.WriteLine($"{name}:{resourceString}");
                return (Name: name, resourceString: resourceString);
            }))
    .Where(x => !string.IsNullOrWhiteSpace(x.Name) && !string.IsNullOrWhiteSpace(x.resourceString))
    .DistinctBy(x => x.Name) // Multi-targeting leads to duplicate type names
    .ToDictionary(x => x.Name, x => x.resourceString);

var duplicates = xmlComments.ToLookup(x => x.Value, x => x.Key).Where(x => x.Count() > 1);

foreach (var item in duplicates)
{
    var resxTypeNames = string.Join(Environment.NewLine, item.Select(x => "  " + x));
    Console.WriteLine($"The following resx files have duplicate values '{item.Key}':");
    Console.WriteLine(resxTypeNames);
}

static async IAsyncEnumerable<INamedTypeSymbol?> GetAllResourceSymbols(Compilation? compilation)
{
    var trees = compilation!.SyntaxTrees;
    foreach (var tree in trees)
    {
        //Console.WriteLine($"Getting Symbols for '{tree.FilePath}'");
        var model = compilation.GetSemanticModel(tree, ignoreAccessibility: true);
        var root = await tree.GetRootAsync();
        foreach (var typeDeclaration in root.DescendantNodesAndSelf().Where(x => x.IsKind(SyntaxKind.ClassDeclaration) || x.IsKind(SyntaxKind.RecordDeclaration) || x.IsKind(SyntaxKind.StructDeclaration)))
        {
            if (model.GetDeclaredSymbol(typeDeclaration) is INamedTypeSymbol namedType &&
                namedType.MemberNames.Contains("ResourceManager"))
            {
                yield return namedType;
            }
        }
    }
}

static VisualStudioInstance SelectVisualStudioInstance(VisualStudioInstance[] visualStudioInstances)
{
    Console.WriteLine("Multiple installs of MSBuild detected please select one:");
    for (int i = 0; i < visualStudioInstances.Length; i++)
    {
        Console.WriteLine($"Instance {i + 1}");
        Console.WriteLine($"    Name: {visualStudioInstances[i].Name}");
        Console.WriteLine($"    Version: {visualStudioInstances[i].Version}");
        Console.WriteLine($"    MSBuild Path: {visualStudioInstances[i].MSBuildPath}");
    }

    while (true)
    {
        var userResponse = Console.ReadLine();
        if (int.TryParse(userResponse, out int instanceNumber) &&
            instanceNumber > 0 &&
            instanceNumber <= visualStudioInstances.Length)
        {
            return visualStudioInstances[instanceNumber - 1];
        }
        Console.WriteLine("Input not accepted, try again.");
    }
}

class ConsoleProgressReporter : IProgress<ProjectLoadProgress>
{
    public void Report(ProjectLoadProgress loadProgress)
    {
        var projectDisplay = Path.GetFileName(loadProgress.FilePath);
        if (loadProgress.TargetFramework != null)
        {
            projectDisplay += $" ({loadProgress.TargetFramework})";
        }

        //Console.WriteLine($"{loadProgress.Operation,-15} {loadProgress.ElapsedTime,-15:m\\:ss\\.fffffff} {projectDisplay}");
    }
}