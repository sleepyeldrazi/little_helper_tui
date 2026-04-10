using System.Text.Json;
using Spectre.Console;
using LittleHelper;

namespace LittleHelperTui;

/// <summary>
/// Skill browser. Uses SkillDiscovery to list available skills,
/// renders them as a Spectre tree, and allows injecting skills into prompts.
/// </summary>
public static class SkillBrowser
{
    /// <summary>Browse and optionally inject a skill.</summary>
    /// <returns>Skill content to inject, or null if cancelled.</returns>
    public static string? Browse(IAnsiConsole console, string workingDir)
    {
        var discovery = new SkillDiscovery();
        discovery.Discover(workingDir);

        if (discovery.Skills.Count == 0)
        {
            console.MarkupLine("[dim]No skills found.[/]");
            console.MarkupLine("[dim]Place SKILL.md files in ~/.little_helper/skills/ or .little_helper/skills/[/]");
            return null;
        }

        // Build selection list
        var items = new List<SkillItem>();
        foreach (var skill in discovery.Skills)
            items.Add(new SkillItem(skill));

        items.Add(new SkillItem(null)); // Cancel option

        var selected = console.Prompt(
            new SelectionPrompt<SkillItem>()
                .Title("[bold]Skills:[/]")
                .PageSize(15)
                .UseConverter(item => item.SkillDef != null
                    ? $"{item.SkillDef.Name}  [dim]{item.SkillDef.Description}[/]"
                    : "[dim]Cancel[/]")
                .AddChoices(items));

        if (selected.SkillDef == null)
            return null;

        // Preview the skill
        var skillDef = selected.SkillDef;
        try
        {
            var content = File.ReadAllText(skillDef.FilePath);
            console.Write(new Panel(Markup.Escape(content.Length > 500 ? content[..500] + "..." : content))
                .Header($"[blue]{skillDef.Name}[/]")
                .Border(BoxBorder.Rounded)
                .Expand());
            console.WriteLine();

            // Ask if they want to inject
            var inject = console.Prompt(
                new ConfirmationPrompt("[bold]Inject this skill into your next prompt?[/]"));

            if (inject)
            {
                console.MarkupLine($"[green]Skill '{skillDef.Name}' will be injected.[/]");
                return content;
            }
        }
        catch (Exception ex)
        {
            console.MarkupLine($"[red]Error reading skill: {Markup.Escape(ex.Message)}[/]");
        }

        return null;
    }

    private record SkillItem(SkillDef? SkillDef);
}
