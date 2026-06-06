using System.CommandLine;

namespace Jiangyu.Cli.Commands.Loader;

public static class LoaderCommand
{
    public static Command Create()
    {
        return new Command("loader", "Deploy the Jiangyu loader builds into the game")
        {
            LoaderDeployCommand.Create(),
            LoaderStatusCommand.Create(),
        };
    }
}
