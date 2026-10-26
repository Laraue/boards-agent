namespace Laraue.Boards.Agent.Infrastructure;

/// <summary>Questions in the terminal, for setup and the other interactive commands. End of input counts as Enter.</summary>
public static class ConsolePrompt
{
    public static string Ask(string question)
    {
        Console.Write($"{question} ");
        // End of input (e.g. nothing piped in) counts as Enter.
        return Console.ReadLine()?.Trim() ?? "";
    }

    public static bool Confirm(string question, bool defaultYes)
    {
        var answer = Ask($"{question} [{(defaultYes ? "Y/n" : "y/N")}]");
        return answer.Length == 0 ? defaultYes : answer.StartsWith('y') || answer.StartsWith('Y');
    }

    /// <summary>
    /// A numbered list of <paramref name="options"/> after <paramref name="question"/> (and <paramref name="note"/>), and the
    /// index of the one picked; Enter picks <paramref name="defaultIndex"/>. An option's further lines are indented under it.
    /// </summary>
    public static int Choose(string question, IReadOnlyList<string> options, int defaultIndex, string? note = null, string? defaultLabel = null)
    {
        Console.WriteLine();
        Console.WriteLine(question);
        for (var i = 0; i < options.Count; i++)
        {
            Console.WriteLine($"  {i + 1}. {options[i].Replace("\n", "\n     ")}");
        }

        if (note is not null)
        {
            Console.WriteLine($"  {note}");
        }

        var numbers = Enumerable.Range(1, options.Count).Select(x => x.ToString()).ToList();
        var valid = numbers.Count == 1 ? "1" : $"{string.Join(", ", numbers[..^1])} or {numbers[^1]}";
        while (true)
        {
            var answer = Ask($"Number, or Enter for {defaultLabel ?? numbers[defaultIndex]}:");
            if (answer.Length == 0)
            {
                return defaultIndex;
            }

            if (int.TryParse(answer, out var number) && number >= 1 && number <= options.Count)
            {
                return number - 1;
            }

            Console.WriteLine($"Enter {valid}.");
        }
    }
}
