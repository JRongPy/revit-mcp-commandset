using RevitMCPSDK.API.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;

namespace RevitMCPCommandSet.Utils
{
    public class Logger : ILogger
    {
        private readonly string _logFilePath;
        private LogLevel _currentLogLevel = LogLevel.Info;

        public Logger()
        {
            _logFilePath = Path.Combine(GetLogsDirectoryPath(), $"mcp_command_set_{DateTime.Now:yyyyMMdd}.log");

        }

        public void Log(LogLevel level, string message, params object[] args)
        {
            if (level < _currentLogLevel)
                return;

            string formattedMessage = args.Length > 0 ? string.Format(message, args) : message;
            string logEntry = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {formattedMessage}";

            // 输出到 Debug 窗口
            // Output to debug window.
            System.Diagnostics.Debug.WriteLine(logEntry);

            // 写入日志文件
            // Write to the logfile.
            try
            {
                File.AppendAllText(_logFilePath, logEntry + Environment.NewLine);
            }
            catch
            {
                // 如果写入日志文件失败，不抛出异常
                // If writing to the logfile fails, do not throw an exception.
            }
        }

        public void Debug(string message, params object[] args)
        {
            Log(LogLevel.Debug, message, args);
        }

        public void Info(string message, params object[] args)
        {
            Log(LogLevel.Info, message, args);
        }

        public void Warning(string message, params object[] args)
        {
            Log(LogLevel.Warning, message, args);
        }

        public void Error(string message, params object[] args)
        {
            Log(LogLevel.Error, message, args);
        }

        /// <summary>
        /// Gets the path to the Logs directory
        /// </summary>
        public static string GetLogsDirectoryPath()
        {
            string appDataDirectory;
            try 
            {
                appDataDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            }
            catch
            {
                appDataDirectory = @"D:\MCP_Log";
            }
            string logsDirectory = Path.Combine(appDataDirectory, "RevitMCPCommandSet", "Logs");
            EnsureDirectoryExists(logsDirectory);
            return logsDirectory;

        }
        /// <summary>
        /// Gets the root application data directory
        /// </summary>
        public static string GetAppDataDirectoryPath()
        {
            string applicationPath = System.Reflection.Assembly.GetExecutingAssembly().Location;
            string applicationDirectory = Path.GetDirectoryName(applicationPath);

            return applicationDirectory;
        }
        /// <summary>
        /// Ensures that the specified directory exists
        /// </summary>
        /// <param name="directoryPath">The path to check and create if needed</param>
        private static void EnsureDirectoryExists(string directoryPath)
        {
            if (!Directory.Exists(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }
        }
    }
}
