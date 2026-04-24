using Spectre.Console;

namespace ReserveBlockCore.Services
{
    public class ConsoleWriterService
    {
        public static void Output(string text)
        {
            if(Globals.StopConsoleOutput != true)
            {
                Console.WriteLine(text);
            }
        }

        public static void OutputSameLine(string text)
        {
            if (Globals.StopConsoleOutput != true)
            {
                Console.Write($"\r{text}");
            }
        }

        public static void OutputMarked(string text)
        {
            if (Globals.StopConsoleOutput != true)
            {
                AnsiConsole.MarkupLine($"{text}");
            }
        }

        public static void OutputSameLineMarked(string text)
        {
            if (Globals.StopConsoleOutput != true)
            {
                AnsiConsole.Markup($"\r{text}");
            }
        }

        public static void OutputVal(string text)
        {
            if (Globals.StopValConsoleOutput != true)
            {
                int consoleWidth = Console.WindowWidth;
                string dashes = new string('-', consoleWidth - 1);
                Console.WriteLine(dashes);
                Console.WriteLine(text);
            }
        }

        /// <summary>Validator console lines for caster/consensus diagnostics. Gated by <see cref="Globals.CasterLogEnabled"/> (not <see cref="Globals.StopValConsoleOutput"/>).</summary>
        public static void OutputValCaster(string text)
        {
            if (!Globals.CasterLogEnabled || Globals.StopConsoleOutput)
                return;

            int consoleWidth = Console.WindowWidth;
            string dashes = new string('-', consoleWidth - 1);
            Console.WriteLine(dashes);
            Console.WriteLine(text);
        }

        public static void OutputValSameLine(string text)
        {
            if (Globals.StopValConsoleOutput != true)
            {
                Console.Write($"\r{text}");
            }
        }

        public static void OutputValMarked(string text)
        {
            if (Globals.StopValConsoleOutput != true)
            {
                AnsiConsole.MarkupLine($"{text}");
            }
        }

        public static void OutputValSameLineMarked(string text)
        {
            if (Globals.StopValConsoleOutput != true)
            {
                AnsiConsole.Markup($"\r{text}");
            }
        }
    }
}
