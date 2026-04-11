using System.ComponentModel;
using LittleHelper;
using LittleHelperTui.Views;
using Terminal.Gui;

namespace LittleHelperTui;

/// <summary>
/// Skill browser - uses core SkillDiscovery, shows name + description.
/// </summary>
public static class SkillBrowser
{
    /// <summary>
    /// Browse for skills and return selected skill content.
    /// </summary>
    public static string? Browse(string workingDir)
    {
        var discovery = new SkillDiscovery();
        discovery.Discover(workingDir);

        if (discovery.Skills.Count == 0)
        {
            MessageBox.ErrorQuery("No Skills", "No skills found in ~/.little_helper/skills/ or ./.little_helper/skills/", "OK");
            return null;
        }

        var dialog = new SkillSelectionDialog(discovery.Skills);
        Application.Run(dialog);
        return dialog.SelectedContent;
    }

    /// <summary>
    /// Dialog for selecting a skill. Shows name and description.
    /// </summary>
    private class SkillSelectionDialog : Dialog
    {
        public string? SelectedContent { get; private set; }

        private readonly IReadOnlyList<SkillDef> _skills;
        private readonly ListView _listView;

        public SkillSelectionDialog(IReadOnlyList<SkillDef> skills)
        {
            _skills = skills;
            Title = "Skills";
            Width = Dim.Percent(60);
            Height = Dim.Percent(70);
            ColorScheme = DarkColors.Dialog;

            var items = skills.Select(s => $"{s.Name}  {s.Description}").ToList();

            _listView = new ListView
            {
                X = 1,
                Y = 1,
                Width = Dim.Fill(2),
                Height = Dim.Fill(4),
                Source = new ListWrapper<string>(
                    new System.Collections.ObjectModel.ObservableCollection<string>(items))
            };

            _listView.OpenSelectedItem += (s, e) => SelectAndClose(e.Item);

            var selectButton = new Button { Title = "Select", IsDefault = true };
            selectButton.Accept += (s, e) =>
            {
                SelectAndClose(_listView.SelectedItem);
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

        private void SelectAndClose(int index)
        {
            if (index >= 0 && index < _skills.Count)
            {
                try
                {
                    SelectedContent = File.ReadAllText(_skills[index].FilePath);
                }
                catch { }
                Application.RequestStop();
            }
        }
    }
}
