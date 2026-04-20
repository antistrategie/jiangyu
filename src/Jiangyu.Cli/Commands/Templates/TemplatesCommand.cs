using System.CommandLine;

namespace Jiangyu.Cli.Commands.Templates;

public static class TemplatesCommand
{
    public static Command Create()
    {
        var command = new Command("templates", "Template inventory, listing, and inspection")
        {
            TemplatesIndexCommand.Create(),
            TemplatesListCommand.Create(),
            TemplatesSearchCommand.Create(),
            TemplatesInspectCommand.Create(),
            TemplatesQueryCommand.Create(),
            TemplatesBaselineCommand.Create(),
        };

        return command;
    }
}
