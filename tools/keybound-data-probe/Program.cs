using System;
using System.IO;
using System.Linq;
using System.Text;
using Lumina;
using Lumina.Data;
using Lumina.Excel;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length != 1 && args.Length != 3 && args.Length != 4)
        {
            Console.Error.WriteLine("Usage: keybound-data-probe <sqpack-path> [--export-words <output.tsv> | --resolve-ids <input.tsv> <output.tsv>]");
            return 2;
        }

        var data = new GameData(Path.GetFullPath(args[0]), new LuminaOptions
        {
            DefaultExcelLanguage = Language.ChineseSimplified
        });
        if (args.Length == 3)
        {
            if (!String.Equals(args[1], "--export-words", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine("Unknown option: " + args[1]);
                return 2;
            }
            return ExportWords(data, Path.GetFullPath(args[2]));
        }
        if (args.Length == 4)
        {
            if (!String.Equals(args[1], "--resolve-ids", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine("Unknown option: " + args[1]);
                return 2;
            }
            return ResolveIds(data, Path.GetFullPath(args[2]), Path.GetFullPath(args[3]));
        }

        string[] words = { "key", "type", "gold", "saucer", "brawl", "arcade", "mini", "text", "word" };
        var matches = data.Excel.SheetNames
            .Where(name => words.Any(word => name.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0))
            .OrderBy(name => name)
            .ToArray();

        Console.WriteLine("Sheet count: " + data.Excel.SheetNames.Count);
        foreach (string name in matches)
            Console.WriteLine(name);

        Dump(data, "KTGTypeWordProgram");
        Dump(data, "KTGTypeWordTextData");
        return 0;
    }

    private static int ResolveIds(GameData data, string inputPath, string outputPath)
    {
        string directory = Path.GetDirectoryName(outputPath);
        if (!String.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        var sheets = new System.Collections.Generic.Dictionary<string, ExcelSheet<RawRow>>(
            StringComparer.OrdinalIgnoreCase);
        using var writer = new StreamWriter(outputPath, false, new UTF8Encoding(false));
        writer.WriteLine("# kind\tid\tname");
        foreach (string raw in File.ReadLines(inputPath))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                continue;
            string[] parts = line.Split('\t');
            if (parts.Length < 2 ||
                !UInt32.TryParse(parts[1], System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out uint rowId))
                continue;
            string sheetName = String.Equals(parts[0], "action", StringComparison.OrdinalIgnoreCase)
                ? "Action"
                : String.Equals(parts[0], "bnpc", StringComparison.OrdinalIgnoreCase)
                    ? "BNpcName"
                    : null;
            if (sheetName == null)
                continue;
            if (!sheets.TryGetValue(sheetName, out ExcelSheet<RawRow> sheet))
            {
                RawExcelSheet rawSheet = data.Excel.GetRawSheet(sheetName, Language.ChineseSimplified);
                sheet = new ExcelSheet<RawRow>(rawSheet);
                sheets[sheetName] = sheet;
            }
            string name;
            try
            {
                RawRow row = sheet.GetRow(rowId);
                name = Clean(row.ReadColumn(0));
            }
            catch
            {
                name = String.Empty;
            }
            writer.Write(parts[0]);
            writer.Write('\t');
            writer.Write(parts[1].ToUpperInvariant());
            writer.Write('\t');
            writer.WriteLine(name);
        }
        return 0;
    }

    private static int ExportWords(GameData data, string outputPath)
    {
        RawExcelSheet sheet = data.Excel.GetRawSheet("KTGTypeWordTextData", Language.ChineseSimplified);
        if (sheet.Columns.Count < 2)
        {
            Console.Error.WriteLine("KTGTypeWordTextData has fewer than two columns.");
            return 1;
        }

        string directory = Path.GetDirectoryName(outputPath);
        if (!String.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var typed = new ExcelSheet<RawRow>(sheet);
        int count = 0;
        using (var writer = new StreamWriter(outputPath, false, new UTF8Encoding(false)))
        {
            writer.WriteLine("# row_id\tdisplay_text\tinput_text");
            for (int index = 0; index < typed.Count; index++)
            {
                RawRow row = typed.GetRowAt(index);
                string display = Clean(row.ReadColumn(0));
                string input = Clean(row.ReadColumn(1));
                if (!IsChineseQuestion(display, input))
                    continue;
                writer.Write(row.RowId);
                writer.Write('\t');
                writer.Write(display);
                writer.Write('\t');
                writer.WriteLine(input);
                count++;
            }
        }

        Console.WriteLine("Exported " + count + " rows: " + outputPath);
        return 0;
    }

    private static bool IsChineseQuestion(string display, string input)
    {
        bool hasHan = display.Any(value => value >= '\u4e00' && value <= '\u9fff');
        return hasHan && input.Length != 0 && input.All(value =>
            (value >= 'A' && value <= 'Z') ||
            (value >= 'a' && value <= 'z') || value == 'ü' || value == 'Ü');
    }

    private static string Clean(object value)
    {
        return value == null ? String.Empty : value.ToString()
            .Replace("\t", " ").Replace("\r", " ").Replace("\n", " ");
    }

    private static void Dump(GameData data, string name)
    {
        Console.WriteLine();
        Console.WriteLine("SHEET " + name);
        RawExcelSheet sheet = data.Excel.GetRawSheet(name, Language.ChineseSimplified);
        Console.WriteLine("Rows=" + sheet.Count + " Columns=" + sheet.Columns.Count);
        for (int column = 0; column < sheet.Columns.Count; column++)
            Console.WriteLine("COL " + column + " " + sheet.Columns[column].Type + " @" + sheet.Columns[column].Offset);

        var typed = new ExcelSheet<RawRow>(sheet);
        for (int index = 0; index < typed.Count; index++)
        {
            RawRow row = typed.GetRowAt(index);
            string[] values = Enumerable.Range(0, sheet.Columns.Count)
                .Select(column => Format(row.ReadColumn(column)))
                .ToArray();
            Console.WriteLine(row.RowId + "\t" + String.Join("\t", values));
        }
    }

    private static string Format(object value)
    {
        if (value == null)
            return "<null>";
        return value.ToString().Replace("\t", " ").Replace("\r", " ").Replace("\n", " ");
    }
}
