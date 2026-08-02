using System.Diagnostics;
using System.Threading.Tasks;

namespace TotallyHot.ArcRouter.Tools
{
    /// <summary>
    /// A tool to run visible tests.
    /// </summary>
    public class RunVisibleTests
    {
        /// <summary>
        /// Runs `dotnet test` in the specified working directory.
        /// </summary>
        /// <param name="workingDirectory">The directory from which to run the tests.</param>
        /// <returns>The output of the test run.</returns>
        public async Task<string> RunAsync(string workingDirectory)
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = "test",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = workingDirectory
                }
            };

            process.Start();
            string output = await process.StandardOutput.ReadToEndAsync();
            string error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                return $"Error running tests: {error}";
            }

            return output;
        }
    }
}

