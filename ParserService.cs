
        //private void DownloadFileFromFtp(string ftpUrl, string username, string password, string localPath)
        //{
        //    FtpWebRequest request = (FtpWebRequest)WebRequest.Create(ftpUrl);
        //    request.Method = WebRequestMethods.Ftp.DownloadFile;
        //    request.Credentials = new NetworkCredential(username, password);

        //    using (FtpWebResponse response = (FtpWebResponse)request.GetResponse())
        //    using (Stream responseStream = response.GetResponseStream())
        //    using (FileStream fileStream = new FileStream(localPath, FileMode.Create))
        //    {
        //        responseStream.CopyTo(fileStream);
        //    }


// Add required namespaces for SFTP and logging
using Renci.SshNet;
using Serilog;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Data.SqlClient;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.ServiceProcess;
using System.Text;
using System.Threading.Tasks;
using static Smartattomtaxroll.Program;
//using Serilog.Sinks.Console;


namespace SmartAttomTaxroll
    {
        public partial class ParserService : ServiceBase
        {
            public ParserService()
            {
                InitializeComponent();
            }

            internal void OnStart(string[] args)
            {
                try
                {
                // Download files from SFTP to local folder
                var downloader = new AttomFtpDownloader(Log.Logger);
                downloader.Run();

                var processor = new AttomFileProcessor(Config.extractFolder, Config.connString);
                    processor.Run();
                }
                catch (Exception ex)
                {
                    Log.Fatal(ex, "Unexpected error in Windows Service FTP downloader.");
                }
                finally
                {
                    Log.CloseAndFlush();
                }
        }

            protected override void OnStop()
            {
            }
        }   

    // SFTP file downloader class
    public class AttomFtpDownloader
    {
        private readonly ILogger _log;

        public AttomFtpDownloader(ILogger log)
        {
            _log = log;
        }

        public void Run()
        {
            try
            {
                // ✅ Ensure local folders exist
                if (!Directory.Exists(Config.LocalFolder))
                {
                    Directory.CreateDirectory(Config.LocalFolder);
                    _log.Information("Created local folder: {Folder}", Config.LocalFolder);
                }
                else
                {
                    _log.Information("Local folder already exists: {Folder}", Config.LocalFolder);
                }

                if (!Directory.Exists(Config.ExtractFolder))
                {
                    Directory.CreateDirectory(Config.ExtractFolder);
                    _log.Information("Created extract folder: {Folder}", Config.ExtractFolder);
                }
                else
                {
                    _log.Information("Extract folder already exists: {Folder}", Config.ExtractFolder);
                }

                var client = new SftpClient(Config.SftpHost, Config.SftpPort, Config.SftpUser, Config.SftpPass);

                _log.Information("Connecting to SFTP: {Host}:{Port}", Config.SftpHost, Config.SftpPort);
                client.Connect();

                if (!client.IsConnected)
                {
                    _log.Error("Failed to connect to SFTP server {Host}", Config.SftpHost);
                    return;
                }

                _log.Information("Connected to SFTP server. Current dir: {Pwd}", client.WorkingDirectory);

                // ✅ Ensure remote folders exist
                EnsureRemoteFolder(client, Config.RemoteOutgoing);
                EnsureRemoteFolder(client, Config.RemoteProcessed);
                EnsureRemoteFolder(client, Config.RemoteTroubleshoot);

                // List files in Outgoing
                var files = client.ListDirectory(Config.RemoteOutgoing)
                                  .Where(f => !f.IsDirectory && !f.Name.StartsWith("."))
                                  .ToList();

                if (files.Count == 0)
                {
                    _log.Information("No files available in remote folder {Remote}", Config.RemoteOutgoing);
                }

                foreach (var file in files)
                {
                    if (!client.IsConnected)
                    {
                        _log.Error("Failed to connect to SFTP server {Host}", Config.SftpHost);
                        client.Connect();
                        _log.Error("Connected to SFTP server {Host}", Config.SftpHost);
                    }
                    bool match = Config.SeriesKeys.Any(k => file.Name.StartsWith(k, StringComparison.OrdinalIgnoreCase));
                    if (!match)
                    {
                        _log.Information("Skipping file {File}, no matching series key.", file.Name);
                        continue;
                    }

                    string localFilePath = Path.Combine(Config.LocalFolder, file.Name);

                    _log.Information("Preparing to download file {File} -> {LocalFile}", file.FullName, localFilePath);

                    bool success = false;
                    int attempt = 0;

                    while (!success && attempt < Config.MaxRetries)
                    {
                        attempt++;
                        try
                        {
                            using (var fs = new FileStream(localFilePath, FileMode.Create, FileAccess.Write))
                            {
                                ulong totalBytes = (ulong)file.Attributes.Size;
                                double totalMB = totalBytes / 1024.0 / 1024.0;

                                int lastPercent = -1;

                                client.DownloadFile(file.FullName, fs, downloaded =>
                                {
                                    double percent = (downloaded / (double)totalBytes) * 100;
                                    int currentPercent = (int)percent;
                                    double downloadedMB = downloaded / 1024.0 / 1024.0;

                                    if (currentPercent != lastPercent)
                                    {
                                        Console.Write($"\rDownloading {file.Name}: {currentPercent}% ({downloadedMB:N2} MB / {totalMB:N2} MB)   ");
                                        lastPercent = currentPercent;
                                    }
                                });
                            }
                            Console.WriteLine();

                            _log.Information("Successfully downloaded file {File} on attempt {Attempt}.", file.Name, attempt);
                            success = true;

                            // ✅ Extract ZIP after download
                            if (Path.GetExtension(localFilePath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
                            {
                                string extractPath = Path.Combine(Config.ExtractFolder, Path.GetFileNameWithoutExtension(file.Name));
                                if (!Directory.Exists(extractPath))
                                    Directory.CreateDirectory(extractPath);

                                ZipFile.ExtractToDirectory(localFilePath, extractPath);
                                _log.Information("Extracted ZIP {File} to {ExtractPath}", localFilePath, extractPath);
                            }

                            // ✅ Move remote file to Processed
                            string processedPath = Config.RemoteProcessed + file.Name;
                            client.RenameFile(file.FullName, processedPath);
                            _log.Information("Moved remote file {File} to {Processed}", file.FullName, processedPath);
                        }
                        catch (Exception ex)
                        {
                            _log.Error(ex, "Error downloading file {File} (Attempt {Attempt}/{Max})", file.Name, attempt, Config.MaxRetries);
                            if (attempt == Config.MaxRetries)
                            {
                                try
                                {
                                    // Move to Troubleshoot after failure
                                    string troubleshootPath = Config.RemoteTroubleshoot + file.Name;
                                    client.RenameFile(file.FullName, troubleshootPath);
                                    _log.Warning("Moved failed file {File} to {Troubleshoot}", file.FullName, troubleshootPath);
                                }
                                catch (Exception moveEx)
                                {
                                    _log.Error(moveEx, "Failed to move file {File} to Troubleshoot.", file.FullName);
                                }
                            }
                        }
                    }
                }

                client.Disconnect();
                _log.Information("Disconnected from SFTP server.");
            }
            catch (Exception ex)
            {
                _log.Fatal(ex, "Unexpected error in FTP downloader.");
            }
        }
        private void EnsureRemoteFolder(SftpClient client, string remotePath)
        {
            if (!client.Exists(remotePath))
            {
                _log.Warning("Remote folder {RemotePath} does not exist. Creating recursively...", remotePath);
                CreateRemoteDirectoryRecursive(client, remotePath);
                _log.Information("Created remote folder: {RemotePath}", remotePath);
            }
            else
            {
                _log.Information("Remote folder already exists: {RemotePath}", remotePath);
            }
        }

        private void CreateRemoteDirectoryRecursive(SftpClient client, string path)
        {
            string current = "";
            foreach (string part in path.Trim('/').Split('/'))
            {
                current += "/" + part;
                if (!client.Exists(current))
                {
                    client.CreateDirectory(current);
                }
            }
        }
    }

    public class AttomFileProcessor
    {
        private readonly string _extractFolder;
        private readonly string _connectionString;

        // File type mapping (priority + parser type)
        // File type mapping (priority + parser type)
        private readonly Dictionary<int, string> _fileTypePriority = new Dictionary<int, string>
            {
                { 1, "ASSESSOR" },
                { 2, "PREFORECLOSURE" },
                { 3, "RECORDER" },
                { 4, "ASSESSORDELETE" },
                { 5, "RECORDERDELETE" },
                { 6, "PARCEL" },
                { 7, "PROPERTYTOPARCEL" },
                { 8, "AVM" },
                { 9, "AMORTIZEDEQUITY" } // <-- added pattern
            };


        public AttomFileProcessor(string extractFolder, string connectionString)
        {
            _extractFolder = extractFolder;
            _connectionString = connectionString;
        }

        public void Run()
        {
            Console.WriteLine("📂 Scanning Extracted Folder and Subfolders: " + _extractFolder);

            // Recursively scan folder and subfolders
            var allFiles = GetAllFilesRecursive(_extractFolder, "*.TXT");

            if (!allFiles.Any())
            {
                Console.WriteLine("⚠️ No TXT files found in extract folder or subfolders.");
                return;
            }

            // Build processing queue by priority
            var processingQueue = new List<(int Type, string FilePath)>();

            foreach (var kvp in _fileTypePriority.OrderBy(k => k.Key))
            {
                int parserType = kvp.Key;
                string keyword = kvp.Value.ToUpper();

                var matchedFiles = allFiles.Where(f => Path.GetFileName(f).ToUpper().Contains(keyword))
                                           .OrderBy(f => f) // keep numeric order
                                           .ToList();

                foreach (var file in matchedFiles)
                {
                    processingQueue.Add((parserType, file));
                }
            }

            Console.WriteLine($"✅ Found {processingQueue.Count} files to queue.");

            // Insert files into StaginBulkImportFileQueue
            // InsertFileQueue(processingQueue);

            BulkInsert(processingQueue);
        }

        private List<string> GetAllFilesRecursive(string folder, string searchPattern)
        {
            var files = new List<string>();

            try
            {
                files.AddRange(Directory.GetFiles(folder, searchPattern));

                foreach (var subFolder in Directory.GetDirectories(folder))
                {
                    files.AddRange(GetAllFilesRecursive(subFolder, searchPattern));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Error accessing folder {folder}: {ex.Message}");
            }

            return files;
        }

        private void InsertFileQueue(List<(int Type, string FilePath)> queue)
        {
            using (var conn = new SqlConnection(_connectionString))
            {
                conn.Open();

                foreach (var (type, filePath) in queue)
                {
                    string sql = @"INSERT INTO [dbo].[StaginBulkImportFileQueue]
                                   (Name, Provider, ParserType, FilePath, Processed)
                                   VALUES (@Name, @Provider, @ParserType, @FilePath, NULL)";

                    using (var cmd = new SqlCommand(sql, conn))
                    {
                        cmd.Parameters.AddWithValue("@Name", Path.GetFileName(filePath));
                        cmd.Parameters.AddWithValue("@Provider", "ATTOM"); // Adjust if needed
                        cmd.Parameters.AddWithValue("@ParserType", type);
                        cmd.Parameters.AddWithValue("@FilePath", filePath);

                        int rows = cmd.ExecuteNonQuery();
                        Console.WriteLine($"➡️ Queued: {Path.GetFileName(filePath)} (Type {type}) - Rows inserted: {rows}");
                    }
                }
            }
        }

        private void BulkInsert(List<(int Type, string FilePath)> queue)
        {
            //queue = queue.Where(x => x.FilePath.Contains("_CA_")).ToList();
            queue = queue.Where(x => !x.FilePath.Contains("_done")).ToList();
            using (var conn = new SqlConnection(_connectionString))
            {
                conn.Open();
                CreateLog log1 = new CreateLog();

                foreach (var (type, filePath) in queue)
                {
                    using (var cmd = new SqlCommand("AttomTaxImportStagingBulkInsert", conn))
                    {
                        cmd.CommandType = CommandType.StoredProcedure;
                        cmd.CommandTimeout = 0; // no timeout

                        cmd.Parameters.AddWithValue("@attomDataParserType", type);
                        cmd.Parameters.AddWithValue("@filePath", filePath);
                        int rows = 0;
                        try
                        {
                            rows = cmd.ExecuteNonQuery();
                            if (rows == -1)
                            {
                                string directory = Path.GetDirectoryName(filePath);
                                string oldName = Path.GetFileNameWithoutExtension(filePath);
                                string extension = Path.GetExtension(filePath);

                                // Add suffix
                                string newFileName = oldName + "_done" + extension; // report_backup.csv
                                string newPath = Path.Combine(directory, newFileName);

                                File.Move(filePath, newPath);
                            }

                        }
                        catch (Exception ex)
                        {
                            log1.CreateDebugLog("Error in Bulk Insert: " + ex.Message + " --- File Path: " + filePath);
                        }
                    }
                }
            }
        }
    }

    public static class Config
    {
        public static string SftpHost = "data.attomdata.com";
        public static int SftpPort = 22;
        public static string SftpUser = "1parkplace";
        public static string SftpPass = @"\kJ{pWxvt3E%8L";

        public static string LocalFolder = @"\\192.168.29.45\K$\TaxData\Attom\Processing";
        public static string ExtractFolder = @"\\192.168.29.45\K$\TaxData\Attom\Extracting";
        public static int MaxRetries = 3;
        public static string RemoteOutgoing = "/Outgoing/CA/";
        public static string RemoteProcessed = "/Outgoing/Processed/";
        public static string RemoteTroubleshoot = "/Outgoing/Troubleshoot/";
        public static string extractFolder = @"\\192.168.29.45\K$\TaxData\Attom\Extracting"; /* @"D:\manoj\Extracted";*/
        public static string connString = "Server=server-mssql1.istrategy.com;Database=TitleData;User Id=sa;Password=neo222;Encrypt=True;TrustServerCertificate=True;";


        public static List<string> SeriesKeys = new List<string>
            {
                "1PARKPLACE_RECORDER_",
                "1PARKPLACE_TAXASSESSOR_",
                "1PARKPLACE_FORECLOSURE_",
                "1PARKPLACE_XREF_PROPERTYTOBOUNDARYMATCH_PARCEL_",
                "1PARKPLACE_TEST_AMORTIZEDEQUITY_",
                "1PARKPLACE_TEST_LOANMODEL_",
                "1PARKPLACE_CA_FORECLOSURE_",
                "1PARKPLACE_CA_PROPERTYDELETES_",
                "1PARKPLACE_CA_RECORDER_",
                "1PARKPLACE_CA_RECORDERDELETES_",
                "1PARKPLACE_CA_RECORDERDELETES_",
                "1PARKPLACE_PROPERTYDELETES_",
                "1PARKPLACE_RECORDERDELETES_",
                "1PARKPLACE_CA_TAXASSESSOR_",
                "1PARKPLACE_TAXASSESSOR_"
            };
    }


    public class CreateLog
    {
        public void CreateDebugLog(string ErorMessage)
        {
            string debugFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Debug");

            // Create the folder if it doesn't exist
            if (!Directory.Exists(debugFolder))
            {
                Directory.CreateDirectory(debugFolder);
            }

            // Log file path
            string logFile = Path.Combine(debugFolder, "logFile.txt");

           
            // Append log messages with new line
            File.AppendAllText(logFile, DateTime.Now.ToString()+" ----"+ ErorMessage + Environment.NewLine);
        }

    }
}