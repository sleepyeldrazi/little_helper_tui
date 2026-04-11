using System.ComponentModel;
using System.Text.Json;
using Terminal.Gui;

namespace LittleHelperTui.Dialogs;

/// <summary>
/// Dialog for browsing and viewing session logs.
/// </summary>
public class SessionsDialog : Dialog
{
    private ListView _listView;
    private List<SessionInfo> _sessions = new();

    public SessionsDialog(int? showSessionIdx = null)
    {
        Title = "Sessions";
        Width = Dim.Percent(80);
        Height = Dim.Percent(80);

        LoadSessions();

        if (showSessionIdx.HasValue && showSessionIdx.Value >= 0 && showSessionIdx.Value < _sessions.Count)
        {
            ShowSessionDetail(_sessions[showSessionIdx.Value]);
            return;
        }

        // Session list
        var listItems = new System.Collections.ObjectModel.ObservableCollection<string>(_sessions.Select((s, i) => $"{i + 1}. {s.Model} - {s.Date:yyyy-MM-dd HH:mm} - {s.DurationMs / 1000}s"));

        _listView = new ListView
        {
            X = 1,
            Y = 1,
            Width = Dim.Fill(2),
            Height = Dim.Fill(4),
            Source = new ListWrapper<string>(listItems)
        };

        _listView.OpenSelectedItem += (s, e) =>
        {
            if (e.Item >= 0 && e.Item < _sessions.Count)
            {
                ShowSessionDetail(_sessions[e.Item]);
            }
        };

        // Buttons
        var viewButton = new Button { Title = "View" };
        viewButton.Accept += (s, e) =>
        {
            if (_listView.SelectedItem >= 0 && _listView.SelectedItem < _sessions.Count)
            {
                ShowSessionDetail(_sessions[_listView.SelectedItem]);
            }
            if (e is HandledEventArgs he) he.Handled = true;
        };

        var refreshButton = new Button { Title = "Refresh" };
        refreshButton.Accept += (s, e) =>
        {
            LoadSessions();
            var items = new System.Collections.ObjectModel.ObservableCollection<string>(_sessions.Select((s, i) => $"{i + 1}. {s.Model} - {s.Date:yyyy-MM-dd HH:mm}"));
            _listView.Source = new ListWrapper<string>(items);
            if (e is HandledEventArgs he) he.Handled = true;
        };

        var closeButton = new Button { Title = "Close" };
        closeButton.Accept += (s, e) =>
        {
            Application.RequestStop();
            if (e is HandledEventArgs he) he.Handled = true;
        };

        AddButton(viewButton);
        AddButton(refreshButton);
        AddButton(closeButton);

        Add(_listView);
    }

    private void LoadSessions()
    {
        _sessions.Clear();

        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".little_helper", "logs");

        if (!Directory.Exists(logDir))
            return;

        var files = Directory.GetFiles(logDir, "*.jsonl")
            .OrderByDescending(f => File.GetLastWriteTime(f))
            .Take(50);

        foreach (var file in files)
        {
            try
            {
                var lines = File.ReadAllLines(file);
                if (lines.Length == 0) continue;

                var firstLine = lines[0];
                var lastLine = lines[^1];

                var startDoc = JsonDocument.Parse(firstLine);
                var endDoc = JsonDocument.Parse(lastLine);

                var model = startDoc.RootElement.GetProperty("model").GetString() ?? "unknown";
                var timestamp = startDoc.RootElement.GetProperty("timestamp").GetString() ?? "";
                var duration = endDoc.RootElement.TryGetProperty("duration_ms", out var dur)
                    ? dur.GetInt32()
                    : 0;

                _sessions.Add(new SessionInfo
                {
                    FilePath = file,
                    Model = model,
                    Date = DateTime.TryParse(timestamp, out var dt) ? dt : File.GetLastWriteTime(file),
                    DurationMs = duration,
                    Lines = lines.ToList()
                });
            }
            catch { /* skip malformed */ }
        }
    }

    private void ShowSessionDetail(SessionInfo session)
    {
        var detailDialog = new SessionDetailDialog(session);
        Application.Run(detailDialog);
    }

    public class SessionInfo
    {
        public string FilePath { get; set; } = "";
        public string Model { get; set; } = "";
        public DateTime Date { get; set; }
        public int DurationMs { get; set; }
        public List<string> Lines { get; set; } = new();
    }
}

/// <summary>
/// Dialog showing session details.
/// </summary>
public class SessionDetailDialog : Dialog
{
    public SessionDetailDialog(SessionsDialog.SessionInfo session)
    {
        Title = $"Session: {session.Model}";
        Width = Dim.Percent(90);
        Height = Dim.Percent(90);

        // Parse and format session entries
        var entries = new System.Collections.ObjectModel.ObservableCollection<string>();

        foreach (var line in session.Lines.Take(200)) // Limit display
        {
            try
            {
                var doc = JsonDocument.Parse(line);
                var type = doc.RootElement.GetProperty("type").GetString() ?? "unknown";

                var entry = type switch
                {
                    "session_start" => FormatSessionStart(doc.RootElement),
                    "step" => FormatStep(doc.RootElement),
                    "tool" => FormatTool(doc.RootElement),
                    "session_end" => FormatSessionEnd(doc.RootElement),
                    _ => $"[{type}]"
                };

                entries.Add(entry);
            }
            catch
            {
                entries.Add(line.Length > 100 ? line[..100] + "..." : line);
            }
        }

        if (session.Lines.Count > 200)
        {
            entries.Add($"... ({session.Lines.Count - 200} more lines)");
        }

        // List view
        var listView = new ListView
        {
            X = 1,
            Y = 1,
            Width = Dim.Fill(2),
            Height = Dim.Fill(4),
            Source = new ListWrapper<string>(entries)
        };

        // Close button
        var closeButton = new Button { Title = "Close", IsDefault = true };
        closeButton.Accept += (s, e) =>
        {
            Application.RequestStop();
            if (e is HandledEventArgs he) he.Handled = true;
        };

        AddButton(closeButton);
        Add(listView);
    }

    private static string FormatSessionStart(JsonElement el)
    {
        var model = el.GetProperty("model").GetString() ?? "unknown";
        var dir = el.GetProperty("working_directory").GetString() ?? "";
        return $"[START] {model} in {Path.GetFileName(dir)}";
    }

    private static string FormatStep(JsonElement el)
    {
        var step = el.GetProperty("step").GetInt32();
        var tokens = el.GetProperty("tokens_used").GetInt32();
        return $"[STEP {step}] {tokens} tokens";
    }

    private static string FormatTool(JsonElement el)
    {
        var tool = el.GetProperty("tool").GetString() ?? "unknown";
        var duration = el.TryGetProperty("duration_ms", out var dur) ? dur.GetInt32() : 0;
        return $"[TOOL] {tool} ({duration}ms)";
    }

    private static string FormatSessionEnd(JsonElement el)
    {
        var success = el.GetProperty("success").GetBoolean();
        var tokens = el.GetProperty("total_tokens").GetInt32();
        return $"[END] {(success ? "Success" : "Failed")}, {tokens} total tokens";
    }
}
