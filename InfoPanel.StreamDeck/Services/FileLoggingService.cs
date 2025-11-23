using System;
using System.IO;

namespace InfoPanel.StreamDeck.Services
{
    /// <summary>
    /// Provides file-based logging functionality with user-controllable debug output
    /// Implements standard logging patterns with rotation and multiple log levels
    /// </summary>
    public class FileLoggingService : IDisposable
    {
        #region Fields
        
        private readonly ConfigurationService _configService;
        private readonly string _logFilePath;
        private readonly object _logLock = new();
        private StreamWriter? _logWriter;
        private bool _disposed = false;
        
        #endregion

        #region Constructor
        
        public FileLoggingService(ConfigurationService configService)
        {
            _configService = configService ?? throw new ArgumentNullException(nameof(configService));
            
            // Create log file path
            var logDirectory = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".";
            _logFilePath = Path.Combine(logDirectory, "StreamDeck-debug.log");
            
            InitializeLogging();
        }
        
        #endregion

        #region Initialization
        
        /// <summary>
        /// Initializes the logging system
        /// </summary>
        private void InitializeLogging()
        {
            try
            {
                if (_configService.IsDebugLoggingEnabled)
                {
                    // Create or append to log file
                    _logWriter = new StreamWriter(_logFilePath, append: true);
                    _logWriter.AutoFlush = true;
                    
                    // Log session start
                    WriteToLog("INFO", "=== StreamDeck Debug Session Started ===");
                    WriteToLog("INFO", $"Plugin Version: 1.0.0");
                    WriteToLog("INFO", $"Log Level: {_configService.DebugLogLevel}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FileLoggingService] Error initializing logging: {ex.Message}");
            }
        }
        
        #endregion

        #region Logging Methods
        
        /// <summary>
        /// Logs an informational message
        /// </summary>
        public void LogInfo(string message)
        {
            LogMessage("INFO", message);
        }
        
        /// <summary>
        /// Logs a warning message
        /// </summary>
        public void LogWarning(string message)
        {
            LogMessage("WARN", message);
        }
        
        /// <summary>
        /// Logs an error message
        /// </summary>
        public void LogError(string message)
        {
            LogMessage("ERROR", message);
        }
        
        /// <summary>
        /// Logs an error with exception details
        /// </summary>
        public void LogError(string message, Exception exception)
        {
            LogMessage("ERROR", $"{message}: {exception.Message}");
            LogMessage("ERROR", $"Stack Trace: {exception.StackTrace}");
        }
        
        /// <summary>
        /// Logs a debug message (only if debug logging is enabled)
        /// </summary>
        public void LogDebug(string message)
        {
            if (ShouldLogLevel("DEBUG"))
            {
                LogMessage("DEBUG", message);
            }
        }
        
        /// <summary>
        /// Logs a verbose message (only if verbose logging is enabled)
        /// </summary>
        public void LogVerbose(string message)
        {
            if (ShouldLogLevel("VERBOSE"))
            {
                LogMessage("VERBOSE", message);
            }
        }
        
        /// <summary>
        /// Logs a message with specified level
        /// </summary>
        private void LogMessage(string level, string message)
        {
            if (!_configService.IsDebugLoggingEnabled || _disposed)
                return;
            
            try
            {
                WriteToLog(level, message);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FileLoggingService] Error writing log: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Writes formatted log entry to file
        /// </summary>
        private void WriteToLog(string level, string message)
        {
            if (_logWriter == null) return;
            
            lock (_logLock)
            {
                try
                {
                    var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                    var logEntry = $"[{timestamp}] [{level}] {message}";
                    
                    _logWriter.WriteLine(logEntry);
                    
                    // Also write to console for immediate feedback
                    Console.WriteLine($"[StreamDeck] {logEntry}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[FileLoggingService] Error writing to log file: {ex.Message}");
                }
            }
        }
        
        #endregion

        #region Log Level Control
        
        /// <summary>
        /// Determines if a message should be logged based on current log level
        /// </summary>
        private bool ShouldLogLevel(string messageLevel)
        {
            if (!_configService.IsDebugLoggingEnabled)
                return false;
            
            var configuredLevel = _configService.DebugLogLevel.ToUpperInvariant();
            
            // Log level hierarchy: ERROR > WARN > INFO > DEBUG > VERBOSE
            return configuredLevel switch
            {
                "ERROR" => messageLevel == "ERROR",
                "WARN" => messageLevel is "ERROR" or "WARN",
                "INFO" => messageLevel is "ERROR" or "WARN" or "INFO",
                "DEBUG" => messageLevel is "ERROR" or "WARN" or "INFO" or "DEBUG",
                "VERBOSE" => true, // Log everything
                _ => messageLevel is "ERROR" or "WARN" or "INFO" // Default to INFO level
            };
        }
        
        #endregion

        #region Data Logging Helpers
        
        /// <summary>
        /// Logs monitoring data for debugging
        /// </summary>
        public void LogMonitoringData(string dataSource, object data)
        {
            if (ShouldLogLevel("DEBUG"))
            {
                LogMessage("DEBUG", $"[{dataSource}] Data: {data}");
            }
        }
        
        /// <summary>
        /// Logs performance metrics
        /// </summary>
        public void LogPerformanceMetric(string metricName, double value, string unit = "")
        {
            if (ShouldLogLevel("DEBUG"))
            {
                var unitText = string.IsNullOrEmpty(unit) ? "" : $" {unit}";
                LogMessage("DEBUG", $"[PERF] {metricName}: {value:F2}{unitText}");
            }
        }
        
        /// <summary>
        /// Logs sensor update information
        /// </summary>
        public void LogSensorUpdate(string sensorName, object value)
        {
            if (ShouldLogLevel("VERBOSE"))
            {
                LogMessage("VERBOSE", $"[SENSOR] {sensorName} = {value}");
            }
        }
        
        /// <summary>
        /// Logs configuration changes
        /// </summary>
        public void LogConfigChange(string setting, string oldValue, string newValue)
        {
            LogMessage("INFO", $"[CONFIG] {setting}: '{oldValue}' -> '{newValue}'");
        }
        
        /// <summary>
        /// Logs connection events
        /// </summary>
        public void LogConnectionEvent(string eventType, string details = "")
        {
            var message = string.IsNullOrEmpty(details) ? 
                $"[CONNECTION] {eventType}" : 
                $"[CONNECTION] {eventType}: {details}";
            LogMessage("INFO", message);
        }
        
        #endregion

        #region TODO: Add Custom Logging Methods
        
        // TODO: Add logging methods specific to your plugin's needs
        // Examples:
        
        // /// <summary>
        // /// Logs API call information
        // /// </summary>
        // public void LogApiCall(string endpoint, int statusCode, TimeSpan duration)
        // {
        //     LogMessage("DEBUG", $"[API] {endpoint} -> {statusCode} ({duration.TotalMilliseconds:F0}ms)");
        // }
        
        // /// <summary>
        // /// Logs database operation information
        // /// </summary>
        // public void LogDatabaseOperation(string operation, string table, TimeSpan duration)
        // {
        //     LogMessage("DEBUG", $"[DB] {operation} on {table} ({duration.TotalMilliseconds:F0}ms)");
        // }
        
        // /// <summary>
        // /// Logs file operation information
        // /// </summary>
        // public void LogFileOperation(string operation, string filePath, long fileSize = -1)
        // {
        //     var sizeText = fileSize >= 0 ? $" ({fileSize} bytes)" : "";
        //     LogMessage("DEBUG", $"[FILE] {operation}: {filePath}{sizeText}");
        // }
        
        // /// <summary>
        // /// Logs network operation information
        // /// </summary>
        // public void LogNetworkOperation(string operation, string address, TimeSpan duration)
        // {
        //     LogMessage("DEBUG", $"[NET] {operation} to {address} ({duration.TotalMilliseconds:F0}ms)");
        // }
        
        #endregion

        #region Cleanup
        
        /// <summary>
        /// Rotates log file if it gets too large
        /// </summary>
        public void RotateLogIfNeeded()
        {
            try
            {
                if (!File.Exists(_logFilePath))
                    return;
                
                var fileInfo = new FileInfo(_logFilePath);
                const long maxSizeBytes = 10 * 1024 * 1024; // 10 MB
                
                if (fileInfo.Length > maxSizeBytes)
                {
                    // Close current writer
                    _logWriter?.Close();
                    _logWriter?.Dispose();
                    
                    // Rename old log file
                    var backupPath = _logFilePath.Replace(".log", $"-backup-{DateTime.Now:yyyyMMdd-HHmmss}.log");
                    File.Move(_logFilePath, backupPath);
                    
                    // Reinitialize logging
                    InitializeLogging();
                    
                    LogInfo($"Log file rotated. Backup created: {Path.GetFileName(backupPath)}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FileLoggingService] Error rotating log file: {ex.Message}");
            }
        }
        
        #endregion

        #region IDisposable Implementation
        
        public void Dispose()
        {
            if (_disposed)
                return;
            
            try
            {
                if (_logWriter != null)
                {
                    WriteToLog("INFO", "=== StreamDeck Debug Session Ended ===");
                    _logWriter.Close();
                    _logWriter.Dispose();
                    _logWriter = null;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FileLoggingService] Error disposing: {ex.Message}");
            }
            finally
            {
                _disposed = true;
            }
        }
        
        #endregion
    }
}