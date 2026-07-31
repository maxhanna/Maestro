#r "nuget: Jint, 4.1.0"

using System;
using System.IO;
using System.Text.RegularExpressions;
using Jint;

// 1. Gather all files matching the glob pattern
string searchFolder = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
if (!Directory.Exists(searchFolder))
{
    Console.WriteLine($"Directory not found: {searchFolder}");
    return 0; 
}

string[] jsFiles = Directory.GetFiles(searchFolder, "*.js", SearchOption.AllDirectories);
bool hasErrors = false;

Console.WriteLine($"Starting pure C# JavaScript linting pass on {jsFiles.Length} files...");

foreach (var filePath in jsFiles)
{
    try
    {
        string jsContent = File.ReadAllText(filePath);

        // PrepareScript parses without executing. Engine.Modules.Add was used here
        // before, but that only registers the source text — Jint defers parsing until
        // a module is actually imported, so syntax errors were never surfaced and this
        // pass silently accepted every file.
        Engine.PrepareScript(jsContent, filePath);
    }
    catch (Exception ex)
    {
        // Jint ends the message with "(<source>:line:column)" — the source is the full
        // file path, which itself contains colons on Windows, so anchor on the trailing
        // digit pair. Pulling it out makes the error line clickable in the IDE build
        // output instead of always pointing at line 1.
        var pos = Regex.Match(ex.Message, @"(\d+):(\d+)\)");
        var line = pos.Success ? pos.Groups[1].Value : "1";
        var col = pos.Success ? pos.Groups[2].Value : "1";
        Console.Error.WriteLine($"{filePath}({line},{col}): error JS0001: JavaScript Lint/Syntax Error: {ex.Message.Split('\n')[0]}");
        hasErrors = true;
    }
}

// Exit code 1 halts 'dotnet build' if any JavaScript files are broken
return hasErrors ? 1 : 0;
