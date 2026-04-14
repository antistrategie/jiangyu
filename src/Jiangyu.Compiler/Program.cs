using Jiangyu.Compiler.Commands;

if (args.Length == 0)
{
    PrintUsage();
    return 1;
}

return args[0] switch
{
    "init" => await InitCommand.RunAsync(args[1..]),
    "compile" => await CompileCommand.RunAsync(args[1..]),
    "assets" => await AssetsCommand.RunAsync(args[1..]),
    _ => PrintUsage()
};

static int PrintUsage()
{
    Console.Error.WriteLine("Usage: jiangyu <command>");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Commands:");
    Console.Error.WriteLine("  init       Scaffold a new mod project");
    Console.Error.WriteLine("  compile    Compile mod assets into AssetBundles");
    Console.Error.WriteLine("  assets     Asset pipeline: index, search, export, inspect");
    return 1;
}
