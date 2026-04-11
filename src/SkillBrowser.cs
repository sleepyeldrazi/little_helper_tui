using System.ComponentModel;
using System.Text;
using Terminal.Gui;

namespace LittleHelperTui;

/// <summary>
/// Skill browser - discovers and loads SKILL.md files.
/// </summary>
public static class SkillBrowser
{
    /// <summary>
    /// Browse for skills and return selected skill content.
    /// </summary>
    public static string? Browse(string workingDir)
    {
        var skills = DiscoverSkills(workingDir);

        if (skills.Count == 0)
        {
            MessageBox.ErrorQuery("No Skills", "No skills found in ~/.little_helper/skills/ or ./.little_helper/skills/", "OK");
            return null;
        }

        var dialog = new SkillSelectionDialog(skills);
        Application.Run(dialog);
        return dialog.SelectedSkill?.Content;
    }

    private static List<SkillInfo> DiscoverSkills(string workingDir)
    {
        var skills = new List<SkillInfo>();

        // Global skills
        var globalDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".little_helper", "skills");

        if (Directory.Exists(globalDir))
        {
            foreach (var file in Directory.GetFiles(globalDir, "*.md", SearchOption.AllDirectories))
            {
                skills.Add(LoadSkill(file, "global"));
            }
        }

        // Local skills
        var localDir = Path.Combine(workingDir, ".little_helper", "skills");
        if (Directory.Exists(localDir))
        {
            foreach (var file in Directory.GetFiles(localDir, "*.md", SearchOption.AllDirectories))
            {
                skills.Add(LoadSkill(file, "local"));
            }
        }

        return skills;
    }

    private static SkillInfo LoadSkill(string path, string source)
    {
        var content = File.ReadAllText(path);
        var name = Path.GetFileNameWithoutExtension(path);

        // Try to extract title from first line
        var firstLine = content.Split('\n').FirstOrDefault()?.Trim() ?? "";
        if (firstLine.StartsWith("# "))
        {
            name = firstLine[2..].Trim();
        }

        return new SkillInfo
        {
            Name = name,
            Path = path,
            Source = source,
            Content = content
        };
    }

    private class SkillInfo
    {
        public string Name { get; set; } = "";
        public string Path { get; set; } = "";
        public string Source { get; set; } = "";
        public string Content { get; set; } = "";
    }

    /// <summary>
    /// Dialog for selecting a skill.
    /// </summary>
    private class SkillSelectionDialog : Dialog
    {
        public SkillInfo? SelectedSkill { get; private set; }

        private List<SkillInfo> _skills;
        private ListView _listView;

        public SkillSelectionDialog(List<SkillInfo> skills)
        {
            _skills = skills;
            Title = "Select Skill";
            Width = Dim.Percent(60);
            Height = Dim.Percent(70);

            // List view
            _listView = new ListView
            {
                X = 1,
                Y = 1,
                Width = Dim.Fill(2),
                Height = Dim.Fill(4),
                Source = new ListWrapper<string>(new System.Collections.ObjectModel.ObservableCollection<string>(skills.Select(s => $"{s.Name} ({s.Source})")))
            };

            _listView.OpenSelectedItem += (s, e) =>
            {
                if (e.Item >= 0 && e.Item < _skills.Count)
                {
                    SelectedSkill = _skills[e.Item];
                    Application.RequestStop();
                }
            };

            // Buttons
            var selectButton = new Button { Title = "Select", IsDefault = true };
            selectButton.Accept += (s, e) =>
            {
                if (_listView.SelectedItem >= 0 && _listView.SelectedItem < _skills.Count)
                {
                    SelectedSkill = _skills[_listView.SelectedItem];
                    Application.RequestStop();
                }
                if (e is HandledEventArgs he) he.Handled = true;
            };

            var cancelButton = new Button { Title = "Cancel" };
            cancelButton.Accept += (s, e) =>
            {
                Application.RequestStop();
                if (e is HandledEventArgs he) he.Handled = true;
            };

            AddButton(selectButton);
            AddButton(cancelButton);
            Add(_listView);
        }
    }
}
