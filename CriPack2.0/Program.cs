using System.Text;
using CriPack.Archive;

// If launched with arguments (e.g. drag-and-drop or command line), run once, then pause.
if (args.Length > 0)
{
    RunCommand(args);
    Console.WriteLine();
    Console.WriteLine("Press any key to exit...");
    Console.ReadKey(true);
    return;
}

// Otherwise: interactive mode
Console.WriteLine("CPK Tool - type 'help' for commands, 'quit' to exit.");
PrintUsage();

while (true)
{
    Console.WriteLine();
    Console.Write("> ");
    string? line = Console.ReadLine();

    if (line == null) break; // input stream closed
    line = line.Trim();
    if (line.Length == 0) continue;

    string[] parts = ParseArguments(line).ToArray();
    string command = parts[0].ToLowerInvariant();

    if (command is "quit" or "exit" or "q") break;
    if (command is "help" or "?") { PrintUsage(); continue; }
    if (command is "cls" or "clear") { Console.Clear(); continue; }

    RunCommand(parts);
}

// ---------- Command handling ----------

static void RunCommand(string[] parts)
{
    try
    {
        string command = parts[0].ToLowerInvariant();

        switch (command)
        {
            case "list" when parts.Length >= 2:
                using (var archive = new CpkArchive(parts[1]))
                {
                    foreach (var entry in archive.Entries)
                        Console.WriteLine($"{entry.FullPath}  {entry.FileSize}/{entry.ExtractSize}  @0x{entry.AbsoluteOffset:X}");
                    Console.WriteLine($"{archive.Entries.Count} files.");
                }
                break;

            case "extract" when parts.Length >= 3:
                CpkTool.ExtractAll(cpkPath: parts[1], outputDirectory: parts[2]);
                Console.WriteLine("Extraction complete.");
                break;

            case "repack" when parts.Length >= 4:
                CpkTool.RepackFromDirectory(originalCpkPath: parts[1], modifiedDirectory: parts[2], outputCpkPath: parts[3]);
                Console.WriteLine("Repack complete.");
                break;

            default:
                Console.WriteLine("Unknown command or missing arguments.");
                PrintUsage();
                break;
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error: {ex.Message}");
    }
}

static void PrintUsage()
{
    Console.WriteLine("""
        Commands:
          list    <original.cpk>
          extract <original.cpk> <output-directory>
          repack  <original.cpk> <modified-directory> <new.cpk>
          help | cls | quit

        Paths containing spaces must be wrapped in double quotes.
        You can drag a file or folder into this window to paste its path.
        """);
}

// Splits a line into arguments, honoring double quotes (drag-and-drop paths are quoted).
static IEnumerable<string> ParseArguments(string line)
{
    var current = new StringBuilder();
    var insideQuotes = false;

    foreach (var character in line)
    {
        if (character == '"')
        {
            insideQuotes = !insideQuotes;
            continue;
        }
        if (char.IsWhiteSpace(character) && !insideQuotes)
        {
            if (current.Length > 0)
            {
                yield return current.ToString();
                current.Clear();
            }
            continue;
        }
        current.Append(character);
    }
    if (current.Length > 0)
        yield return current.ToString();
}
