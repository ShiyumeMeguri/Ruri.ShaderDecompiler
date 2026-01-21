using System;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;

namespace Ruri.ShaderDecompiler.Utils
{
    public static class ProcessUtils
    {
        public static int RunProcess(string fileName, string arguments, out string output, out string error)
        {
            var processStartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = processStartInfo };
            
            var outputBuilder = new StringBuilder();
            var errorBuilder = new StringBuilder();

            process.OutputDataReceived += (sender, e) => { if (e.Data != null) outputBuilder.AppendLine(e.Data); };
            process.ErrorDataReceived += (sender, e) => { if (e.Data != null) errorBuilder.AppendLine(e.Data); };

            try
            {
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                process.WaitForExit();

                output = outputBuilder.ToString();
                error = errorBuilder.ToString();

                return process.ExitCode;
            }
            catch (Exception ex)
            {
                output = "";
                error = ex.Message;
                return -1;
            }
        }
    }
}
