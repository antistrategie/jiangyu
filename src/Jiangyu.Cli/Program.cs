using System.CommandLine;
using Jiangyu.Cli.Commands;

var root = new RootCommand("Jiangyu — modkit for MENACE")
{
    InitCommand.Create(),
    CompileCommand.Create(),
    AssetsCommand.Create()
};

return await root.Parse(args).InvokeAsync();
