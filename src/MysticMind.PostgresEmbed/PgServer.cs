using System;
using System.IO;
using System.Diagnostics;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Polly;

namespace MysticMind.PostgresEmbed
{
    public class PgServer : IDisposable
    {
        private const string PgSuperuser = "postgres";
        private const string PgHost = "localhost";
        private const string PgDbname = "postgres";
        private const string PgStopWaitS = "5";
        private const int PgStartupWaitMs = 10 * 1000;
        private const string PgStopMode = "fast";

        private string _pgBinaryFullPath;

        private readonly string _pgCtlBin;
        private readonly string _initDbBin;
        private readonly string _postgresBin;

        private readonly bool _clearInstanceDirOnStop;

        private readonly bool _clearWorkingDirOnStart;

        private Process _pgServerProcess;

        private readonly List<string> _pgServerParams = new();

        private readonly List<PgExtensionConfig> _pgExtensions = new();

        private readonly bool _addLocalUserAccessPermission;

        private readonly Policy _downloadRetryPolicy;
        private readonly Policy _deleteFoldersRetryPolicy;
        
        private readonly Platform _platform;
        private readonly Architecture _architecture;

        public PgServer(
            string pgVersion,
            string pgUser = PgSuperuser,
            string dbDir = "",
            Guid? instanceId = null,
            int port = 0,
            Dictionary<string, string> pgServerParams = null,
            List<PgExtensionConfig> pgExtensions = null,
            bool addLocalUserAccessPermission = false,
            bool clearInstanceDirOnStop = false, 
            bool clearWorkingDirOnStart=false,
            int deleteFolderRetryCount =5, 
            int deleteFolderInitialTimeout =16, 
            int deleteFolderTimeoutFactor =2,
            string locale = "",
            Platform? platform = null)
        {
            
            _pgCtlBin = "pg_ctl";
            _initDbBin = "initdb";
            _postgresBin = "postgres";
            PgVersion = pgVersion;

            if (platform.HasValue)
            {
                _platform = platform.Value;    
            }
            else
            {
                platform = Utils.GetPlatform();
            }

            if (platform == null)
            {
                throw new UnsupportedPlatformException();
            }

            _platform = platform.Value;
            
            _architecture = Utils.GetArchitecture(_platform);

            PgUser = String.IsNullOrEmpty(pgUser) ? PgSuperuser : pgUser;

            DbDir = Path.Combine(string.IsNullOrEmpty(dbDir) ? "." : dbDir, "pg_embed");

            PgPort = port == 0 ? Utils.GetAvailablePort() : port;

            if (pgServerParams != null)
            {
                foreach (var item in pgServerParams)
                {
                    _pgServerParams.Add($"-c {item.Key}={item.Value}");
                }
            }

            if (pgExtensions != null)
            {
                _pgExtensions.AddRange(pgExtensions);
            }

            instanceId ??= Guid.NewGuid();

            _clearInstanceDirOnStop = clearInstanceDirOnStop;
            _clearWorkingDirOnStart = clearWorkingDirOnStart;

            _addLocalUserAccessPermission = addLocalUserAccessPermission;

            BinariesDir = Path.Combine(DbDir, "binaries");
            InstanceDir = Path.Combine(DbDir, instanceId.ToString());
            PgBinDir = Path.Combine(InstanceDir, "bin");
            DataDir = Path.Combine(InstanceDir, "data");

            // setup the policy for retry pertaining to downloading binary
            _downloadRetryPolicy =
                Policy.Handle<Exception>()
                    .WaitAndRetry(new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4) });
            //Set up the policy for retry pertaining to folder deletion.
            _deleteFoldersRetryPolicy =
                Policy.Handle<Exception>()
                    .WaitAndRetry(deleteFolderRetryCount, retryAttempt =>TimeSpan.FromMilliseconds(deleteFolderInitialTimeout *(int) Math.Pow(deleteFolderTimeoutFactor, retryAttempt-1)));

            if (!string.IsNullOrEmpty(locale))
            {
                Locale = locale;
            }

            if (_platform != Platform.Windows && string.IsNullOrEmpty(Locale))
            {
                Locale = "en_US.UTF-8";
            }
        }

        public string PgVersion { get; private set; }

        public string PgUser { get; private set; }

        public string DbDir { get; private set; }

        public string BinariesDir { get; private set; }

        public string InstanceDir { get; private set; }

        public string PgBinDir { get; private set; }

        public string DataDir { get; private set; }

        public int PgPort { get; private set; }

        public string Locale { get; private set; }

        public string PgDbName => PgDbname;

        private void DownloadPgBinary()
        {
            var downloader = new DefaultPostgresBinaryDownloader(PgVersion, BinariesDir, _platform, _architecture);

            try
            {
                _pgBinaryFullPath = _downloadRetryPolicy.Execute(() => downloader.Download());
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to download PgBinary", ex);
            }
        }

        private void DownloadPgExtensions()
        {
            foreach (var pgExtensionInstance in _pgExtensions.Select(extensionConfig => new PgExtension(BinariesDir, InstanceDir, extensionConfig)))
            {
                _downloadRetryPolicy.Execute(() => pgExtensionInstance.Download());
            }
        }

        private void CreateDirs()
        {
            Directory.CreateDirectory(DbDir);
            Directory.CreateDirectory(BinariesDir);
        }

        private void RemoveWorkingDir() => DeleteDirectory(DbDir);

        private void RemoveInstanceDir() => DeleteDirectory(InstanceDir);

        private void DeleteDirectory(string directoryPath)
        {
            // From http://stackoverflow.com/questions/329355/cannot-delete-directory-with-directory-deletepath-true/329502#329502

            if (!Directory.Exists(directoryPath))
            {
                Trace.WriteLine($"Directory '{directoryPath}' is missing and can't be removed.");
                return;
            }

            NormalizeAttributes(directoryPath);
            _deleteFoldersRetryPolicy.Execute(() =>Directory.Delete(directoryPath, true));
        }

        private static void NormalizeAttributes(string directoryPath)
        {
            var filePaths = Directory.GetFiles(directoryPath);
            var subdirectoryPaths = Directory.GetDirectories(directoryPath);

            foreach (var filePath in filePaths)
            {
                File.SetAttributes(filePath, FileAttributes.Normal);
            }

            foreach (var subdirectoryPath in subdirectoryPaths)
            {
                NormalizeAttributes(subdirectoryPath);
            }

            File.SetAttributes(directoryPath, FileAttributes.Normal);
        }

        private void ExtractPgBinary()
        {
            Utils.ExtractZip(_pgBinaryFullPath, InstanceDir);
        }

        private void ExtractPgExtensions()
        {
            foreach (var extensionConfig in _pgExtensions)
            {
                var pgExtensionInstance = new PgExtension(BinariesDir, InstanceDir, extensionConfig);
                _downloadRetryPolicy.Execute(() => pgExtensionInstance.Extract());
            }
        }

        // In some cases like CI environments, local user account will have write access
        // on the Instance directory (Postgres expects write access on the parent of data directory)
        // Otherwise when running initdb, it results in 'initdb: could not change permissions of directory'
        // Also note that the local account should have admin rights to change folder permissions
        private void AddLocalUserAccessPermission()
        {
            if (_platform != Platform.Windows)
            {
                return;
            }

            var filename = "icacls.exe";
            var args = new List<string>();

            // get the local user under which the program runs
            var currentLocalUser = Environment.GetEnvironmentVariable("Username");

            args.Add(InstanceDir);
            args.Add("/t");
            args.Add("/grant:r");
            args.Add($"{currentLocalUser}:(OI)(CI)F");

            try
            {
                var result = Utils.RunProcess(filename, args);

                if (result.ExitCode != 0)
                {
                    throw new Exception($"Adding full access permission to local user account on instance folder returned an error code {result.ExitCode} {result.Output} {result.Error}");
                }
            }
            catch (Exception ex)
            {
                throw new Exception("Error occurred while adding full access permission to local account on instance folder", ex);
            }
        }

        private void SetBinariesAsExecutable()
        {
            if (_platform == Platform.Windows)
            {
                return;    
            }
            
            var username = Environment.UserName;

            Utils.RunProcess("chmod",  new List<string>
            {
                $"+x {Path.Combine(PgBinDir, _initDbBin)}"
            });
            Utils.RunProcess("chmod",  new List<string>
            {
                $"+x {Path.Combine(PgBinDir, _pgCtlBin)}"
            });
            Utils.RunProcess("chmod",  new List<string>
            {
                $"+x {Path.Combine(PgBinDir, _postgresBin)}"
            });
        }

        private void InitDb()
        {
            var filename = Path.Combine(PgBinDir, _initDbBin);
            var args = new List<string>
            {
                // add data dir
                $"-D {DataDir}",
                // add super user
                $"-U {PgUser}",
                // add encoding
                "-E UTF-8"
            };

            // add locale if provided
            if (Locale != null)
            {
                args.Add($"--locale {Locale}");
            }

            try
            {
                var result = Utils.RunProcess(filename, args);

                if (result.ExitCode != 0)
                {
                    throw new Exception($"InitDb execution returned an error code {result.ExitCode} {result.Output} {result.Error}");
                }
            }
            catch (Exception ex)
            {
                throw new Exception("Error occurred while executing InitDb", ex);
            }
        }

        private bool VerifyReady()
        {
            // var filename = Path.Combine(PgBinDir, PsqlExe);
            //
            // var args = new List<string>
            // {
            //     // add host
            //     $"-h {PgHost}",
            //     //add port
            //     $"-p {PgPort}",
            //     //add  user
            //     $"-U {PgUser}",
            //     // add database name
            //     $"-d {PgDbName}",
            //     // add command
            //     $"-c \"SELECT 1 as test\""
            // };
            //
            // var result = Utils.RunProcess(filename, args);
            //
            // return result.ExitCode == 0;
            using var tcpClient = new TcpClient();
            try
            {
                tcpClient.Connect(PgHost, PgPort);
                return true;
            }
            catch (Exception)
            {
                // intentionally left unhandled
            }

            return false;
        }

        private void StartServer()
        {
            var filename = Path.Combine(PgBinDir, _pgCtlBin);

            var args = new List<string>
            {
                // add the data dir arg
                $"-D {DataDir}",
                // add user
                $"-U {PgUser}"
            };

            // create the init options arg
            var initOptions = new List<string>
            {
                // run without fsync
                "-F",
                //set the port
                $"-p {PgPort}"
            };

            // add the additional parameters passed
            initOptions.AddRange(_pgServerParams);

            // add options arg
            args.Add($"-o \"{string.Join(" ", initOptions)}\"");

            // add start arg
            args.Add("start");

            try
            {
                _pgServerProcess = new Process();

                _pgServerProcess.StartInfo.RedirectStandardError = true;
                _pgServerProcess.StartInfo.RedirectStandardOutput = true;
                _pgServerProcess.StartInfo.UseShellExecute = false;
                _pgServerProcess.EnableRaisingEvents = true;

                _pgServerProcess.StartInfo.FileName = filename;
                _pgServerProcess.StartInfo.Arguments = string.Join(" ", args);
                _pgServerProcess.StartInfo.CreateNoWindow = true;

                _pgServerProcess.Start();

                // allow some time for postgres to start
                var watch = new Stopwatch();
                watch.Start();

                WaitForServerStartup(watch);
            }
            catch (Exception ex)
            {
                throw new Exception("Exception occurred while starting Pg server", ex);
            }

        }

        private void WaitForServerStartup(Stopwatch watch)
        {
            while (watch.ElapsedMilliseconds < PgStartupWaitMs)
            {
                // verify if server ready
                if (VerifyReady())
                {
                    return;
                }

                Thread.Sleep(100);
            }

            watch.Stop();

            throw new IOException($"Gave up waiting for server to start after {PgStartupWaitMs}ms");
        }

        private void StopServer()
        {
            var filename = Path.Combine(PgBinDir, _pgCtlBin);

            var args = new List<string>
            {
                // add data dir
                $"-D {DataDir}",
                // add user
                $"-U {PgUser}",
                // add stop mode
                $"-m {PgStopMode}",
                // stop wait secs
                $"-t {PgStopWaitS}",
                // add stop action
                "stop"
            };

            try
            {
                Utils.RunProcess(filename, args);
            }
            catch
            {
                // ignored
            }
        }

        private void KillServerProcess()
        {
            try
            {
                _pgServerProcess.Kill();
            }
            catch
            {
                // ignored
            }
        }

        public void Start()
        {
            // clear working directory based on flag passed
            if (_clearWorkingDirOnStart)
            {
                RemoveWorkingDir();
            }

            if (!Directory.Exists(InstanceDir))
            {
                CreateDirs();

                // if the file already exists, download will be skipped
                DownloadPgBinary();

                // if the file already exists, download will be skipped
                DownloadPgExtensions();

                ExtractPgBinary();
                ExtractPgExtensions();

                if (_addLocalUserAccessPermission)
                {
                    AddLocalUserAccessPermission();
                }

                SetBinariesAsExecutable();
                InitDb();
                StartServer();
            } 
            else
            {
                StartServer();
            }
            
        }

        public void Stop()
        {
            StopServer();
            KillServerProcess();

            // clear instance directory based on flag passed
            if (_clearInstanceDirOnStop)
            {
                RemoveInstanceDir();
            }
        }

        public async Task StartAsync(CancellationToken token)
        {
            await Task.Run(Start, token);
        }

        public async Task StartAsync() => await StartAsync(CancellationToken.None);

        public async Task StopAsync(CancellationToken token)
        {
            await Task.Run(Stop, token);
        }

        public async Task StopAsync() => await StopAsync(CancellationToken.None);

        public void Dispose()
        {
            Stop();
        }
    }
}
