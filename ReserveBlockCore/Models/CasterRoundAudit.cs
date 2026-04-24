using ReserveBlockCore.Services;
using ReserveBlockCore.Utilities;
using Spectre.Console;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace ReserveBlockCore.Models
{
    public class CasterRoundAudit
    {
        public long BlockHeight { get; set; }
        public long TimeStart { get; set; }
        public int Step { get; set; }
        public string StepMessage { get; set; }
        public List<string> StepMessages { get; set; }
        public int Cycles { get; set; } //we want this to always be 1.
        private Stopwatch TimeRunning { get; set; }

        public CasterRoundAudit(long _blockHeight)
        {
            BlockHeight = _blockHeight;
            Step = 0;
            StepMessage = "Round Started";
            Cycles = 0;
            StepMessages = new List<string>();
            TimeStart = TimeUtil.GetTime();
            TimeRunning = new Stopwatch();
            TimeRunning.Start();
        }

        public bool IsOverCycle()
        {
            if(Cycles > 1)
                return true;

            return false;
        }
        public void AddStep(string _stepMessage, bool outputToConsole = false)
        {
            var stepMessage = $"[{Step}]: {StepMessage}";
            StepMessage = _stepMessage;
            Step++; 
            StepMessages.Add(stepMessage);

            if(outputToConsole)
                ThrottledOutput();
        }
        public TimeSpan GetElapsedTime()
        {
            return TimeRunning.Elapsed;
        }
        public void AddCycle()
        {
            Cycles++;
        }
        public void StopAuditTimer()
        {
            TimeRunning.Stop();
        }
        public async Task AddToCasterRoundAuditDict()
        {
            if(!Globals.CasterRoundAuditDict.ContainsKey(BlockHeight))
            {
                TimeRunning.Stop();
                while(!Globals.CasterRoundAuditDict.TryAdd(BlockHeight, this))
                {
                    await Task.Delay(100);
                }
                    
            }
        }
        private DateTime lastUpdate = DateTime.MinValue;

        private void ThrottledOutput(int milliseconds = 2000)
        {
            if ((DateTime.Now - lastUpdate).TotalMilliseconds >= milliseconds)
            {
                Output();
                lastUpdate = DateTime.Now;
            }
        }

        private void Output()
        {
            // Clear the console to make it look like the output is updating
            if (!Globals.CasterLogEnabled)
                return;

            Console.SetCursorPosition(0, 0);


            // Print the basic info of the current audit
            ConsoleWriterService.OutputValCaster($"Block Height: {BlockHeight}");
            ConsoleWriterService.OutputValCaster($"Elapsed Time: {TimeRunning.Elapsed}");
            ConsoleWriterService.OutputValCaster($"Cycles: {Cycles}");

            // Print the current step and message
            ConsoleWriterService.OutputValCaster($"Current Step: {Step}");
            ConsoleWriterService.OutputValCaster($"Step Message: {StepMessage}");
            ConsoleWriterService.OutputValCaster("\r\n");

            // Print each step message
            ConsoleWriterService.OutputValCaster("Step History:");
            if(StepMessages.Any())
            {
                ConsoleWriterService.OutputValCaster($"Total Step Messages: {StepMessages.Count()}");
            }
            else
            {
                ConsoleWriterService.OutputValCaster("No History.");
            }
            
        }
    }
}
