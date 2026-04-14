using System.CommandLine;
using Jiangyu.Cli.Commands;

var root = new RootCommand("Jiangyu — modkit for MENACE");

root.Add(InitCommand.Create());
root.Add(CompileCommand.Create());
root.Add(AssetsCommand.Create());

return await root.Parse(args).InvokeAsync();
