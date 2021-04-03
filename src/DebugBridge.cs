﻿using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.IO;
using System.IO.Compression;
using System.Globalization;
using System.Net;
using Serilog.Core;
using Avalonia.Interactivity;

namespace QuestPatcher
{
    public class DebugBridge
    {
        private static readonly CompareInfo compareInfo = new CultureInfo((int) CultureTypes.AllCultures).CompareInfo;
        private const string DEFAULT_APP_ID = "com.AnotherAxiom.GorillaTag"; // Used if no appId.txt is present in appdata

        public string APP_ID { get; }
        private string ADB_LOG_PATH;

        private MainWindow window;
        private Logger logger;
        private bool adbOnPath = false;
        private Process? logcatProcess;

        public DebugBridge(MainWindow window)
        {
            this.window = window;
            this.logger = window.Logger;
            this.ADB_LOG_PATH = window.DATA_PATH + "adb.log";

            try
            {
                this.APP_ID = File.ReadAllText(window.DATA_PATH + "appId.txt");
                logger.Information("Read app ID " + APP_ID + " from appdata");
            }   catch(FileNotFoundException)
            {
                logger.Information("Using default app ID: " + DEFAULT_APP_ID);
                this.APP_ID = DEFAULT_APP_ID;
            }
        }

        // Replaces all support ADB placeholders in this set of command arguments
        // Currently, there is only one. {app-id} is replaced with the chosen app.
        private string HandlePlaceholders(string command)
        {
            command = command.Replace("{app-id}", APP_ID);
            return command;
        }

        private bool ContainsIgnoreCase(string str, string lookingFor)
        {
            return compareInfo.IndexOf(str, lookingFor, CompareOptions.IgnoreCase) >= 0;
        }

        private Process createStartInfo(string command)
        {
            Process process = new Process();
            process.StartInfo.FileName = (adbOnPath ? "" : window.DATA_PATH + "platform-tools/") + (OperatingSystem.IsWindows() ? "adb.exe" : "adb");
            process.StartInfo.Arguments = HandlePlaceholders(command);
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.CreateNoWindow = true;

            return process;
        }

        // Runs an ADB command with arguments command. (this should not include the "adb" itself)
        // Returns the error output and the standard output concatenated together
        // Task will complete once this command exits.
        public async Task<string> RunCommandAsync(string command)
        {
            Process process = createStartInfo(command);

            logger.Verbose("Executing ADB command: adb " + process.StartInfo.Arguments);

            process.Start();

            string errorOutput = await process.StandardError.ReadToEndAsync();
            string output = await process.StandardOutput.ReadToEndAsync();

            logger.Verbose("Standard output: " + output);
            logger.Verbose("Error output: " + errorOutput);

            await process.WaitForExitAsync();

            if (ContainsIgnoreCase(output, "error") || ContainsIgnoreCase(output, "failed"))
            {
                throw new Exception(output);
            }
            string fullOutput = errorOutput + output;

            return fullOutput;
        }

        private async Task CheckIfAdbOnPath()
        {
            adbOnPath = true;

            try
            {
                await RunCommandAsync("version");
            }   catch (Exception) // Thrown if the file doesn't exist
            {
                adbOnPath = false;
            }
        }

        // Downloads the Android platform-tools if they aren't present
        public async Task InstallIfMissing()
        {
            await CheckIfAdbOnPath();
            if(adbOnPath)
            {
                logger.Information("Located ADB installation on PATH");
                return;
            }

            if(Directory.Exists(window.DATA_PATH + "platform-tools/"))
            {
                logger.Information("Platform-tools already installed");
                return;
            }

            WebClient webClient = new WebClient();

            logger.Information("Installing platform-tools!");
            await webClient.DownloadFileTaskAsync(FindPlatformToolsLink(), window.TEMP_PATH + "platform-tools.zip");
            logger.Information("Extracting . . .");
            await Task.Run(() => {
                ZipFile.ExtractToDirectory(window.TEMP_PATH + "platform-tools.zip", window.DATA_PATH);
            });
            File.Delete(window.TEMP_PATH + "platform-tools.zip");

            if(!OperatingSystem.IsWindows())
            {
                logger.Information("Making ADB executable . . .");
                await MakeAdbExecutable();
            }

            logger.Information("Done!");
        }

        // Uses chmod to make the downloaded platform-tools executable on linux - this avoids having to close QP and do it manually
        private async Task MakeAdbExecutable()
        {
            Process process = new Process();

            string command = "chmod +x " + window.DATA_PATH + "platform-tools/adb";

            process.StartInfo.FileName = "/bin/bash";
            process.StartInfo.Arguments = "-c \" " + command.Replace("\"", "\\\"") + " \"";
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.CreateNoWindow = true;
            process.Start();

            string output = process.StandardOutput.ReadToEnd();
            string errorOutput = process.StandardError.ReadToEnd();
            logger.Verbose("Output: " + output);
            logger.Verbose("Error output: " + errorOutput);

            await process.WaitForExitAsync();
        }

        // Returns the correct platform-tools download link for the installed OS
        private string FindPlatformToolsLink()
        {
            if (OperatingSystem.IsWindows())
            {
                return "https://dl.google.com/android/repository/platform-tools-latest-windows.zip";
            }
            else if (OperatingSystem.IsLinux())
            {
                return "https://dl.google.com/android/repository/platform-tools-latest-linux.zip";
            }
            else if (OperatingSystem.IsMacOS())
            {
                return "https://dl.google.com/android/repository/platform-tools-latest-darwin.zip";
            }

            throw new Exception("ADB is not available for your operating system!");
        }

        public void OnStartLogcatClick(object? sender, RoutedEventArgs args)
        {
            if(logcatProcess != null)
            {
                // Kill the existing ADB process
                window.LogcatButton.Content = "Start ADB Log"; 
                logger.Verbose("Killing logcat process");
                logcatProcess.Kill();
                logcatProcess = null;
                return;
            }   else
            {
                window.LogcatButton.Content = "Stop ADB Log";
                File.Delete(ADB_LOG_PATH); // Avoid appending to the existing
            }

            TextWriter outputWriter = new StreamWriter(File.OpenWrite(ADB_LOG_PATH));

            logcatProcess = createStartInfo("logcat");
            logcatProcess.EnableRaisingEvents = true;

            // Redirect standard output to the ADB log file
            logcatProcess.OutputDataReceived += delegate (object sender, DataReceivedEventArgs args)   {
                try
                {
                    outputWriter.WriteLine(args.Data);
                }
                catch (ObjectDisposedException)
                {
                    logger.Verbose("ADB attempted to send data after it was closed");
                }
            };

            logcatProcess.Exited += delegate (object? sender, EventArgs args)
            {
                outputWriter.Close();
            };

            logger.Verbose("Starting logcat");
            logcatProcess.Start();
            logcatProcess.BeginOutputReadLine();
        }

        public void OnOpenLogsClick(object? sender, RoutedEventArgs args)
        {
            Process.Start(new ProcessStartInfo()
            {
                FileName = window.DATA_PATH,
                UseShellExecute = true,
                Verb = "open"
            });
        }
    }
}