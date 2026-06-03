using System.CommandLine;
using Jiangyu.Cli.Commands;
using Jiangyu.Cli.Commands.Assets;
using Jiangyu.Cli.Commands.Templates;

var root = new RootCommand("Jiangyu — modkit for MENACE")
{
    InitCommand.Create(),
    CompileCommand.Create(),
    DeployCommand.Create(),
    AssetsCommand.Create(),
    TemplatesCommand.Create(),
    UnityCommand.Create(),
    CodeCommand.Create()
};

return await root.Parse(args).InvokeAsync();
