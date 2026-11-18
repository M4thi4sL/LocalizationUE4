
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;
using System.Runtime.InteropServices;

namespace TranslationEditor
{
    public class CsvImportEntry
    {
        public string Key { get; set; }
        public string Namespace { get; set; }
        public string Translation { get; set; }
        public string ExistingTranslation { get; set; }

        // Original matching result: Matched, Not found, Ambiguous, etc.
        public string MatchStatus { get; set; }

        // Dynamic preview result: Will update, Unchanged, Skipped, etc.
        public string Status { get; set; }

        public InternalRecord Record { get; set; }

        public bool CanImport
        {
            get { return Status == "Will update"; }
        }
    }

   public class CsvImportPreview
    {
        public List<CsvImportEntry> Entries { get; set; }

        public int MatchedCount
        {
            get { return CountMatchStatus("Matched"); }
        }

        public int ImportCount
        {
            get { return CountStatus("Will update"); }
        }

        public int UnchangedCount
        {
            get { return CountStatus("Unchanged"); }
        }

        public int SkippedCount
        {
            get { return CountStatus("Skipped"); }
        }

        public int UnmatchedCount
        {
            get { return CountStatus("Not found"); }
        }

        public int AmbiguousCount
        {
            get { return CountStatus("Ambiguous"); }
        }

        public int DuplicateCount
        {
            get { return CountStatus("Duplicate CSV key"); }
        }

        public void UpdateStatuses(bool overwriteExisting)
        {
            foreach (CsvImportEntry entry in Entries)
            {
                if (entry.MatchStatus != "Matched")
                {
                    entry.Status = entry.MatchStatus;
                    continue;
                }

                // Nothing to change.
                if (entry.ExistingTranslation == entry.Translation)
                {
                    entry.Status = "Unchanged";
                }
                // Existing translation should be preserved.
                else if (!overwriteExisting &&
                         !String.IsNullOrEmpty(entry.ExistingTranslation))
                {
                    entry.Status = "Skipped";
                }
                else
                {
                    entry.Status = "Will update";
                }
            }
        }

        private int CountStatus(string status)
        {
            int count = 0;

            foreach (CsvImportEntry entry in Entries)
            {
                if (entry.Status == status)
                    count++;
            }

            return count;
        }

        private int CountMatchStatus(string status)
        {
            int count = 0;

            foreach (CsvImportEntry entry in Entries)
            {
                if (entry.MatchStatus == status)
                    count++;
            }

            return count;
        }
    }

    public static class CsvTranslationImporter
    {
        public static CsvImportPreview Preview(
            string fileName,
            InternalFormat document,
            string culture)
        {
            if (document == null)
                throw new ArgumentNullException("document");

            if (!document.Cultures.Contains(culture))
                throw new ArgumentException("Culture not found: " + culture);

            // Read with automatic BOM detection.
            // Supports UTF-8 and UTF-16 CSV files.
            string csv;

            using (StreamReader reader = new StreamReader(
                fileName, Encoding.UTF8, true))
            {
                csv = reader.ReadToEnd();
            }

            List<List<string>> rows = ParseCsv(csv);

            if (rows.Count < 2)
                throw new FormatException("CSV contains no translation entries.");

            int keyColumn = -1;
            int translationColumn = -1;

            for (int i = 0; i < rows[0].Count; i++)
            {
                string header = rows[0][i].Trim();

                if (header == "Key")
                    keyColumn = i;

                if (header == "SourceString")
                    translationColumn = i;
            }

            if (keyColumn < 0 || translationColumn < 0)
            {
                throw new FormatException(
                    "CSV must contain 'Key' and 'SourceString' columns.");
            }

            // Build an index of all Unreal localization records.
            Dictionary<string, List<InternalRecord>> records =
                new Dictionary<string, List<InternalRecord>>(
                    StringComparer.Ordinal);

            Dictionary<InternalRecord, string> namespaces =
                new Dictionary<InternalRecord, string>();

            foreach (InternalNamespace ns in document.Namespaces)
            {
                foreach (InternalRecord record in ns.Children)
                {
                    List<InternalRecord> matches;

                    if (!records.TryGetValue(record.Key, out matches))
                    {
                        matches = new List<InternalRecord>();
                        records.Add(record.Key, matches);
                    }

                    matches.Add(record);
                    namespaces[record] = ns.Name;
                }
            }

            // Count CSV key occurrences.
            Dictionary<string, int> csvKeyCounts =
                new Dictionary<string, int>(StringComparer.Ordinal);

            for (int i = 1; i < rows.Count; i++)
            {
                if (keyColumn >= rows[i].Count)
                    continue;

                string key = rows[i][keyColumn].Trim();

                if (key == "")
                    continue;

                if (!csvKeyCounts.ContainsKey(key))
                    csvKeyCounts.Add(key, 0);

                csvKeyCounts[key]++;
            }

            CsvImportPreview preview = new CsvImportPreview();
            preview.Entries = new List<CsvImportEntry>();

            // Match every CSV entry.
            for (int i = 1; i < rows.Count; i++)
            {
                List<string> row = rows[i];

                if (keyColumn >= row.Count ||
                    translationColumn >= row.Count)
                {
                    throw new FormatException(
                        "Invalid CSV row: " + (i + 1));
                }

                string key = row[keyColumn].Trim();
                string translation = row[translationColumn];

                CsvImportEntry entry = new CsvImportEntry();
                entry.Key = key;
                entry.Translation = translation;
                entry.MatchStatus = "Not found";
                
                if (key == "")
                {
                    entry.MatchStatus = "Not found";
                    
                }
                else if (csvKeyCounts[key] > 1)
                {
                    entry.MatchStatus = "Duplicate CSV key";
                }
                else
                {
                    List<InternalRecord> matches;

                    if (records.TryGetValue(key, out matches))
                    {
                        if (matches.Count > 1)
                        {
                            entry.MatchStatus = "Ambiguous";
                        }
                        else
                        {
                            entry.Record = matches[0];
                            entry.Namespace = namespaces[entry.Record];
                            entry.ExistingTranslation =
                                entry.Record[culture];

                            entry.MatchStatus = "Matched";
                        }
                    }
                }

                preview.Entries.Add(entry);
            }

            return preview;
        }

        // Simple RFC4180-style CSV parser.
        // Supports quoted fields, escaped quotes and multiline fields.
        private static List<List<string>> ParseCsv(string text)
        {
            List<List<string>> rows = new List<List<string>>();
            List<string> row = new List<string>();
            StringBuilder field = new StringBuilder();

            bool insideQuotes = false;

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];

                if (insideQuotes)
                {
                    if (c == '"')
                    {
                        if (i + 1 < text.Length &&
                            text[i + 1] == '"')
                        {
                            field.Append('"');
                            i++;
                        }
                        else
                        {
                            insideQuotes = false;
                        }
                    }
                    else
                    {
                        field.Append(c);
                    }
                }
                else
                {
                    if (c == '"')
                    {
                        if (field.Length != 0)
                            throw new FormatException(
                                "Unexpected quote in CSV.");

                        insideQuotes = true;
                    }
                    else if (c == ',')
                    {
                        row.Add(field.ToString());
                        field.Clear();
                    }
                    else if (c == '\r' || c == '\n')
                    {
                        row.Add(field.ToString());
                        field.Clear();

                        if (row.Count > 1 ||
                            row[0] != "")
                            rows.Add(row);

                        row = new List<string>();

                        if (c == '\r' &&
                            i + 1 < text.Length &&
                            text[i + 1] == '\n')
                            i++;
                    }
                    else
                    {
                        field.Append(c);
                    }
                }
            }

            if (insideQuotes)
                throw new FormatException(
                    "CSV contains an unclosed quoted field.");

            if (field.Length > 0 || row.Count > 0)
            {
                row.Add(field.ToString());
                rows.Add(row);
            }

            return rows;
        }
    }

    // Import preview window
    public class CsvImportDialog : Form
    {
       
        private void ApplyDarkTheme()
        {
            Color background = Color.FromArgb(30, 30, 30);
            Color surface = Color.FromArgb(37, 37, 38);
            Color foreground = Color.FromArgb(220, 220, 220);
            Color border = Color.FromArgb(60, 60, 60);
            Color accent = Color.FromArgb(55, 100, 150);

            BackColor = background;
            ForeColor = foreground;

            foreach (Control control in Controls)
            {
                control.BackColor = background;
                control.ForeColor = foreground;
            }

            // Style the translation preview grid
            foreach (Control control in Controls)
            {
                DataGridView grid = control as DataGridView;

                if (grid != null)
                {
                    grid.BackgroundColor = background;
                    grid.GridColor = border;
                    grid.BorderStyle = BorderStyle.None;

                    grid.DefaultCellStyle.BackColor = surface;
                    grid.DefaultCellStyle.ForeColor = foreground;
                    grid.DefaultCellStyle.SelectionBackColor = accent;
                    grid.DefaultCellStyle.SelectionForeColor = Color.White;

                    grid.ColumnHeadersDefaultCellStyle.BackColor =
                        Color.FromArgb(45, 45, 48);

                    grid.ColumnHeadersDefaultCellStyle.ForeColor = foreground;
                    grid.ColumnHeadersDefaultCellStyle.SelectionBackColor =
                        Color.FromArgb(45, 45, 48);

                    grid.EnableHeadersVisualStyles = false;

                    grid.RowHeadersDefaultCellStyle.BackColor = surface;
                    grid.RowHeadersDefaultCellStyle.ForeColor = foreground;
                }

                Panel panel = control as Panel;

                if (panel != null)
                    panel.BackColor = background;
            }

            // Style buttons
            foreach (Control control in GetAllControls(this))
            {
                Button button = control as Button;

                if (button != null)
                {
                    button.FlatStyle = FlatStyle.Flat;
                    button.BackColor = surface;
                    button.ForeColor = foreground;
                    button.FlatAppearance.BorderColor = border;
                }
            }
        }

        private IEnumerable<Control> GetAllControls(Control parent)
        {
            foreach (Control control in parent.Controls)
            {
                yield return control;

                foreach (Control child in GetAllControls(control))
                    yield return child;
            }
        }

        
        
        public CsvImportDialog(
            CsvImportPreview preview,
            string culture)
        {
            Text = "Import CSV translations - " + culture;
            Width = 1000;
            Height = 600;
            MinimumSize = new Size(750, 400);
            StartPosition = FormStartPosition.CenterParent;

            DataGridView grid = new DataGridView();
            grid.Dock = DockStyle.Fill;
            grid.ReadOnly = true;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.AutoSizeColumnsMode =
                DataGridViewAutoSizeColumnsMode.Fill;
            grid.SelectionMode =
                DataGridViewSelectionMode.FullRowSelect;
            grid.RowHeadersVisible = false;

            grid.Columns.Add("Key", "Key");
            grid.Columns.Add("Namespace", "Namespace");
            grid.Columns.Add("Existing", "Current translation");
            grid.Columns.Add("Incoming", "CSV translation");
            grid.Columns.Add("Status", "Status");

            foreach (CsvImportEntry entry in preview.Entries)
            {
                int index = grid.Rows.Add(
                    entry.Key,
                    entry.Namespace,
                    entry.ExistingTranslation,
                    entry.Translation,
                    entry.Status);

                if (!entry.CanImport)
                {
                    grid.Rows[index].DefaultCellStyle.ForeColor =
                        Color.Gray;
                }
            }

            Label summary = new Label();
            summary.AutoSize = true;
            summary.Padding = new Padding(8);
            summary.Text =
                "Matched: " + preview.MatchedCount +
                "    Not found: " + preview.UnmatchedCount +
                "    Ambiguous: " + preview.AmbiguousCount +
                "    Duplicate CSV keys: " + preview.DuplicateCount;

            Button importButton = new Button();
            importButton.Text =
                "Import " + preview.MatchedCount + " translations";
            importButton.AutoSize = true;
            importButton.DialogResult = DialogResult.OK;
            importButton.Enabled = preview.MatchedCount > 0;

            Button cancelButton = new Button();
            cancelButton.Text = "Cancel";
            cancelButton.AutoSize = true;
            cancelButton.DialogResult = DialogResult.Cancel;

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.Dock = DockStyle.Right;
            buttons.AutoSize = true;
            buttons.Padding = new Padding(8);
            buttons.Controls.Add(importButton);
            buttons.Controls.Add(cancelButton);

            Panel bottom = new Panel();
            bottom.Dock = DockStyle.Bottom;
            bottom.Height = 48;
            bottom.Controls.Add(buttons);
            bottom.Controls.Add(summary);

            Controls.Add(grid);
            Controls.Add(bottom);

            AcceptButton = importButton;
            CancelButton = cancelButton;
            
            ApplyDarkTheme();
        }
        
            
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(
            IntPtr hwnd,
            int attr,
            ref int attrValue,
            int attrSize);

             
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);

            int useDarkMode = 1;

            DwmSetWindowAttribute(
                Handle,
                20,
                ref useDarkMode,
                sizeof(int));
        }
    }
}
