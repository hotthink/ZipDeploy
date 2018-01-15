﻿using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace ZipDeploy
{
    public class ZipDeploy
    {
        private object              _installLock        = new object();
        private bool                _installerDetected;
        private ILogger<ZipDeploy>  _log;
        private RequestDelegate     _next;
        private string              _iisUrl;

        public ZipDeploy(RequestDelegate next, ILogger<ZipDeploy> log, ZipDeployOptions options)
        {
            _log = log;
            _next = next;
            _iisUrl = options.IisUrl;

            _log.LogInformation($"ZipDeploy started [IisUrl={_iisUrl}]");

            CompleteInstallation();
            StartWatchingForInstaller();
            DetectInstaller();
        }

        public async Task Invoke(HttpContext context)
        {
            await _next(context);

            if (!_installerDetected)
                return;

            _log.LogAndSwallowException("ZipDeploy.Invoke - installerDetected", () =>
            {
                var callIis = false;

                lock (_installLock)
                {
                    if (HasPublish())
                    {
                        callIis = true;
                        InstallBinaries();
                    }
                }

                if (callIis)
                    CallIis().GetAwaiter().GetResult();
            });
        }

        private void InstallBinaries()
        {
            _log.LogDebug("Installing binaries (and renaming old ones)");

            var config = (string)null;

            _log.LogDebug("Opening publish.zip");
            using (var zipFile = ZipFile.OpenRead("publish.zip"))
            {
                var entries = zipFile.Entries
                    .ToDictionary(zfe => zfe.FullName, zfe => zfe);

                _log.LogDebug($"{entries.Count} entries in zip");

                var dlls = entries.Keys
                    .Where(k => Path.GetExtension(k)?.ToLower() == ".dll")
                    .ToList();

                _log.LogDebug($"{dlls.Count} dlls in zip");

                var dllsWithoutExtension = dlls.Select(dll => Path.GetFileNameWithoutExtension(dll)).ToList();

                foreach (var entry in entries)
                {
                    var fullName = entry.Key;

                    if (!dllsWithoutExtension.Contains(Path.GetFileNameWithoutExtension(fullName)))
                        continue;

                    if (File.Exists(fullName))
                    {
                        var destinationFile = $"{fullName}.fordelete.txt";

                        if (File.Exists(destinationFile))
                        {
                            _log.LogDebug($"deleting existing {destinationFile}");
                            File.Delete(destinationFile);
                        }

                        _log.LogDebug($"renaming {fullName} to {destinationFile}");
                        File.Move(fullName, destinationFile);
                    }

                    var zipEntry = entry.Value;

                    using (var streamWriter = File.Create(fullName))
                    using (var zipInput = zipEntry.Open())
                    {
                        _log.LogDebug($"extracting {fullName}");
                        zipInput.CopyTo(streamWriter);
                    }
                }

                if (entries.ContainsKey("web.config"))
                {
                    using (var zipInput = entries["web.config"].Open())
                    using (var sr = new StreamReader(zipInput))
                        config = sr.ReadToEnd();
                }
            }

            if (File.Exists("installing.zip"))
            {
                _log.LogDebug($"deleting existing installing.zip");
                File.Delete("installing.zip");
            }

            _log.LogDebug($"renaming publish.zip to installing.zip");
            File.Move("publish.zip", "installing.zip");

            _log.LogDebug("Binaries extracted, triggering restart by 'touching' web.config");

            config = config ?? File.ReadAllText("web.config");
            File.WriteAllText("web.config", config);

            new Thread(() => ReUpdateWebConfig(config)).Start();
        }

        private void ReUpdateWebConfig(string config)
        {
            Thread.Sleep(1000);

            _log.LogDebug("process still running; re-touching web.config");
            File.WriteAllText("web.config", config);
            new Thread(() => ReUpdateWebConfig(config)).Start();
        }

        private void CompleteInstallation()
        {
            if (File.Exists("installing.zip"))
            {
                using (var zipFile = ZipFile.OpenRead("installing.zip"))
                {
                    var entries = zipFile.Entries
                        .Where(e => e.Length != 0)
                        .ToDictionary(zfe => zfe.FullName, zfe => zfe);

                    var dlls = entries.Keys
                        .Where(k => Path.GetExtension(k)?.ToLower() == ".dll")
                        .ToList();

                    var dllsWithoutExtension = dlls.Select(dll => Path.GetFileNameWithoutExtension(dll)).ToList();

                    foreach (var entry in entries)
                    {
                        var fullName = entry.Key;

                        if (dllsWithoutExtension.Contains(Path.GetFileNameWithoutExtension(fullName)))
                            continue;

                        if (fullName == "web.config")
                            continue;

                        if (File.Exists(fullName))
                        {
                            var destinationFile = $"{fullName}.fordelete.txt";

                            if (File.Exists(destinationFile))
                                File.Delete(destinationFile);

                            File.Move(fullName, destinationFile);
                        }

                        var zipEntry = entry.Value;

                        var folder = Path.GetDirectoryName(fullName);

                        if (!string.IsNullOrWhiteSpace(folder))
                            Directory.CreateDirectory(folder);

                        using (var streamWriter = File.Create(fullName))
                        using (var zipInput = zipEntry.Open())
                            zipInput.CopyTo(streamWriter);
                    }
                }

                if (File.Exists("deployed.zip"))
                    File.Delete("deployed.zip");

                File.Move("installing.zip", "deployed.zip");
            }

            Task.Factory.StartNew(DeleteForDeleteFiles);
        }

        private void DeleteForDeleteFiles()
        {
            foreach (var forDelete in Directory.GetFiles(".", "*.fordelete.txt", SearchOption.AllDirectories))
            {
                while (File.Exists(forDelete))
                {
                    try
                    {
                        File.Delete(forDelete);
                    }
                    catch
                    {
                        Thread.Sleep(0);
                    }
                }
            }
        }

        private void StartWatchingForInstaller()
        {
            var fsw = new FileSystemWatcher(Environment.CurrentDirectory, "publish.zip");
            fsw.Created += ZipFileChange;
            fsw.Changed += ZipFileChange;
            fsw.Renamed += ZipFileChange;
            fsw.EnableRaisingEvents = true;
            _log.LogInformation($"Watching for publish.zip in {Environment.CurrentDirectory}");
        }

        private void ZipFileChange(object sender, FileSystemEventArgs e)
        {
            _log.LogAndSwallowException("ZipFileChange", DetectInstaller);
        }
        
        private void DetectInstaller()
        {
            if (_installerDetected)
                return;

            lock (_installLock)
            {
                if (_installerDetected)
                    return;

                if (HasPublish())
                {
                    _log.LogDebug("Detected installer");
                    _installerDetected = true;
                    CallIis().GetAwaiter().GetResult();
                }
            }
        }

        private bool HasPublish()
        {
            return File.Exists("publish.zip");
        }

        private Task<HttpResponseMessage> CallIis()
        {
            if (string.IsNullOrWhiteSpace(_iisUrl))
                return Task.FromResult<HttpResponseMessage>(null);

            _log.LogDebug($"Making request to IIS: {_iisUrl}");
            return new HttpClient().GetAsync(_iisUrl);
        }
    }
}
