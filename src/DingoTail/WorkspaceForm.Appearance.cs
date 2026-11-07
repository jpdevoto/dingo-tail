// Copyright (C) 2026 Juan Pablo Devoto
// SPDX-License-Identifier: GPL-3.0-only

using System.Diagnostics;
using System.Globalization;

namespace DingoTail;

internal sealed partial class WorkspaceForm
{
    private HighlightPalette? highlightPalette;

    private async Task LoadHighlightPaletteAsync()
    {
        try
        {
            var rules = state.HighlightRules.ToArray();
            var palette = await Task.Run(() => new HighlightPalette(rules));
            if (!closing && highlightPalette is null)
                ApplyHighlightPalette(palette);
        }
        catch (ArgumentException error)
        {
            if (!closing)
            {
                status.Text = "Could not load highlighting rules. " + error.Message;
                var defaults = await Task.Run(() => new HighlightPalette(HighlightRule.Defaults()));
                if (!closing && highlightPalette is null)
                    ApplyHighlightPalette(defaults);
            }
        }
    }

    private void ApplyHighlightPalette(HighlightPalette palette)
    {
        highlightPalette = palette;
        foreach (var pane in panes)
            pane.SetHighlightPalette(palette);
    }

    private void ShowHighlighting()
    {
        using var dialog = AppDialog("Appearance & highlighting", new Size(750, 480));
        var fontSize = new NumericUpDown { Minimum = 10, Maximum = 24, Value = state.LogFontSize, AccessibleName = "Log font size", Width = 65 };
        Chrome.StyleField(fontSize);
        var fontBar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 36 };
        fontBar.Controls.Add(new Label { Text = "Log font size", AutoSize = true });
        fontBar.Controls.Add(fontSize);
        fontBar.Controls.Add(new Label { Text = "logical pixels", AutoSize = true });
        var initialLines = new NumericUpDown
        {
            Minimum = InitialLineCount.Minimum, Maximum = InitialLineCount.Maximum,
            Value = state.InitialLines, AccessibleName = "Initial lines to load", Width = 85
        };
        Chrome.StyleField(initialLines);
        var historyBar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 36 };
        historyBar.Controls.Add(new Label { Text = "Initial lines to load", AutoSize = true });
        historyBar.Controls.Add(initialLines);
        var historyHelp = new Label
        {
            Dock = DockStyle.Top, Height = 40, ForeColor = Chrome.Muted,
            Text = "Applies when starting a reader. Restart running readers to use a changed value. Byte limits may result in fewer lines."
        };

        var editor = new DataGridView
        {
            AccessibleName = "Ordered highlighting rules", Dock = DockStyle.Fill, AllowUserToAddRows = false,
            AllowUserToDeleteRows = false, AllowUserToOrderColumns = false, RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false,
            BackgroundColor = Chrome.Frame, GridColor = Chrome.Edge, BorderStyle = BorderStyle.None,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, EnableHeadersVisualStyles = false,
            ColumnHeadersDefaultCellStyle = new() { BackColor = Chrome.Panel, ForeColor = Chrome.Text },
            DefaultCellStyle = new()
            {
                BackColor = Chrome.Panel, ForeColor = Chrome.Text, SelectionBackColor = Chrome.Hover,
                SelectionForeColor = Chrome.Text
            }
        };
        editor.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "On", FillWeight = 42 });
        editor.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Name" });
        editor.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Pattern", FillWeight = 150 });
        editor.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "Regex", FillWeight = 55 });
        editor.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "Match case", FillWeight = 80 });
        editor.Columns.Add(new DataGridViewButtonColumn { HeaderText = "Foreground", FlatStyle = FlatStyle.Flat });
        editor.Columns.Add(new DataGridViewButtonColumn { HeaderText = "Background", FlatStyle = FlatStyle.Flat });
        editor.CellFormatting += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex is not (5 or 6) ||
                !TryRgb(Convert.ToString(e.Value) ?? "", out var rgb) || SystemInformation.HighContrast)
                return;
            var color = Color.FromArgb(rgb | unchecked((int)0xff000000));
            var text = color.GetBrightness() >= 0.5f ? Color.Black : Color.White;
            e.CellStyle!.BackColor = e.CellStyle.SelectionBackColor = color;
            e.CellStyle.ForeColor = e.CellStyle.SelectionForeColor = text;
        };
        editor.CellContentClick += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex is not (5 or 6))
                return;
            var cell = editor.Rows[e.RowIndex].Cells[e.ColumnIndex];
            using var picker = new ColorDialog { FullOpen = true };
            if (TryRgb(Convert.ToString(cell.Value) ?? "", out var rgb))
                picker.Color = Color.FromArgb(rgb | unchecked((int)0xff000000));
            if (picker.ShowDialog(dialog) == DialogResult.OK)
                cell.Value = $"#{picker.Color.ToArgb() & 0xffffff:X6}";
        };
        foreach (DataGridViewColumn column in editor.Columns)
            column.SortMode = DataGridViewColumnSortMode.NotSortable;
        foreach (var rule in state.HighlightRules)
            editor.Rows.Add(rule.Enabled, rule.Name, rule.Pattern, rule.Regex, rule.CaseSensitive,
                $"#{rule.Foreground:X6}", $"#{rule.Background:X6}");

        var note = new Label
        {
            Dock = DockStyle.Top, Height = 34, Padding = new Padding(8, 6, 8, 0),
            Text = "First enabled match wins. Select a color swatch to open the color picker."
        };
        var error = new Label
        {
            Dock = DockStyle.Bottom, Height = 48, Padding = new Padding(8, 3, 8, 0),
            ForeColor = Color.FromArgb(255, 155, 144), AccessibleName = "Highlighting validation",
        };
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(5) };
        Button ActionButton(string text, Action action)
        {
            var button = Chrome.TextButton(text);
            button.Click += (_, _) => action();
            actions.Controls.Add(button);
            return button;
        }
        void MoveRule(int offset)
        {
            editor.EndEdit();
            if (editor.CurrentRow is not { } row)
                return;
            var index = row.Index + offset;
            if (index < 0 || index >= editor.Rows.Count)
                return;
            editor.Rows.Remove(row);
            editor.Rows.Insert(index, row);
            editor.CurrentCell = row.Cells[0];
        }
        ActionButton("Add rule", () =>
        {
            var index = editor.Rows.Add(true, "New rule", "", false, false, "#D8DFE9", "#12151B");
            editor.CurrentCell = editor.Rows[index].Cells[2];
            editor.BeginEdit(true);
        });
        ActionButton("Remove rule", () =>
        {
            if (editor.CurrentRow is { } row)
                editor.Rows.Remove(row);
        });
        ActionButton("Move up", () => MoveRule(-1));
        ActionButton("Move down", () => MoveRule(1));
        var apply = Chrome.TextButton("Apply and save", primary: true);
        actions.Controls.Add(apply);
        var cancel = ActionButton("Cancel", () => dialog.Close());
        dialog.CancelButton = cancel;

        apply.Click += async (_, _) =>
        {
            editor.EndEdit();
            error.Text = "";
            var rules = new List<HighlightRule>();
            foreach (DataGridViewRow row in editor.Rows)
            {
                string Cell(int index) => Convert.ToString(row.Cells[index].Value) ?? "";
                var pattern = Cell(2);
                if (pattern.Length == 0 || !TryRgb(Cell(5), out var foreground) || !TryRgb(Cell(6), out var background))
                {
                    error.Text = $"Rule {row.Index + 1} needs a pattern and two #RRGGBB colors.";
                    editor.CurrentCell = row.Cells[pattern.Length == 0 ? 2 : 5];
                    editor.Focus();
                    return;
                }
                rules.Add(new(Cell(1), pattern, row.Cells[0].Value is true, row.Cells[3].Value is true,
                    row.Cells[4].Value is true, foreground, background));
            }

            editor.Enabled = actions.Enabled = fontBar.Enabled = historyBar.Enabled = false;
            try
            {
                var palette = await Task.Run(() => new HighlightPalette(rules));
                if (dialog.IsDisposed || closing)
                    return;
                var previousFont = state.LogFontSize;
                var previousInitialLines = state.InitialLines;
                state.InitialLines = (int)initialLines.Value;
                state.LogFontSize = (int)fontSize.Value;
                var previous = state.HighlightRules;
                state.HighlightRules = rules;
                if (!Save())
                {
                    state.HighlightRules = previous;
                    state.LogFontSize = previousFont;
                    state.InitialLines = previousInitialLines;
                    error.Text = "Could not save appearance settings. Check workspace access and retry.";
                    return;
                }
                ApplyHighlightPalette(palette);
                foreach (var pane in panes) pane.SetLogFont(state.LogFontSize);
                dialog.DialogResult = DialogResult.OK;
            }
            catch (ArgumentException validation)
            {
                if (!dialog.IsDisposed)
                    error.Text = validation.Message;
            }
            finally
            {
                if (!dialog.IsDisposed)
                    editor.Enabled = actions.Enabled = fontBar.Enabled = historyBar.Enabled = true;
            }
        };

        dialog.Controls.Add(editor);
        dialog.Controls.Add(note);
        dialog.Controls.Add(historyHelp);
        dialog.Controls.Add(historyBar);
        dialog.Controls.Add(fontBar);
        dialog.Controls.Add(error);
        dialog.Controls.Add(actions);
        dialog.ShowDialog(this);
    }

    private static bool TryRgb(string value, out int rgb)
    {
        var text = value.Trim().TrimStart('#');
        rgb = 0;
        return text.Length == 6 && int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out rgb);
    }

    private static Form AppDialog(string title, Size size)
    {
        var dialog = new Form
        {
            Text = title, StartPosition = FormStartPosition.CenterParent, AutoScaleMode = AutoScaleMode.Dpi,
            ClientSize = size, ShowInTaskbar = false, MinimizeBox = false, MaximizeBox = false
        };
        Chrome.StyleForm(dialog);
        return dialog;
    }

    private static Stream AppResource(string name) =>
        typeof(WorkspaceForm).Assembly.GetManifestResourceStream("DingoTail." + name)!;

    private void ShowAbout()
    {
        using var dialog = AppDialog("About DingoTail", new Size(510, 520));
        using var mascotStream = AppResource("Mascot.png");
        using var wordmarkStream = AppResource("Wordmark.png");
        using var mascot = Image.FromStream(mascotStream);
        using var wordmark = Image.FromStream(wordmarkStream);
        var header = new TableLayoutPanel { Dock = DockStyle.Top, Height = 250, ColumnCount = 1, Padding = new Padding(12) };
        header.RowStyles.Add(new RowStyle(SizeType.Absolute, 105));
        header.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        header.RowStyles.Add(new RowStyle(SizeType.Absolute, 65));
        header.Controls.Add(new PictureBox { Image = mascot, SizeMode = PictureBoxSizeMode.Zoom, Dock = DockStyle.Fill,
            AccessibleName = "Dingo mascot" });
        header.Controls.Add(new PictureBox { Image = wordmark, SizeMode = PictureBoxSizeMode.Zoom, Dock = DockStyle.Fill,
            AccessibleName = "DingoTail wordmark" });
        header.Controls.Add(new Label
        {
            Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
            Text = $"Version {typeof(WorkspaceForm).Assembly.GetName().Version}\nCopyright © 2026 Juan Pablo Devoto\n" +
                "Code: GNU GPL v3 only · No warranty\nDingoTail name and logos: rights reserved",
        });
        var links = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(12, 0, 12, 5) };
        foreach (var (label, url) in new[]
        {
            ("dingodicom.com", "https://dingodicom.com/"),
            ("GitHub · jpdevoto/dingo-tail", "https://github.com/jpdevoto/dingo-tail"),
        })
        {
            var link = new LinkLabel
            {
                Text = label, AutoSize = true, LinkColor = Chrome.Accent, ActiveLinkColor = Chrome.Text,
                VisitedLinkColor = Chrome.Accent, Margin = new Padding(3, 3, 15, 3),
            };
            link.Links.Add(0, label.Length, url);
            link.LinkClicked += (_, e) =>
            {
                try { Process.Start(new ProcessStartInfo((string)e.Link!.LinkData!) { UseShellExecute = true }); }
                catch (System.ComponentModel.Win32Exception failure) { Chrome.Message(dialog, "Could not open link", failure.Message); }
            };
            links.Controls.Add(link);
        }
        var license = new TextBox
        {
            Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
            AccessibleName = "License and branding terms",
        };
        Chrome.StyleField(license);
        license.Text = string.Join(Environment.NewLine + Environment.NewLine,
            new[] { "Copyright.txt", "Branding.txt", "License.txt" }.Select(name =>
            {
                using var reader = new StreamReader(AppResource(name));
                return reader.ReadToEnd().TrimEnd();
            }));
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
        var close = Chrome.TextButton("Close", primary: true);
        close.DialogResult = DialogResult.OK;
        bottom.Controls.Add(close);
        dialog.CancelButton = close;
        dialog.Controls.Add(license);
        dialog.Controls.Add(links);
        dialog.Controls.Add(header);
        dialog.Controls.Add(bottom);
        dialog.Shown += (_, _) => links.Controls[0].Focus();
        dialog.ShowDialog(this);
    }
}


