using VerifiedXCore.Services;

namespace VerifiedXCore.Utilities
{
    public class ReadLineUtility
    {
        public static async Task<string?> ReadLine()
        {
            var readLine = Console.ReadLine();

            if(readLine == "/menu")
            {
                MainMenuReturn();
                throw new Exception("/menu was been entered. Returning to main menu");
            }

            if(readLine == "/btc")
            {

            }

            return readLine;
        }

        private static void MainMenuReturn() => VerifiedXCore.Utilities.SafeTask.Forget(MainMenuReturnAsync(), "ReadLineUtility.MainMenuReturn");

        private static async Task MainMenuReturnAsync()
        {
            var delay = Task.Delay(3000);
            Console.WriteLine("Returning you to main menu in 3 seconds.");
            await delay;
            StartupService.MainMenu();
        }
    }
}
